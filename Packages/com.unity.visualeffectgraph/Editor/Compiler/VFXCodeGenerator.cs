using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.VFX;

using UnityEditor.ShaderGraph;
using UnityEditor.Graphing.Util;
using UnityEditor.ShaderGraph.Serialization;

using Object = UnityEngine.Object;
using System.Text.RegularExpressions;
using System.Globalization;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

namespace UnityEditor.VFX
{
    static class VFXCodeGenerator
    {
        public const uint nbThreadsPerGroup = 64u;

        private static string GetIndent(string src, int index)
        {
            var indent = "";
            index--;
            while (index > 0 && (src[index] == ' ' || src[index] == '\t'))
            {
                indent = src[index] + indent;
                index--;
            }
            return indent;
        }

        //This function insure to keep padding while replacing a specific string
        private static void ReplaceMultiline(StringBuilder target, string targetQuery, StringBuilder value)
        {
            Profiler.BeginSample("ReplaceMultiline");

            string[] delim = { System.Environment.NewLine, "\n" };
            var valueLines = value.ToString().Split(delim, System.StringSplitOptions.None);
            if (valueLines.Length <= 1)
            {
                target.Replace(targetQuery, value.ToString());
            }
            else
            {
                while (true)
                {
                    var targetCopy = target.ToString();
                    var index = targetCopy.IndexOf(targetQuery, StringComparison.Ordinal);
                    if (index == -1)
                    {
                        break;
                    }

                    var indent = GetIndent(targetCopy, index);
                    var currentValue = new StringBuilder();
                    foreach (var line in valueLines)
                    {
                        currentValue.Append(indent + line + '\n');
                    }
                    target.Replace(indent + targetQuery, currentValue.ToString());
                }
            }

            Profiler.EndSample();
        }

        internal static VFXShaderWriter GenerateLoadAttribute(string matching, VFXContext context, VFXTaskCompiledData taskData)
        {
            var r = new VFXShaderWriter();

            var regex = new Regex(matching);
            var attributesFromContext = context.GetData().GetAttributes().Where(o => regex.IsMatch(o.attrib.name)).ToArray();
            var attributesSource = attributesFromContext.Where(a => context.GetData().IsSourceAttributeUsed(a.attrib, context)).ToArray();
            var attributesCurrent = attributesFromContext.Where(a => context.GetData().IsCurrentAttributeUsed(a.attrib, context) || (context.contextType == VFXContextType.Init && context.GetData().IsAttributeStored(a.attrib))).ToArray();

            //< Current Attribute
            foreach (var attribute in attributesCurrent.Select(o => o.attrib))
            {
                var name = attribute.GetNameInCode(VFXAttributeLocation.Current);
                if (attribute.name != VFXAttribute.EventCount.name)
                {
                    if (context.contextType != VFXContextType.Init && context.GetData().IsAttributeStored(attribute))
                    {
                        r.WriteAssignement(attribute.type, name, context.GetData().GetLoadAttributeCode(attribute, VFXAttributeLocation.Current));
                    }
                    else
                    {
                        r.WriteAssignement(attribute.type, name, attribute.value.GetCodeString(null));
                    }
                }
                else
                {
                    r.WriteAssignement(attribute.type, name, attribute.value.GetCodeString(null));
                    for (uint i = 0; i < taskData.linkedEventOut.Length; ++i)
                    {
                        r.WriteLine();
                        var linkedEventOut = taskData.linkedEventOut[i];
                        var capacity = (uint)linkedEventOut.data.GetSettingValue("capacity");
                        r.WriteFormat("uint {0}_{1} = 0u; uint {0}_{1}_Capacity = {2};", VFXAttribute.EventCount.name, VFXCodeGeneratorHelper.GeneratePrefix(i), capacity);
                    }
                }
                r.WriteLine();
            }

            //< Source Attribute (default temporary behavior, source is always the initial current value except for init context)
            foreach (var attribute in attributesSource.Select(o => o.attrib))
            {
                var name = attribute.GetNameInCode(VFXAttributeLocation.Source);
                if (context.contextType == VFXContextType.Init)
                {
                    r.WriteAssignement(attribute.type, name, context.GetData().GetLoadAttributeCode(attribute, VFXAttributeLocation.Source));
                }
                else
                {
                    if (attributesCurrent.Any(o => o.attrib.name == attribute.name))
                    {
                        var reference = new VFXAttributeExpression(new VFXAttribute(attribute.name, attribute.value), VFXAttributeLocation.Current);
                        r.WriteAssignement(reference.valueType, name, reference.GetCodeString(null));
                    }
                    else
                    {
                        r.WriteAssignement(attribute.type, name, attribute.value.GetCodeString(null));
                    }
                }
                r.WriteLine();
            }
            return r;
        }

        private const string eventListOutName = "eventListOut";

        static private VFXShaderWriter GenerateStoreAttribute(string matching, VFXContext context, uint linkedOutCount)
        {
            var r = new VFXShaderWriter();
            var regex = new Regex(matching);

            var attributesFromContext = context.GetData().GetAttributes().Where(o => regex.IsMatch(o.attrib.name) &&
                context.GetData().IsAttributeStored(o.attrib) &&
                (context.contextType == VFXContextType.Init || context.GetData().IsCurrentAttributeWritten(o.attrib, context))).ToArray();

            foreach (var attribute in attributesFromContext.Select(o => o.attrib))
            {
                r.Write(context.GetData().GetStoreAttributeCode(attribute, new VFXAttributeExpression(attribute).GetCodeString(null)));
                r.WriteLine(';');
            }

            if (regex.IsMatch(VFXAttribute.EventCount.name))
            {
                for (uint i = 0; i < linkedOutCount; ++i)
                {
                    var prefix = VFXCodeGeneratorHelper.GeneratePrefix(i);
                    r.WriteLineFormat(@"
for (uint i_{0} = 0; i_{0} < min({1}_{0}, {1}_{0}_Capacity); ++i_{0})
    AppendEventBuffer({2}_{0}, index, {1}_{0}_Capacity, instanceIndex);
AppendEventTotalCount({2}_{0}, min({1}_{0}, {1}_{0}_Capacity), instanceIndex);
",
                        prefix,
                        VFXAttribute.EventCount.name,
                        eventListOutName);
                }
            }
            return r;
        }
        static internal VFXShaderWriter GenerateSetInstancingIndices(VFXContext context)
        {
            var r = new VFXShaderWriter();

            // Hardcoded, duplicated from VFXParticleCommon.template
            r.WriteLine("uint instanceIndex, instanceActiveIndex;");
            r.WriteLine("index = VFXInitInstancing(index, instanceIndex, instanceActiveIndex);");

            return r;
        }

        static internal VFXShaderWriter GenerateLoadParameter(string matching, VFXNamedExpression[] namedExpressions, Dictionary<VFXExpression, string> expressionToName)
        {
            var r = new VFXShaderWriter();
            var regex = new Regex(matching);

            var filteredNamedExpressions = namedExpressions.Where(o => regex.IsMatch(o.name) &&
                !(expressionToName.ContainsKey(o.exp) && expressionToName[o.exp] == o.name));     // if parameter already in the global scope, there's nothing to do

            bool needScope = false;
            foreach (var namedExpression in filteredNamedExpressions)
            {
                r.WriteVariable(namedExpression.exp.valueType, namedExpression.name, "0");
                r.WriteLine();
                needScope = true;
            }

            if (needScope)
            {
                var expressionToNameLocal = new Dictionary<VFXExpression, string>(expressionToName);
                r.EnterScope();
                foreach (var namedExpression in filteredNamedExpressions)
                {
                    if (!expressionToNameLocal.ContainsKey(namedExpression.exp))
                    {
                        r.WriteVariable(namedExpression.exp, expressionToNameLocal);
                        r.WriteLine();
                    }
                    r.WriteAssignement(namedExpression.exp.valueType, namedExpression.name, expressionToNameLocal[namedExpression.exp]);
                    r.WriteLine();
                }
                r.ExitScope();
            }

            return r;
        }

        static public StringBuilder Build(VFXContext context, VFXTask task, VFXCompilationMode compilationMode,
            VFXTaskCompiledData taskData, HashSet<string> dependencies, bool forceShadeDebugSymbols)
        {
            var templatePath = string.Format("{0}.template", task.templatePath);

            dependencies.Add(AssetDatabase.AssetPathToGUID(templatePath));
            return Build(context, task, templatePath, compilationMode, taskData, dependencies, forceShadeDebugSymbols);
        }

        static private void GetFunctionName(VFXBlock block, out string functionName, out string comment)
        {
            var settings = block.GetSettings(true).ToArray();
            if (settings.Length > 0)
            {
                comment = "";
                int hash = 0;
                foreach (var setting in settings)
                {
                    var value = setting.value;
                    hash = (hash * 397) ^ value.GetHashCode();
                    comment += string.Format("{0}:{1} ", setting.field.Name, value.ToString());
                }
                functionName = string.Format("{0}_{1}", block.GetType().Name, hash.ToString("X"));
            }
            else
            {
                comment = null;
                functionName = block.GetType().Name;
            }
        }

        static private string FormatPath(string path)
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
#if !UNITY_EDITOR_LINUX
                .ToLowerInvariant()
#endif
                ;
        }

        static IEnumerable<Match> GetUniqueMatches(string regexStr, string src)
        {
            var regex = new Regex(regexStr);
            var matches = regex.Matches(src);
            return matches.Cast<Match>().GroupBy(m => m.Groups[0].Value).Select(g => g.First());
        }

        static private StringBuilder GetFlattenedTemplateContent(string path, List<string> includes, IEnumerable<string> defines, HashSet<string> dependencies)
        {
            var formattedPath = FormatPath(path);

            if (includes.Contains(formattedPath))
            {
                var includeHierarchy = new StringBuilder(string.Format("Cyclic VFXInclude dependency detected: {0}\n", formattedPath));
                foreach (var str in Enumerable.Reverse<string>(includes))
                    includeHierarchy.Append(str + '\n');
                throw new InvalidOperationException(includeHierarchy.ToString());
            }

            includes.Add(formattedPath);
            var templateContent = new StringBuilder(System.IO.File.ReadAllText(formattedPath));

            foreach (var match in GetUniqueMatches("\\${VFXInclude(RP|)\\(\\\"(.*?)\\\"\\)(,.*)?}", templateContent.ToString()))
            {
                var groups = match.Groups;
                var renderPipelineInclude = groups[1].Value == "RP";
                var includePath = groups[2].Value;


                if (groups.Count > 3 && !String.IsNullOrEmpty(groups[2].Value))
                {
                    var allDefines = groups[3].Value.Split(new char[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    var neededDefines = allDefines.Where(d => d[0] != '!');
                    var forbiddenDefines = allDefines.Except(neededDefines).Select(d => d.Substring(1));
                    if (!neededDefines.All(d => defines.Contains(d)) || forbiddenDefines.Any(d => defines.Contains(d)))
                    {
                        ReplaceMultiline(templateContent, groups[0].Value, new StringBuilder());
                        continue;
                    }
                }

                string absolutePath;
                if (renderPipelineInclude)
                    absolutePath = VFXLibrary.currentSRPBinder.templatePath + "/" + includePath;
                else
                    absolutePath = VisualEffectGraphPackageInfo.assetPackagePath + "/" + includePath;
                dependencies.Add(AssetDatabase.AssetPathToGUID(absolutePath));

                var includeBuilder = GetFlattenedTemplateContent(absolutePath, includes, defines, dependencies);
                ReplaceMultiline(templateContent, groups[0].Value, includeBuilder);
            }

            includes.Remove(formattedPath);
            return templateContent;
        }

        static private void SubstituteMacros(StringBuilder builder)
        {
            var definesToCode = new Dictionary<string, string>();
            var source = builder.ToString();
            Regex beginRegex = new Regex("\\${VFXBegin:(.*)}");

            int currentPos = -1;
            int builderOffset = 0;
            while ((currentPos = source.IndexOf("${", StringComparison.Ordinal)) != -1)
            {
                int endPos = source.IndexOf('}', currentPos);
                if (endPos == -1)
                    throw new FormatException("Ill-formed VFX tag (Missing closing brace");

                var tag = source.Substring(currentPos, endPos - currentPos + 1);
                // Replace any tag found
                string macro;
                if (definesToCode.TryGetValue(tag, out macro))
                {
                    builder.Remove(currentPos + builderOffset, tag.Length);
                    var indentedMacro = macro.Replace("\n", "\n" + GetIndent(source, currentPos));
                    builder.Insert(currentPos + builderOffset, indentedMacro);
                }
                else
                {
                    const string endStr = "${VFXEnd}";
                    var match = beginRegex.Match(source, currentPos, tag.Length);
                    if (match.Success)
                    {
                        var macroStartPos = match.Index + match.Length;
                        var macroEndCodePos = source.IndexOf(endStr, macroStartPos);
                        if (macroEndCodePos == -1)
                            throw new FormatException("${VFXBegin} found without ${VFXEnd}");

                        var defineStr = "${" + match.Groups[1].Value + "}";
                        definesToCode[defineStr] = source.Substring(macroStartPos, macroEndCodePos - macroStartPos);

                        // Remove the define in builder
                        builder.Remove(match.Index + builderOffset, macroEndCodePos - match.Index + endStr.Length);
                    }
                    else if (tag == endStr)
                        throw new FormatException("${VFXEnd} found without ${VFXBegin}");
                    else // Remove undefined tag
                        builder.Remove(currentPos + builderOffset, tag.Length);
                }

                builderOffset += currentPos;
                source = builder.ToString(builderOffset, builder.Length - builderOffset);
            }
        }

        internal static void BuildContextBlocks(VFXContext context, VFXTaskCompiledData taskData,
            out string blockFunctionContent,
            out string blockCallFunctionContent)
        {
            //< Block processor
            var blockFunction = new VFXShaderWriter();
            var blockCallFunction = new VFXShaderWriter();
            var blockDeclared = new HashSet<string>();
            var expressionToName = context.GetData().GetAttributes().ToDictionary(o => new VFXAttributeExpression(o.attrib) as VFXExpression, o => (new VFXAttributeExpression(o.attrib)).GetCodeString(null));
            expressionToName = expressionToName.Union(taskData.uniformMapper.expressionToCode).ToDictionary(s => s.Key, s => s.Value);

            int cpt = 0;
            foreach (var current in context.activeFlattenedChildrenWithImplicit)
            {
                BuildBlock(taskData, blockFunction, blockCallFunction, blockDeclared, expressionToName, current, ref cpt);
            }

            blockFunctionContent = blockFunction.builder.ToString();
            blockCallFunctionContent = blockCallFunction.builder.ToString();
        }

        internal static void BuildParameterBuffer(VFXTaskCompiledData taskData, IEnumerable<string> filteredOutTextures, out string parameterBufferContent, out bool needsGraphValueStruct) //TODO: pass all in one? Do we need some info out of that method?
        {
            var parameterBuffer = new VFXShaderWriter();
            needsGraphValueStruct = parameterBuffer.WriteGraphValuesStruct(taskData.uniformMapper);
            parameterBuffer.WriteLine();
            parameterBuffer.WriteBufferTypeDeclaration(taskData.graphicsBufferUsage.Values);
            parameterBuffer.WriteLine();
            parameterBuffer.WriteBuffer(taskData.uniformMapper, taskData.graphicsBufferUsage);
            parameterBuffer.WriteLine();
            parameterBuffer.WriteTexture(taskData.uniformMapper, filteredOutTextures);
            parameterBufferContent = parameterBuffer.ToString();
        }

        internal static void BuildVertexProperties(VFXContext context, VFXTaskCompiledData taskData, out string vertexProperties)
        {
            var expressionToName = context.GetData().GetAttributes().ToDictionary(o => new VFXAttributeExpression(o.attrib) as VFXExpression, o => (new VFXAttributeExpression(o.attrib)).GetCodeString(null));
            expressionToName = expressionToName.Union(taskData.uniformMapper.expressionToCode).ToDictionary(s => s.Key, s => s.Value);

            var mainParameters = taskData.gpuMapper.CollectExpression(-1).ToArray();

            var additionalVertexProperties = new VFXShaderWriter();

            foreach (string vertexParameter in context.vertexParameters)
            {
                var filteredNamedExpression = mainParameters.FirstOrDefault(o => vertexParameter == o.name &&
                    !(expressionToName.ContainsKey(o.exp) && expressionToName[o.exp] == o.name));                                                              // if parameter already in the global scope, there's nothing to do

                if (filteredNamedExpression.exp != null)
                {
                    additionalVertexProperties.WriteVariable(filteredNamedExpression.exp.valueType, filteredNamedExpression.name + "__", "0");
                    var expressionToNameLocal = new Dictionary<VFXExpression, string>(expressionToName);
                    additionalVertexProperties.EnterScope();
                    {
                        if (!expressionToNameLocal.ContainsKey(filteredNamedExpression.exp))
                        {
                            additionalVertexProperties.WriteVariable(filteredNamedExpression.exp, expressionToNameLocal);
                            additionalVertexProperties.WriteLine();
                        }
                        additionalVertexProperties.WriteAssignement(filteredNamedExpression.exp.valueType, filteredNamedExpression.name + "__", expressionToNameLocal[filteredNamedExpression.exp]);
                        additionalVertexProperties.WriteLine();
                    }
                    additionalVertexProperties.ExitScope();
                }
            }

            vertexProperties = additionalVertexProperties.ToString();
        }

        internal static void BuildVertexPropertiesAssign(VFXContext context, VFXTaskCompiledData taskData, out string buildVertexPropertiesGeneration)
        {
            var expressionToName = context.GetData().GetAttributes().ToDictionary(o => new VFXAttributeExpression(o.attrib) as VFXExpression, o => (new VFXAttributeExpression(o.attrib)).GetCodeString(null));
            expressionToName = expressionToName.Union(taskData.uniformMapper.expressionToCode).ToDictionary(s => s.Key, s => s.Value);

            var mainParameters = taskData.gpuMapper.CollectExpression(-1).ToArray();

            var vertexInputsGeneration = new VFXShaderWriter();

            foreach (string vertexParameter in context.vertexParameters)
            {
                var filteredNamedExpression = mainParameters.FirstOrDefault(o => vertexParameter == o.name);
                if (filteredNamedExpression.exp == null)
                    throw new InvalidOperationException(string.Format("Cannot find vertex property : {0}", vertexParameter));

                // If the parameter is in the global scope, read from the cbuffer directly (no suffix).
                if (!(expressionToName.ContainsKey(filteredNamedExpression.exp) && expressionToName[filteredNamedExpression.exp] == filteredNamedExpression.name))
                    vertexInputsGeneration.WriteAssignement(filteredNamedExpression.exp.valueType, $"properties.{filteredNamedExpression.name}", $"{filteredNamedExpression.name}__");
                else
                    vertexInputsGeneration.WriteAssignement(filteredNamedExpression.exp.valueType, $"properties.{filteredNamedExpression.name}", $"{filteredNamedExpression.name}");

                vertexInputsGeneration.WriteLine();
            }

            buildVertexPropertiesGeneration = vertexInputsGeneration.ToString();
        }

        internal static void BuildInterpolatorBlocks(VFXContext context, VFXTaskCompiledData taskData, bool raytracing,
            out string interpolatorsGeneration)
        {
            var expressionToName = context.GetData().GetAttributes().ToDictionary(o => new VFXAttributeExpression(o.attrib) as VFXExpression, o => (new VFXAttributeExpression(o.attrib)).GetCodeString(null));
            expressionToName = expressionToName.Union(taskData.uniformMapper.expressionToCode).ToDictionary(s => s.Key, s => s.Value);

            var mainParameters = taskData.gpuMapper.CollectExpression(-1).ToArray();

            var additionalInterpolantsGeneration = new VFXShaderWriter();
            var additionalInterpolantsPreparation = new VFXShaderWriter();
            string varyingVariableName = raytracing ? "input." : "output.";
            foreach (string fragmentParameter in context.fragmentParameters)
            {
                var filteredNamedExpression = mainParameters.FirstOrDefault(o => fragmentParameter == o.name &&
                    !(expressionToName.ContainsKey(o.exp) && expressionToName[o.exp] == o.name)); // if parameter already in the global scope, there's nothing to do

                if (filteredNamedExpression.exp != null)
                {
                    additionalInterpolantsGeneration.WriteVariable(filteredNamedExpression.exp.valueType, filteredNamedExpression.name + "__", "0");
                    var expressionToNameLocal = new Dictionary<VFXExpression, string>(expressionToName);
                    additionalInterpolantsGeneration.EnterScope();
                    {
                        if (!expressionToNameLocal.ContainsKey(filteredNamedExpression.exp))
                        {
                            additionalInterpolantsGeneration.WriteVariable(filteredNamedExpression.exp, expressionToNameLocal);
                            additionalInterpolantsGeneration.WriteLine();
                        }
                        additionalInterpolantsGeneration.WriteAssignement(filteredNamedExpression.exp.valueType, filteredNamedExpression.name + "__", expressionToNameLocal[filteredNamedExpression.exp]);
                        additionalInterpolantsGeneration.WriteLine();
                    }
                    additionalInterpolantsGeneration.ExitScope();
                    additionalInterpolantsGeneration.WriteAssignement(filteredNamedExpression.exp.valueType, varyingVariableName + filteredNamedExpression.name, filteredNamedExpression.name + "__");
                    additionalInterpolantsPreparation.WriteVariable(filteredNamedExpression.exp.valueType, filteredNamedExpression.name, "i." + filteredNamedExpression.name);
                }
            }

            interpolatorsGeneration = additionalInterpolantsGeneration.ToString();
        }

        internal static void BuildFragInputsGeneration(VFXContext context, VFXTaskCompiledData taskData, bool useFragInputs, out string buildFragInputsGeneration)
        {
            var expressionToName = context.GetData().GetAttributes().ToDictionary(o => new VFXAttributeExpression(o.attrib) as VFXExpression, o => (new VFXAttributeExpression(o.attrib)).GetCodeString(null));
            expressionToName = expressionToName.Union(taskData.uniformMapper.expressionToCode).ToDictionary(s => s.Key, s => s.Value);

            var mainParameters = taskData.gpuMapper.CollectExpression(-1).ToArray();

            var fragInputsGeneration = new VFXShaderWriter();

            foreach (string fragmentParameter in context.fragmentParameters)
            {
                var filteredNamedExpression = mainParameters.FirstOrDefault(o => fragmentParameter == o.name);
                if (filteredNamedExpression.exp == null)
                    throw new InvalidOperationException("FragInputs generation failed to find expected parameter: " + fragmentParameter);

                var isInterpolant = !(expressionToName.ContainsKey(filteredNamedExpression.exp) && expressionToName[filteredNamedExpression.exp] == filteredNamedExpression.name);

                var surfaceSetter = useFragInputs ? "output.vfx" : "output";
                fragInputsGeneration.WriteAssignement(filteredNamedExpression.exp.valueType, $"{surfaceSetter}.{filteredNamedExpression.name}", $"{(isInterpolant ? "input." : string.Empty)}{filteredNamedExpression.name}");
                fragInputsGeneration.WriteLine();
            }

            buildFragInputsGeneration = fragInputsGeneration.ToString();
        }

        internal static void BuildPixelPropertiesAssign(VFXContext context, VFXTaskCompiledData taskData, bool useFragInputs, out string buildFragInputsGeneration)
        {
            var expressionToName = context.GetData().GetAttributes().ToDictionary(o => new VFXAttributeExpression(o.attrib) as VFXExpression, o => (new VFXAttributeExpression(o.attrib)).GetCodeString(null));
            expressionToName = expressionToName.Union(taskData.uniformMapper.expressionToCode).ToDictionary(s => s.Key, s => s.Value);

            var mainParameters = taskData.gpuMapper.CollectExpression(-1).ToArray();

            var fragInputsGeneration = new VFXShaderWriter();

            foreach (string fragmentParameter in context.fragmentParameters)
            {
                var filteredNamedExpression = mainParameters.FirstOrDefault(o => fragmentParameter == o.name);
                var surfaceGetter = useFragInputs ? "fragInputs.vfx" : "fragInputs";
                fragInputsGeneration.WriteAssignement(filteredNamedExpression.exp.valueType, $"properties.{filteredNamedExpression.name}", $"{surfaceGetter}.{filteredNamedExpression.name}");
                fragInputsGeneration.WriteLine();
            }

            buildFragInputsGeneration = fragInputsGeneration.ToString();
        }

        internal static void BuildFillGraphValues(VFXTaskCompiledData taskData, VFXDataParticle.GraphValuesLayout graphValuesLayout,
            VFXUniformMapper systemUniformMapper,
            out string fillGraphValues)
        {
            var fillGraphValuesShaderWriter = new VFXShaderWriter();
            fillGraphValuesShaderWriter.GenerateFillGraphValuesStruct(taskData.uniformMapper, graphValuesLayout);
            fillGraphValues = fillGraphValuesShaderWriter.ToString();
        }

        static private StringBuilder Build(VFXContext context, VFXTask task, string templatePath, VFXCompilationMode compilationMode,
                VFXTaskCompiledData taskData, HashSet<string> dependencies, bool enableShaderDebugSymbols)
        {
            if (!context.SetupCompilation())
                return null;

            if (context is VFXShaderGraphParticleOutput shaderGraphContext &&
                shaderGraphContext.GetOrRefreshShaderGraphObject() != null &&
                shaderGraphContext.GetOrRefreshShaderGraphObject().generatesWithShaderGraph)
            {
                var result = TryBuildFromShaderGraph(shaderGraphContext, taskData);

                // If the ShaderGraph generation path was successful, use the result, otherwise fall back to the VFX generation path.
                if (result != null)
                {
                    context.EndCompilation();
                    return result;
                }
            }

            var allAdditionalDefines = context.additionalDefines.Concat(task.additionalDefines ?? Enumerable.Empty<string>());
            var stringBuilder = GetFlattenedTemplateContent(templatePath, new List<string>(), allAdditionalDefines, dependencies);

            var allCurrentAttributes = context.GetData().GetAttributes().Where(a =>
                (context.GetData().IsCurrentAttributeUsed(a.attrib, context)) ||
                (context.contextType == VFXContextType.Init && context.GetData().IsAttributeStored(a.attrib))); // In init, needs to declare all stored attributes for intialization

            var allSourceAttributes = context.GetData().GetAttributes().Where(a => (context.GetData().IsSourceAttributeUsed(a.attrib, context)));

            var globalDeclaration = new VFXShaderWriter();
            globalDeclaration.WriteBufferTypeDeclaration(taskData.graphicsBufferUsage.Values);
            globalDeclaration.WriteLine();
            var particleData = (context.GetData() as VFXDataParticle);
            var systemUniformMapper = particleData.systemUniformMapper;
            taskData.uniformMapper.OverrideNamesWithOther(systemUniformMapper);
            var needsGraphValueStruct = globalDeclaration.WriteGraphValuesStruct(taskData.uniformMapper);
            globalDeclaration.WriteLine();

            globalDeclaration.WriteBuffer(taskData.uniformMapper, taskData.graphicsBufferUsage);
            globalDeclaration.WriteLine();
            globalDeclaration.WriteTexture(taskData.uniformMapper);
            globalDeclaration.WriteAttributeStruct(allCurrentAttributes.Select(a => a.attrib), "VFXAttributes");
            globalDeclaration.WriteLine();
            globalDeclaration.WriteAttributeStruct(allSourceAttributes.Select(a => a.attrib), "VFXSourceAttributes");
            globalDeclaration.WriteLine();

            globalDeclaration.WriteEventBuffers(eventListOutName, taskData.linkedEventOut.Length);

            //< Block processor
            var blockFunction = new VFXShaderWriter();
            var blockCallFunction = new VFXShaderWriter();
            var blockDeclared = new HashSet<string>();

            var expressionToName = context.GetData().GetAttributes()
                .ToDictionary(o => new VFXAttributeExpression(o.attrib) as VFXExpression, o => (new VFXAttributeExpression(o.attrib)).GetCodeString(null));
            expressionToName = expressionToName.Union(taskData.uniformMapper.expressionToCode)
                .ToDictionary(s => s.Key, s => s.Value);

            int cpt = 0;
            foreach (var current in context.activeFlattenedChildrenWithImplicit)
            {
                BuildBlock(taskData, blockFunction, blockCallFunction, blockDeclared, expressionToName, current, ref cpt);
            }

            //< Final composition
            var globalIncludeContent = new VFXShaderWriter();

            if (enableShaderDebugSymbols)
            {
                globalIncludeContent.WriteLine("#pragma enable_d3d11_debug_symbols");
                globalIncludeContent.WriteLine();
            }

            globalIncludeContent.WriteLine("#define NB_THREADS_PER_GROUP " + nbThreadsPerGroup);
            globalIncludeContent.WriteLine("#define HAS_VFX_ATTRIBUTES 1");
            globalIncludeContent.WriteLine("#define VFX_PASSDEPTH_ACTUAL (0)");
            globalIncludeContent.WriteLine("#define VFX_PASSDEPTH_MOTION_VECTOR (1)");
            globalIncludeContent.WriteLine("#define VFX_PASSDEPTH_SELECTION (2)");
            globalIncludeContent.WriteLine("#define VFX_PASSDEPTH_PICKING (3)");
            globalIncludeContent.WriteLine("#define VFX_PASSDEPTH_SHADOW (4)");

            foreach (var attribute in allCurrentAttributes)
                globalIncludeContent.WriteLineFormat("#define VFX_USE_{0}_{1} 1", attribute.attrib.name.ToUpper(CultureInfo.InvariantCulture), "CURRENT");
            foreach (var attribute in allSourceAttributes)
                globalIncludeContent.WriteLineFormat("#define VFX_USE_{0}_{1} 1", attribute.attrib.name.ToUpper(CultureInfo.InvariantCulture), "SOURCE");

            foreach (var additionnalHeader in context.additionalDataHeaders)
                globalIncludeContent.WriteLine(additionnalHeader);

            foreach (var additionnalDefine in allAdditionalDefines)
                globalIncludeContent.WriteLineFormat("#define {0}{1}", additionnalDefine, additionnalDefine.Contains(' ') ? "" : " 1");

            // We consider that tasks are always generating a compute shader.
            bool generateComputes = task.shaderType == VFXTaskShaderType.ComputeShader;

            var renderTemplatePipePath = VFXLibrary.currentSRPBinder.templatePath;
            var renderRuntimePipePath = VFXLibrary.currentSRPBinder.runtimePath;
            if (!generateComputes && !string.IsNullOrEmpty(renderTemplatePipePath))
            {
                string renderPipePasses = renderTemplatePipePath + "/VFXPasses.template";
                globalIncludeContent.Write(GetFlattenedTemplateContent(renderPipePasses, new List<string>(), allAdditionalDefines, dependencies));
            }

            if (context.GetData() is ISpaceable)
            {
                var spaceable = context.GetData() as ISpaceable;
                globalIncludeContent.WriteLineFormat("#define {0} 1", spaceable.space == VFXSpace.World ? "VFX_WORLD_SPACE" : "VFX_LOCAL_SPACE");
            }
            globalIncludeContent.WriteLineFormat("#include \"{0}/VFXDefines.hlsl\"", renderRuntimePipePath);

            if (needsGraphValueStruct)
                globalIncludeContent.WriteLine("#define VFX_USE_GRAPH_VALUES 1");

            foreach (string s in GetInstancingAdditionalDefines(context, task, particleData))
                globalIncludeContent.WriteLine(s);

            var perPassIncludeContent = new VFXShaderWriter();
            string renderPipeCommon = context.doesIncludeCommonCompute ? "Packages/com.unity.visualeffectgraph/Shaders/Common/VFXCommonCompute.hlsl" : renderRuntimePipePath + "/VFXCommon.hlsl";
            perPassIncludeContent.WriteLine("#include \"" + renderPipeCommon + "\"");
            perPassIncludeContent.WriteLine("#include \"Packages/com.unity.visualeffectgraph/Shaders/VFXCommon.hlsl\"");
            if (!generateComputes)
            {
                perPassIncludeContent.WriteLine("#include \"Packages/com.unity.visualeffectgraph/Shaders/VFXCommonOutput.hlsl\"");
            }

            // Per-block defines
            var defines = Enumerable.Empty<string>();
            foreach (var block in context.activeFlattenedChildrenWithImplicit)
                defines = defines.Concat(block.defines);
            var uniqueDefines = new HashSet<string>(defines);
            foreach (var define in uniqueDefines)
                globalIncludeContent.WriteLineFormat("#define {0}{1}", define, define.Contains(' ') ? "" : " 1");

            // Per-block includes
            var includes = Enumerable.Empty<string>();
            foreach (var block in context.activeFlattenedChildrenWithImplicit)
                includes = includes.Concat(block.includes);
            var uniqueIncludes = new HashSet<string>(includes);
            foreach (var includePath in uniqueIncludes)
                perPassIncludeContent.WriteLine(string.Format("#include \"{0}\"", includePath));


            ReplaceMultiline(stringBuilder, "${VFXGlobalInclude}", globalIncludeContent.builder);
            ReplaceMultiline(stringBuilder, "${VFXGlobalDeclaration}", globalDeclaration.builder);
            ReplaceMultiline(stringBuilder, "${VFXPerPassInclude}", perPassIncludeContent.builder);
            ReplaceMultiline(stringBuilder, "${VFXGeneratedBlockFunction}", blockFunction.builder);
            ReplaceMultiline(stringBuilder, "${VFXProcessBlocks}", blockCallFunction.builder);

            VFXShaderWriter fillGraphValueStruct = new VFXShaderWriter();
            fillGraphValueStruct.GenerateFillGraphValuesStruct(taskData.uniformMapper, particleData.graphValuesLayout);
            ReplaceMultiline(stringBuilder, "${VFXLoadGraphValues}", fillGraphValueStruct.builder);

            var mainParameters = taskData.gpuMapper.CollectExpression(-1).ToArray();
            foreach (var match in GetUniqueMatches("\\${VFXLoadParameter:{(.*?)}}", stringBuilder.ToString()))
            {
                var str = match.Groups[0].Value;
                var pattern = match.Groups[1].Value;
                var loadParameters = GenerateLoadParameter(pattern, mainParameters, expressionToName);
                ReplaceMultiline(stringBuilder, str, loadParameters.builder);
            }
            var additionalInterpolantsGeneration = new VFXShaderWriter();
            var additionalInterpolantsDeclaration = new VFXShaderWriter();
            var additionalInterpolantsPreparation = new VFXShaderWriter();


            int normSemantic = 0;

            foreach (string fragmentParameter in context.fragmentParameters)
            {
                var filteredNamedExpression = mainParameters.FirstOrDefault(o => fragmentParameter == o.name &&
                    !(expressionToName.ContainsKey(o.exp) && expressionToName[o.exp] == o.name)); // if parameter already in the global scope, there's nothing to do

                if (filteredNamedExpression.exp != null)
                {
                    if (!filteredNamedExpression.exp.Is(VFXExpression.Flags.Constant))
                    {
                        additionalInterpolantsDeclaration.WriteDeclaration(filteredNamedExpression.exp.valueType, filteredNamedExpression.name, $"NORMAL{normSemantic++}");
                        additionalInterpolantsGeneration.WriteVariable(filteredNamedExpression.exp.valueType, filteredNamedExpression.name + "__", "0");
                        var expressionToNameLocal = new Dictionary<VFXExpression, string>(expressionToName);
                        additionalInterpolantsGeneration.EnterScope();
                        {
                            if (!expressionToNameLocal.ContainsKey(filteredNamedExpression.exp))
                            {
                                additionalInterpolantsGeneration.WriteVariable(filteredNamedExpression.exp, expressionToNameLocal);
                                additionalInterpolantsGeneration.WriteLine();
                            }
                            additionalInterpolantsGeneration.WriteAssignement(filteredNamedExpression.exp.valueType, filteredNamedExpression.name + "__", expressionToNameLocal[filteredNamedExpression.exp]);
                            additionalInterpolantsGeneration.WriteLine();
                        }
                        additionalInterpolantsGeneration.ExitScope();
                        additionalInterpolantsGeneration.WriteAssignement(filteredNamedExpression.exp.valueType, "o." + filteredNamedExpression.name, filteredNamedExpression.name + "__");
                        additionalInterpolantsPreparation.WriteVariable(filteredNamedExpression.exp.valueType, filteredNamedExpression.name, "i." + filteredNamedExpression.name);
                    }
                    else
                        additionalInterpolantsPreparation.WriteVariable(filteredNamedExpression.exp.valueType, filteredNamedExpression.name, filteredNamedExpression.exp.GetCodeString(null));
                }
            }
            ReplaceMultiline(stringBuilder, "${VFXAdditionalInterpolantsGeneration}", additionalInterpolantsGeneration.builder);
            ReplaceMultiline(stringBuilder, "${VFXAdditionalInterpolantsDeclaration}", additionalInterpolantsDeclaration.builder);
            ReplaceMultiline(stringBuilder, "${VFXAdditionalInterpolantsPreparation}", additionalInterpolantsPreparation.builder);

            //< Load Attribute
            if (stringBuilder.ToString().Contains("${VFXLoadAttributes}"))
            {
                var loadAttributes = GenerateLoadAttribute(".*", context, taskData);
                ReplaceMultiline(stringBuilder, "${VFXLoadAttributes}", loadAttributes.builder);
            }

            foreach (var match in GetUniqueMatches("\\${VFXLoadAttributes:{(.*?)}}", stringBuilder.ToString()))
            {
                var str = match.Groups[0].Value;
                var pattern = match.Groups[1].Value;
                var loadAttributes = GenerateLoadAttribute(pattern, context, taskData);
                ReplaceMultiline(stringBuilder, str, loadAttributes.builder);
            }

            //< Store Attribute
            if (stringBuilder.ToString().Contains("${VFXStoreAttributes}"))
            {
                var storeAttribute = GenerateStoreAttribute(".*", context, (uint)taskData.linkedEventOut.Length);
                ReplaceMultiline(stringBuilder, "${VFXStoreAttributes}", storeAttribute.builder);
            }

            foreach (var match in GetUniqueMatches("\\${VFXStoreAttributes:{(.*?)}}", stringBuilder.ToString()))
            {
                var str = match.Groups[0].Value;
                var pattern = match.Groups[1].Value;
                var storeAttributes = GenerateStoreAttribute(pattern, context, (uint)taskData.linkedEventOut.Length);
                ReplaceMultiline(stringBuilder, str, storeAttributes.builder);
            }

            //< Detect needed pragma require
            var useCubeArray = taskData.uniformMapper.textures.Any(o => o.valueType == VFXValueType.TextureCubeArray);
            var pragmaRequire = useCubeArray ? new StringBuilder("#pragma require cubearray") : new StringBuilder();
            ReplaceMultiline(stringBuilder, "${VFXPragmaRequire}", pragmaRequire);
            if (VFXLibrary.currentSRPBinder != null)
            {
                var allowedRenderers = new StringBuilder("#pragma only_renderers ");
                allowedRenderers.Append(String.Join(" ", VFXLibrary.currentSRPBinder.GetSupportedGraphicDevices().Select(d => DeviceTypeToShaderString(d))));
                ReplaceMultiline(stringBuilder, "${VFXPragmaOnlyRenderers}", allowedRenderers);
            }

            foreach (var addionalReplacement in context.additionalReplacements)
            {
                ReplaceMultiline(stringBuilder, addionalReplacement.Key, addionalReplacement.Value.builder);
            }

            // Replace defines
            SubstituteMacros(stringBuilder);

            if (VFXViewPreference.advancedLogs)
                Debug.LogFormat("GENERATED_OUTPUT_FILE_FOR : {0}\n{1}", context.ToString(), stringBuilder.ToString());

            context.EndCompilation();
            return stringBuilder;
        }

        static string DeviceTypeToShaderString(GraphicsDeviceType deviceType) => deviceType switch
        {
            GraphicsDeviceType.Direct3D11 => "d3d11",
            GraphicsDeviceType.OpenGLCore => "glcore",
            GraphicsDeviceType.OpenGLES3 => "gles3",
            GraphicsDeviceType.Metal => "metal",
            GraphicsDeviceType.Vulkan => "vulkan",
            GraphicsDeviceType.XboxOne => "xboxone",
            GraphicsDeviceType.GameCoreXboxOne => "xboxone",
            GraphicsDeviceType.GameCoreXboxSeries => "xboxseries",
            GraphicsDeviceType.PlayStation4 => "playstation",
            GraphicsDeviceType.Switch => "switch",
            GraphicsDeviceType.PlayStation5 => "ps5",
            _ => throw new Exception($"Graphics Device Type '{deviceType}' not supported in shader string."),
        };

        private static StringBuilder TryBuildFromShaderGraph(VFXShaderGraphParticleOutput context, VFXTaskCompiledData taskData)
        {
            var stringBuilder = new StringBuilder();

            // Reconstruct the ShaderGraph.
            var path = AssetDatabase.GetAssetPath(context.GetOrRefreshShaderGraphObject());

            List<PropertyCollector.TextureInfo> configuredTextures;
            AssetCollection assetCollection = new AssetCollection();
            MinimalGraphData.GatherMinimalDependenciesFromFile(path, assetCollection);

            var textGraph = File.ReadAllText(path, Encoding.UTF8);
            var graph = new GraphData
            {
                messageManager = new MessageManager(),
                assetGuid = AssetDatabase.AssetPathToGUID(path)
            };
            MultiJson.Deserialize(graph, textGraph);
            graph.OnEnable();
            graph.ValidateGraph();

            // Check the validity of the shader graph (unsupported keywords or shader property usage).
            if (VFXLibrary.currentSRPBinder == null || !VFXLibrary.currentSRPBinder.IsGraphDataValid(graph))
                return null;

            var target = graph.activeTargets.Where(o =>
            {
                if (o.SupportsVFX())
                {
                    //We are assuming the target has been implemented in the same package than srp binder.
                    var srpBinderAssembly = VFXLibrary.currentSRPBinder.GetType().Assembly;
                    var targetAssembly = o.GetType().Assembly;
                    if (srpBinderAssembly == targetAssembly)
                        return true;
                }
                return false;
            }).FirstOrDefault();

            if (target == null || !target.TryConfigureContextData(context, taskData))
                return null;

            // Use ShaderGraph to generate the VFX shader.
            var text = ShaderGraphImporter.GetShaderText(path, out configuredTextures, assetCollection, graph, GenerationMode.VFX, new[] { target });

            // Append the shader + strip the name header (VFX stamps one in later on).
            stringBuilder.Append(text);
            stringBuilder.Remove(0, text.IndexOf("{", StringComparison.Ordinal));

            return stringBuilder;
        }

        private static void BuildBlock(VFXTaskCompiledData taskData, VFXShaderWriter blockFunction, VFXShaderWriter blockCallFunction, HashSet<string> blockDeclared, Dictionary<VFXExpression, string> expressionToName, VFXBlock block, ref int blockIndex)
        {
            // Check enabled state
            VFXExpression enabledExp = taskData.gpuMapper.FromNameAndId(VFXBlock.activationSlotName, blockIndex);
            bool needsEnabledCheck = enabledExp != null && !enabledExp.Is(VFXExpression.Flags.Constant);
            if (enabledExp != null && !needsEnabledCheck && !enabledExp.Get<bool>())
                throw new ArgumentException("This method should not be called on a disabled block");

            var parameters = block.mergedAttributes.Select(o =>
            {
                return new VFXShaderWriter.FunctionParameter
                {
                    name = o.attrib.name,
                    expression = new VFXAttributeExpression(o.attrib) as VFXExpression,
                    mode = o.mode
                };
            }).ToList();

            foreach (var parameter in block.parameters)
            {
                var expReduced = taskData.gpuMapper.FromNameAndId(parameter.name, blockIndex);
                if (VFXExpression.IsTypeValidOnGPU(expReduced.valueType))
                {
                    parameters.Add(new VFXShaderWriter.FunctionParameter
                    {
                        name = parameter.name,
                        expression = expReduced,
                        mode = VFXAttributeMode.None
                    });
                }
            }

            string methodName, commentMethod;
            GetFunctionName(block, out methodName, out commentMethod);
            if (!blockDeclared.Contains(methodName))
            {
                blockDeclared.Add(methodName);
                blockFunction.WriteBlockFunction(taskData.gpuMapper,
                    methodName,
                    block.source,
                    parameters,
                    commentMethod);
            }

            var expressionToNameLocal = expressionToName;
            bool needsEnabledScope = needsEnabledCheck && !expressionToNameLocal.ContainsKey(enabledExp);
            bool hasParameterTransformation = parameters.Any(o => !expressionToNameLocal.ContainsKey(o.expression));
            bool needsParametersScope = needsEnabledCheck || hasParameterTransformation;

            if (needsEnabledScope || hasParameterTransformation)
            {
                expressionToNameLocal = new Dictionary<VFXExpression, string>(expressionToNameLocal);
            }

            if (needsEnabledScope)
            {
                blockCallFunction.EnterScope();
                blockCallFunction.WriteVariable(enabledExp, expressionToNameLocal);
            }

            if (needsEnabledCheck)
            {
                blockCallFunction.WriteLineFormat("if ({0})", expressionToNameLocal[enabledExp]);
            }

            if (needsParametersScope)
            {
                blockCallFunction.EnterScope();
                foreach (var exp in parameters.Select(o => o.expression))
                {
                    if (expressionToNameLocal.ContainsKey(exp))
                        continue;
                    blockCallFunction.WriteVariable(exp, expressionToNameLocal);
                }
            }

            var indexEventCount = parameters.FindIndex(o => o.name == VFXAttribute.EventCount.name);
            if (indexEventCount != -1)
            {
                if ((parameters[indexEventCount].mode & VFXAttributeMode.Read) != 0)
                    throw new InvalidOperationException(string.Format("{0} isn't expected as read (special case)", VFXAttribute.EventCount.name));
                blockCallFunction.WriteLineFormat("{0} = 0u;", VFXAttribute.EventCount.GetNameInCode(VFXAttributeLocation.Current));
            }

            blockCallFunction.WriteCallFunction(methodName,
                parameters,
                taskData.gpuMapper,
                expressionToNameLocal);

            if (indexEventCount != -1)
            {
                foreach (var outputSlot in block.outputSlots.SelectMany(o => o.LinkedSlots))
                {
                    var eventIndex = Array.FindIndex(taskData.linkedEventOut, o => o.slot == outputSlot);
                    if (eventIndex != -1)
                        blockCallFunction.WriteLineFormat("{0}_{1} += {2};", VFXAttribute.EventCount.name, VFXCodeGeneratorHelper.GeneratePrefix((uint)eventIndex), VFXAttribute.EventCount.GetNameInCode(VFXAttributeLocation.Current));
                }
            }

            if (needsParametersScope)
                blockCallFunction.ExitScope();

            if (needsEnabledScope)
                blockCallFunction.ExitScope();

            blockIndex++;
        }

        internal static IEnumerable<string> GetInstancingAdditionalDefines(VFXContext context, VFXTask task, VFXDataParticle particleData)
        {
            yield return "#define VFX_USE_INSTANCING 1";

            bool isOutputTask = task != null && (task.type & VFXTaskType.Output) != 0;
            if (context is VFXAbstractParticleOutput output && isOutputTask)
            {
                uint fixedSize;
                if (!output.IsInstancingFixedSize(out fixedSize))
                {
                    fixedSize = particleData.alignedCapacity;
                }
                yield return "#define VFX_INSTANCING_FIXED_SIZE " + fixedSize;
                yield return "#pragma multi_compile_instancing";
            }
            else
            {
                if (context is VFXBasicInitialize)
                {
                    yield return "#define VFX_INSTANCING_VARIABLE_SIZE 1";
                }
                else
                {
                    yield return "#define VFX_INSTANCING_FIXED_SIZE " + Math.Max(particleData.alignedCapacity, nbThreadsPerGroup);
                }
            }

            bool hasActiveIndirection = context.contextType == VFXContextType.Filter || context.contextType == VFXContextType.Output;
            if (hasActiveIndirection)
                yield return "#define VFX_INSTANCING_ACTIVE_INDIRECTION 1";

            bool hasBatchIndirection = true;
            if (hasBatchIndirection)
                yield return "#define VFX_INSTANCING_BATCH_INDIRECTION 1";
        }
    }
}
