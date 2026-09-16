# Changelog

本包遵循语义化版本。

## [Unreleased]

- 输出文件命名统一为「配置名_角色」，让名字直接对应作用：参数 LUT 由 `_Params.asset` 改为 `_ParamLut.asset`，来源映射表由 `_Sources.asset` 改为 `_SourceMap.asset`。旧名字的文件不自动删除或迁移，重新合并后材质会改绑新名字的文件。
- 文档补充来源映射表的布局：宽为来源数、高 1，R 为网格索引、G 为材质索引，两个映射在同一个纹素的通道里而不是两行，顶点着色器一次 Load 就能同时取回两者；README、设计说明、示例与 hlsl 注释同步。
- 材质同样按引用去重：同一个材质被多个来源引用时只占参数 LUT 的一列、纹理数组的一层，不再为每个来源重复存同一张贴图；数组层数由来源数改为去重后的材质数。
- 来源映射表 `_SourceMap.asset` 增加 G 通道：R 仍为该来源使用的网格索引，G 为该来源使用的材质索引；新增 `HmMeshMergeLoadSourceMaterial` 取材质索引。
- 参数 LUT 的横轴与纹理数组的层号改按材质索引读取：`HmMeshMergeLoadParam(纹理, materialIndex, 行号)` 的第二个参数不再传来源索引，自有 Shader 需先用 `HmMeshMergeLoadSourceMaterial` 换出材质索引。生成模板与示例已同步。
- 贴图校验与数组日志改按层（材质）列出，消息中的层号与生成的数组一致。
- 修复重复几何：按引用对来源网格去重，同一个网格在合并网格里只保留一份顶点与三角形，不再随材质组合重复；顶点索引通道改存网格索引，新增来源映射表 `_SourceMap.asset`（Shader 侧 `_HmMeshMergeSources`）记录来源索引到网格索引的映射。
- 激活索引改为只在顶点着色器取一次：换成材质索引后以 nointerpolation 插值给片元，片元不再重读 _MeshMergeIndex 或 unity_ObjectToWorld._m33，避免片元阶段取到别的实例。
- 生成模板与示例同步：顶点着色器先用 `HmMeshMergeLoadSourceMesh` 把激活来源换成网格索引，再调用 `HmMeshMergeIsMeshVisible`；`HmMeshMergeIsSourceVisible` 已移除。自有 Shader 需要同步改名，并保证传入的是网格索引。
- 输出 Shader 必须声明 2D 属性 `_HmMeshMergeSources`，否则合并报错；生成的材质会自动绑定该来源映射表。
- 包名统一为 `com.hm.meshmerge`，与包目录 `Packages/com.hm.meshmerge` 一致；作者署名改为 `huangmin`。已安装该包的工程（如 HmSlgGame）需要把依赖名从 `com.huangmin.meshmerge` 改为 `com.hm.meshmerge` 后再更新包。
- 修复在 git 或本地安装该包时生成 Shader 无法包含 HmMeshMerge.hlsl 的报错：模板不再写死包路径，改为按工具所在工程解析出的包路径生成；示例 Shader 与文档同步使用实际包名。
- 排除由管线提供的 unity_ 内置属性，修复 unity_Lightmaps 重复声明；输出 Shader 已报错时停止绑定材质。
- 按属性类型复制普通材质值，数组槽位只接收生成的 Texture2DArray，不再整体复制源材质的 2D 贴图绑定。

- 修复内置贴图特殊 GUID 被登记为产物依赖而导致的导入断言与循环；数组来源限定为 Assets / Packages 中的贴图，内置或非资产来源给出明确提示。
- 修复参数量化与错误属性去重：浮点 LUT 保留负数和超出 0–1 的数值，属性逐项列出。
- 参数刷新保留旧行号和选择，只追加新项；Tiling/Offset 纳入参数表。
- 保留网格顶点色及 UV0–UV7；输出 Mesh、Material 和浮点 LUT 更新时保持 GUID。
- 纹理规格不同只报错，由用户手工统一；数组逐层写 CPU 像素数据以供保存，需 Read/Write，禁止 Crunch。
- Shader 为各属性生成具体读取函数，普通材质属性继续绑定；显示、阴影与深度统一 Alpha 裁剪。
- 修复窗口未应用编辑就合并、空输入校验异常，以及临时资源释放。
- 同步当前说明与 URP 示例；旧 PNG 参数纹理保留，重新合并改绑浮点 .asset。
- 窗口编辑改为提交即写盘（AssetDatabase.SaveAssetIfDirty），关闭窗口时再提交一次；配置不再只停留在内存脏状态。
- 数组参数不一致时改为一次性汇总明细：按来源贴图逐条列出路径、宽高、实际格式（含 sRGB）、mip、Read/Write、Crunch 与采样设置，并在同一条目内紧接“建议”（宽高按各层最大尺寸给建议），不再分成“需要修改的层”和“全部来源贴图”两段；明细只在点击“执行合并”时输出，数组资产重导时只留一行未生成提示。
- 引用同一组贴图的多个属性（例如 _BaseMap 与 _MainTex 指向同一对贴图）共用一条报告，属性名并列，参数清单不再逐个属性重复。
- 需要修改的贴图改为在校验结束时统一输出：先按贴图合并建议并去重，每张贴图只出一条携带贴图对象的错误日志，条目只含路径与建议（不带属性名与层号），单击该条即可在 Project 中定位；层号与各层参数保留在汇总表中。
- 校验与生成失败在 Console 只输出一次汇总：窗口不再重复打印异常本身，其他未预期异常仍补记堆栈。
- mip 建议只在各层尺寸已经一致时给出：尺寸不一致时层数差异由尺寸决定，避免给出与宽高建议冲突的目标值；该建议同时提示检查 Generate Mip Maps 与 Mipmap Limit。
- 本轮只进行静态检查，未执行 Unity 编译、导入或运行。

## [0.1.0] - 2026-09-15

以下为初始开发记录，当前行为以 Unreleased 与 README 为准。

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
