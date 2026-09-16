# HmMeshMerge 设计说明

当前契约以用户需求定稿及本次确认的“尺寸不一致只报错并给出建议尺寸，用户手工统一”为准。旧图集、flipbook、顶点外观参数和默认顶点色方案已作废。

## 主链与职责

入口 HmMeshMergeWindow.Execute 先把 SerializedObject 的编辑应用到配置并写盘，再调用 HmMeshMergeBuilder.Build。窗口内每次提交编辑同样即时写盘，关闭窗口时再提交一次。

| 类型 | 职责及调用关系 |
|---|---|
| HmMeshMergeAsset | 配置和导出结果的唯一载体，运行时按只读配置使用 |
| HmMeshMergeSource / ParameterEntry / Channel | 来源、稳定行、索引通道的数据契约 |
| HmMeshMergeWindow | 新建/选择配置、序列化编辑（提交即写盘）、显式重排确认、显示消息并同步 Console |
| HmMeshMergeBuilder | 校验（贴图不一致时逐层输出可定位的日志）→ 按材质去重生成数组 → 按网格去重合并几何 → 生成 LUT 与来源映射表 → 生成/选择 Shader → 绑定并保存输出 |
| HmMeshMergeParameterWriter | 读取和比较原始值、刷新稳定参数表、生成及保存浮点 LUT |
| HmMeshMergeTextureArrayImporter | 根据源贴图导入产物生成每层像素都有 CPU 数据的 Texture2DArray |
| HmMeshMergeTextureSet | 生成阶段的属性名和数组引用 |
| HmMeshMergeShaderWriter | 逐属性读取函数、URP 无光照展示及三个 Pass |
| HmMeshMergeShaderPatcher | 复制来源 Shader 文本并注入接入点：三个属性、脚本包含与查找纹理声明、索引通道与实例化输入、材质索引插值通道、每个顶点入口的可见性包装；只插入与改名，不改写数值引用与贴图取样 |
| HmMeshMerge.hlsl / HmMeshMergeIndex | 网格可见性判断、来源映射表与 LUT 读取；自定义路径的可选矩阵索引接口 |

Runtime 编入 HmMeshMerge，不引用 UnityEditor。Editor 编入仅 Editor 平台的 HmMeshMergeEditor，只依赖 Runtime。没有新增程序集或辅助框架。

## 网格

- 按引用对来源网格与材质去重：顺序都按在来源列表中首次出现。同一个网格只保留一份顶点与三角形，下标就是顶点上写的网格索引；同一个材质只占 LUT 的一列与数组的一层。全部来源共用一个子网格；不会复制源对象的 Transform，各源保持自己的局部坐标。
- 来源映射表（_SourceMap.asset）宽为来源数、高 1，RGBAFloat、线性、Point、Clamp，X 为来源索引，R 为该来源使用的网格索引，G 为该来源使用的材质索引。两个映射装在同一个纹素的 R/G 通道里，不分成两行：顶点着色器同时需要这两个索引，一次 Load 取回 float4 即可，分成两行就要两次顶点纹理获取；B、A 保留恒为零，供以后扩展。激活来源到网格、材质索引的换算由 Shader 用 HmMeshMergeLoadSourceMesh / HmMeshMergeLoadSourceMaterial 完成。
- 默认在空闲 UV3 的 x 分量写整数网格索引；可选 UV1–UV7 或顶点色。顶点色索引为 byte，Shader 解码 round(r * 255)，上限是 256 个网格（按去重后的网格数校验，不是来源数）。
- 保留已有法线、切线、颜色及全部 UV 的值。UV 采用来源中的最大维度；缺少属性的来源补零，颜色补白。不是顶点缓冲区逐字节复制，不承诺原始属性格式和顶点 ID 不变。
- 选中索引通道被任意来源占用即报错。蒙皮、BlendShape 与非三角形拓扑报错，避免静默丢失数据。多子网格使用该来源指定的同一材质。
- 超过 65535 顶点时用 UInt32；目标平台能否绘制由人工验证。
- 所有三角形的顶点来自同一网格；三个 Pass 都先用来源映射表换出网格索引再判断，被隐藏时使用确定在裁剪空间外的位置。

## 参数表与 LUT

首次列出 Shader 声明的材质属性（排除由管线管理的 unity_ 内置属性），不按相同值删除“别名”。可比较差异的属性排前并启用，相同项排后停用。二维贴图的 Tiling/Offset 作为属性名加 _ST 的向量候选参与比较，网格 UV 不烘焙 ST。

刷新保留已分配的属性、行号和开关，新增项只追加；消失的属性停用但保留行。刷新不会自动改变已有的用户选择。只有显式重排才删除停用行并重新编号，窗口先提示影响。行号无效、重复、越界，启用项重复或不存在，合并都会报错。

数值采用 RGBAFloat 原生 .asset，线性、Point、Clamp、无 mip、无压缩。宽度是按引用去重后的材质数（同一材质只占一列），高度覆盖所有已分配行，停用行填零。无启用数值则不生成 LUT，也不绑定旧 LUT。Color 在 Linear 项目中转成线性值；Vector、Float、Range 保留原始数值；Integer 不能被 float 精确表示时报错。

读取函数 HmMeshMergeLoadParam(texture, materialIndex, row) 使用整数坐标 Load，materialIndex 是顶点着色器用来源映射表从激活来源索引换出的材质索引：既不是顶点上的网格索引，也不是 _MeshMergeIndex 的值，因此同一网格的不同材质组合各自取到自己的数值。启用的数值访问器读取实际行，其他访问器返回普通材质属性；停用项明确使用第一来源的值。

## 纹理数组

只有启用且引用不同的二维贴图属性生成数组。层按去重后的材质排列：同一个材质只占一层，因此材质被多个来源复用时不会把同一张贴图重复放进数组。不同属性分别绑定，不能因当前引用相同就抹去一个属性。引用相同的纹理保持原始维度和原始材质引用。

.hmtexarray 源文件为空，贴图引用按顺序存在其 .meta 导入设置中。Importer 对有效来源先声明 DependsOnArtifact，随后校验和生成；错误时也保留已知依赖，便于源贴图修复后再次导入。

校验实际宽高、graphicsFormat（包含 sRGB）、mip 层数和采样设置；不以压缩设置名称替代实际格式。任一参数不一致只在点击“执行合并”时报出明细：按层贴图逐条列出参数，需要修改的层在原条目内直接跟“建议”，引用同一组贴图的属性（如 _BaseMap 与 _MainTex 指向同一批贴图）并列属性名、共用一条报告；数组资产重导（改源贴图、切平台、打开工程）时只记录一行“未生成数组”的提示，不在非合并时机刷出长清单。需要修改的贴图在校验结束时按贴图合并建议并去重，每张贴图只出一条携带贴图对象的日志（不含属性名与层号），单击该条即可在 Project 中定位对应贴图；校验失败的汇总仍只输出一次，异常本身不再重复打印。插件不缩放、不改源导入设置，由用户手动统一。来源需手动开启 Read/Write，Crunch 需关闭。数组按实际 TextureFormat、mip 数与线性标识创建；发生格式回退即报错。逐层逐 mip 的 GetPixelData / SetPixelData 写入 CPU 数据后 Apply，避免只复制 GPU 内容却缺少可保存像素。

源贴图和输出数组保留 CPU 数据以满足导入与保存，需要计入内存开销。切换目标平台后的格式由实际源导入产物决定；未出包验证前不声称 Android 一定是 ASTC。

API 依据：[SetPixelData](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Texture2DArray.SetPixelData.html)、[Texture2DArray 构造器](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Texture2DArray-ctor.html)。

## Shader 与集成边界

生成器不修改源 Shader 文件：模板路径完全不读源文本，复制路径也只读取文本、把改好的副本写成输出 Shader。每个普通材质属性都有 Properties / 声明 / 具体读取函数；unity_ 内置属性交给管线声明和绑定。二维贴图函数应用自己的 ST。主贴图与主颜色按 Shader 标记或常见名称作展示，绝不把第一个数值行当作颜色。Forward、ShadowCaster、DepthOnly 统一 Alpha 裁剪，阴影覆盖方向光和点/聚光灯路径。

默认激活索引来自 _MeshMergeIndex 材质/实例属性。模板基于 URP，不承诺 SRP Batcher 兼容；其他管线要移植对应宏和 Pass。专用 Shader 由使用者复制并接入自己的算法，而非任意 Shader 效果的转换器。

顶点着色器用来源映射表把激活来源索引换成网格索引与材质索引：网格索引与顶点上的网格索引比较决定可见性，材质索引作为 nointerpolation 插值交给片元。纹理数组层号与参数行都按该材质索引读取；激活索引只在顶点着色器取一次，片元不重新读取实例属性或矩阵 m33（两者只在顶点阶段对应本次绘制）。生成模板要求输出 Shader 声明 _HmMeshMergeSources（来源映射表，R 网格索引、G 材质索引）与 _HmMeshMergeParams（参数 LUT）两个 2D 属性。

生成模板对 HmMeshMerge.hlsl 的包含路径在生成时按当前工程解析出的包路径写入，因此嵌入包（目录名）与 git、本地安装（包名）都能编译；示例 Shader 是静态文件，只能写死当前包名。

勾选「尝试修改来源shader(副本)」时走 HmMeshMergeShaderPatcher：读取来源 Shader 文本，注入三个属性、脚本包含与两张查找纹理声明、顶点输入结构体的索引通道与实例化输入、插值结构体的 nointerpolation 材质索引通道，并把每个 #pragma vertex 入口改名后追加可见性包装；相对路径的 #include 改写成工程内的完整路径。只做插入与改名，源 Shader 的光照、风动、Alpha 裁剪等逻辑原样保留，也因此不承担任意 Shader 效果的转换责任：逐来源不同的数值引用与贴图取样不自动改写，只写入生成文件头部注释的待办（数值改用 HmMeshMergeLoadParam、贴图改用 2DArray 采样）；UsePass 引用的 Pass 注入不到，同样记入待办并提示隐藏来源仍会绘制。该路径不生成纹理数组，贴图规格与数组要求的校验随之跳过，材质仍绑定参数 LUT 与来源映射表。结构上定位不到必需元素时抛错而不是产出半成品：没有同时带 POSITION 与 SV_POSITION 的结构体、没有 Properties 块、索引通道与源 Shader 已用语义冲突、顶点入口的参数或返回结构体不是注入过通道的那两个。两条路径都先校验所有来源使用同一个 Shader。

m33 编码作为已存在的公开接口保留，仅用于完全受控的自定义绘制：编码后不再是标准仿射矩阵，索引 0 时矩阵奇异，逆矩阵与剔除不能继续依赖普通 TRS 假设。普通验证使用材质属性路径。

不提供渲染器、批次管理、实例剔除、动画烘焙或 HmSlgGame 接入。减少 draw call 仍需调用方把同 Mesh、同 Material 的实例实际组织到同一批次。

## 资产、失败与迁移

- 输出位于配置资产目录，命名规则为「配置名_角色.扩展名」：_Mesh.asset（合并网格）、_Material.mat（输出材质）、_Shader.shader（生成的着色器）、_ParamLut.asset（参数 LUT）、_SourceMap.asset（来源映射表）、_Array_{属性名}.hmtexarray（纹理数组，属性名去掉下划线后重名时追加属性行号）。Mesh、Material、浮点 LUT、来源映射表原地 CopySerialized，保持 GUID。Shader 在文件里的名字是「HmMeshMerge/配置名」，工程里有同名配置时按资产路径顺序补 1 起的序号，避免同名 Shader 互相顶替（见下）。
- 普通数组路径沿用旧命名；仅属性文件名冲突时增加标识。源 Shader 的插件保留名冲突在生成前报错。
- 自定义输出 Shader 的数组维度、LUT 属性和来源映射表属性必须满足契约，否则报错。输出 Shader 已有编译错误时，停止创建材质。普通参数按类型逐项复制，转换为数组的槽位跳过源 2D 贴图绑定，只接收生成数组。
- 复制来源 Shader 时输出 Shader 是来源的注入副本，属性与源一致，因此普通参数的类型校验恒等通过，也不生成数组；来源必须是工程内的 .shader 资产，内置 Shader 与 ShaderGraph 报错。生成文件每次合并都被覆盖，长期修改需另存为自有 Shader。
- Shader 名不用配置 GUID，也不用时间戳：名字里出现哈希既不可读，也会在每次换机器、换配置时变化。只在工程里存在同名配置（同一个配置文件名的多个配置资产）时才补 1 起的序号，序号按资产路径排序分配，因此原样重复合并不会改名，生成的文件重新合并后仍是同一份 diff。
- Builder 对非持久化的临时 Mesh、Texture、Material 使用 finally 释放；Importer 失败也释放临时数组。
- 配置结果引用在生成完成后发布，但资产导出不是文件事务；I/O 或导入中途失败可能留下部分新文件或已更新文件。修复原因后重新合并。
- 旧 _Params.png、_Params.asset、_Sources.asset 与历史图集、Shader 副本都不自动删除或迁移。重新合并按新名字生成并绑定参数 LUT 与来源映射表；旧生成资产不会因改源码自动更新。

## 人工验证

检查来源索引切换、不同/相同参数、负数和大于 1 的数值、HDR 颜色、ST、UV/颜色动画数据、阴影和深度裁剪；核对同一网格、同一材质被多个来源交叉引用时几何只有一份、数组只为每个材质留一层，且各来源的数值与层不串用。顶点着色器用 Load 读来源映射表（顶点纹理获取），需在目标平台确认可用。再次合并后检查场景/Prefab 中的 Mesh 与 Material 引用；关闭重开编辑器后检查数组像素，以及目标平台包内格式。复制来源 Shader 路径还需确认注入副本能编译、风动等自有逻辑未被破坏、被隐藏来源不投影，并逐条处理头部注释里的待办。

仅进行了静态检查；未启动 Unity、导入、执行测试或构建。代码未编译，由用户人工编译验证。
