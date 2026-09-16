# HmMeshMerge 开发纪要

- 记录日期：2026-09-16（Asia/Shanghai）
- 开发阶段：接手初版实现、核心重构及用户联调问题修复
- 项目：HmMeshMerge
- 环境：Unity 2022.3.62f2；当前宿主项目使用 URP 14.0.12
- 插件路径：Packages/com.hm.meshmerge
- 需求依据：用户提供的《HmMeshMerge-插件需求定稿.md》及本次会话后续确认
- 原始需求文件：C:/Users/HM/Documents/HmMeshMerge-插件需求定稿.md
- 记录口径：区分用户确认的需求、当前源码实现、用户实际报错和待验证事项；不把静态检查视为运行验收。

## 一、背景与目标

初版插件由其他 AI 开发。用户认为实现存在问题，要求接手、优化和重构。

插件的目标是：把多个共享同一 Shader 的模型合并为一个 Mesh 和一个材质，在顶点中记录网格索引，来源到网格、材质的映射写在来源映射表里，绘制时只显示指定来源。这样可以为调用方组织不同模型品种的实例合批提供统一的 Mesh 与 Material。

**合并资源本身不等于已经降低 draw call。** 调用方仍需把使用相同合并资源的实例组织到同一批次。插件不提供渲染器、批次管理、实例剔除或宿主游戏接入。

## 二、最终需求与本次确认

| 需求项 | 最终约定 |
|---|---|
| 分发 | 独立 UPM Package，包名 com.hm.meshmerge |
| 程序集与命名空间 | Runtime 为 HmMeshMerge，Editor 为 HmMeshMergeEditor，均使用同名单层命名空间 |
| 输入 | 多组 Mesh + Material；所有来源使用同一个 Shader |
| 输出 | 一个合并 Mesh、一个材质，以及按需生成的纹理数组、参数 LUT 和专用 Shader |
| 网格新增数据 | 只新增网格索引，不再写入外观参数；同一网格被多个来源引用时几何只存一份 |
| 默认索引通道 | UV3，即零基编号的 TEXCOORD3；可选择其他支持的空闲通道 |
| 源网格数据 | 保留已有 UV、顶点色等数据，避免破坏依赖这些通道的动画 |
| 数值参数 | 写入 _HmMeshMergeParams；横轴为材质索引（按材质引用去重），纵轴为参数表行号 |
| 贴图 | 使用 Texture2DArray，层号等于材质索引（同一材质只占一层），网格 UV 不重映射 |
| 来源与网格、材质 | 来源是「网格 + 材质」的组合；网格与材质都按引用去重，来源索引到两者的映射写在 _SourceMap.asset |
| 数组资产生成 | 使用 .hmtexarray ScriptedImporter；不保留 flipbook 路径 |
| 参数候选 | 列出源 Shader 的普通材质属性；插件不能判断某属性是否被 Shader 代码实际使用 |
| 首次参数表 | 各来源取值不同的排前并启用；取值相同的排后并停用 |
| 行号稳定 | 刷新和停用不能改变已有行号；只有用户显式执行“重新整理参数表”才重新编号 |
| 配置载体 | 合并资产本身就是配置，窗口不另存第二份设置 |
| 窗口初始入口 | 未绑定配置时提供“新建 / 选择资产” |
| 输出 Shader | 默认不改写源 Shader，生成基础展示和可复制的逐属性读取模板；另有「尝试修改来源shader(副本)」开关，勾选后复制来源 Shader 并注入接入点 |
| Shader 引用 | 生成后回填输出 Shader；留空或仍指向默认生成路径时重新生成 |
| 消息 | 窗口显示的提示、错误同步到 Console |
| 尺寸不一致 | **本次用户明确选择：只报错，由用户手动统一尺寸，不自动缩放或修改源导入设置** |

### 已作废的旧方案

- 默认使用顶点色存索引：已改为默认 UV3，顶点色只作为可选通道。
- 顶点通道存放颜色、阈值等外观参数：已改为参数 LUT。
- 图集、UV 重映射、模型按索引偏移位置：不再采用。
- flipbook 作为备用路径：不保留。
- 窗口维护一份设置、合并时再复制到配置资产：已统一为直接编辑资产。
- 尺寸不一致时弹窗后自动缩小：本次按用户确认移除。

### 本次实现选择及其边界

以下属于针对需求所采用的实现，不等同于用户原始需求中的逐项指定：

- LUT 使用 RGBAFloat 原生资产，替代初版 8 位 PNG。
- 数组通过 CPU 原始像素数据保存，来源需手动开启 Read/Write，不支持 Crunch。
- 当前生成模板和示例仍针对 URP；其他管线需要移植管线相关声明与 Pass。
- unity_ 前缀的管线内置属性不作为普通材质参数生成。
- 蒙皮、BlendShape、非三角形拓扑明确报错，需先处理为支持的静态网格。
- 「尝试修改来源shader(副本)」是可选能力：复制来源 Shader 文本并注入接入点，只做插入与改名，因此保留风动、光照等自有逻辑；逐来源不同的数值引用与贴图取样不自动改写，改为写入生成文件头部注释的待办。

## 三、源码组织与职责

### Runtime：数据契约与 Shader 片段

| 文件 / 类型 | 职责 |
|---|---|
| [HmMeshMergeAsset](../Packages/com.hm.meshmerge/Runtime/HmMeshMergeAsset.cs) | 保存来源列表、参数表、索引通道、输出 Shader，以及合并 Mesh 和 Material 引用 |
| [HmMeshMergeSource](../Packages/com.hm.meshmerge/Runtime/HmMeshMergeSource.cs) | 一组 Mesh 与 Material；列表下标即来源索引 |
| [HmMeshMergeParameterEntry](../Packages/com.hm.meshmerge/Runtime/HmMeshMergeParameterEntry.cs) | 属性名 propertyName、稳定行号 row、启用状态 active |
| [HmMeshMergeChannel](../Packages/com.hm.meshmerge/Runtime/HmMeshMergeChannel.cs) | UV1–UV7 与顶点色的索引通道选项 |
| [HmMeshMergeIndex](../Packages/com.hm.meshmerge/Runtime/HmMeshMergeIndex.cs) | 保留已有矩阵 m33 索引读写接口，仅供已适配的自定义绘制路径使用 |
| [HmMeshMerge.hlsl](../Packages/com.hm.meshmerge/Runtime/HmMeshMerge.hlsl) | 索引解码、激活索引读取、来源可见性判断、按行读取 LUT |

Runtime 不依赖 UnityEditor。配置资产在运行时按只读数据使用；当前公开数据接口并不从语言层面强制不可变。

### Editor：交互、编排与生成

| 文件 / 类型 | 主要入口与职责 |
|---|---|
| [HmMeshMergeWindow](../Packages/com.hm.meshmerge/Editor/HmMeshMergeWindow.cs) | Execute、FillParametersFromShader、CompactParameterTable；负责资产选择、序列化编辑、操作入口与消息展示 |
| [HmMeshMergeBuilder](../Packages/com.hm.meshmerge/Editor/HmMeshMergeBuilder.cs) | Build、Validate；组织完整合并流程，处理材质绑定、资产保存和结果发布 |
| [HmMeshMergeParameterWriter](../Packages/com.hm.meshmerge/Editor/HmMeshMergeParameterWriter.cs) | BuildInitialTable、RefreshTable、ReadValue、Build、Save；维护参数表并生成浮点 LUT |
| [HmMeshMergeTextureArrayImporter](../Packages/com.hm.meshmerge/Editor/HmMeshMergeTextureArrayImporter.cs) | OnImportAsset、ValidateTextures；登记来源依赖并生成持久化 Texture2DArray，当前导入器版本为 3 |
| [HmMeshMergeShaderWriter](../Packages/com.hm.meshmerge/Editor/HmMeshMergeShaderWriter.cs) | Write；生成属性、声明、读取函数、显示与阴影/深度 Pass，最后统一换行为 LF |
| [HmMeshMergeShaderPatcher](../Packages/com.hm.meshmerge/Editor/HmMeshMergeShaderPatcher.cs) | Patch；复制来源 Shader 文本并注入接入点（属性、包含与查找纹理声明、索引通道与实例化输入、材质索引插值通道、顶点入口可见性包装），只插入与改名，结构不符时报错 |
| [HmMeshMergeTextureSet](../Packages/com.hm.meshmerge/Editor/HmMeshMergeTextureSet.cs) | 生成阶段传递属性名与 Texture2DArray 引用 |

Editor 程序集只包含 Editor 平台，引用 Runtime。本轮新增 HmMeshMergeShaderPatcher（复制来源 Shader 并注入），仍没有新增程序集、测试或辅助框架；删除了原来只服务于丢弃通道方案的 MeshData 嵌套累积器。

## 四、合并主流程

入口为 Tools/HmMeshMerge/网格合并。

1. **应用窗口编辑**：Window.Execute 调用 ApplyModifiedProperties，确保本次合并读取的是最新配置。
2. **校验基础输入**：Builder.Validate 检查配置位置、来源、Shader、网格通道、拓扑、参数表与贴图要求。
3. **首次初始化参数表**：参数表为空时，BuildInitialTable 按源材质差异填表，再次执行完整校验。
4. **生成纹理数组**：BuildTextureSets 创建或更新 .hmtexarray 的导入器引用列表，由用户触发的合并流程调用 SaveAndReimport。
5. **检查数组导入结果**：读取导入日志；存在错误即停止，避免把旧产物或失败状态当作成功。
6. **合并几何**：CollectMeshes 按引用去重来源网格，BuildMergedMesh 拼接去重后的顶点与三角形，CopyUvChannels 保留 UV 数据，再写入网格索引。
7. **生成数值 LUT**：ParameterWriter.Build 根据已启用数值行生成纹理，没有需要写入的数值时返回 null。
8. **确定输出 Shader**：ResolveOutputShader 使用用户指定的自定义 Shader，或更新默认生成文件并加载。
9. **构造材质**：BuildMaterial 先检查输出 Shader 是否已有错误，再复制普通属性、绑定数组、LUT 和来源映射表。
10. **保存与发布**：保存 Mesh、LUT、Material，最后回填配置结果并保存资产。
11. **释放临时资源**：成功或失败均通过 finally 清理尚未交给资产库持有的对象。

说明：源码中的导入调用是用户点击合并后执行的业务流程。AI 在开发期间没有主动运行这些入口，也没有启动 Unity、触发编译或运行测试。

### 生成文件清单

命名规则统一为 `{配置名}_{角色}.{扩展名}`：`{配置名}` 是配置文件不带扩展名的名字，全部产物生成在配置资产所在目录；角色名与文件作用一一对应。

| 生成文件 | 类型 | 作用 |
|---|---|---|
| `{配置名}_Mesh.asset` | Mesh | 合并网格：去重后各源网格的局部顶点、法线、切线、顶点色、UV0–UV7，并额外写入网格索引通道 |
| `{配置名}_Material.mat` | Material | 输出材质：绑定生成的纹理数组、参数 LUT、来源映射表；普通属性取第一来源的材质值 |
| `{配置名}_Shader.shader` | Shader | 输出 Shader：默认是生成的 URP 展示与接入模板（Properties、纹理声明、逐属性读取函数、索引判断与 Alpha 裁剪）；勾选「尝试修改来源shader(副本)」时改为来源 Shader 的注入副本 |
| `{配置名}_ParamLut.asset` | Texture2D | 参数 LUT：X 为材质索引、Y 为稳定参数行，存放各来源的数值参数；无启用数值时不生成 |
| `{配置名}_SourceMap.asset` | Texture2D | 来源映射表：宽为来源数、高 1，把来源索引映射到网格索引与材质索引 |
| `{配置名}_Array_{属性名}.hmtexarray` | Texture2DArray | 每个启用且各来源引用不同的二维贴图属性一份数组，层号即材质索引 |
| `{配置名}_Array_{属性名}_{属性行号}.hmtexarray` | Texture2DArray | 属性名去掉下划线后重名时的区分变体，行号取 Shader 属性下标 |
| `{配置名}.asset` | HmMeshMergeAsset | 配置本身：来源列表、参数表、索引通道与输出引用 |

Mesh、Material、Shader、参数 LUT 与来源映射表按路径原地 CopySerialized 保留 GUID；纹理数组按路径替换导入设置。改名前的旧产物（`_Params.asset`、`_Sources.asset`、更早的 `_Params.png`）不自动删除或迁移，重新合并后材质改绑新名字的文件。

## 五、网格合并实现

### 来源与坐标

- 来源索引取配置 Sources 的列表下标；来源是「网格 + 材质」的组合。
- 网格按引用去重：同一个 Mesh 被多个来源引用时只写一份顶点与三角形，网格索引按首次出现的次序从 0 开始。
- 材质按引用去重：同一个 Material 被多个来源引用时只占数值 LUT 的一列与数组的一层，材质索引同样按首次出现的次序从 0 开始。
- 来源映射表把来源索引映射到网格索引与材质索引：宽为来源数、高 1 的 RGBAFloat 贴图，R 为网格索引、G 为材质索引——两个映射装在同一个纹素的通道里，不分成两行（顶点着色器同时需要这两个索引，一次 Load 取回 float4 即可，省一次顶点纹理获取；B、A 留空备用）。Shader 侧用它换算后再判断可见性与取数值。
- 顶点保留每个源 Mesh 的局部坐标，不读取场景 Transform，不做空间排列或位置偏移。
- 每个来源的三角形索引加上此前已累计的顶点数，最终合并为一个子网格。
- 支持一个来源包含多个三角形子网格，但它们统一使用该来源提供的一个材质。

### 通道保留

- 保留已有 Position、Normal、Tangent、Color 和 UV0–UV7 的值。
- UV 按各来源中的最大维度合并；缺失 UV、法线、切线的数据补零，缺失顶点色补白。
- 索引通道必须在所有来源上都未被占用；冲突即报错。
- UV 通道存入 float2 的 x 分量；顶点色通道存入 Color32.r，并在 Shader 中乘 255 解码：两者存的都是网格索引。
- 顶点数超过 65535 时使用 UInt32，否则使用 UInt16。

这不是顶点缓冲区逐字节复制：通道存储格式可能变化，合并后顶点编号也会变化。依赖源顶点 ID 的动画不能仅靠保留 UV/颜色保证兼容。缺失法线时不会自动生成法线。

## 六、参数表与数值 LUT

### 候选收集与差异比较

- 遍历源 Shader 声明的普通材质属性，排除 unity_ 管线内置属性。
- Color、Vector、Float、Range、Integer 按类型读取，不再先转成 Color32。
- 纹理差异按引用比较；二维贴图的 Tiling/Offset 以“属性名 + _ST”的向量候选独立比较。
- 不再因为两个不同属性当前取值相同就把它们当成别名删除。
- 插件无法判断一个声明属性是否真的参与 Shader 计算，普通候选由使用者决定是否启用。

### 稳定行号

- 首次填表：不同项排前并启用，相同项排后并停用。
- RefreshTable：保留已有行号和选择，新增项追加到最大行号之后。
- 源 Shader 中已经不存在的属性停用，旧行仍保留。
- 已有相同项后来变成不同值时，刷新不会擅自改动原来的启用选择，需用户调整。
- 窗口中已分配的属性名只读，避免把已有行号重新解释为另一个参数。
- 显式重排会移除停用项并重新编号；操作前提示复制到自有 Shader 的行号需要同步修改。

### LUT 格式

| 项 | 当前实现 |
|---|---|
| Shader 属性 | _HmMeshMergeParams |
| 保存格式 | RGBAFloat 原生 .asset |
| 宽度 | 来源数量 |
| 高度 | 最大已分配行号 + 1，包含保留空行 |
| 采样设置 | Linear、Point、Clamp、无 mip、无压缩 |
| 空行 | 填零 |
| 没有启用数值 | 不生成 LUT，材质不继续绑定旧 LUT |
| Color | Linear 项目中转换为线性颜色后写入 |
| 其他数值 | 保留原值，不夹到 0–1 |
| Integer | 转为 float 无法精确表示时明确报错 |
| 读取 | HmMeshMergeLoadParam 使用整数坐标 Load |

来源数量受 2048 与当前设备最大纹理尺寸限制；顶点色模式按去重后的网格数另限 256。参数行号也检查上限、重复和合法性。

## 七、纹理数组与导入依赖

### 何时生成数组

- 参数被启用，且各来源的纹理引用不同时，才生成 Texture2DArray。
- 所有来源引用同一纹理时，继续使用普通纹理，不生成重复层数组。
- 未启用的不同参数使用第一来源的普通材质值。
- 不同贴图属性独立保留名称与绑定，不因引用相同就从属性列表删除。
- 启用的不同纹理必须是 Texture2D；不是把任意 Cube、3D 或已有数组再嵌套为二维数组。

### 持久化方式

.hmtexarray 文件内容为空，来源引用按层顺序存放在其 .meta 的 _textures 列表。Importer 创建 Texture2DArray 后逐层、逐 mip 使用 GetPixelData / SetPixelData 写入 CPU 像素数据，再调用 Apply(false, false) 并发布主资产。

此实现明确要求源贴图开启 Read/Write，禁止 Crunch，并保留数组 CPU 数据以供保存；需要计入相应内存开销。没有新增自动开启 Read/Write 或自动改压缩设置的逻辑。

### 校验与依赖规则

- 校验宽高、实际 graphicsFormat（含 sRGB）、mip 数、Filter、Wrap、Aniso 和 mip bias。
- 尺寸不一致只报错；不缩放，不修改源贴图。
- 创建数组后检查实际格式和 mip 数；出现格式回退即失败。
- DependsOnArtifact 只登记 Assets/ 或 Packages/ 下的来源路径。
- Unity 内置贴图虽有特殊路径和 GUID，但不登记为普通导入产物依赖。
- 内置或非资产贴图作为数组来源时明确报错，提示停用该属性或指定实际贴图。
- 校验失败前仍登记已知有效来源的依赖，便于来源修复后重新触发导入。

依赖关系用于跟随源导入产物变化，不代表 Android 输出格式已验收。目标平台上的数组格式、保存后像素内容、体积和内存均需人工确认。

## 八、Shader 与材质实现

### 生成文本

Write 按顺序生成：

1. 来源说明、参数行说明和接入注释。
2. Properties。
3. 纹理、采样器和普通数值声明。
4. 每个普通属性对应的 HmRead / HmSample 函数。
5. 顶点来源选择逻辑。
6. 基础显示函数及 ForwardUnlit、ShadowCaster、DepthOnly。
7. 将 CRLF 和独立 CR 统一为 LF，消除 Windows AppendLine 与多行模板混用产生的换行警告。

unity_ 内置属性交给管线头文件声明和绑定，不再重复生成。专用 Shader 名是「HmMeshMerge/配置名」，只有工程里存在同名配置（同一个配置文件名的多个配置资产）时才按资产路径顺序补 1 起的序号，避免同名 Shader 互相顶替；不用配置 GUID 或时间戳，名字可读、可提交，重新合并也不会改名。

### 数值与贴图读取

- 启用数值的 HmRead 函数直接包含实际 LUT 行号，并返回对应标量或向量。
- 停用数值的函数返回普通材质属性，值来自第一来源。
- HmSample 按属性维度生成采样；转换为二维数组的属性用材质索引选择层，与顶点上的网格索引、来源索引都无关。激活索引只在顶点着色器取一次，换出的材质索引以 nointerpolation 插值给片元，避免片元重复读取实例属性或 m33。
- 数值访问器同样按材质索引取 LUT 行，因此同一网格搭配不同材质时各自的数值与贴图互不串用，重复材质也不会多占列与层。
- 二维贴图采样前应用其 _ST；网格 UV 不被改写。

### 基础显示

- 主贴图、主颜色优先按 MainTexture / MainColor 标记识别，后备为 _BaseMap / _MainTex、_BaseColor / _Color。
- 不再把参数表中第一个启用数值当作颜色。
- 存在 _Cutoff 时示范 Alpha 裁剪；有 _AlphaClip 时受其控制。
- 来源选择和 Alpha 裁剪同时用于显示、阴影和深度；顶点着色器先用 HmMeshMergeLoadSourceMesh 把激活来源换成网格索引，再用 HmMeshMergeIsMeshVisible 判断。
- 阴影代码包含方向光以及点光/聚光灯分支与近裁剪面处理。

模板不还原源 Shader 的完整光照、透明混合、法线解码、动画或专有效果。主贴图、颜色和裁剪参数仍是普通参数表项，没有恢复为独立配置字段。

### 复制来源 Shader（可选）

配置里的 `patchSourceShader` 打开时，ResolveOutputShader 改调 HmMeshMergeShaderPatcher.Patch，输出的 `{配置名}_Shader.shader` 是来源 Shader 的注入副本；关闭时仍走 HmMeshMergeShaderWriter 模板。两条路径都先校验所有来源使用同一个 Shader。源 Shader 文件只被读取，不写回。

- 注入顺序：改写相对 `#include` 为工程内完整路径 → 记录 UsePass 待办 → 注入三个属性 → 仅在整份文件没有实例化变体时给每个 `#pragma vertex` 补 `#pragma multi_compile_instancing` → 在每个 HLSL 块的最后一个 `#include` 之后插入 HmMeshMerge.hlsl 包含与两张查找纹理的 TEXTURE2D 声明 → 顶点输入结构体加 `float2 meshIndex`（顶点色通道用 `float4`）与 `UNITY_VERTEX_INPUT_INSTANCE_ID`（结构体已有该宏时不重复加）→ 插值结构体加 `nointerpolation float materialIndex`（槽位取已用 TEXCOORD 最大值 +1）→ 每个 `#pragma vertex` 入口改名成 `HmMeshMergeSource_<入口名>`，原位置追加包装函数。
- 包装函数：`UNITY_SETUP_INSTANCE_ID`、调用改名后的原函数、用 HmMeshMergeLoadSourceMaterial 换出材质索引写进插值结构体、用 HmMeshMergeLoadSourceMesh 换出网格索引与顶点上的网格索引比较，不一致时把 SV_POSITION 成员写到 `float4(2, 2, 2, 1)`。顶点色通道解码用 HmMeshMergeDecodeColorIndex（读 r），UV 通道用 HmMeshMergeDecodeUvIndex（读 x）。
- 只做插入与改名，不改写数值引用与贴图取样，所以源 Shader 的光照、风动、Alpha 裁剪原样保留；生成文件头部注释列出待办：逐来源不同的数值（改用 HmMeshMergeLoadParam + 行号）、逐来源不同的贴图（改 2DArray 采样）、UsePass 引用的 Pass（注入不到，隐藏来源仍会绘制）、索引通道选顶点色而源 Shader 把顶点色当遮罩。
- 插入位置或目标结构不成立时直接抛错，不产出半成品：来源不是工程内的 `.shader` 资产、没有同时带 POSITION 与 SV_POSITION 的结构体、没有 Properties 块、索引通道语义冲突、顶点输入已有 meshIndex、顶点入口的第一个参数或返回结构体不是注入过通道的那两个。
- 该路径不生成纹理数组（sets 为空），因此跳过贴图规格与数组维度的校验；材质仍绑定参数 LUT 与来源映射表，普通属性按第一来源复制。生成文件每次合并被覆盖，作为产物的副本按 ShaderWriter 的同一路径换行规则输出 LF。

### 材质绑定

- BuildMaterial 读取 ShaderHasError；输出 Shader 已有错误时停止，不继续发布貌似成功的材质。
- CopySourceProperties 按类型逐项复制普通参数，替代 CopyPropertiesFromMaterial 的整体复制。
- 转换为数组的槽位跳过源 2D 纹理，保留其 Tiling/Offset，随后只绑定生成的 Texture2DArray。
- 普通纹理的源维度、目标维度与实际纹理维度须匹配。
- 数组属性必须在目标 Shader 中声明为 2DArray；LUT 必须声明为 2D。

### 激活索引

默认通过材质或实例属性 _MeshMergeIndex 传入。当前模板不承诺 SRP Batcher 兼容。

已有 m33 编码接口继续保留，但已纠正文档：改写后矩阵不再是标准 TRS，索引 0 会使矩阵奇异。只有自行控制变换、逆矩阵和剔除的绘制路径才能使用，不能直接推广到任意 Unity 实例绘制 API。URP 示例已改为默认使用材质属性。

## 九、资产保存与清理

- 配置保存在 Assets 下，生成文件放在配置同目录。
- 已有 Mesh、Material、浮点 LUT 使用 CopySerialized 原地更新，保留已有 GUID。
- 普通数组输出沿用既有命名；属性名去除前导下划线后若发生文件名冲突，则增加区分标识。
- 禁止把当前配置的输出 Mesh 或 Material 同时作为来源，避免覆盖输入。
- 临时资源由创建流程释放；持久化资源交给 AssetDatabase 持有。
- 输出不是文件事务。中途 I/O 或导入失败可能留下部分文件，不能把“最后发布配置引用”解释为全部文件自动回滚。
- 插件正常合并不会自动删除旧 PNG、图集或历史 Shader 副本。

### 本次用户授权的清空操作

在材质文件缺失、残留 .meta 反复导入期间，用户明确要求“清空”。按该指示清理生成结果残留，并把配置中的 _mergedMesh、_material、outputShader 置空；来源列表与参数表保留。

这是一项本次会话中的资源清理操作，不是插件新增的自动清理功能。删除后重新生成资源会取得新 GUID，场景中的旧输出引用需要重新绑定；原地更新保留 GUID 的承诺不适用于先删除再创建。

## 十、问题与处理记录

| 问题 | 已取得的证据 / 原因 | 处理 | 验证状态 |
|---|---|---|---|
| 数值被压到 0–1、细微差异丢失 | 初版先转 Color32 再比较与保存 | 改用原始数值比较和 RGBAFloat LUT | 源码已修改，数值显示待人工验证 |
| 不同属性被误删 | 初版将当前取值相同的属性视为别名 | 属性独立列出，不按相同值删除 | 静态复核完成 |
| 参数行号漂移 | “列出属性”覆盖旧表并重编号 | RefreshTable 保留旧行，新项追加 | 静态复核完成，实际刷新待验证 |
| 动画通道丢失 | 初版仅保留基础 UV，丢弃额外 UV 与颜色 | 保留 UV0–UV7、颜色等已有值 | 静态复核完成，动画待验证 |
| 二次合并破坏引用 | 初版删除再创建 Mesh | 已有资产原地更新 | 场景引用保留待人工验证 |
| 窗口读取旧配置 | 合并入口先于序列化编辑应用 | Execute 先 ApplyModifiedProperties | 源码已修改 |
| 内置贴图导致导入断言与循环 | _MainTex 数组 .meta 引用特殊 GUID 0000000000000000f000000000000000；日志提示内置资源依赖无效 | 过滤非 Assets/Packages 依赖，报出内置来源，导入器版本改为 3 | 修复后尚无完整无错误导入确认 |
| PreprocessAsset 空引用 | 日志先报材质不在 SourceAssetDB、.meta 存在而 .mat 缺失，然后发生 InitPostprocessors / PreprocessAsset 异常 | 按用户确认清空生成残留与配置输出引用 | 已核对清理结果；不把它误写成已定位插件中的空引用语句 |
| 2DArray 赋给 2D 的错误 | 生成 _BaseMap 已声明为 2DArray，但日志更早报 unity_Lightmaps 重复声明、Shader 编译失败 | 排除管线内置属性；Shader 报错时停止；材质按类型复制并跳过数组槽位的源 2D 贴图 | 源码已修改，需重新生成后验证 |
| Shader 混用换行 | 当时生成文件含 301 处 CRLF 与 126 处独立 LF | Write 返回前统一为 LF | C# 语法与差异检查通过，等待用户重新生成确认 |
| HmSlgGame 中生成 Shader 报 Couldn't open include file 'Packages/com.hm.meshmerge/Runtime/HmMeshMerge.hlsl' | HmSlgGame 用 git URL 安装该包，包按当时 package.json 的 name（com.huangmin.meshmerge）落到 Library/PackageCache，可解析的路径只有 Packages/com.huangmin.meshmerge；而生成模板写死的 com.hm.meshmerge 只对应本工程里嵌入目录的物理名 | 模板改为生成时用 PackageInfo.FindForAssembly 解析实际包路径；随后按用户决定把包名改回 com.hm.meshmerge，与目录名统一 | 源码已修改，等待用户在 HmSlgGame 更新包后重新合并验证 |
| 合并后的网格没有去重，同一网格的顶点与三角形重复出现 | 原实现按来源逐个拼接几何；来源是「网格 + 材质」的组合，mesh a、mesh b 与 material a、material b 交叉组合时 mesh a 与 mesh b 各出现两次 | 按引用对来源网格去重，顶点通道改存网格索引；新增来源映射表 _SourceMap.asset（Shader 侧 _HmMeshMergeSources）记录来源索引到网格索引的映射；参数 LUT 行与纹理数组层号改按材质索引读取（见下一条），避免改索引语义后同一网格的不同材质组合串用数值与贴图 | 源码已修改，等待用户重新合并后验证重复几何与逐来源切换 |
| 材质会随来源组合重复占用数组层与 LUT 列 | 原实现按来源建数组层（层数 = 来源数）、按来源建 LUT 列；来源是「网格 + 材质」的组合，material a 被两个来源引用时，同一张贴图会被放进两层 | 按引用对材质去重（与网格同规则），数组层数与 LUT 宽度改为去重后的材质数；来源映射表增加 G 通道记录材质索引，数值与数组层都按材质索引读取 | 源码已修改，等待用户重新合并后核对数组层数等于材质数、逐来源切换取值正确 |
| 合并后树冠风动消失 | 生成的是 URP 无光照模板，不含源 Shader 的风动逻辑，复制到自有 Shader 需人工逐项接入 | 增加可选开关「尝试修改来源shader(副本)」：复制来源 Shader 文本并注入索引通道、脚本包含与查找纹理声明、材质索引插值通道和每个顶点入口的可见性包装，风动、光照等逻辑原样保留；不自动改写逐来源数值与贴图取样，连同 UsePass 引用的 Pass 一并列成头部注释里的待办 | 源码已修改，等待用户开启开关后重新合并并在 Unity 中编译验证 |
| 生成 Shader 的名字带哈希不便于管理 | Shader 名写作 `HmMeshMerge/{配置名}_{配置GUID}`，Unity 的 Shader 报错里显示的就是这个名字，看起来像生成了带哈希的文件 | 改为 `HmMeshMerge/{配置名}`；只有工程里存在同名配置时才按资产路径顺序补 1 起的序号，避免同名 Shader 互相顶替。不用时间戳，重复合并不会改名，生成文件重新合并后仍是同一份 diff | 源码已修改，等待用户重新合并确认 Shader 名 |

上述“源码已修改”不等于用户已经确认问题消失。特别是材质维度错误的最终运行结果，不能仅凭修复代码推断已通过。

包名与嵌入目录名曾不一致（package.json 为 com.huangmin.meshmerge，目录为 Packages/com.hm.meshmerge，自提交 4dabbfb 起），是本次报错的直接原因之一。按用户决定统一为 com.hm.meshmerge：package.json 的 name 与目录名一致，作者署名 huangmin；HmSlgGame 的依赖名需要同步改为 com.hm.meshmerge。生成模板仍按运行工程解析出的包路径写入，不再依赖两者是否一致。

## 十一、验证记录与下一步

### 已执行

- 完整阅读需求及项目适用规范。
- 对照源码检查输入、输出、参数表、材质绑定和资源生命周期。
- 使用 Roslyn ParseText 进行 C# 语法解析；全包 12 个 C# 文件解析无语法错误，后续相关修复也进行了语法检查。
- 按 HmMeshMergeShaderPatcher 的定位与注入算法在目标 Shader（NSLG/GPU Animation/Vertex Color Wind Lit URP）上逐步复算：结构体区间、属性插入点、包含锚点、索引通道与材质索引槽位、顶点入口签名与包装文本都符合预期。
- 执行 git diff --check。
- 读取用户提供的报错及相关 Editor.log / 导入工作进程日志；核对生成 Shader、资产引用和 URP 本地声明。

### 未执行或尚未确认

- AI 没有执行 Unity 编译、脚本 Reload、导入、测试、运行或构建。
- 用户已经在 Unity 中触发过合并和编译并提供报错；这些反馈不能替代最终版本的完整验收。
- 没有确认最终 Shader 在所有 Pass、实例化变体及目标平台上全部通过。
- 没有测量 draw call、GPU 时间、内存或包体，不能宣称已获得量化性能收益。
- 没有完成 HmSlgGame 接入、批次归并和实例索引传递。
- 没有在 Unity 中编译过注入后的 Shader；复制路径依赖顶点入口的参数与返回结构体就是注入过通道的结构体，多顶点输入结构体或内置 Shader、ShaderGraph 会直接报错，需要真实工程验证。

### 建议的人工验证顺序

1. 等待脚本编译完成，在原配置上刷新属性并确认启用项，再执行合并。
2. 检查 Console 首条错误，确认生成 Shader 无重复声明、维度和混合换行问题。
3. 使用普通 MeshRenderer 切换 _MeshMergeIndex，核对每个来源显示正确；同时检查合并 Mesh 的顶点数等于去重后各网格顶点数之和、数组层数与 LUT 宽度等于去重后的材质数，来源交叉组合时都不再重复。
4. 检查颜色、负数、大于 1 的数值、ST，以及参数停用、追加和显式重排。
5. 检查 Alpha 裁剪在显示、阴影与深度中的一致性。
6. 修改来源后再次合并，确认未删除重建的 Mesh 与 Material 引用保持。
7. 重开项目检查数组像素仍存在，再进行 Android 实际格式、包体和运行验证。
8. 勾选「尝试修改来源shader(副本)」再合并一次，确认注入副本无编译错误、风动等自有逻辑保留、被隐藏来源不投影，并逐条处理生成文件头部列出的待办。

**代码未编译，由用户人工编译验证。**

## 十二、相关文档

- [插件使用说明](../Packages/com.hm.meshmerge/README.md)
- [当前设计说明](../Packages/com.hm.meshmerge/Documentation~/Design.md)
- [变更记录](../Packages/com.hm.meshmerge/CHANGELOG.md)
- [URP 示例说明](../Packages/com.hm.meshmerge/Samples~/UrpCutout/README.md)

本纪要用于记录本轮需求、实现与联调过程；后续继续开发时，应同时核对当前源码和最新用户决定。
