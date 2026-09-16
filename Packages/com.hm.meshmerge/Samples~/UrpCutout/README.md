# URP Cutout 参考 Shader

示例默认用 _MeshMergeIndex 材质/实例属性选择来源，用 UV3（TEXCOORD3）读取顶点来源索引。普通 MeshRenderer 可直接在材质面板中切换索引。

_BaseMap 必须绑定 Texture2DArray。LUT 的 **第 0 行作为 RGBA 颜色、第 1 行作为 Cutoff** 仅为示例约定；必须按实际配置表修改两处 Load 行号，并启用对应参数。本示例不会自动知道配置表，也不会自动升级普通 2D 贴图为数组；请优先复制工具为实际资产生成的读取函数。

ForwardLit、ShadowCaster、DepthOnly 都处理来源选择和 Alpha 裁剪。示例保留简单 URP 光照，工具生成的模板则为无光照。

通过 Package Manager 导入示例或复制 Shader 到 Assets 后，可把配置中的“输出 Shader”指向它。改动索引通道时，必须同时修改 Attributes 的语义与解码函数。

示例里的 `#include "Packages/com.hm.meshmerge/Runtime/HmMeshMerge.hlsl"` 是写死的静态路径，必须与包名一致；改包名后要手工修改。工具生成的模板不需要关心这一点，它按工具所在工程的实际包路径写入。

矩阵 m33 编码只用于已适配的自定义绘制，不是普通 TRS；不要把改写矩阵直接传入未经适配的绘制、逆矩阵或剔除流程。实例属性路径也不承诺 SRP Batcher 兼容。

代码未编译，由用户人工编译验证。
