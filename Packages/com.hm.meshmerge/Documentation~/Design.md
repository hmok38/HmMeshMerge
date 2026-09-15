# HmMeshMerge 插件设计文档

> 本文档整理自在 HmSlgGame 项目中的方案讨论，用于在新 Unity 项目中创建 HmMeshMerge UPM 包。
> 在新项目会话中请先完整读取本文档，再按《Unity系统设计与编码规范》（用户级）、公司级 `CodeStyleGuide` / `CodeStructureGuide` 实施。
> 文档中的“已确认决策”是用户明确拍板的，不要自行改动；带“建议”字样的可在实施时复核调整。

## 1. 插件目标与边界

**目标**：把 N 个共享同一 shader 的模型合并成 1 个 Mesh + 1 个材质。每个顶点带“来源索引”，绘制时按索引只显示其中一个来源。用于把“多品种、每品种少量实例”的绘制合批，降低 draw call。

**边界**：

- 做：合并（几何 + 外观 + 索引属性）、配套 shader 片段、索引传递的运行时辅助、合并结果的描述资产。
- 不做：不提供渲染器、不做批次管理/剔除/实例化绘制、不绑定 GPUInstance / SRP Batcher / 任何具体合批技术、不做动画烘焙（但索引属性可供使用者自己的动画数据选择使用）。
- 不假设宿主项目的资源系统、渲染管线或目录结构。

## 2. 已确认决策（用户拍板）

| 项 | 决策 |
|---|---|
| 分发形态 | UPM Package，在新 Unity 项目中创建 |
| 包名 | `com.hm.meshmerge` |
| 程序集 / 命名空间 | Runtime：`HmMeshMerge`；Editor：`HmMeshMergeEditor`（单层 PascalCase，与程序集同名） |
| 图集 tile 尺寸 | **统一尺寸，作为约束要求遵守**：合并时校验所有源 base 贴图尺寸一致，不一致直接报错列出不符项，不静默缩放 |
| 索引通道 | **默认顶点色**（COLOR），并提供其他通道选择 |
| 外观参数通道 | **默认 UV1**，提供选择，且不允许与索引通道冲突 |

## 3. 数据契约

这是插件与使用者之间唯一的固定约定。通道可配置，但“生成侧写入的通道”与“渲染侧 shader 读取的通道”必须一致（顶点属性是编译期绑定的）。

| 载体 | 内容 | 写入方 |
|---|---|---|
| TEXCOORD0 | UV（合并时重映射到图集区域） | 合并工具 |
| 索引通道 | 来源索引，0..N-1 整数 | 合并工具 |
| 外观通道 | (tint.r, tint.g, tint.b, cutoff) 共 4 分量 | 合并工具 |
| 材质 | 图集贴图 + 插件参考 shader（或使用者自己的 shader） | 合并工具 |

**索引通道（默认顶点色）**

- 顶点色为 8 位/通道，用其中一个分量存索引：写入 `(index)/255`，shader 侧解码 `round(color.r * 255.0)`。
- 因此默认通道下子树数量上限为 256。
- 可选改为任意空闲 UV 通道：存储为 float，精度不受 256 限制，解码 `round(uv.x)`。
- 风险提示：顶点色是 8 位量化数据，若在目标项目实测发现数值不稳定（色彩空间/平台差异），切换为 UV 通道即可，工具支持。

**外观参数通道（默认 UV1，必须 4 分量）**

- `mesh.SetUVs(1, List<Vector4>)` 使 UV1 为 4 分量；若源 mesh 的 UV1 已被占用（如光照贴图 UV），必须改选其他空闲通道。
- 已核验：本项目树模型只用了 Position/Normal/Tangent/UV0，顶点色与 UV1 均空闲。

**外观参数来自哪里**：合并工具从每个源的材质读取，属性名可配置（默认 `_BaseMap` 兼容 `_MainTex`、`_BaseColor` 兼容 `_Color`、`_Cutoff`）。这是“插件能读取其他参数吗”的写入侧答案——能，按配置的属性名从源材质读取。

**渲染侧读取**：插件参考 shader 的顶点着色器直接读取这些顶点属性，在片元里把 tint 乘进采样结果、用 cutoff 做 `clip`。使用者也把自己的 shader 按同样的通道读取即可——顶点属性在 VS 里是直接可读的。

## 4. 生成侧设计（Editor）

**输入**：N 组（Mesh + Material），每组一个显示名。

**流程**（入口平铺调用，细节进私有方法）：

1. 校验：所有源共享同一 shader；源 UV 在 [0,1]（图集不支持 tiling/wrap）；**所有 base 贴图尺寸一致**；目标顶点通道空闲；顶点总数在索引格式允许范围内。
2. 合并几何：顶点拼接、索引重映射、写入来源索引与外观参数。**不做位置偏移**，合并后包围盒保持正常尺度，矩形筛选与剔除不受影响。
3. 图集打包：统一 tile 尺寸（校验保证一致），tile 间加 padding 并做边缘像素扩展（否则 cutout 树叶在 mip 下会串色），UV 重映射 `uv' = (uv * tileSize + tileOrigin) / atlasSize`。
4. 生成资产：合并 Mesh、图集 Texture、材质、描述资产。

**输出资产**：

- 合并 Mesh（`IndexFormat.UInt16` 优先，超限用 UInt32 并提示）
- 图集 Texture（普通 Texture2D 资产，可用平台压缩）
- 材质（引用图集与参考 shader）
- `HmMeshMergeAsset`（ScriptableObject）：合并 Mesh / 图集 / 材质引用、子树清单（索引 → 显示名 / 图集区域）、本次使用的通道配置。

**类草案**（实施时按规范复核，不制造碎片、不做未确认的扩展）：

- `HmMeshMergeWindow`（EditorWindow）：选择源、配置通道与图集、执行、查看结果。只做交互，不承载算法。
- `HmMeshMergeBuilder`：合并主体。几何合并、索引写入、图集打包、资产写入按步骤在入口平铺调用；细节进私有方法。
- `HmMeshMergeSource`：一个源项（Mesh、Material、显示名）的数据类型。
- `HmMeshMergeAsset`：见上。

## 5. 运行侧设计（Runtime）

**核心逻辑**（顶点着色器）：

```hlsl
if (abs(sourceIndex - activeIndex) > 0.5)
{
    output.positionCS = float4(0, 0, 0, 0);   // w=0，整个三角形被裁剪，不进光栅化
    return output;
}
```

合并时保证一个三角形的三个顶点属于同一来源，因此顶点级判断等价于三角形级判断。

**激活索引的两条传递路径**（由使用者的渲染路径选择，shader 用宏切换）：

- 路径 A（标准）：`UNITY_DEFINE_INSTANCED_PROP(float, _MeshMergeIndex)` + `UNITY_ACCESS_INSTANCED_PROP`。普通 MeshRenderer 走材质/MPB 设值（per-draw），`DrawMeshInstanced` 走 MPB 数组（逐实例），同一份 shader 代码覆盖两种场景。
- 路径 B（矩阵分量）：把索引写进实例矩阵的 `m33`（标准 TRS 下恒为 1，可安全占用），适用于任何只传 `Matrix4x4` 的绘制路径。代价：shader 必须手写位置变换 `mul((float3x3)unity_ObjectToWorld, positionOS) + unity_ObjectToWorld._m03_m13_m23`，不能用 `TransformObjectToWorld`（w 分量会被索引污染）。

**Runtime 类草案**：

- `HmMeshMergeAsset`（ScriptableObject，见上）
- `HmMeshMergeIndex`（静态类）：矩阵分量的写入/读取辅助，保证编码解码与 shader 一致。
- `HmMeshMerge.hlsl`：索引读取 + 隐藏判断 + 外观参数读取的 shader 片段，供参考 shader include，也供使用者拷进自己的 shader。
- 一个参考 shader：以 URP 为目标（宿主项目用 URP），展示如何接入索引判断与图集 + tint/cutoff。其他管线由使用者自行移植片段。

目标管线若与实际项目不同，参考 shader 需要替换，但 hlsl 片段与数据契约保持不变。

## 6. 校验规则与约束

- 所有源共享同一 shader（不同 shader 的模型不能合并）。
- 源 UV 必须在 [0,1]；有 tiling/wrap 的模型报错，不静默修改。材质自身的 ST 会烘焙进 UV 后参与重映射。
- 图集 tile 尺寸必须统一，不一致时报错。
- 索引通道与外观通道不得相同；通道被源数据占用时报错。
- 顶点色作索引通道时子树数 ≤ 256。
- 外观参数固定为 4 分量（tint.rgb + cutoff），通道需要可容纳 4 分量。
- 改外观参数需要重新执行合并；工具应支持从既有 `HmMeshMergeAsset` 仅重烘焙，不必重选源。

## 7. 验收场景

1. **通用场景**：在任意空项目中合并 2 个不同材质（不同贴图 + 不同 tint）的模型，生成 1 个 Mesh + 1 个材质；用 MeshRenderer 逐个切换索引，画面上正确显示对应模型，其余不出现。
2. **索引传递**：路径 A（MPB）与路径 B（矩阵分量）分别验证同一合并资产可用。
3. **边界**：贴图尺寸不一致 / UV 越界 / 通道占用 / shader 不一致时给出明确报错，不产出损坏资产。

Unity 编译与运行验证由用户手动完成，AI 不自动编译、不新增测试。

## 8. 后续阶段：HmSlgGame 接入（不属于本包范围）

宿主项目的 `Assets/NSLGDemoTerrain/GPUInstanceThree` 是第一个使用方，接入要点（另立任务）：

- 现状：Config 定义 81 种树，36 张 Forest_Node 布局实际使用 23 种（24 个 mesh+材质组合），1,003,802 个实例；实际用到 10 个 Mesh（顶点 40~141）、32 个材质、9 张生效的 diffuse 贴图。
- 接入形态：把 23 个渲染对象归并为 1 个（合并 mesh + 单材质），每个实例带来源索引；用路径 B（矩阵分量）传索引，因为 `GPUInstanceRenderer` 只上传 `Matrix4x4[]`。
- 注意：页与批次按 renderObject 组织，只把 23 个 renderObject 指向同一合并 mesh 不会减少 dc，必须真正归并成一个渲染对象。
- 实例的 `boundsXZ` 要按原树的包围盒写，不能用合并 mesh 的并集包围盒。
- 全图 LOD 会隐藏全部树，峰值可见约 100~200 棵，顶点放大代价可忽略；批量上限 `MaxInstancesPerDraw = 511` 在每树种可见实例远小于 511 时不是瓶颈。

## 9. 实施提醒

- 先建包骨架（package.json、Runtime/、Editor/），再实现合并工具，最后做参考 shader 与文档。
- 遵守公司结构规范的单层 PascalCase 程序集/命名空间、Runtime 不引用 UnityEditor、一个文件一个顶层类型。
- 不自行新增测试、不自行执行 Unity 编译；完成后由用户在 Unity 中手动验证。
- 插件自己的设计文档随包维护（`Documentation~/` 或包内说明），本文件可作为起点。
