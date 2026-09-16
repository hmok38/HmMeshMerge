using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using HmMeshMerge;
using UnityEditor;
using UnityEngine;

namespace HmMeshMergeEditor
{
    /// <summary>
    /// 复制来源 Shader 的文本并注入合并接入点：四个属性、hlsl 包含、查找纹理声明、索引通道、
    /// 材质索引插值通道与每个顶点入口的可见性判断。只做插入与改名，不改写数值属性引用和贴图
    /// 取样写法，因此源 Shader 的光照、风动等自有逻辑原样保留；需要逐来源取值的位置记录在
    /// 生成文件的头部注释里，由使用者按行处理。定位不到必需元素时直接报错，不生成半成品。
    /// </summary>
    internal static class HmMeshMergeShaderPatcher
    {
        /// <summary>顶点入口改名后的内部函数前缀；原入口名保留，转成注入可见性判断的包装函数。</summary>
        private const string VERTEX_PREFIX = "HmMeshMergeSource_";

        /// <summary>注入的激活索引属性名；与 HmMeshMerge.hlsl 的读取函数保持一致。</summary>
        private const string INDEX_PROPERTY = "_MeshMergeIndex";

        /// <summary>
        /// 复制 sourceShader 并注入接入点。notes 由调用方预置逐来源差异的待办，本方法再追加
        /// 自动改写过程中发现的问题；返回注入后的完整 Shader 文本。
        /// </summary>
        public static string Patch(Shader sourceShader, HmMeshMergeChannel channel, string shaderName,
            List<string> notes)
        {
            string path = AssetDatabase.GetAssetPath(sourceShader);
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".shader", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"只能复制 .shader 文本。「{sourceShader.name}」的实际路径是「{path}」，可能是内置 Shader 或 ShaderGraph。" +
                    "请关闭「尝试修改来源shader(副本)」改用模板，或手工接入后指定为输出 Shader。");
            }

            var lines = new List<string>(File.ReadAllText(path)
                .Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'));
            var edits = new List<(int index, List<string> lines)>();
            (int open, int close, string body) input = FindStruct(lines, "POSITION");
            (int open, int close, string body) output = FindStruct(lines, "SV_POSITION");
            if (input.close < 0 || output.close < 0)
            {
                throw new InvalidOperationException(
                    $"「{sourceShader.name}」里没有找到同时带 POSITION 与 SV_POSITION 语义的结构体，无法注入索引通道与可见性判断。" +
                    "请关闭「尝试修改来源shader(副本)」，或手工接入后指定为输出 Shader。");
            }

            RewriteRelativeIncludes(lines, path, notes);
            NoteUnpatchedPasses(lines, notes);
            InjectProperties(lines, edits);
            InjectInstancing(lines, edits);
            InjectDeclarations(lines, edits, notes);
            string indexMember = AddIndexChannel(lines, edits, channel, input);
            bool materialIndex = AddMaterialIndexChannel(lines, edits, output, notes);
            string positionMember = SemanticMember(lines, output, "SV_POSITION");
            WrapVertexFunctions(lines, edits, channel, positionMember, materialIndex,
                StructName(lines, input.open), StructName(lines, output.open), notes, indexMember);
            return Compose(ApplyEdits(lines, edits), sourceShader, path, shaderName, channel, notes);
        }

        /// <summary>把相对包含改成工程内的完整路径，否则复制到配置目录后找不到文件。</summary>
        private static void RewriteRelativeIncludes(List<string> lines, string shaderPath, List<string> notes)
        {
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            string shaderFolder = Path.GetDirectoryName(shaderPath);
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i];
                int start = line.IndexOf("#include", StringComparison.Ordinal);
                int quote = start < 0 ? -1 : line.IndexOf('"', start);
                int end = quote < 0 ? -1 : line.IndexOf('"', quote + 1);
                if (end < 0)
                {
                    continue;
                }

                string target = line.Substring(quote + 1, end - quote - 1);
                if (target.StartsWith("Assets/", StringComparison.Ordinal) ||
                    target.StartsWith("Packages/", StringComparison.Ordinal))
                {
                    continue;
                }

                string full = Path.GetFullPath(Path.Combine(projectRoot,
                    shaderFolder ?? string.Empty, target));
                if (!File.Exists(full))
                {
                    notes.Add($"第 {i + 1} 行：相对包含「{target}」找不到文件，复制后可能无法编译，请改成工程内的完整路径。");
                    continue;
                }

                lines[i] = line.Substring(0, quote + 1) + full.Substring(projectRoot.Length + 1).Replace('\\', '/') +
                    line.Substring(end);
            }
        }

        /// <summary>
        /// UsePass 引用的 Pass 在其他 Shader 里，注入不到，只能记待办：没有可见性判断时，
        /// 隐藏的来源仍会绘制这些 Pass（例如继续投射阴影）。
        /// </summary>
        private static void NoteUnpatchedPasses(List<string> lines, List<string> notes)
        {
            var passes = new List<string>();
            for (int i = 0; i < lines.Count; i++)
            {
                string trimmed = lines[i].Trim();
                if (!trimmed.StartsWith("UsePass ", StringComparison.Ordinal))
                {
                    continue;
                }

                string name = LastToken(trimmed).Trim('"');
                if (name.Length > 0 && !passes.Contains(name))
                {
                    passes.Add(name);
                }
            }

            if (passes.Count > 0)
            {
                notes.Add($"{string.Join("、", passes)} 由 UsePass 引用，注入不到，没有可见性判断，" +
                    "隐藏来源仍会绘制这些 Pass；请按本文件的顶点入口自行补上，或改用自带的 Pass。");
            }
        }

        /// <summary>在 Properties 块里注入激活索引与两张查找纹理；材质必须声明它们才能绑定。</summary>
        private static void InjectProperties(List<string> lines, List<(int index, List<string> lines)> edits)
        {
            int properties = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                if (string.Equals(lines[i].Trim(), "Properties", StringComparison.Ordinal))
                {
                    properties = i;
                    break;
                }
            }

            if (properties < 0)
            {
                throw new InvalidOperationException("来源 Shader 没有 Properties 块，无法注入合并接入属性。请手工接入后指定为输出 Shader。");
            }

            int brace = properties;
            while (brace < lines.Count && lines[brace].IndexOf('{') < 0)
            {
                brace++;
            }

            if (brace >= lines.Count)
            {
                throw new InvalidOperationException("来源 Shader 的 Properties 块没有左花括号，无法注入合并接入属性。");
            }

            string indent = Indent(lines[brace]) + "    ";
            edits.Add((brace + 1, new List<string>
            {
                indent + "// HmMeshMerge 注入：激活来源索引、参数 LUT 与来源映射表。",
                indent + "_HmMeshMergeFilterVertices(\"按索引筛选顶点\", Float) = 1",
                indent + INDEX_PROPERTY + "(\"来源索引\", Float) = 0",
                indent + "_HmMeshMergeParams(\"参数 LUT\", 2D) = \"white\" {}",
                indent + "_HmMeshMergeSources(\"来源映射表\", 2D) = \"black\" {}"
            }));
        }

        /// <summary>给带顶点入口的块补上实例化变体，让材质属性或 MaterialPropertyBlock 能逐实例传索引。</summary>
        private static void InjectInstancing(List<string> lines, List<(int index, List<string> lines)> edits)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].IndexOf("#pragma multi_compile_instancing", StringComparison.Ordinal) >= 0)
                {
                    return;
                }
            }

            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].Trim().StartsWith("#pragma vertex", StringComparison.Ordinal))
                {
                    edits.Add((i + 1, new List<string> { Indent(lines[i]) + "#pragma multi_compile_instancing" }));
                }
            }
        }

        /// <summary>在每个 HLSL 块的最后一个 #include 之后注入读数脚本与两张查找纹理的声明。</summary>
        private static void InjectDeclarations(List<string> lines, List<(int index, List<string> lines)> edits,
            List<string> notes)
        {
            bool shared = false;
            for (int i = 0; i < lines.Count && !shared; i++)
            {
                shared = lines[i].Trim().StartsWith("HLSLINCLUDE", StringComparison.Ordinal);
            }

            for (int i = 0; i < lines.Count; i++)
            {
                string trimmed = lines[i].Trim();
                bool opener = trimmed.StartsWith("HLSLINCLUDE", StringComparison.Ordinal) ||
                    (!shared && (trimmed.StartsWith("HLSLPROGRAM", StringComparison.Ordinal) ||
                        trimmed.StartsWith("CGPROGRAM", StringComparison.Ordinal)));
                if (!opener)
                {
                    continue;
                }

                int end = i + 1;
                while (end < lines.Count && !lines[end].Trim().StartsWith("ENDHLSL", StringComparison.Ordinal) &&
                    !lines[end].Trim().StartsWith("ENDCG", StringComparison.Ordinal))
                {
                    end++;
                }

                int anchor = -1;
                for (int j = i + 1; j < end; j++)
                {
                    if (lines[j].TrimStart().StartsWith("#include", StringComparison.Ordinal))
                    {
                        anchor = j;
                    }
                }

                string indent;
                int index;
                if (anchor < 0)
                {
                    indent = Indent(lines[i]) + "    ";
                    index = i + 1;
                    notes.Add($"第 {i + 1} 行：该 HLSL 块没有 #include，声明已插到块首；若它看不到管线核心头文件，" +
                        "请把注入的声明移到正确的 #include 之后。");
                }
                else
                {
                    indent = Indent(lines[anchor]);
                    index = anchor + 1;
                }

                edits.Add((index, new List<string>
                {
                    indent + "// HmMeshMerge 注入：索引读取与两张查找纹理；纹理由合并工具生成的材质绑定。",
                    indent + "#include \"" + HmMeshMergeShaderWriter.RuntimeIncludePath() + "\"",
                    indent + "TEXTURE2D(_HmMeshMergeParams);",
                    indent + "TEXTURE2D(_HmMeshMergeSources);",
                    indent + "float _HmMeshMergeFilterVertices;"
                }));
            }
        }

        /// <summary>复用已有的索引语义，否则添加 meshIndex；关闭筛选时不使用该通道的值。</summary>
        private static string AddIndexChannel(List<string> lines, List<(int index, List<string> lines)> edits,
            HmMeshMergeChannel channel, (int open, int close, string body) span)
        {
            string semantic = channel == HmMeshMergeChannel.VertexColor
                ? "COLOR"
                : "TEXCOORD" + (int)channel;
            bool hasIndexSemantic = Regex.IsMatch(span.body, @":\s*" + semantic + @"\b");
            if (!hasIndexSemantic && Regex.IsMatch(span.body, @"\bmeshIndex\b"))
            {
                throw new InvalidOperationException("来源 Shader 的顶点输入已有 meshIndex 成员，无法注入索引通道。请手工接入。");
            }

            string indent = BodyIndent(lines, span);
            var inserted = new List<string>();
            if (!hasIndexSemantic)
            {
                inserted.Add(indent + (channel == HmMeshMergeChannel.VertexColor
                    ? "float4 meshIndex : COLOR;"
                    : "float2 meshIndex : " + semantic + ";"));
            }

            if (!span.body.Contains("UNITY_VERTEX_INPUT_INSTANCE_ID"))
            {
                inserted.Add(indent + "UNITY_VERTEX_INPUT_INSTANCE_ID");
            }

            edits.Add((span.close, inserted));
            return hasIndexSemantic ? SemanticMember(lines, span, semantic) : "meshIndex";
        }

        /// <summary>给插值结构体加材质索引通道；槽位用满时记待办并跳过。</summary>
        private static bool AddMaterialIndexChannel(List<string> lines, List<(int index, List<string> lines)> edits,
            (int open, int close, string body) span, List<string> notes)
        {
            if (Regex.IsMatch(span.body, @"\bmaterialIndex\b"))
            {
                notes.Add("插值结构体已有 materialIndex 成员，未注入材质索引通道，请确认它由插件赋值。");
                return false;
            }

            int last = -1;
            foreach (Match match in Regex.Matches(span.body, @"TEXCOORD(\d+)"))
            {
                last = Math.Max(last, int.Parse(match.Groups[1].Value));
            }

            int free = last + 1;
            if (free > 15)
            {
                notes.Add("插值结构体的 TEXCOORD 槽位已用满，未注入材质索引通道；逐来源数值需要自己传参。");
                return false;
            }

            edits.Add((span.close, new List<string>
            {
                BodyIndent(lines, span) + "nointerpolation float materialIndex : TEXCOORD" + free + ";"
            }));
            return true;
        }

        /// <summary>把每个 #pragma vertex 指向的入口改成包装函数，在原始顶点结果上套可见性判断。</summary>
        private static void WrapVertexFunctions(List<string> lines, List<(int index, List<string> lines)> edits,
            HmMeshMergeChannel channel, string positionMember, bool materialIndex, string inputStruct,
            string outputStruct, List<string> notes, string indexMember)
        {
            var names = new List<string>();
            for (int i = 0; i < lines.Count; i++)
            {
                string trimmed = lines[i].Trim();
                if (!trimmed.StartsWith("#pragma vertex", StringComparison.Ordinal))
                {
                    continue;
                }

                string name = LastToken(trimmed);
                if (name.Length > 0 && !names.Contains(name))
                {
                    names.Add(name);
                }
            }

            if (names.Count == 0)
            {
                notes.Add("没有找到 #pragma vertex，未注入可见性判断。");
                return;
            }

            foreach (string name in names)
            {
                var pattern = new Regex(@"^[ \t]*(?<type>[A-Za-z_][A-Za-z0-9_]*)[ \t]+(?<name>" +
                    Regex.Escape(name) + @")[ \t]*\((?<parameters>[^)]*)\)");
                int signature = -1;
                Match match = null;
                for (int i = 0; i < lines.Count; i++)
                {
                    Match candidate = pattern.Match(lines[i]);
                    if (!candidate.Success || lines[i].TrimEnd().EndsWith(";", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    signature = i;
                    match = candidate;
                    break;
                }

                if (signature < 0)
                {
                    notes.Add($"没有找到顶点入口 {name} 的定义，未注入可见性判断。");
                    continue;
                }

                int open = FindBrace(lines, signature);
                int close = open < 0 ? -1 : FindBlockEnd(lines, open);
                if (close < 0)
                {
                    notes.Add($"顶点入口 {name} 的函数体不完整，未注入可见性判断。");
                    continue;
                }

                string returnType = match.Groups["type"].Value;
                string parameters = match.Groups["parameters"].Value;
                var declared = SplitParameters(parameters);
                bool valueReturn = !string.Equals(returnType, "void", StringComparison.Ordinal);
                if (declared.Count == 0)
                {
                    notes.Add($"顶点入口 {name} 没有参数，未注入可见性判断。");
                    continue;
                }

                string outputName;
                string resultType = returnType;
                if (valueReturn)
                {
                    outputName = "hmOutput";
                }
                else
                {
                    string[] outTokens = LastTokens(declared[declared.Count - 1]);
                    if (outTokens.Length < 3 || (outTokens[0] != "out" && outTokens[0] != "inout"))
                    {
                        notes.Add($"顶点入口 {name} 既不是返回值也不是 out 参数返回，未注入可见性判断。");
                        continue;
                    }

                    outputName = outTokens[outTokens.Length - 1];
                    resultType = outTokens[outTokens.Length - 2];
                }

                RequireVertexShape(name, declared[0], resultType, inputStruct, outputStruct);
                string indent = Indent(lines[signature]);
                string internalName = VERTEX_PREFIX + name;
                var args = new List<string>();
                foreach (string parameter in declared)
                {
                    args.Add(LastToken(parameter));
                }

                int nameIndex = match.Groups["name"].Index;
                lines[signature] = lines[signature].Substring(0, nameIndex) + internalName +
                    lines[signature].Substring(nameIndex + name.Length);
                edits.Add((close + 1, BuildWrapper(indent, returnType, parameters, name, internalName,
                    string.Join(", ", args), outputName, valueReturn, channel, positionMember, materialIndex, indexMember)));
            }
        }

        /// <summary>取参数的类型名，用于核对顶点入口的参数与返回结构体。</summary>
        private static string ParameterType(string parameter)
        {
            string[] tokens = LastTokens(parameter);
            return tokens.Length < 2 ? string.Empty : tokens[tokens.Length - 2];
        }

        /// <summary>
        /// 顶点入口的输入与返回结构体必须是注入过通道的那两个；否则包装函数读不到成员，
        /// 生成的文件必定编译失败，这里直接报错而不是产出半成品。
        /// </summary>
        private static void RequireVertexShape(string name, string firstParameter, string resultType,
            string inputStruct, string outputStruct)
        {
            if (!string.Equals(ParameterType(firstParameter), inputStruct, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"顶点入口 {name} 的第一个参数不是注入索引通道的 {inputStruct}，无法注入可见性判断。" +
                    "请关闭「尝试修改来源shader(副本)」，或手工接入后指定为输出 Shader。");
            }

            if (!string.Equals(resultType, outputStruct, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"顶点入口 {name} 的返回结构体是 {resultType}，不是注入材质索引通道的 {outputStruct}，无法注入可见性判断。" +
                    "请关闭「尝试修改来源shader(副本)」，或手工接入后指定为输出 Shader。");
            }
        }

        private static List<string> BuildWrapper(string indent, string returnType, string parameters, string name,
            string internalName, string args, string outputName, bool valueReturn, HmMeshMergeChannel channel,
            string positionMember, bool materialIndex, string indexMember)
        {
            string parameter = LastToken(parameters.Split(',')[0].Trim());
            string indexValue = channel == HmMeshMergeChannel.VertexColor
                ? "HmMeshMergeDecodeColorIndex(" + parameter + "." + indexMember + ".r)"
                : "HmMeshMergeDecodeUvIndex(" + parameter + "." + indexMember + ".x)";
            var wrapper = new List<string>
            {
                string.Empty,
                indent + "// HmMeshMerge 注入：先算原始顶点，再按激活来源把不属于本网格的顶点移出裁剪空间。",
                indent + returnType + " " + name + "(" + parameters + ")",
                indent + "{",
                indent + "    UNITY_SETUP_INSTANCE_ID(" + parameter + ");"
            };
            wrapper.Add(valueReturn
                ? indent + "    " + returnType + " " + outputName + " = " + internalName + "(" + args + ");"
                : indent + "    " + internalName + "(" + args + ");");
            wrapper.Add(indent + "    float hmSourceIndex = HmMeshMergeGetActiveIndex();");
            if (materialIndex)
            {
                wrapper.Add(indent + "    " + outputName +
                    ".materialIndex = HmMeshMergeLoadSourceMaterial(_HmMeshMergeSources, hmSourceIndex);");
            }

            wrapper.Add(indent + "    float hmMeshIndex = HmMeshMergeLoadSourceMesh(_HmMeshMergeSources, hmSourceIndex);");
            wrapper.Add(indent + "    if (!HmMeshMergeIsMeshVisible(" + indexValue + ", hmMeshIndex, _HmMeshMergeFilterVertices))");
            wrapper.Add(indent + "    {");
            wrapper.Add(indent + "        " + outputName + "." + positionMember + " = float4(2.0, 2.0, 2.0, 1.0);");
            wrapper.Add(indent + "    }");
            wrapper.Add(valueReturn ? indent + "    return " + outputName + ";" : indent + "    return;");
            wrapper.Add(indent + "}");
            return wrapper;
        }

        private static string Compose(List<string> lines, Shader sourceShader, string sourcePath, string shaderName,
            HmMeshMergeChannel channel, List<string> notes)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].TrimStart().StartsWith("Shader ", StringComparison.Ordinal))
                {
                    lines[i] = "Shader \"" + shaderName + "\"";
                    break;
                }
            }

            var text = new StringBuilder();
            text.AppendLine("// HmMeshMerge 复制来源 Shader 并注入合并接入点；下次合并会覆盖本文件，请复制到自有 Shader 后再修改。");
            text.AppendLine("// 来源：" + sourcePath + "（" + sourceShader.name + "）");
            text.AppendLine("// 已注入：" + INDEX_PROPERTY + "、_HmMeshMergeParams、_HmMeshMergeSources、_HmMeshMergeFilterVertices 四个属性，索引读取脚本包含与两张查找纹理声明，");
            text.AppendLine("// 索引通道 " + SemanticOf(channel) + "、顶点输入实例化、材质索引插值通道，以及每个顶点入口的可见性判断。");
            text.AppendLine("// 未自动改写：逐来源不同的数值属性引用与贴图取样，仍按普通材质属性读取，等于使用第一来源的值。");
            if (notes.Count == 0)
            {
                text.AppendLine("// 待人工处理：无。");
            }
            else
            {
                text.AppendLine("// 待人工处理：");
                for (int i = 0; i < notes.Count; i++)
                {
                    text.AppendLine("//   " + (i + 1) + ". " + notes[i]);
                }
            }

            text.AppendLine();
            foreach (string line in lines)
            {
                text.AppendLine(line);
            }

            return text.ToString().Replace("\r\n", "\n").Replace("\r", "\n");
        }

        private static List<string> ApplyEdits(List<string> lines, List<(int index, List<string> lines)> edits)
        {
            edits.Sort((left, right) => right.index.CompareTo(left.index));
            var result = new List<string>(lines);
            foreach ((int index, List<string> inserted) in edits)
            {
                result.InsertRange(index, inserted);
            }

            return result;
        }

        private static string SemanticOf(HmMeshMergeChannel channel)
        {
            return channel == HmMeshMergeChannel.VertexColor ? "COLOR" : "TEXCOORD" + (int)channel;
        }

        private static (int open, int close, string body) FindStruct(List<string> lines, string semantic)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                if (!lines[i].Trim().StartsWith("struct ", StringComparison.Ordinal))
                {
                    continue;
                }

                int open = FindBrace(lines, i);
                int close = open < 0 ? -1 : FindBlockEnd(lines, open);
                if (close < 0)
                {
                    continue;
                }

                string body = string.Join("\n", lines.GetRange(open, close - open));
                if (Regex.IsMatch(body, @":\s*" + semantic + @"\b"))
                {
                    return (open, close, body);
                }
            }

            return (-1, -1, string.Empty);
        }

        /// <summary>取结构体声明里的名字，用于核对顶点入口的参数与返回结构体。</summary>
        private static string StructName(List<string> lines, int open)
        {
            for (int i = open; i >= 0; i--)
            {
                string trimmed = lines[i].Trim();
                if (!trimmed.StartsWith("struct ", StringComparison.Ordinal))
                {
                    continue;
                }

                string[] tokens = LastTokens(trimmed);
                return tokens.Length < 2 ? string.Empty : tokens[1].TrimEnd('{');
            }

            return string.Empty;
        }

        /// <summary>取结构体里某个语义对应的成员名，用于在包装函数里改写入裁剪空间的位置。</summary>
        private static string SemanticMember(List<string> lines, (int open, int close, string body) span,
            string semantic)
        {
            Match match = Regex.Match(span.body, @"\b(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*:\s*" + semantic + @"\b");
            if (!match.Success)
            {
                throw new InvalidOperationException($"插值结构体里没有找到 {semantic} 对应的成员名，无法注入可见性判断。");
            }

            return match.Groups["name"].Value;
        }

        private static int FindBrace(List<string> lines, int from)
        {
            for (int i = from; i < lines.Count; i++)
            {
                if (lines[i].IndexOf('{') >= 0)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>按花括号配对找出块的最后一行；不处理字符串与注释里的花括号。</summary>
        private static int FindBlockEnd(List<string> lines, int open)
        {
            int depth = 0;
            for (int i = open; i < lines.Count; i++)
            {
                foreach (char character in lines[i])
                {
                    if (character == '{')
                    {
                        depth++;
                    }
                    else if (character == '}')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            return i;
                        }
                    }
                }
            }

            return -1;
        }

        private static List<string> SplitParameters(string parameters)
        {
            var result = new List<string>();
            foreach (string part in parameters.Split(','))
            {
                string trimmed = part.Trim();
                if (trimmed.Length > 0)
                {
                    result.Add(trimmed);
                }
            }

            return result;
        }

        private static string[] LastTokens(string text)
        {
            return text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static string LastToken(string text)
        {
            string[] tokens = LastTokens(text);
            return tokens.Length == 0 ? string.Empty : tokens[tokens.Length - 1];
        }

        private static string Indent(string line)
        {
            int length = 0;
            while (length < line.Length && (line[length] == ' ' || line[length] == '\t'))
            {
                length++;
            }

            return line.Substring(0, length);
        }

        /// <summary>结构体成员的缩进：有成员时沿用第一个成员的缩进，空结构体时按声明行向内一层。</summary>
        private static string BodyIndent(List<string> lines, (int open, int close, string body) span)
        {
            for (int i = span.open + 1; i < span.close; i++)
            {
                if (lines[i].Trim().Length > 0)
                {
                    return Indent(lines[i]);
                }
            }

            return Indent(lines[span.open]) + "    ";
        }
    }
}
