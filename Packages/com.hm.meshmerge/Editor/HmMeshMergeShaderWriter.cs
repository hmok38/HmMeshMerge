using System.Collections.Generic;
using System.Text;
using HmMeshMerge;
using UnityEngine;

namespace HmMeshMergeEditor
{
    /// <summary>
    /// 生成专用着色器：不依赖源着色器的结构，只做无光照展示——采样贴图数组、取参数表第一行。
    /// 各来源的参数写在查找纹理里（横轴为来源索引），顶点只携带来源索引；
    /// 生成文本的注释里列出每个下标对应的源与源材质参数，便于对照。
    /// </summary>
    internal static class HmMeshMergeShaderWriter
    {
        /// <summary>生成着色器文本；参数表决定各参数在查找纹理里的行号，贴图集合决定声明哪些贴图。</summary>
        public static string Write(List<HmMeshMergeSource> sources, IReadOnlyList<HmMeshMergeParameterEntry> table,
            List<HmMeshMergeTextureSet> textureSets, HmMeshMergeChannel indexChannel, string shaderName)
        {
            string indexType = indexChannel == HmMeshMergeChannel.VertexColor ? "float4" : "float2";
            string indexSemantic = indexChannel == HmMeshMergeChannel.VertexColor
                ? "COLOR"
                : "TEXCOORD" + (int)indexChannel;
            string decode = indexChannel == HmMeshMergeChannel.VertexColor
                ? "HmMeshMergeDecodeColorIndex(input.sourceIndex.r)"
                : "HmMeshMergeDecodeUvIndex(input.sourceIndex.x)";

            var text = new StringBuilder();
            WriteHeader(text, sources, table, textureSets);
            text.AppendLine("Shader \"" + shaderName + "\"");
            text.AppendLine("{");
            text.AppendLine("    Properties");
            text.AppendLine("    {");
            text.AppendLine("        _MeshMergeIndex(\"Mesh Merge Index\", Float) = 0");
            text.AppendLine($"        {HmMeshMergeParameterWriter.TEXTURE_NAME}(\"Params\", 2D) = \"white\" {{}}" +
                "   // 必须在 Properties 里声明，材质才能绑定");
            text.AppendLine("        // 贴图属性按数组声明，材质才能在代码里绑定纹理数组。");
            foreach (HmMeshMergeTextureSet set in textureSets)
            {
                text.AppendLine($"        {set.propertyName}(\"{set.propertyName}\", 2DArray) = \"\" {{}}");
            }
            text.AppendLine("    }");
            text.AppendLine();
            text.AppendLine("    SubShader");
            text.AppendLine("    {");
            text.AppendLine("        Tags");
            text.AppendLine("        {");
            text.AppendLine("            \"RenderPipeline\" = \"UniversalPipeline\"");
            text.AppendLine("            \"RenderType\" = \"TransparentCutout\"");
            text.AppendLine("            \"Queue\" = \"AlphaTest\"");
            text.AppendLine("        }");
            text.AppendLine();
            text.AppendLine("        Cull Off");
            WriteSharedHlsl(text, textureSets, indexType, indexSemantic, decode, FirstActiveRow(table, textureSets));
            WriteForwardPass(text);
            WriteShadowCasterPass(text);
            WriteDepthOnlyPass(text);
            text.AppendLine("    }");
            text.AppendLine();
            text.AppendLine("    FallBack Off");
            text.AppendLine("}");
            return text.ToString();
        }

        private static void WriteHeader(StringBuilder text, List<HmMeshMergeSource> sources,
            IReadOnlyList<HmMeshMergeParameterEntry> table, List<HmMeshMergeTextureSet> textureSets)
        {
            text.AppendLine("// 由 HmMeshMerge 生成，请勿手改：下次合并会覆盖。");
            text.AppendLine("//");
            text.AppendLine($"// 参数纹理 {HmMeshMergeParameterWriter.TEXTURE_NAME}：横轴为来源索引，纵轴为下列行。");
            text.AppendLine("// 行号一经分配即保持稳定；移除的参数保留为空行，不会让后面的行号前移。");
            text.AppendLine("//");
            text.AppendLine("// 来源索引（顶点索引通道携带，即下表未列出的 sourceIndex）：");
            for (int i = 0; i < sources.Count; i++)
            {
                text.AppendLine($"//   [{i}] 源网格 {sources[i].mesh.name}，材质 {sources[i].material.name}");
            }

            text.AppendLine("//");
            text.AppendLine("// 参数行（行号一经分配即保持稳定，可直接写进自有着色器）：");
            foreach (HmMeshMergeParameterEntry entry in table)
            {
                text.AppendLine(FormatParameterRow(entry, textureSets));
            }

            text.AppendLine("//");
            text.AppendLine("// 接到自有着色器时，按下三步照抄（本文件只做无光照展示，不含任何属性的特定用法）：");
            text.AppendLine("//");
            text.AppendLine("// 1) Properties 块里声明（贴图属性必须按 2DArray 声明，材质才能绑定纹理数组）：");
            text.AppendLine($"//      {HmMeshMergeParameterWriter.TEXTURE_NAME}(\"Params\", 2D) = \"white\" {{}}");
            foreach (HmMeshMergeTextureSet set in textureSets)
            {
                text.AppendLine($"//      {set.propertyName}(\"{set.propertyName}\", 2DArray) = \"\" {{}}");
            }

            text.AppendLine("//");
            text.AppendLine("// 2) HLSL 里声明：");
            text.AppendLine($"//      TEXTURE2D({HmMeshMergeParameterWriter.TEXTURE_NAME});");
            text.AppendLine("//      HmMeshMergeLoadParam 由 HmMeshMerge.hlsl 提供，不用另写；这一行放在取值之前即可。");
            foreach (HmMeshMergeTextureSet set in textureSets)
            {
                text.AppendLine($"//      TEXTURE2D_ARRAY({set.propertyName}); SAMPLER(sampler{set.propertyName});");
            }

            text.AppendLine("//");
            text.AppendLine("// 3) 取值：把顶点里解出的来源索引原样传到片元，再按上面的行号取参数；");
            text.AppendLine("//    纹理数组的层号即来源索引，因此 UV 不需要任何改动。");
            text.AppendLine("//      uint idx = (uint)round(sourceIndex);");
            if (textureSets.Count > 0)
            {
                string first = textureSets[0].propertyName;
                text.AppendLine($"//      half4 albedo = SAMPLE_TEXTURE2D_ARRAY({first}, sampler{first}, uv, idx);");
            }

            text.AppendLine($"//      float4 param = HmMeshMergeLoadParam({HmMeshMergeParameterWriter.TEXTURE_NAME}, sourceIndex, 行号);");
            text.AppendLine("//      col.rgb *= param.rgb;");
            text.AppendLine();
        }

        /// <summary>列出一行参数的取法：数值参数给出可直接复制的取值语句，贴图参数指向纹理数组。</summary>
        private static string FormatParameterRow(HmMeshMergeParameterEntry entry, List<HmMeshMergeTextureSet> textureSets)
        {
            if (!entry.active)
            {
                return $"//   第 {entry.row} 行：{entry.propertyName}（空行，保留）";
            }

            if (IsTextureSet(entry.propertyName, textureSets))
            {
                return $"//   第 {entry.row} 行：{entry.propertyName}（贴图属性，由纹理数组承载，不进参数纹理）";
            }

            return $"//   第 {entry.row} 行：{entry.propertyName}" +
                $"  →  float4 {entry.propertyName} = " +
                $"HmMeshMergeLoadParam({HmMeshMergeParameterWriter.TEXTURE_NAME}, sourceIndex, {entry.row});";
        }

        private static bool IsTextureSet(string propertyName, List<HmMeshMergeTextureSet> textureSets)
        {
            foreach (HmMeshMergeTextureSet set in textureSets)
            {
                if (set.propertyName == propertyName)
                {
                    return true;
                }
            }

            return false;
        }

        private static void WriteSharedHlsl(StringBuilder text, List<HmMeshMergeTextureSet> textureSets,
            string indexType, string indexSemantic, string decode, int firstRow)
        {
            text.AppendLine();
            text.AppendLine("        HLSLINCLUDE");
            text.AppendLine("        #include \"Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl\"");
            text.AppendLine("        // Shadows.hlsl 依赖 CommonMaterial.hlsl 里的 LerpWhiteTo；官方的 Input.hlsl 会带入它。");
            text.AppendLine("        #include \"Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl\"");
            text.AppendLine("        #include \"Packages/com.hm.meshmerge/Runtime/HmMeshMerge.hlsl\"");
            text.AppendLine();
            text.AppendLine("        // 各贴图属性是纹理数组：层号即来源索引，UV 保持原样。");
            foreach (HmMeshMergeTextureSet set in textureSets)
            {
                text.AppendLine($"        TEXTURE2D_ARRAY({set.propertyName});");
                text.AppendLine($"        SAMPLER(sampler{set.propertyName});");
            }

            text.AppendLine();
            text.AppendLine("        // 各来源参数：横轴为来源索引，纵轴为参数表的行；取值用 HmMeshMergeLoadParam。");
            text.AppendLine($"        TEXTURE2D({HmMeshMergeParameterWriter.TEXTURE_NAME});");
            text.AppendLine();
            text.AppendLine("        struct Attributes");
            text.AppendLine("        {");
            text.AppendLine("            float4 positionOS : POSITION;");
            text.AppendLine("            float3 normalOS : NORMAL;");
            text.AppendLine("            float2 uv : TEXCOORD0;");
            text.AppendLine($"            {indexType} sourceIndex : {indexSemantic};");
            text.AppendLine("            UNITY_VERTEX_INPUT_INSTANCE_ID");
            text.AppendLine("        };");
            text.AppendLine();
            text.AppendLine("        struct Varyings");
            text.AppendLine("        {");
            text.AppendLine("            float4 positionCS : SV_POSITION;");
            text.AppendLine("            float3 positionWS : TEXCOORD0;");
            text.AppendLine("            float3 normalWS : TEXCOORD1;");
            text.AppendLine("            float2 uv : TEXCOORD2;");
            text.AppendLine("            float sourceIndex : TEXCOORD3;");
            text.AppendLine("            UNITY_VERTEX_INPUT_INSTANCE_ID");
            text.AppendLine("        };");
            text.AppendLine();
            text.AppendLine("        // 来源索引与本次绘制不一致时返回零值顶点（w = 0），整个三角形被裁剪。");
            text.AppendLine("        bool PrepareVertex(Attributes input, out Varyings output)");
            text.AppendLine("        {");
            text.AppendLine("            output = (Varyings)0;");
            text.AppendLine("            UNITY_SETUP_INSTANCE_ID(input);");
            text.AppendLine("            UNITY_TRANSFER_INSTANCE_ID(input, output);");
            text.AppendLine("            output.positionCS = float4(0.0, 0.0, 0.0, 0.0);");
            text.AppendLine();
            text.AppendLine($"            output.sourceIndex = {decode};");
            text.AppendLine("            if (!HmMeshMergeIsSourceVisible(output.sourceIndex, HmMeshMergeGetActiveIndex()))");
            text.AppendLine("            {");
            text.AppendLine("                return false;");
            text.AppendLine("            }");
            text.AppendLine();
            text.AppendLine("            output.positionWS = TransformObjectToWorld(input.positionOS.xyz);");
            text.AppendLine("            output.normalWS = TransformObjectToWorldNormal(input.normalOS);");
            text.AppendLine("            output.uv = input.uv;");
            text.AppendLine("            return true;");
            text.AppendLine("        }");
            text.AppendLine();
            text.AppendLine("        // 展示用：取参数表第一个启用行乘到颜色上，确认参数纹理接对了。");
            text.AppendLine("        half4 SampleSource(Varyings input)");
            text.AppendLine("        {");
            if (textureSets.Count > 0)
            {
                string first = textureSets[0].propertyName;
                text.AppendLine("            uint index = (uint)round(input.sourceIndex);");
                text.AppendLine($"            half4 sample = SAMPLE_TEXTURE2D_ARRAY({first}, sampler{first}, input.uv, index);");
            }
            else
            {
                text.AppendLine("            half4 sample = half4(1.0, 1.0, 1.0, 1.0);");
            }
            if (firstRow >= 0)
            {
                text.AppendLine($"            sample.rgb *= HmMeshMergeLoadParam({HmMeshMergeParameterWriter.TEXTURE_NAME}," +
                    $" input.sourceIndex, {firstRow}).rgb;");
            }

            text.AppendLine("            return sample;");
            text.AppendLine("        }");
            text.AppendLine("        ENDHLSL");
        }

        private static void WriteForwardPass(StringBuilder text)
        {
            text.AppendLine();
            text.AppendLine("        Pass");
            text.AppendLine("        {");
            text.AppendLine("            Name \"ForwardLit\"");
            text.AppendLine("            Tags { \"LightMode\" = \"UniversalForward\" }");
            text.AppendLine();
            text.AppendLine("            HLSLPROGRAM");
            text.AppendLine("            #pragma target 3.5");
            text.AppendLine("            #pragma vertex Vert");
            text.AppendLine("            #pragma fragment Frag");
            text.AppendLine("            #pragma multi_compile_instancing");
            text.AppendLine();
            text.AppendLine("            Varyings Vert(Attributes input)");
            text.AppendLine("            {");
            text.AppendLine("                Varyings output;");
            text.AppendLine("                if (PrepareVertex(input, output))");
            text.AppendLine("                {");
            text.AppendLine("                    output.positionCS = TransformWorldToHClip(output.positionWS);");
            text.AppendLine("                }");
            text.AppendLine();
            text.AppendLine("                return output;");
            text.AppendLine("            }");
            text.AppendLine();
            text.AppendLine("            half4 Frag(Varyings input) : SV_Target");
            text.AppendLine("            {");
            text.AppendLine("                return half4(SampleSource(input).rgb, 1.0h);");
            text.AppendLine("            }");
            text.AppendLine("            ENDHLSL");
            text.AppendLine("        }");
        }

        private static void WriteShadowCasterPass(StringBuilder text)
        {
            text.AppendLine();
            text.AppendLine("        Pass");
            text.AppendLine("        {");
            text.AppendLine("            Name \"ShadowCaster\"");
            text.AppendLine("            Tags { \"LightMode\" = \"ShadowCaster\" }");
            text.AppendLine();
            text.AppendLine("            ZWrite On");
            text.AppendLine("            ZTest LEqual");
            text.AppendLine("            ColorMask 0");
            text.AppendLine();
            text.AppendLine("            HLSLPROGRAM");
            text.AppendLine("            #pragma target 3.5");
            text.AppendLine("            #pragma vertex ShadowVert");
            text.AppendLine("            #pragma fragment ShadowFrag");
            text.AppendLine("            #pragma multi_compile_instancing");
            text.AppendLine("            #include \"Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl\"");
            text.AppendLine();
            text.AppendLine("            float3 _LightDirection;");
            text.AppendLine();
            text.AppendLine("            float4 ShadowVert(Attributes input) : SV_POSITION");
            text.AppendLine("            {");
            text.AppendLine("                Varyings output;");
            text.AppendLine("                if (!PrepareVertex(input, output))");
            text.AppendLine("                {");
            text.AppendLine("                    return output.positionCS;");
            text.AppendLine("                }");
            text.AppendLine();
            text.AppendLine("                float3 direction = normalize(_LightDirection);");
            text.AppendLine("                return TransformWorldToHClip(ApplyShadowBias(output.positionWS, output.normalWS, direction));");
            text.AppendLine("            }");
            text.AppendLine();
            text.AppendLine("            half4 ShadowFrag() : SV_Target");
            text.AppendLine("            {");
            text.AppendLine("                return 0;");
            text.AppendLine("            }");
            text.AppendLine("            ENDHLSL");
            text.AppendLine("        }");
        }

        private static void WriteDepthOnlyPass(StringBuilder text)
        {
            text.AppendLine();
            text.AppendLine("        Pass");
            text.AppendLine("        {");
            text.AppendLine("            Name \"DepthOnly\"");
            text.AppendLine("            Tags { \"LightMode\" = \"DepthOnly\" }");
            text.AppendLine();
            text.AppendLine("            ZWrite On");
            text.AppendLine("            ColorMask R");
            text.AppendLine();
            text.AppendLine("            HLSLPROGRAM");
            text.AppendLine("            #pragma target 3.5");
            text.AppendLine("            #pragma vertex DepthVert");
            text.AppendLine("            #pragma fragment DepthFrag");
            text.AppendLine("            #pragma multi_compile_instancing");
            text.AppendLine();
            text.AppendLine("            float4 DepthVert(Attributes input) : SV_POSITION");
            text.AppendLine("            {");
            text.AppendLine("                Varyings output;");
            text.AppendLine("                if (!PrepareVertex(input, output))");
            text.AppendLine("                {");
            text.AppendLine("                    return output.positionCS;");
            text.AppendLine("                }");
            text.AppendLine();
            text.AppendLine("                return TransformWorldToHClip(output.positionWS);");
            text.AppendLine("            }");
            text.AppendLine();
            text.AppendLine("            half4 DepthFrag() : SV_Target");
            text.AppendLine("            {");
            text.AppendLine("                return 0;");
            text.AppendLine("            }");
            text.AppendLine("            ENDHLSL");
            text.AppendLine("        }");
        }

        /// <summary>参数表里第一个启用且由参数纹理承载的行的行号；没有则返回 -1。</summary>
        private static int FirstActiveRow(IReadOnlyList<HmMeshMergeParameterEntry> table,
            List<HmMeshMergeTextureSet> textureSets)
        {
            foreach (HmMeshMergeParameterEntry entry in table)
            {
                if (entry.active && !IsTextureSet(entry.propertyName, textureSets))
                {
                    return entry.row;
                }
            }

            return -1;
        }
    }
}
