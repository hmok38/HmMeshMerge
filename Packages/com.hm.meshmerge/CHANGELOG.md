# Changelog

本包遵循语义化版本。

## [0.1.0] - 2026-09-15

- 建立包骨架：`com.hm.meshmerge`，包含 `HmMeshMerge`（Runtime）与 `HmMeshMergeEditor`（Editor）两个程序集定义。
- Runtime：
  - `HmMeshMergeAsset`：**同时是配置与结果**（源列表、参数表、索引通道、属性名、输出 Shader，以及合并出的网格、材质）。窗口只编辑它，不另存一份设置；改完网格或材质后把同一资产选回来即可重新合并；
  - `HmMeshMergeParameterEntry`：参数表的一项（属性名、行号、是否启用），行号一经分配即保持稳定；
  - `HmMeshMergeSource`：参与合并的网格与材质；
  - `HmMeshMergeChannel`：索引写入的顶点通道枚举（顶点色或任一 UV 通道，默认 UV3）；
  - `HmMeshMergeIndex`：把激活索引编解码进实例矩阵 m33 的辅助；
  - `HmMeshMerge.hlsl`：来源索引判断片段（两条激活索引来源：矩阵分量 / 实例属性），以及来源参数读取片段——提供 `HmMeshMergeLoadParam(纹理, sourceIndex, 行号)`，按参数表行号取查找纹理 `_HmMeshMergeParams` 里的一行；纹理作为参数传入，本文件不声明它，因此与使用者的着色器和工具既有生成物都不冲突；
- Editor：
  - `HmMeshMergeWindow`：选择源、配置通道与输出位置、执行合并；
  - `HmMeshMergeBuilder`：校验（通道占用、UV 范围、贴图尺寸、网格形态）、几何合并与资产生成；
  - `HmMeshMergeShaderWriter`：生成专用无光照着色器——采样各贴图属性的纹理数组（层号即索引），并在 Properties 里声明 `_HmMeshMergeParams`（不声明材质就绑不上参数纹理），取参数表第一个启用行做展示；不含任何属性的特定用法（颜色、裁剪等一律由使用者在自有着色器里按参数表实现）；生成后自动回填到资产的「输出 Shader」，同路径覆盖不产生多余文件；文件头按参数表逐行列出属性名、行号与可直接复制的 `HmMeshMergeLoadParam` 取值语句；
  - `HmMeshMergeParameterWriter`：参数表管理与查找纹理生成——勾选参数按行写入（横轴为来源索引），行号一经分配即保持稳定，取消勾选的行保留为空行，只有显式重排才会改变行号；纹理强制不压缩（ASTC 分块会让相邻来源互相污染）；
  - `HmMeshMergeTextureArrayImporter`：把若干张独立贴图导入成一个纹理数组资产（源文件 `.hmtexarray`，导入设置存在它的 `.meta` 里）。层号即来源索引、UV 不重映射；对每张源贴图声明 `DependsOnArtifact` 建立平台依赖，切 Build Target 时数组随源贴图一起按平台重导（Android 得 ASTC、桌面得 BC）。层数只受 `SystemInfo.maxTextureArraySlices` 约束，没有"层数 × 尺寸"的乘法上限；
  - `HmMeshMergeTextureSet`：一个贴图属性的合并结果（属性名与纹理数组资产）；
  - `HmMeshMergeWindow`：绑定一份配置资产并直接编辑它（新建 / 选择入口）；源列表增删、参数表编辑（启用开关、按源着色器列出属性、重新整理行号）；
  - `HmMeshMergeSettings`：索引通道、输出 Shader 与源材质属性名配置。
- Sample：`Samples~/UrpCutout` 参考着色器，展示 ForwardLit / ShadowCaster / DepthOnly 三个 Pass 的索引判断、纹理数组采样与参数查找纹理取值（行号来自参数表）。
- 文档：`Documentation~/Design.md` 设计文档。
- 状态：仅完成静态检查（Roslyn 解析 9 个 C# 文件无语法错误、JSON 合法、编码与行尾符合项目 `.editorconfig`）；Unity 编译与运行由用户手动验证。
