# URP Cutout 参考着色器

展示 HmMeshMerge 合并网格在 URP 中的三处接入点：

1. **来源索引判断**：顶点着色器比较顶点上的来源索引与本次绘制的激活索引，不一致时把顶点停在裁剪空间 `w = 0`，三角形整体被裁剪。三个 Pass（ForwardLit、ShadowCaster、DepthOnly）都做了同样的判断——漏掉阴影或深度 Pass，隐藏的来源仍会投射阴影、写深度。
2. **贴图数组**：每个贴图属性是一份纹理数组，层号即来源索引，UV 原样采样即可，不需要按来源调整。
3. **来源参数**：颜色、裁剪阈值等数值参数写在查找纹理 `_HmMeshMergeParams` 里（横轴为来源索引、纵轴为参数表的行），用 `HmMeshMerge.hlsl` 的 `HmMeshMergeLoadParam(纹理, sourceIndex, 行号)` 取值。行号来自合并资产的参数表，生成着色器的文件头会逐个列出。

## 参数行号

参数纹理必须在着色器的 Properties 块里声明，材质才能绑定：

```hlsl
_HmMeshMergeParams("Params", 2D) = "white" {}
```

纹理要在自己的着色器里声明，Properties 与 HLSL 各一处：前者决定材质能否绑定，后者决定取值能否编译，这一行放在取值之前即可。取值函数由 `HmMeshMerge.hlsl` 提供，把纹理作为参数传入：

```hlsl
float4 param = HmMeshMergeLoadParam(_HmMeshMergeParams, sourceIndex, 行号);
```

行号一经分配就保持稳定：移除参数只把该行置空，不会让后面的行号前移，所以复制到自有着色器里的行号不会静默失效。只有显式执行「重新整理参数表」才会重排。

## 索引来源

着色器默认通过实例矩阵的 `m33` 读取激活索引（`#define HM_MESH_MERGE_INDEX_FROM_MATRIX`），配合 `HmMeshMergeIndex.WriteToMatrix` 使用：适用于自己提供实例矩阵的绘制路径，且不引入材质属性，不影响 SRP Batcher 兼容性。位置变换取 `TransformObjectToWorld` 的返回值即可，索引编码只影响 `w` 分量。普通 MeshRenderer 的矩阵来自 Transform，m33 恒为 1，固定显示索引 1，因此该路径不适合场景里手动切换。

要逐次切换显示哪一个来源（场景验证、或配合 MaterialPropertyBlock），改用材质属性路径：注释掉着色器 HLSLINCLUDE 里的 `#define HM_MESH_MERGE_INDEX_FROM_MATRIX`，并在 Properties 块声明索引属性。想直接在材质面板里调整就不加 `[PerRendererData]`：

```hlsl
_MeshMergeIndex("Mesh Merge Index", Float) = 0
```

由 MaterialPropertyBlock 提供时加 `[PerRendererData]`：

```hlsl
[PerRendererData] _MeshMergeIndex("Mesh Merge Index", Float) = 0
```

该属性不在 `UnityPerMaterial` 中，使用属性路径的着色器不参与 SRP Batcher 批处理。

## 索引通道

顶点上的来源索引通道要与合并资产的「索引通道」设置一致。本样例用默认的 UV3（`float2 sourceIndex : TEXCOORD3`，用 `HmMeshMergeDecodeUvIndex` 解码）；合并资产改选顶点色时，声明改成 `float4 sourceIndex : COLOR`，解码改成 `HmMeshMergeDecodeColorIndex(input.sourceIndex.r)`。

## 使用

把本目录的着色器复制到项目 Assets 下（或在 Package Manager 中导入本示例），再把它作为合并材质的 Shader：合并窗口的「输出 Shader」指向它即可，留空或指向工具生成的那份时，工具会在资产的同目录重新生成一份专用着色器。
