// HmMeshMerge 参考着色器（URP，Alpha 裁剪）。
// 展示三处接入点：
//   1. 顶点着色器按来源索引隐藏不需要的来源；
//   2. 贴图走纹理数组，层号即来源索引，UV 原样采样；
//   3. 各来源的颜色与裁剪阈值来自参数查找纹理 _HmMeshMergeParams，
//      用 HmMeshMerge.hlsl 的 HmMeshMergeLoadParam 按参数表行号取，不做材质参数。
// 索引默认来自实例矩阵的 m33（HmMeshMergeIndex.WriteToMatrix），
// 位置变换取 TransformObjectToWorld 的 xyz，索引编码不影响位置与法线。
Shader "HmMeshMerge/URP Cutout"
{
    Properties
    {
        _BaseMap("Base Map", 2DArray) = "" {}
        // 参数查找纹理必须在 Properties 里声明，材质才能绑定：合并工具生成的材质会把它设进来。
        _HmMeshMergeParams("Params", 2D) = "white" {}
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

        // 改用材质属性或 MaterialPropertyBlock 传索引时：注释掉下面这行，并在 Properties 块声明索引属性：
        //   _MeshMergeIndex("Mesh Merge Index", Float) = 0                    在材质面板里直接调整
        //   [PerRendererData] _MeshMergeIndex("Mesh Merge Index", Float) = 0  由 MaterialPropertyBlock 提供
        #define HM_MESH_MERGE_INDEX_FROM_MATRIX
        // HmMeshMergeLoadParam 由该文件提供；查找纹理和普通材质属性一样要自己声明。
        #include "Packages/com.hm.meshmerge/Runtime/HmMeshMerge.hlsl"

        TEXTURE2D_ARRAY(_BaseMap);
        SAMPLER(sampler_BaseMap);
        TEXTURE2D(_HmMeshMergeParams);

        struct Attributes
        {
            float4 positionOS : POSITION;
            float3 normalOS : NORMAL;
            float2 uv : TEXCOORD0;
            // 索引通道，要与合并资产里的「索引通道」设置一致：这里是默认的 UV3。
            // 选顶点色通道时改成 float4 sourceIndex : COLOR; 并用 HmMeshMergeDecodeColorIndex 解码。
            float2 sourceIndex : TEXCOORD3;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float3 positionWS : TEXCOORD0;
            half3 normalWS : TEXCOORD1;
            float2 uv : TEXCOORD2;
            float sourceIndex : TEXCOORD3;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        // 判断本顶点是否属于本次绘制要显示的来源；被隐藏时顶点停在 w = 0，三角形整体被裁剪。
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

            output.uv = input.uv;
            output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
            output.normalWS = TransformObjectToWorldNormal(input.normalOS);
            return true;
        }

        // 行号来自合并资产的参数表，生成着色器的文件头会逐个列出；按你自己表里的行号改。
        half4 SampleBaseMap(Varyings input)
        {
            uint index = (uint)round(input.sourceIndex);
            half4 sample = SAMPLE_TEXTURE2D_ARRAY(_BaseMap, sampler_BaseMap, input.uv, index);
            sample.rgb *= HmMeshMergeLoadParam(_HmMeshMergeParams, input.sourceIndex, 0).rgb;   // 第 0 行
            clip(sample.a - HmMeshMergeLoadParam(_HmMeshMergeParams, input.sourceIndex, 1).r);  // 第 1 行
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
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

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
                half4 sample = SampleBaseMap(input);
                half3 normalWS = normalize(input.normalWS);
                Light light = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
                half diffuse = saturate(dot(normalWS, light.direction)) * light.shadowAttenuation;
                half3 color = sample.rgb * (SampleSH(normalWS) + light.color * (0.25h + 0.75h * diffuse));
                return half4(color, 1.0h);
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
