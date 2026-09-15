# HmMeshMerge 设计说明

当前契约以用户需求定稿及本次确认的“尺寸不一致只报错，用户手工统一”为准。旧图集、flipbook、顶点外观参数和默认顶点色方案已作废。

## 主链与职责

入口 HmMeshMergeWindow.Execute 先把 SerializedObject 的编辑应用到配置并写盘，再调用 HmMeshMergeBuilder.Build。窗口内每次提交编辑同样即时写盘，关闭窗口时再提交一次。

| 类型 | 职责及调用关系 |
|---|---|
| HmMeshMergeAsset | 配置和导出结果的唯一载体，运行时按只读配置使用 |
| HmMeshMergeSource / ParameterEntry / Channel | 来源、稳定行、索引通道的数据契约 |
| HmMeshMergeWindow | 新建/选择配置、序列化编辑（提交即写盘）、显式重排确认、显示消息并同步 Console |
| HmMeshMergeBuilder | 校验 → 生成数组 → 合并几何 → 生成 LUT → 生成/选择 Shader → 绑定并保存输出 |
| HmMeshMergeParameterWriter | 读取和比较原始值、刷新稳定参数表、生成及保存浮点 LUT |
| HmMeshMergeTextureArrayImporter | 根据源贴图导入产物生成每层像素都有 CPU 数据的 Texture2DArray |
| HmMeshMergeTextureSet | 生成阶段的属性名和数组引用 |
| HmMeshMergeShaderWriter | 逐属性读取函数、URP 无光照展示及三个 Pass |
| HmMeshMerge.hlsl / HmMeshMergeIndex | 来源索引判断、LUT 读取；自定义路径的可选矩阵索引接口 |

Runtime 编入 HmMeshMerge，不引用 UnityEditor。Editor 编入仅 Editor 平台的 HmMeshMergeEditor，只依赖 Runtime。没有新增程序集或辅助框架。

## 网格

- 保持各源的局部坐标，把三角形索引加顶点偏移后拼接成一个子网格；不会复制源对象的 Transform。
- 默认在空闲 UV3 的 x 分量写整数来源索引；可选 UV1–UV7 或顶点色。顶点色索引为 byte，Shader 解码 round(r * 255)，最多 256 个来源。
- 保留已有法线、切线、颜色及全部 UV 的值。UV 采用来源中的最大维度；缺少属性的来源补零，颜色补白。不是顶点缓冲区逐字节复制，不承诺原始属性格式和顶点 ID 不变。
- 选中索引通道被任意来源占用即报错。蒙皮、BlendShape 与非三角形拓扑报错，避免静默丢失数据。多子网格使用该来源指定的同一材质。
- 超过 65535 顶点时用 UInt32；目标平台能否绘制由人工验证。
- 所有三角形的顶点来自同一来源；三个 Pass 都按来源隐藏，使用确定在裁剪空间外的位置。

## 参数表与 LUT

首次列出 Shader 声明的材质属性（排除由管线管理的 unity_ 内置属性），不按相同值删除“别名”。可比较差异的属性排前并启用，相同项排后停用。二维贴图的 Tiling/Offset 作为属性名加 _ST 的向量候选参与比较，网格 UV 不烘焙 ST。

刷新保留已分配的属性、行号和开关，新增项只追加；消失的属性停用但保留行。刷新不会自动改变已有的用户选择。只有显式重排才删除停用行并重新编号，窗口先提示影响。行号无效、重复、越界，启用项重复或不存在，合并都会报错。

数值采用 RGBAFloat 原生 .asset，线性、Point、Clamp、无 mip、无压缩。宽度是来源数，高度覆盖所有已分配行，停用行填零。无启用数值则不生成 LUT，也不绑定旧 LUT。Color 在 Linear 项目中转成线性值；Vector、Float、Range 保留原始数值；Integer 不能被 float 精确表示时报错。

读取函数 HmMeshMergeLoadParam(texture, sourceIndex, row) 使用整数坐标 Load。启用的数值访问器读取实际行，其他访问器返回普通材质属性；停用项明确使用第一来源的值。

## 纹理数组

只有启用且引用不同的二维贴图属性生成数组。不同属性分别绑定，不能因当前引用相同就抹去一个属性。引用相同的纹理保持原始维度和原始材质引用。

.hmtexarray 源文件为空，贴图引用按顺序存在其 .meta 导入设置中。Importer 对有效来源先声明 DependsOnArtifact，随后校验和生成；错误时也保留已知依赖，便于源贴图修复后再次导入。

校验实际宽高、graphicsFormat（包含 sRGB）、mip 层数和采样设置；不以压缩设置名称替代实际格式。尺寸不一致只报错，不修改源贴图。来源需手动开启 Read/Write，Crunch 需关闭。数组按实际 TextureFormat、mip 数与线性标识创建；发生格式回退即报错。逐层逐 mip 的 GetPixelData / SetPixelData 写入 CPU 数据后 Apply，避免只复制 GPU 内容却缺少可保存像素。

源贴图和输出数组保留 CPU 数据以满足导入与保存，需要计入内存开销。切换目标平台后的格式由实际源导入产物决定；未出包验证前不声称 Android 一定是 ASTC。

API 依据：[SetPixelData](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Texture2DArray.SetPixelData.html)、[Texture2DArray 构造器](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Texture2DArray-ctor.html)。

## Shader 与集成边界

生成器不改源 Shader。每个普通材质属性都有 Properties / 声明 / 具体读取函数；unity_ 内置属性交给管线声明和绑定。二维贴图函数应用自己的 ST。主贴图与主颜色按 Shader 标记或常见名称作展示，绝不把第一个数值行当作颜色。Forward、ShadowCaster、DepthOnly 统一 Alpha 裁剪，阴影覆盖方向光和点/聚光灯路径。

默认激活索引来自 _MeshMergeIndex 材质/实例属性。模板基于 URP，不承诺 SRP Batcher 兼容；其他管线要移植对应宏和 Pass。专用 Shader 由使用者复制并接入自己的算法，而非任意 Shader 效果的转换器。

m33 编码作为已存在的公开接口保留，仅用于完全受控的自定义绘制：编码后不再是标准仿射矩阵，索引 0 时矩阵奇异，逆矩阵与剔除不能继续依赖普通 TRS 假设。普通验证使用材质属性路径。

不提供渲染器、批次管理、实例剔除、动画烘焙或 HmSlgGame 接入。减少 draw call 仍需调用方把同 Mesh、同 Material 的实例实际组织到同一批次。

## 资产、失败与迁移

- 输出位于配置资产目录。Mesh、Material、浮点 LUT 原地 CopySerialized，保持 GUID；Shader 名含配置 GUID，避免不同目录同名配置的 Shader 名碰撞。
- 普通数组路径沿用旧命名；仅属性文件名冲突时增加标识。源 Shader 的插件保留名冲突在生成前报错。
- 自定义输出 Shader 的数组维度与 LUT 属性必须满足契约，否则报错。输出 Shader 已有编译错误时，停止创建材质。普通参数按类型逐项复制，转换为数组的槽位跳过源 2D 贴图绑定，只接收生成数组。
- Builder 对非持久化的临时 Mesh、Texture、Material 使用 finally 释放；Importer 失败也释放临时数组。
- 配置结果引用在生成完成后发布，但资产导出不是文件事务；I/O 或导入中途失败可能留下部分新文件或已更新文件。修复原因后重新合并。
- 旧 _Params.png 和历史图集、Shader 副本不自动删除。重新合并绑定新的浮点 LUT；旧生成资产不会因改源码自动更新。

## 人工验证

检查来源索引切换、不同/相同参数、负数和大于 1 的数值、HDR 颜色、ST、UV/颜色动画数据、阴影和深度裁剪；再次合并后检查场景/Prefab 中的 Mesh 与 Material 引用；关闭重开编辑器后检查数组像素，以及目标平台包内格式。

仅进行了静态检查；未启动 Unity、导入、执行测试或构建。代码未编译，由用户人工编译验证。
