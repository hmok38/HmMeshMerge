// 由 HmMeshMerge 生成，请勿手改：下次合并会覆盖。
//
// 参数纹理 _HmMeshMergeParams：横轴为来源索引，纵轴为下列行。
// 行号一经分配即保持稳定；移除的参数保留为空行，不会让后面的行号前移。
//
// 来源索引（顶点索引通道携带，即下表未列出的 sourceIndex）：
//   [0] 源网格 tree_remake_02_ec304abe27193ba5，材质 blue
//   [1] 源网格 tree_remake_02_low_49491449df84709e，材质 Red
//
// 参数行（行号一经分配即保持稳定，可直接写进自有着色器）：
//   第 0 行：_BaseMap（贴图属性，由纹理数组承载，不进参数纹理）
//   第 1 行：_BaseColor  →  float4 _BaseColor = HmMeshMergeLoadParam(_HmMeshMergeParams, sourceIndex, 1);
//   第 2 行：_MainTex  →  float4 _MainTex = HmMeshMergeLoadParam(_HmMeshMergeParams, sourceIndex, 2);
//   第 3 行：_Color  →  float4 _Color = HmMeshMergeLoadParam(_HmMeshMergeParams, sourceIndex, 3);
//
// 接到自有着色器时，按下三步照抄（本文件只做无光照展示，不含任何属性的特定用法）：
//
// 1) Properties 块里声明（贴图属性必须按 2DArray 声明，材质才能绑定纹理数组）：
//      _HmMeshMergeParams("Params", 2D) = "white" {}
//      _BaseMap("_BaseMap", 2DArray) = "" {}
//
// 2) HLSL 里声明：
//      TEXTURE2D(_HmMeshMergeParams);
//      HmMeshMergeLoadParam 由 HmMeshMerge.hlsl 提供，不用另写；这一行放在取值之前即可。
//      TEXTURE2D_ARRAY(_BaseMap); SAMPLER(sampler_BaseMap);
//
// 3) 取值：把顶点里解出的来源索引原样传到片元，再按上面的行号取参数；
//    纹理数组的层号即来源索引，因此 UV 不需要任何改动。
//      uint idx = (uint)round(sourceIndex);
//      half4 albedo = SAMPLE_TEXTURE2D_ARRAY(_BaseMap, sampler_BaseMap, uv, idx);
//      float4 param = HmMeshMergeLoadParam(_HmMeshMergeParams, sourceIndex, 行号);
//      col.rgb *= param.rgb;

Shader "HmMeshMerge/HmMeshMerge"
{
    Properties
    {
        _MeshMergeIndex("Mesh Merge Index", Float) = 0
        _HmMeshMergeParams("Params", 2D) = "white" {}   // 必须在 Properties 里声明，材质才能绑定
        // 贴图属性按数组声明，材质才能在代码里绑定纹理数组。
        _BaseMap("_BaseMap", 2DArray) = "" {}
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "TransparentCutout"
            "Queue" = "AlphaTest"
        }

        Cull Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        // Shadows.hlsl 依赖 CommonMaterial.hlsl 里的 LerpWhiteTo；官方的 Input.hlsl 会带入它。
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
        #include "Packages/com.hm.meshmerge/Runtime/HmMeshMerge.hlsl"

        // 各贴图属性是纹理数组：层号即来源索引，UV 保持原样。
        TEXTURE2D_ARRAY(_BaseMap);
        SAMPLER(sampler_BaseMap);

        // 各来源参数：横轴为来源索引，纵轴为参数表的行；取值用 HmMeshMergeLoadParam。
        TEXTURE2D(_HmMeshMergeParams);

        struct Attributes
        {
            float4 positionOS : POSITION;
            float3 normalOS : NORMAL;
            float2 uv : TEXCOORD0;
            float2 sourceIndex : TEXCOORD3;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float3 positionWS : TEXCOORD0;
            float3 normalWS : TEXCOORD1;
            float2 uv : TEXCOORD2;
            float sourceIndex : TEXCOORD3;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        // 来源索引与本次绘制不一致时返回零值顶点（w = 0），整个三角形被裁剪。
        bool PrepareVertex(Attributes input, out Varyings output)
        {
            output = (Varyings)0;
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_TRANSFER_INSTANCE_ID(input, output);
            output.positionCS = float4(0.0, 0.0, 0.0, 0.0);

            output.sourceIndex = HmMeshMergeDecodeUvIndex(input.sourceIndex.x);
            if (!HmMeshMergeIsSourceVisible(output.sourceIndex, HmMeshMergeGetActiveIndex()))
            {
                return false;
            }

            output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
            output.normalWS = TransformObjectToWorldNormal(input.normalOS);
            output.uv = input.uv;
            return true;
        }

        // 展示用：取参数表第一个启用行乘到颜色上，确认参数纹理接对了。
        half4 SampleSource(Varyings input)
        {
            uint index = (uint)round(input.sourceIndex);
            half4 sample = SAMPLE_TEXTURE2D_ARRAY(_BaseMap, sampler_BaseMap, input.uv, index);
            sample.rgb *= HmMeshMergeLoadParam(_HmMeshMergeParams, input.sourceIndex, 1).rgb;
            return sample;
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

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
                return half4(SampleSource(input).rgb, 1.0h);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;

            float4 ShadowVert(Attributes input) : SV_POSITION
            {
                Varyings output;
                if (!PrepareVertex(input, output))
                {
                    return output.positionCS;
                }

                float3 direction = normalize(_LightDirection);
                return TransformWorldToHClip(ApplyShadowBias(output.positionWS, output.normalWS, direction));
            }

            half4 ShadowFrag() : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex DepthVert
            #pragma fragment DepthFrag
            #pragma multi_compile_instancing

            float4 DepthVert(Attributes input) : SV_POSITION
            {
                Varyings output;
                if (!PrepareVertex(input, output))
                {
                    return output.positionCS;
                }

                return TransformWorldToHClip(output.positionWS);
            }

            half4 DepthFrag() : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }
    }

    FallBack Off
}
