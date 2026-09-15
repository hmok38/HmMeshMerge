#ifndef HM_MESH_MERGE_INCLUDED
#define HM_MESH_MERGE_INCLUDED

// HmMeshMerge 的来源索引判断片段。使用前请确保着色器已包含所在管线的核心头文件
// （例如 URP 的 Core.hlsl），本文件依赖其中的实例化宏。
//
// 顶点上的来源索引与本次绘制的激活索引不一致时，把该顶点移出裁剪空间（例如 float4(2, 2, 2, 1)），
// 整个三角形被裁剪，不进入光栅化。该判断必须加到每个 Pass 的顶点着色器，
// 包括阴影和深度 Pass，否则隐藏的来源仍会投射阴影。
//
// 顶点侧的来源索引：顶点色通道用 HmMeshMergeDecodeColorIndex 解码；
// UV 通道直接写入整数，用 round(uv.x) 读取即可。
//
// 来源参数的读取：合并网格的顶点只带索引，各来源的颜色、阈值等数值写在查找纹理
// _HmMeshMergeParams 里（横轴为来源索引，纵轴为参数表的行），用 HmMeshMergeLoadParam
// 取值；行号由合并资产的参数表记录，生成着色器的文件头会逐个列出。这张纹理与普通材质
// 属性一样由使用者的着色器自己声明，取值时把纹理传进函数即可。

// 激活索引默认来自材质/实例属性 _MeshMergeIndex。
// 生成模板在 Properties 中声明该属性，可在材质面板或 MaterialPropertyBlock 中调整。
// 默认实例属性声明不在 UnityPerMaterial 中，不承诺 SRP Batcher 兼容；集成方按自己的绘制路径声明。
//
// 可选定义 HM_MESH_MERGE_INDEX_FROM_MATRIX，从 unity_ObjectToWorld._m33 读取。
// 这会改变标准仿射矩阵（索引 0 时矩阵奇异），仅适合已控制变换、逆矩阵和剔除逻辑的自定义路径。
// 不能把编码后的矩阵直接当作标准 TRS 用于任意 Unity 绘制 API。

#if !defined(HM_MESH_MERGE_INDEX_FROM_MATRIX)
UNITY_INSTANCING_BUFFER_START(HmMeshMergeProps)
    UNITY_DEFINE_INSTANCED_PROP(float, _MeshMergeIndex)
UNITY_INSTANCING_BUFFER_END(HmMeshMergeProps)
#endif

// 取本次绘制要显示的来源索引。
float HmMeshMergeGetActiveIndex()
{
#if defined(HM_MESH_MERGE_INDEX_FROM_MATRIX)
    return unity_ObjectToWorld._m33;
#else
    return UNITY_ACCESS_INSTANCED_PROP(HmMeshMergeProps, _MeshMergeIndex);
#endif
}

// 顶点色通道存的是 8 位量化索引，解码回整数。
float HmMeshMergeDecodeColorIndex(float colorChannelValue)
{
    return round(colorChannelValue * 255.0);
}

// UV 通道直接存整数索引；round 用于消除插值与浮点误差。
float HmMeshMergeDecodeUvIndex(float channelValue)
{
    return round(channelValue);
}

// 顶点的来源索引是否与激活索引一致；不一致的顶点应被移出裁剪空间。
bool HmMeshMergeIsSourceVisible(float sourceIndex, float activeIndex)
{
    return abs(sourceIndex - activeIndex) < 0.5;
}

// 取某来源的一行参数：paramsTexture 是合并工具写入的参数查找纹理（横轴为来源索引，
// 纵轴为参数表的行；生成的材质把它绑在 _HmMeshMergeParams 上），sourceIndex 是顶点
// 着色器解出的来源索引，row 是参数表里的行号。取值走 Load 的整数坐标，不需要采样器。
// 纹理按参数传入，本文件因此不依赖任何材质属性声明，包含顺序不受限。
// 参数从顶点插值到片元，round 用于消除插值误差；行号一经分配即保持稳定，
// 可以直接写死在自有着色器里。
float4 HmMeshMergeLoadParam(Texture2D paramsTexture, float sourceIndex, int row)
{
    return paramsTexture.Load(int3((int)round(sourceIndex), row, 0));
}

#endif
