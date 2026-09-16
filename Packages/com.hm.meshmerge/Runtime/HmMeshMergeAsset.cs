using System.Collections.Generic;
using UnityEngine;

namespace HmMeshMerge
{
    /// <summary>
    /// 一次网格合并的结果与配方，由合并工具写入，运行时只读。
    /// 源是「网格 + 材质」的组合，列表下标即来源索引；使用方按该下标传入激活索引。
    /// 同一个网格或同一个材质被多个来源引用时都只保留一份：顶点上写的是网格索引，
    /// 数值 LUT 的横轴与纹理数组的层号是材质索引，两者由生成的来源映射表对应。
    /// </summary>
    public sealed class HmMeshMergeAsset : ScriptableObject
    {
        /// <summary>序列化字段名，供编辑工具用 SerializedProperty 查找。</summary>
        public const string SOURCES_FIELD = "_sources";

        /// <summary>序列化字段名，供编辑工具用 SerializedProperty 查找。</summary>
        public const string PARAMETERS_FIELD = "_parameters";

        [SerializeField] private List<HmMeshMergeSource> _sources = new List<HmMeshMergeSource>();
        [SerializeField] private List<HmMeshMergeParameterEntry> _parameters = new List<HmMeshMergeParameterEntry>();
        [SerializeField] private Mesh _mergedMesh;
        [SerializeField] private Material _material;

        /// <summary>
        /// 索引写入通道，默认 UV3（TEXCOORD3）；必须确认所选通道未被任何源网格占用。
        /// </summary>
        public HmMeshMergeChannel indexChannel = HmMeshMergeChannel.TexCoord3;

        /// <summary>输出材质使用的 Shader。工具生成后会自动引用到这里；留空或清掉则重新生成。</summary>
        public Shader outputShader;

        /// <summary>
        /// 开启后复制来源 Shader 并注入合并接入点，保留源 Shader 的光照、风动等自有逻辑；
        /// 关闭时生成无光照模板。两种方式都只注入索引通道与查找纹理，逐来源的数值与贴图差异
        /// 只在开关关闭时由数组与 LUT 承载，开启时按生成文件头部列出的待办手工处理。
        /// </summary>
        public bool patchSourceShader;

        /// <summary>参与合并的源；下标即来源索引。请勿手工改动本列表。</summary>
        public IReadOnlyList<HmMeshMergeSource> Sources => _sources;

        /// <summary>参数表：属性名与它在参数纹理里的行号。行号稳定，移除的参数保留为空行。</summary>
        public IReadOnlyList<HmMeshMergeParameterEntry> Parameters => _parameters;

        /// <summary>合并后的网格；保留源顶点数据、按网格去重，并新增网格索引。</summary>
        public Mesh MergedMesh => _mergedMesh;

        /// <summary>合并后的材质：挂载各贴图属性的合并结果与参数查找纹理。</summary>
        public Material Material => _material;

        /// <summary>来源数量；顶点色作索引通道时受 256 个网格的限制，与来源数无直接关系。</summary>
        public int SourceCount => _sources.Count;

        /// <summary>替换参数表；由工具在首次填表或重排时调用。</summary>
        public void SetParameters(List<HmMeshMergeParameterEntry> parameters)
        {
            _parameters = parameters;
        }

        /// <summary>合并完成后写入结果部分；配置部分（源、参数表、通道）由使用者在窗口里维护。</summary>
        public void SetResult(Mesh mergedMesh, Material material)
        {
            _mergedMesh = mergedMesh;
            _material = material;
        }
    }
}
