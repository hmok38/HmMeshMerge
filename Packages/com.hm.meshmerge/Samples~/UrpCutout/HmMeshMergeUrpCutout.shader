// HmMeshMerge 参考着色器（URP，Alpha 裁剪）。
// 合并网格里同一个网格只存一份几何，顶点上带的是网格索引；来源索引（源列表下标）由本次绘制决定。
// 展示三处接入点：
//   1. 顶点着色器用来源映射表把激活来源换成网格索引，隐藏不属于该网格的顶点；
//   2. 贴图走纹理数组，层号即材质索引（表里的 G 通道），UV 原样采样；
//   3. 各来源的颜色与裁剪阈值来自参数查找纹理 _HmMeshMergeParams，
//      用 HmMeshMerge.hlsl 的 HmMeshMergeLoadParam 按材质索引和参数表行号取，不做材质参数。
// 来源映射表宽为来源数、高 1：R 是网格索引、G 是材质索引，两个映射在同一个纹素的通道里，
// 不分成两行，所以一次 Load 就能同时取回两者。
// 激活索引默认来自材质/实例属性 _MeshMergeIndex，含义是来源索引，适合先用普通 MeshRenderer 验证。
Shader "HmMeshMerge/URP Cutout"
{
    Properties
    {
        _MeshMergeIndex("来源索引", Float) = 0
        _BaseMap("Base Map", 2DArray) = "" {}
        // 参数查找纹理必须在 Properties 里声明，材质才能绑定：合并工具生成的材质会把它设进来。
        _HmMeshMergeParams("Params", 2D) = "white" {}
        // 来源映射表（R 网格索引、G 材质索引）同样要在 Properties 里声明，合并工具生成的材质会把它设进来。
        _HmMeshMergeSources("Sources", 2D) = "black" {}
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
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"

        // HmMeshMergeLoadSourceMesh、HmMeshMergeIsMeshVisible 与 HmMeshMergeLoadParam 由该文件提供；
        // 两张查找纹理和普通材质属性一样要自己声明。
        // 路径固定为包名 com.hm.meshmerge；改包名后必须同步修改（工具生成的模板按实际安装位置写入）。
        #include "Packages/com.hm.meshmerge/Runtime/HmMeshMerge.hlsl"

        TEXTURE2D_ARRAY(_BaseMap);
        SAMPLER(sampler_BaseMap);
        TEXTURE2D(_HmMeshMergeParams);
        TEXTURE2D(_HmMeshMergeSources);

        struct Attributes
        {
            float4 positionOS : POSITION;
            float3 normalOS : NORMAL;
            float2 uv : TEXCOORD0;
            // 索引通道，要与合并资产里的「索引通道」设置一致：这里是默认的 UV3。
            // 里面是网格索引（去重后的第几个网格），不是来源索引。
            // 选顶点色通道时改成 float4 meshIndex : COLOR; 并用 HmMeshMergeDecodeColorIndex 解码。
            float2 meshIndex : TEXCOORD3;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float3 positionWS : TEXCOORD0;
            half3 normalWS : TEXCOORD1;
            float2 uv : TEXCOORD2;
            // 顶点侧换出的材质索引：片元里不要再取一遍激活索引，实例属性与矩阵 m33 只在顶点阶段有效。
            nointerpolation float materialIndex : TEXCOORD3;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        // 判断本顶点是否属于本次绘制要显示的来源；被隐藏时顶点移出裁剪空间，三角形整体被裁剪。
        // 激活索引是来源索引，先用来源映射表换成网格索引与材质索引，再和顶点上的网格索引比较。
        bool PrepareVertex(Attributes input, out Varyings output)
        {
            output = (Varyings)0;
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_TRANSFER_INSTANCE_ID(input, output);
            output.positionCS = float4(2.0, 2.0, 2.0, 1.0);

            float sourceIndex = HmMeshMergeGetActiveIndex();
            output.materialIndex = HmMeshMergeLoadSourceMaterial(_HmMeshMergeSources, sourceIndex);
            float meshIndex = HmMeshMergeLoadSourceMesh(_HmMeshMergeSources, sourceIndex);
            if (!HmMeshMergeIsMeshVisible(HmMeshMergeDecodeUvIndex(input.meshIndex.x), meshIndex))
            {
                return false;
            }

            output.uv = input.uv;
            output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
            output.normalWS = TransformObjectToWorldNormal(input.normalOS);
            return true;
        }

        // 行号来自合并资产的参数表，生成着色器的文件头会逐个列出；按你自己表里的行号改。
        // 层号和参数行都按顶点插值下来的材质索引取，同一网格配不同材质时各自拿到自己的值。
        half4 SampleBaseMap(Varyings input)
        {
            uint index = (uint)round(input.materialIndex);
            half4 sample = SAMPLE_TEXTURE2D_ARRAY(_BaseMap, sampler_BaseMap, input.uv, index);
            sample *= HmMeshMergeLoadParam(_HmMeshMergeParams, input.materialIndex, 0);   // 第 0 行
            clip(sample.a - HmMeshMergeLoadParam(_HmMeshMergeParams, input.materialIndex, 1).r);  // 第 1 行
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
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            Varyings ShadowVert(Attributes input)
            {
                Varyings output;
                if (!PrepareVertex(input, output))
                {
                    return output;
                }

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
                return output;
            }

            half4 ShadowFrag(Varyings input) : SV_Target
            {
                SampleBaseMap(input);
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

            Varyings DepthVert(Attributes input)
            {
                Varyings output;
                if (!PrepareVertex(input, output))
                {
                    return output;
                }

                output.positionCS = TransformWorldToHClip(output.positionWS);
                return output;
            }

            half4 DepthFrag(Varyings input) : SV_Target
            {
                SampleBaseMap(input);
                return input.positionCS.z;
            }
            ENDHLSL
        }
    }

    FallBack Off
}
