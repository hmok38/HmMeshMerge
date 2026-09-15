// HmMeshMergeShaderWriter 生成；输入为合并配置。下次合并会覆盖，请复制到自有 Shader 后修改。
// 复制对应 Properties、纹理声明、HmRead/HmSample 函数和索引判断到自有 Shader。
// 数值函数直接返回正确的标量/向量；启用行走 LUT，其他参数使用第一来源的普通材质属性。
// _ST 函数返回各贴图的 Tiling.xy 和 Offset.zw，网格 UV 本身未改变。
// 模板基于 URP；其他管线保留数据契约，替换管线相关宏、变换和 Pass。
// 本模板只展示主贴图、主颜色、Alpha 裁剪，不模拟任意源 Shader 的完整效果。
// 默认激活索引是材质/实例属性 _MeshMergeIndex；生成模板不保证 SRP Batcher 兼容。
// 来源 0: tree_remake_02_ec304abe27193ba5 / blue
// 来源 1: tree_remake_02_low_49491449df84709e / Red
// 行 0: _BaseMap，启用（纹理由数组或普通纹理承载，不写数值行）
// 行 1: _MainTex，启用（纹理由数组或普通纹理承载，不写数值行）
// 行 2: _BaseColor，启用（纹理由数组或普通纹理承载，不写数值行）
Shader "HmMeshMerge/HmMeshMerge_40bbf1303b9e45545adf5e36e77b2dbf"
{
    Properties
    {
        _MeshMergeIndex("来源索引", Float) = 0
        _HmMeshMergeParams("来源参数 LUT", 2D) = "black" {}
        _WorkflowMode("WorkflowMode", Float) = 1
        _BaseMap("Albedo", 2DArray) = "" {}
        _BaseColor("Color", Color) = (1, 1, 1, 1)
        _Cutoff("Alpha Cutoff", Float) = 0.5
        _Smoothness("Smoothness", Float) = 0.5
        _SmoothnessTextureChannel("Smoothness texture channel", Float) = 0
        _Metallic("Metallic", Float) = 0
        _MetallicGlossMap("Metallic", 2D) = "white" {}
        _SpecColor("Specular", Color) = (0.199999928, 0.199999928, 0.199999928, 1)
        _SpecGlossMap("Specular", 2D) = "white" {}
        _SpecularHighlights("Specular Highlights", Float) = 1
        _EnvironmentReflections("Environment Reflections", Float) = 1
        _BumpScale("Scale", Float) = 1
        _BumpMap("Normal Map", 2D) = "bump" {}
        _Parallax("Scale", Float) = 0.005
        _ParallaxMap("Height Map", 2D) = "black" {}
        _OcclusionStrength("Strength", Float) = 1
        _OcclusionMap("Occlusion", 2D) = "white" {}
        _EmissionColor("Color", Color) = (0, 0, 0, 1)
        _EmissionMap("Emission", 2D) = "white" {}
        _DetailMask("Detail Mask", 2D) = "white" {}
        _DetailAlbedoMapScale("Scale", Float) = 1
        _DetailAlbedoMap("Detail Albedo x2", 2D) = "linearGrey" {}
        _DetailNormalMapScale("Scale", Float) = 1
        _DetailNormalMap("Normal Map", 2D) = "bump" {}
        _ClearCoatMask("_ClearCoatMask", Float) = 0
        _ClearCoatSmoothness("_ClearCoatSmoothness", Float) = 0
        _Surface("__surface", Float) = 0
        _Blend("__blend", Float) = 0
        _Cull("__cull", Float) = 2
        _AlphaClip("__clip", Float) = 0
        _SrcBlend("__src", Float) = 1
        _DstBlend("__dst", Float) = 0
        _SrcBlendAlpha("__srcA", Float) = 1
        _DstBlendAlpha("__dstA", Float) = 0
        _ZWrite("__zw", Float) = 1
        _BlendModePreserveSpecular("_BlendModePreserveSpecular", Float) = 1
        _AlphaToMask("__alphaToMask", Float) = 0
        _ReceiveShadows("Receive Shadows", Float) = 1
        _QueueOffset("Queue offset", Float) = 0
        _MainTex("BaseMap", 2DArray) = "" {}
        _Color("Base Color", Color) = (1, 1, 1, 1)
        _GlossMapScale("Smoothness", Float) = 0
        _Glossiness("Smoothness", Float) = 0
        _GlossyReflections("EnvironmentReflections", Float) = 0
    }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "TransparentCutout" "Queue" = "AlphaTest" }
        Cull Off
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
        #include "Packages/com.hm.meshmerge/Runtime/HmMeshMerge.hlsl"
        TEXTURE2D(_HmMeshMergeParams);
        TEXTURE2D_ARRAY(_BaseMap); SAMPLER(sampler_BaseMap);
        TEXTURE2D(_MetallicGlossMap); SAMPLER(sampler_MetallicGlossMap);
        TEXTURE2D(_SpecGlossMap); SAMPLER(sampler_SpecGlossMap);
        TEXTURE2D(_BumpMap); SAMPLER(sampler_BumpMap);
        TEXTURE2D(_ParallaxMap); SAMPLER(sampler_ParallaxMap);
        TEXTURE2D(_OcclusionMap); SAMPLER(sampler_OcclusionMap);
        TEXTURE2D(_EmissionMap); SAMPLER(sampler_EmissionMap);
        TEXTURE2D(_DetailMask); SAMPLER(sampler_DetailMask);
        TEXTURE2D(_DetailAlbedoMap); SAMPLER(sampler_DetailAlbedoMap);
        TEXTURE2D(_DetailNormalMap); SAMPLER(sampler_DetailNormalMap);
        TEXTURE2D_ARRAY(_MainTex); SAMPLER(sampler_MainTex);
        CBUFFER_START(UnityPerMaterial)
            float _WorkflowMode;
            float4 _BaseMap_ST;
            float4 _BaseColor;
            float _Cutoff;
            float _Smoothness;
            float _SmoothnessTextureChannel;
            float _Metallic;
            float4 _MetallicGlossMap_ST;
            float4 _SpecColor;
            float4 _SpecGlossMap_ST;
            float _SpecularHighlights;
            float _EnvironmentReflections;
            float _BumpScale;
            float4 _BumpMap_ST;
            float _Parallax;
            float4 _ParallaxMap_ST;
            float _OcclusionStrength;
            float4 _OcclusionMap_ST;
            float4 _EmissionColor;
            float4 _EmissionMap_ST;
            float4 _DetailMask_ST;
            float _DetailAlbedoMapScale;
            float4 _DetailAlbedoMap_ST;
            float _DetailNormalMapScale;
            float4 _DetailNormalMap_ST;
            float _ClearCoatMask;
            float _ClearCoatSmoothness;
            float _Surface;
            float _Blend;
            float _Cull;
            float _AlphaClip;
            float _SrcBlend;
            float _DstBlend;
            float _SrcBlendAlpha;
            float _DstBlendAlpha;
            float _ZWrite;
            float _BlendModePreserveSpecular;
            float _AlphaToMask;
            float _ReceiveShadows;
            float _QueueOffset;
            float4 _MainTex_ST;
            float4 _Color;
            float _GlossMapScale;
            float _Glossiness;
            float _GlossyReflections;
        CBUFFER_END

        // 以下函数可直接复制。调用示例：HmRead_BaseColor(sourceIndex)、HmSample_BaseMap(uv, sourceIndex)。
        // 源属性 _WorkflowMode；普通材质属性。
        float HmRead_WorkflowMode(float sourceIndex) { return _WorkflowMode; }
        // 源属性 _BaseMap_ST；普通材质属性。
        float4 HmRead_BaseMap_ST(float sourceIndex) { return _BaseMap_ST; }
        float4 HmSample_BaseMap(float2 uv, float sourceIndex)
        {
            float4 st = HmRead_BaseMap_ST(sourceIndex);
            uv = uv * st.xy + st.zw;
            return SAMPLE_TEXTURE2D_ARRAY(_BaseMap, sampler_BaseMap, uv, (uint)round(sourceIndex));
        }
        // 源属性 _BaseColor；LUT 第 2 行。
        float4 HmRead_BaseColor(float sourceIndex) { return HmMeshMergeLoadParam(_HmMeshMergeParams, sourceIndex, 2); }
        // 源属性 _Cutoff；普通材质属性。
        float HmRead_Cutoff(float sourceIndex) { return _Cutoff; }
        // 源属性 _Smoothness；普通材质属性。
        float HmRead_Smoothness(float sourceIndex) { return _Smoothness; }
        // 源属性 _SmoothnessTextureChannel；普通材质属性。
        float HmRead_SmoothnessTextureChannel(float sourceIndex) { return _SmoothnessTextureChannel; }
        // 源属性 _Metallic；普通材质属性。
        float HmRead_Metallic(float sourceIndex) { return _Metallic; }
        // 源属性 _MetallicGlossMap_ST；普通材质属性。
        float4 HmRead_MetallicGlossMap_ST(float sourceIndex) { return _MetallicGlossMap_ST; }
        float4 HmSample_MetallicGlossMap(float2 uv, float sourceIndex)
        {
            float4 st = HmRead_MetallicGlossMap_ST(sourceIndex);
            uv = uv * st.xy + st.zw;
            return SAMPLE_TEXTURE2D(_MetallicGlossMap, sampler_MetallicGlossMap, uv);
        }
        // 源属性 _SpecColor；普通材质属性。
        float4 HmRead_SpecColor(float sourceIndex) { return _SpecColor; }
        // 源属性 _SpecGlossMap_ST；普通材质属性。
        float4 HmRead_SpecGlossMap_ST(float sourceIndex) { return _SpecGlossMap_ST; }
        float4 HmSample_SpecGlossMap(float2 uv, float sourceIndex)
        {
            float4 st = HmRead_SpecGlossMap_ST(sourceIndex);
            uv = uv * st.xy + st.zw;
            return SAMPLE_TEXTURE2D(_SpecGlossMap, sampler_SpecGlossMap, uv);
        }
        // 源属性 _SpecularHighlights；普通材质属性。
        float HmRead_SpecularHighlights(float sourceIndex) { return _SpecularHighlights; }
        // 源属性 _EnvironmentReflections；普通材质属性。
        float HmRead_EnvironmentReflections(float sourceIndex) { return _EnvironmentReflections; }
        // 源属性 _BumpScale；普通材质属性。
        float HmRead_BumpScale(float sourceIndex) { return _BumpScale; }
        // 源属性 _BumpMap_ST；普通材质属性。
        float4 HmRead_BumpMap_ST(float sourceIndex) { return _BumpMap_ST; }
        float4 HmSample_BumpMap(float2 uv, float sourceIndex)
        {
            float4 st = HmRead_BumpMap_ST(sourceIndex);
            uv = uv * st.xy + st.zw;
            return SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, uv);
        }
        // 源属性 _Parallax；普通材质属性。
        float HmRead_Parallax(float sourceIndex) { return _Parallax; }
        // 源属性 _ParallaxMap_ST；普通材质属性。
        float4 HmRead_ParallaxMap_ST(float sourceIndex) { return _ParallaxMap_ST; }
        float4 HmSample_ParallaxMap(float2 uv, float sourceIndex)
        {
            float4 st = HmRead_ParallaxMap_ST(sourceIndex);
            uv = uv * st.xy + st.zw;
            return SAMPLE_TEXTURE2D(_ParallaxMap, sampler_ParallaxMap, uv);
        }
        // 源属性 _OcclusionStrength；普通材质属性。
        float HmRead_OcclusionStrength(float sourceIndex) { return _OcclusionStrength; }
        // 源属性 _OcclusionMap_ST；普通材质属性。
        float4 HmRead_OcclusionMap_ST(float sourceIndex) { return _OcclusionMap_ST; }
        float4 HmSample_OcclusionMap(float2 uv, float sourceIndex)
        {
            float4 st = HmRead_OcclusionMap_ST(sourceIndex);
            uv = uv * st.xy + st.zw;
            return SAMPLE_TEXTURE2D(_OcclusionMap, sampler_OcclusionMap, uv);
        }
        // 源属性 _EmissionColor；普通材质属性。
        float4 HmRead_EmissionColor(float sourceIndex) { return _EmissionColor; }
        // 源属性 _EmissionMap_ST；普通材质属性。
        float4 HmRead_EmissionMap_ST(float sourceIndex) { return _EmissionMap_ST; }
        float4 HmSample_EmissionMap(float2 uv, float sourceIndex)
        {
            float4 st = HmRead_EmissionMap_ST(sourceIndex);
            uv = uv * st.xy + st.zw;
            return SAMPLE_TEXTURE2D(_EmissionMap, sampler_EmissionMap, uv);
        }
        // 源属性 _DetailMask_ST；普通材质属性。
        float4 HmRead_DetailMask_ST(float sourceIndex) { return _DetailMask_ST; }
        float4 HmSample_DetailMask(float2 uv, float sourceIndex)
        {
            float4 st = HmRead_DetailMask_ST(sourceIndex);
            uv = uv * st.xy + st.zw;
            return SAMPLE_TEXTURE2D(_DetailMask, sampler_DetailMask, uv);
        }
        // 源属性 _DetailAlbedoMapScale；普通材质属性。
        float HmRead_DetailAlbedoMapScale(float sourceIndex) { return _DetailAlbedoMapScale; }
        // 源属性 _DetailAlbedoMap_ST；普通材质属性。
        float4 HmRead_DetailAlbedoMap_ST(float sourceIndex) { return _DetailAlbedoMap_ST; }
        float4 HmSample_DetailAlbedoMap(float2 uv, float sourceIndex)
        {
            float4 st = HmRead_DetailAlbedoMap_ST(sourceIndex);
            uv = uv * st.xy + st.zw;
            return SAMPLE_TEXTURE2D(_DetailAlbedoMap, sampler_DetailAlbedoMap, uv);
        }
        // 源属性 _DetailNormalMapScale；普通材质属性。
        float HmRead_DetailNormalMapScale(float sourceIndex) { return _DetailNormalMapScale; }
        // 源属性 _DetailNormalMap_ST；普通材质属性。
        float4 HmRead_DetailNormalMap_ST(float sourceIndex) { return _DetailNormalMap_ST; }
        float4 HmSample_DetailNormalMap(float2 uv, float sourceIndex)
        {
            float4 st = HmRead_DetailNormalMap_ST(sourceIndex);
            uv = uv * st.xy + st.zw;
            return SAMPLE_TEXTURE2D(_DetailNormalMap, sampler_DetailNormalMap, uv);
        }
        // 源属性 _ClearCoatMask；普通材质属性。
        float HmRead_ClearCoatMask(float sourceIndex) { return _ClearCoatMask; }
        // 源属性 _ClearCoatSmoothness；普通材质属性。
        float HmRead_ClearCoatSmoothness(float sourceIndex) { return _ClearCoatSmoothness; }
        // 源属性 _Surface；普通材质属性。
        float HmRead_Surface(float sourceIndex) { return _Surface; }
        // 源属性 _Blend；普通材质属性。
        float HmRead_Blend(float sourceIndex) { return _Blend; }
        // 源属性 _Cull；普通材质属性。
        float HmRead_Cull(float sourceIndex) { return _Cull; }
        // 源属性 _AlphaClip；普通材质属性。
        float HmRead_AlphaClip(float sourceIndex) { return _AlphaClip; }
        // 源属性 _SrcBlend；普通材质属性。
        float HmRead_SrcBlend(float sourceIndex) { return _SrcBlend; }
        // 源属性 _DstBlend；普通材质属性。
        float HmRead_DstBlend(float sourceIndex) { return _DstBlend; }
        // 源属性 _SrcBlendAlpha；普通材质属性。
        float HmRead_SrcBlendAlpha(float sourceIndex) { return _SrcBlendAlpha; }
        // 源属性 _DstBlendAlpha；普通材质属性。
        float HmRead_DstBlendAlpha(float sourceIndex) { return _DstBlendAlpha; }
        // 源属性 _ZWrite；普通材质属性。
        float HmRead_ZWrite(float sourceIndex) { return _ZWrite; }
        // 源属性 _BlendModePreserveSpecular；普通材质属性。
        float HmRead_BlendModePreserveSpecular(float sourceIndex) { return _BlendModePreserveSpecular; }
        // 源属性 _AlphaToMask；普通材质属性。
        float HmRead_AlphaToMask(float sourceIndex) { return _AlphaToMask; }
        // 源属性 _ReceiveShadows；普通材质属性。
        float HmRead_ReceiveShadows(float sourceIndex) { return _ReceiveShadows; }
        // 源属性 _QueueOffset；普通材质属性。
        float HmRead_QueueOffset(float sourceIndex) { return _QueueOffset; }
        // 源属性 _MainTex_ST；普通材质属性。
        float4 HmRead_MainTex_ST(float sourceIndex) { return _MainTex_ST; }
        float4 HmSample_MainTex(float2 uv, float sourceIndex)
        {
            float4 st = HmRead_MainTex_ST(sourceIndex);
            uv = uv * st.xy + st.zw;
            return SAMPLE_TEXTURE2D_ARRAY(_MainTex, sampler_MainTex, uv, (uint)round(sourceIndex));
        }
        // 源属性 _Color；普通材质属性。
        float4 HmRead_Color(float sourceIndex) { return _Color; }
        // 源属性 _GlossMapScale；普通材质属性。
        float HmRead_GlossMapScale(float sourceIndex) { return _GlossMapScale; }
        // 源属性 _Glossiness；普通材质属性。
        float HmRead_Glossiness(float sourceIndex) { return _Glossiness; }
        // 源属性 _GlossyReflections；普通材质属性。
        float HmRead_GlossyReflections(float sourceIndex) { return _GlossyReflections; }
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
            nointerpolation float sourceIndex : TEXCOORD3;
        };
        bool PrepareVertex(Attributes input, out Varyings output)
        {
            output = (Varyings)0;
            UNITY_SETUP_INSTANCE_ID(input);
            output.positionCS = float4(2.0, 2.0, 2.0, 1.0);
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
        float4 SampleSource(Varyings input)
        {
            float4 color = float4(1, 1, 1, 1);
            color *= HmSample_BaseMap(input.uv, input.sourceIndex);
            color *= HmRead_BaseColor(input.sourceIndex);
            if (HmRead_AlphaClip(input.sourceIndex) > 0.5)
            {
                clip(color.a - HmRead_Cutoff(input.sourceIndex));
            }
            return color;
        }
        ENDHLSL
        Pass
        {
            Name "ForwardUnlit"
            Tags { "LightMode" = "UniversalForwardOnly" }
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
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
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
        }
    }
    FallBack Off
}
