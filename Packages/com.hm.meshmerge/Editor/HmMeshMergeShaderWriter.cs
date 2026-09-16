using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HmMeshMerge;
using UnityEditor.PackageManager;
using UnityEngine;
using UnityEngine.Rendering;

namespace HmMeshMergeEditor
{
    /// <summary>生成 URP 无光照展示与逐属性接入模板；不改写源 Shader。</summary>
    internal static class HmMeshMergeShaderWriter
    {
        public static string Write(List<HmMeshMergeSource> sources, List<Mesh> meshes, List<Material> materials,
            IReadOnlyList<HmMeshMergeParameterEntry> table, List<HmMeshMergeTextureSet> textureSets,
            HmMeshMergeChannel channel, string shaderName)
        {
            Material source = sources[0].material;
            Shader shader = source.shader;
            var text = new StringBuilder();
            WriteHeader(text, sources, meshes, materials, table);
            text.AppendLine($"Shader \"{Escape(shaderName)}\"");
            text.AppendLine("{");
            WriteProperties(text, source, textureSets);
            text.AppendLine(@"    SubShader
    {
        Tags { ""RenderPipeline"" = ""UniversalPipeline"" ""RenderType"" = ""TransparentCutout"" ""Queue"" = ""AlphaTest"" }
        Cull Off
        HLSLINCLUDE
        #include ""Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl""
        #include ""Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl""");
            text.AppendLine($"        #include \"{RuntimeIncludePath()}\"");
            text.AppendLine("        TEXTURE2D(_HmMeshMergeParams);");
            text.AppendLine("        TEXTURE2D(_HmMeshMergeSources);");
            WriteDeclarations(text, shader, textureSets);
            WriteAccessors(text, shader, table, textureSets);
            WriteVertexCode(text, channel);
            WritePreview(text, shader);
            text.AppendLine("        ENDHLSL");
            WritePasses(text);
            text.AppendLine("    }");
            text.AppendLine("    FallBack Off");
            text.AppendLine("}");
            string result = text.ToString();
            for (int i = 0; i < shader.GetPropertyCount(); i++)
            {
                if (shader.GetPropertyType(i) == ShaderPropertyType.Texture &&
                    shader.GetPropertyTextureDimension(i) == TextureDimension.CubeArray)
                {
                    result = result.Replace("#pragma target 3.5", "#pragma target 4.5\n            #pragma require cubearray");
                    break;
                }
            }

            // AppendLine 使用系统换行，多行模板保留源码换行；输出统一为 LF。
            return result.Replace("\r\n", "\n").Replace("\r", "\n");
        }

        /// <summary>取当前工程里 HmMeshMerge.hlsl 的实际路径；包名或安装位置变化时无需改模板。</summary>
        internal static string RuntimeIncludePath()
        {
            PackageInfo package = PackageInfo.FindForAssembly(typeof(HmMeshMergeShaderWriter).Assembly);
            if (package == null)
            {
                throw new InvalidOperationException("未能定位 HmMeshMerge 包，无法生成 HmMeshMerge.hlsl 的包含路径。");
            }

            return $"{package.assetPath.TrimEnd('/')}/Runtime/HmMeshMerge.hlsl";
        }

        private static void WriteHeader(StringBuilder text, List<HmMeshMergeSource> sources, List<Mesh> meshes,
            List<Material> materials, IReadOnlyList<HmMeshMergeParameterEntry> table)
        {
            text.AppendLine("// HmMeshMergeShaderWriter 生成；输入为合并配置。下次合并会覆盖，请复制到自有 Shader 后修改。");
            text.AppendLine("// 包含路径按本工程实际安装的包位置生成；复制到其他工程时按该工程的包路径修改。");
            text.AppendLine("// 复制对应 Properties、纹理声明、HmRead/HmSample 函数和索引判断到自有 Shader；索引判断依赖 _HmMeshMergeSources。");
            text.AppendLine("// 数值函数直接返回正确的标量/向量；启用行走 LUT，其他参数使用第一来源的普通材质属性。");
            text.AppendLine("// _ST 函数返回各贴图的 Tiling.xy 和 Offset.zw，网格 UV 本身未改变。");
            text.AppendLine("// 模板基于 URP；其他管线保留数据契约，替换管线相关宏、变换和 Pass。");
            text.AppendLine("// 本模板只展示主贴图、主颜色、Alpha 裁剪，不模拟任意源 Shader 的完整效果。");
            text.AppendLine("// 默认激活索引是材质/实例属性 _MeshMergeIndex，含义是来源索引；不保证 SRP Batcher 兼容。");
            text.AppendLine("// _HmMeshMergeFilterVertices 为 1 时按网格索引筛选；为 0 时保留原网格全部顶点。");
            text.AppendLine("// 来源到网格、材质的对应关系在 _HmMeshMergeSources（横轴为来源索引，纵轴一行）：");
            text.AppendLine("// R 是网格索引，G 是材质索引，两个索引在同一个纹素的通道里，不分成两行，一次 Load 同时取回；");
            text.AppendLine("// 数值 LUT 的横轴与纹理数组的层号都按材质索引读取，同一个材质也只占一列、一层。");
            for (int i = 0; i < meshes.Count; i++)
            {
                text.AppendLine($"// 网格 {i}: {Comment(meshes[i].name)}；来源 {SourcesOfMesh(sources, meshes, i)}");
            }

            for (int i = 0; i < materials.Count; i++)
            {
                text.AppendLine($"// 材质 {i}: {Comment(materials[i].name)}；" +
                    $"来源 {SourcesOfMaterial(sources, materials, i)}");
            }

            for (int i = 0; i < sources.Count; i++)
            {
                text.AppendLine($"// 来源 {i}: {Comment(sources[i].mesh.name)} / {Comment(sources[i].material.name)}；" +
                    $"网格 {meshes.IndexOf(sources[i].mesh)}，材质 {materials.IndexOf(sources[i].material)}");
            }

            foreach (HmMeshMergeParameterEntry entry in table)
            {
                text.AppendLine($"// 行 {entry.row}: {Comment(entry.propertyName)}，" +
                    (entry.active ? "启用（纹理由数组或普通纹理承载，不写数值行）" : "停用，行号保留"));
            }
        }

        /// <summary>某个网格被哪些来源使用；只用于生成的文件头注释。</summary>
        private static string SourcesOfMesh(List<HmMeshMergeSource> sources, List<Mesh> meshes, int meshIndex)
        {
            var indices = new List<string>();
            for (int i = 0; i < sources.Count; i++)
            {
                if (meshes.IndexOf(sources[i].mesh) == meshIndex)
                {
                    indices.Add(i.ToString(CultureInfo.InvariantCulture));
                }
            }

            return string.Join("、", indices);
        }

        /// <summary>某个材质被哪些来源使用；只用于生成的文件头注释。</summary>
        private static string SourcesOfMaterial(List<HmMeshMergeSource> sources, List<Material> materials,
            int materialIndex)
        {
            var indices = new List<string>();
            for (int i = 0; i < sources.Count; i++)
            {
                if (materials.IndexOf(sources[i].material) == materialIndex)
                {
                    indices.Add(i.ToString(CultureInfo.InvariantCulture));
                }
            }

            return string.Join("、", indices);
        }

        private static void WriteProperties(StringBuilder text, Material source, List<HmMeshMergeTextureSet> sets)
        {
            text.AppendLine("    Properties");
            text.AppendLine("    {");
            text.AppendLine("        _HmMeshMergeFilterVertices(\"按索引筛选顶点\", Float) = 1");
            text.AppendLine("        _MeshMergeIndex(\"来源索引\", Float) = 0");
            text.AppendLine("        _HmMeshMergeParams(\"来源参数 LUT\", 2D) = \"black\" {}");
            text.AppendLine("        _HmMeshMergeSources(\"来源映射表\", 2D) = \"black\" {}");
            Shader shader = source.shader;
            for (int i = 0; i < shader.GetPropertyCount(); i++)
            {
                string name = shader.GetPropertyName(i);
                // unity_ 属性由管线头文件声明和绑定，不能再次声明或作为普通材质参数生成。
                if (HmMeshMergeParameterWriter.IsEngineProperty(name))
                {
                    continue;
                }

                string label = Escape(shader.GetPropertyDescription(i));
                ShaderPropertyType type = shader.GetPropertyType(i);
                if (type == ShaderPropertyType.Texture)
                {
                    string dimension = IsArray(name, sets) ? "2DArray" : TextureDimensionName(shader, i);
                    string fallback = dimension == "2D" ? Escape(shader.GetPropertyTextureDefaultName(i)) : "";
                    text.AppendLine($"        {name}(\"{label}\", {dimension}) = \"{fallback}\" {{}}");
                }
                else
                {
                    Vector4 value = HmMeshMergeParameterWriter.ReadValue(source, name);
                    string propertyType = type == ShaderPropertyType.Color ? "Color" :
                        type == ShaderPropertyType.Vector ? "Vector" : type == ShaderPropertyType.Int ? "Integer" : "Float";
                    string literal = type == ShaderPropertyType.Color || type == ShaderPropertyType.Vector
                        ? $"({Number(value.x)}, {Number(value.y)}, {Number(value.z)}, {Number(value.w)})"
                        : type == ShaderPropertyType.Int
                            ? source.GetInteger(name).ToString(CultureInfo.InvariantCulture)
                            : Number(value.x);
                    text.AppendLine($"        {name}(\"{label}\", {propertyType}) = {literal}");
                }
            }

            text.AppendLine("    }");
        }

        private static void WriteDeclarations(StringBuilder text, Shader shader, List<HmMeshMergeTextureSet> sets)
        {
            for (int i = 0; i < shader.GetPropertyCount(); i++)
            {
                if (shader.GetPropertyType(i) != ShaderPropertyType.Texture)
                {
                    continue;
                }

                string name = shader.GetPropertyName(i);
                // unity_ 属性由管线头文件声明和绑定，不能再次声明或作为普通材质参数生成。
                if (HmMeshMergeParameterWriter.IsEngineProperty(name))
                {
                    continue;
                }

                string macro = TextureMacro(shader, i, IsArray(name, sets));
                text.AppendLine($"        TEXTURE{macro}({name}); SAMPLER(sampler{name});");
            }

            text.AppendLine("        CBUFFER_START(UnityPerMaterial)");
            text.AppendLine("            float _HmMeshMergeFilterVertices;");
            for (int i = 0; i < shader.GetPropertyCount(); i++)
            {
                string name = shader.GetPropertyName(i);
                // unity_ 属性由管线头文件声明和绑定，不能再次声明或作为普通材质参数生成。
                if (HmMeshMergeParameterWriter.IsEngineProperty(name))
                {
                    continue;
                }

                ShaderPropertyType type = shader.GetPropertyType(i);
                if (type != ShaderPropertyType.Texture)
                {
                    text.AppendLine($"            {ValueType(type)} {name};");
                }
                else if (HmMeshMergeParameterWriter.TryGetType(shader, name + "_ST", out _) &&
                    shader.FindPropertyIndex(name + "_ST") < 0)
                {
                    text.AppendLine($"            float4 {name}_ST;");
                }
            }

            text.AppendLine("        CBUFFER_END");
        }

        private static void WriteAccessors(StringBuilder text, Shader shader,
            IReadOnlyList<HmMeshMergeParameterEntry> table, List<HmMeshMergeTextureSet> sets)
        {
            text.AppendLine();
            text.AppendLine("        // 以下函数可直接复制。调用示例：HmRead_BaseColor(materialIndex)、");
            text.AppendLine("        // HmSample_BaseMap(uv, materialIndex)。materialIndex 是顶点着色器换出的材质索引，");
            text.AppendLine("        // 片元里不要再取一遍激活索引：实例属性与矩阵 m33 只在顶点阶段有效。");
            for (int i = 0; i < shader.GetPropertyCount(); i++)
            {
                string name = shader.GetPropertyName(i);
                // unity_ 属性由管线头文件声明和绑定，不能再次声明或作为普通材质参数生成。
                if (HmMeshMergeParameterWriter.IsEngineProperty(name))
                {
                    continue;
                }

                ShaderPropertyType type = shader.GetPropertyType(i);
                if (type != ShaderPropertyType.Texture)
                {
                    WriteValueAccessor(text, name, type, table);
                    continue;
                }

                bool array = IsArray(name, sets);
                TextureDimension dimension = shader.GetPropertyTextureDimension(i);
                bool hasTransform = dimension == TextureDimension.Tex2D &&
                    HmMeshMergeParameterWriter.TryGetType(shader, name + "_ST", out _);
                if (hasTransform && shader.FindPropertyIndex(name + "_ST") < 0)
                {
                    WriteValueAccessor(text, name + "_ST", ShaderPropertyType.Vector, table);
                }

                string uvType = dimension == TextureDimension.Tex2D ? "float2" :
                    dimension == TextureDimension.CubeArray ? "float4" : "float3";
                text.AppendLine($"        float4 HmSample{name}({uvType} uv, float materialIndex)");
                text.AppendLine("        {");
                if (hasTransform)
                {
                    text.AppendLine($"            float4 st = HmRead{name}_ST(materialIndex);");
                    text.AppendLine("            uv = uv * st.xy + st.zw;");
                }

                string coordinate = array ? "uv, (uint)round(materialIndex)" :
                    dimension == TextureDimension.Tex2DArray ? "uv.xy, uv.z" :
                    dimension == TextureDimension.CubeArray ? "uv.xyz, uv.w" : "uv";
                text.AppendLine($"            return SAMPLE_TEXTURE{TextureMacro(shader, i, array)}" +
                    $"({name}, sampler{name}, {coordinate});");
                text.AppendLine("        }");
            }
        }

        private static void WriteValueAccessor(StringBuilder text, string name, ShaderPropertyType type,
            IReadOnlyList<HmMeshMergeParameterEntry> table)
        {
            int row = ActiveRow(table, name);
            string expression = name;
            if (row >= 0)
            {
                expression = $"HmMeshMergeLoadParam(_HmMeshMergeParams, materialIndex, {row})";
                if (type == ShaderPropertyType.Float || type == ShaderPropertyType.Range || type == ShaderPropertyType.Int)
                {
                    expression += ".x";
                }

                if (type == ShaderPropertyType.Int)
                {
                    expression = "(int)round(" + expression + ")";
                }
            }

            text.AppendLine($"        // 源属性 {name}；" + (row >= 0 ? $"LUT 第 {row} 行，按材质索引取。" : "普通材质属性。"));
            text.AppendLine($"        {ValueType(type)} HmRead{name}(float materialIndex) {{ return {expression}; }}");
        }

        private static void WriteVertexCode(StringBuilder text, HmMeshMergeChannel channel)
        {
            string type = channel == HmMeshMergeChannel.VertexColor ? "float4" : "float2";
            string semantic = channel == HmMeshMergeChannel.VertexColor ? "COLOR" : "TEXCOORD" + (int)channel;
            string decode = channel == HmMeshMergeChannel.VertexColor
                ? "HmMeshMergeDecodeColorIndex(input.meshIndex.r)" : "HmMeshMergeDecodeUvIndex(input.meshIndex.x)";
            text.AppendLine(@"        struct Attributes
        {
            float4 positionOS : POSITION;
            float3 normalOS : NORMAL;
            float2 uv : TEXCOORD0;");
            text.AppendLine($"            {type} meshIndex : {semantic};");
            text.AppendLine(@"            UNITY_VERTEX_INPUT_INSTANCE_ID
        };
        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float3 positionWS : TEXCOORD0;
            float3 normalWS : TEXCOORD1;
            float2 uv : TEXCOORD2;
            nointerpolation float materialIndex : TEXCOORD3;
        };
        bool PrepareVertex(Attributes input, out Varyings output)
        {
            output = (Varyings)0;
            UNITY_SETUP_INSTANCE_ID(input);
            output.positionCS = float4(2.0, 2.0, 2.0, 1.0);");
            text.AppendLine("            // 激活索引是来源索引：顶点侧换成网格索引判断可见性，换成材质索引插值给片元取数值。");
            text.AppendLine("            float sourceIndex = HmMeshMergeGetActiveIndex();");
            text.AppendLine("            output.materialIndex = HmMeshMergeLoadSourceMaterial(_HmMeshMergeSources, " +
                "sourceIndex);");
            text.AppendLine("            float meshIndex = HmMeshMergeLoadSourceMesh(_HmMeshMergeSources, sourceIndex);");
            text.AppendLine($"            if (!HmMeshMergeIsMeshVisible({decode}, meshIndex, _HmMeshMergeFilterVertices))");
            text.AppendLine(@"            {
                return false;
            }
            output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
            output.normalWS = TransformObjectToWorldNormal(input.normalOS);
            output.uv = input.uv;
            return true;
        }");
        }

        private static void WritePreview(StringBuilder text, Shader shader)
        {
            string texture = FindMainProperty(shader, ShaderPropertyType.Texture, ShaderPropertyFlags.MainTexture,
                "_BaseMap", "_MainTex");
            string color = FindMainProperty(shader, ShaderPropertyType.Color, ShaderPropertyFlags.MainColor,
                "_BaseColor", "_Color");
            text.AppendLine("        float4 SampleSource(Varyings input)");
            text.AppendLine("        {");
            text.AppendLine("            float4 color = float4(1, 1, 1, 1);");
            if (texture != null)
            {
                text.AppendLine($"            color *= HmSample{texture}(input.uv, input.materialIndex);");
            }

            if (color != null)
            {
                text.AppendLine($"            color *= HmRead{color}(input.materialIndex);");
            }

            if (HmMeshMergeParameterWriter.TryGetType(shader, "_Cutoff", out ShaderPropertyType cutoffType) &&
                (cutoffType == ShaderPropertyType.Float || cutoffType == ShaderPropertyType.Range))
            {
                if (HmMeshMergeParameterWriter.TryGetType(shader, "_AlphaClip", out ShaderPropertyType alphaType) &&
                    (alphaType == ShaderPropertyType.Float || alphaType == ShaderPropertyType.Range ||
                    alphaType == ShaderPropertyType.Int))
                {
                    text.AppendLine("            if (HmRead_AlphaClip(input.materialIndex) > 0.5)");
                    text.AppendLine("            {");
                    text.AppendLine("                clip(color.a - HmRead_Cutoff(input.materialIndex));");
                    text.AppendLine("            }");
                }
                else
                {
                    text.AppendLine("            clip(color.a - HmRead_Cutoff(input.materialIndex));");
                }
            }

            text.AppendLine("            return color;");
            text.AppendLine("        }");
        }

        private static void WritePasses(StringBuilder text)
        {
            text.AppendLine(@"        Pass
        {
            Name ""ForwardUnlit""
            Tags { ""LightMode"" = ""UniversalForwardOnly"" }
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            Varyings Vert(Attributes input)
            {
                Varyings output;
                if (PrepareVertex(input, output))
                {
                    output.positionCS = TransformWorldToHClip(output.positionWS);
                }
                return output;
            }
            half4 Frag(Varyings input) : SV_Target
            {
                return SampleSource(input);
            }
            ENDHLSL
        }
        Pass
        {
            Name ""ShadowCaster""
            Tags { ""LightMode"" = ""ShadowCaster"" }
            ZWrite On
            ColorMask 0
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include ""Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl""
            float3 _LightDirection;
            float3 _LightPosition;
            Varyings ShadowVert(Attributes input)
            {
                Varyings output;
                if (PrepareVertex(input, output))
                {
                    #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
                        float3 direction = normalize(_LightPosition - output.positionWS);
                    #else
                        float3 direction = _LightDirection;
                    #endif
                    output.positionCS = TransformWorldToHClip(
                        ApplyShadowBias(output.positionWS, output.normalWS, direction));
                    #if UNITY_REVERSED_Z
                        output.positionCS.z = min(output.positionCS.z, UNITY_NEAR_CLIP_VALUE);
                    #else
                        output.positionCS.z = max(output.positionCS.z, UNITY_NEAR_CLIP_VALUE);
                    #endif
                }
                return output;
            }
            half4 ShadowFrag(Varyings input) : SV_Target
            {
                SampleSource(input);
                return 0;
            }
            ENDHLSL
        }
        Pass
        {
            Name ""DepthOnly""
            Tags { ""LightMode"" = ""DepthOnly"" }
            ZWrite On
            ColorMask R
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex DepthVert
            #pragma fragment DepthFrag
            #pragma multi_compile_instancing
            Varyings DepthVert(Attributes input)
            {
                Varyings output;
                if (PrepareVertex(input, output))
                {
                    output.positionCS = TransformWorldToHClip(output.positionWS);
                }
                return output;
            }
            half4 DepthFrag(Varyings input) : SV_Target
            {
                SampleSource(input);
                return input.positionCS.z;
            }
            ENDHLSL
        }");
        }

        private static string FindMainProperty(Shader shader, ShaderPropertyType type, ShaderPropertyFlags flag,
            string preferred, string fallback)
        {
            for (int i = 0; i < shader.GetPropertyCount(); i++)
            {
                if (shader.GetPropertyType(i) == type && (shader.GetPropertyFlags(i) & flag) != 0 &&
                    (type != ShaderPropertyType.Texture || shader.GetPropertyTextureDimension(i) == TextureDimension.Tex2D))
                {
                    return shader.GetPropertyName(i);
                }
            }

            foreach (string name in new[] { preferred, fallback })
            {
                int index = shader.FindPropertyIndex(name);
                if (index >= 0 && shader.GetPropertyType(index) == type &&
                    (type != ShaderPropertyType.Texture ||
                    shader.GetPropertyTextureDimension(index) == TextureDimension.Tex2D))
                {
                    return name;
                }
            }

            return null;
        }

        private static int ActiveRow(IReadOnlyList<HmMeshMergeParameterEntry> table, string name)
        {
            foreach (HmMeshMergeParameterEntry entry in table)
            {
                if (entry.active && entry.propertyName == name)
                {
                    return entry.row;
                }
            }

            return -1;
        }

        private static bool IsArray(string name, List<HmMeshMergeTextureSet> sets)
        {
            return sets.Exists(set => set.propertyName == name);
        }

        private static string ValueType(ShaderPropertyType type)
        {
            return type == ShaderPropertyType.Color || type == ShaderPropertyType.Vector ? "float4" :
                type == ShaderPropertyType.Int ? "int" : "float";
        }

        private static string TextureDimensionName(Shader shader, int index)
        {
            switch (shader.GetPropertyTextureDimension(index))
            {
                case TextureDimension.Tex2D:
                    return "2D";
                case TextureDimension.Tex2DArray:
                    return "2DArray";
                case TextureDimension.Cube:
                    return "Cube";
                case TextureDimension.CubeArray:
                    return "CubeArray";
                case TextureDimension.Tex3D:
                    return "3D";
                default:
                    throw new InvalidOperationException($"不支持纹理维度：{shader.GetPropertyName(index)}");
            }
        }

        private static string TextureMacro(Shader shader, int index, bool array)
        {
            if (array)
            {
                return "2D_ARRAY";
            }

            return TextureDimensionName(shader, index).Replace("CubeArray", "CUBE_ARRAY")
                .Replace("Cube", "CUBE").Replace("2DArray", "2D_ARRAY");
        }

        private static string Number(float value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string Escape(string value)
        {
            return Comment(value).Replace("\\", "/").Replace("\"", "'");
        }

        private static string Comment(string value)
        {
            return value.Replace("\\r", " ").Replace("\\n", " ").Replace("\r", " ").Replace("\n", " ");
        }
    }
}
