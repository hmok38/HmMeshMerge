using System.Collections.Generic;
using UnityEngine;

namespace HmMeshMerge
{
    /// <summary>
    /// 一次网格合并的结果与配方，由合并工具写入，运行时只读。
    /// 源列表的下标与合并网格顶点上的来源索引一致；使用方按该下标传入激活索引。
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
        /// 索引写入的通道；也是新建资产时的默认值。默认用 UV3：它避开了 UV0（主 UV）、
        /// UV1（烘焙 lightmap）、UV2（实时 lightmap）与 UV4（URP 用作顶点输入语义）。
        /// </summary>
        public HmMeshMergeChannel indexChannel = HmMeshMergeChannel.TexCoord3;

        /// <summary>输出材质使用的 Shader。工具生成后会自动引用到这里；留空或清掉则重新生成。</summary>
        public Shader outputShader;

        /// <summary>参与合并的源；下标即来源索引。请勿手工改动本列表。</summary>
        public IReadOnlyList<HmMeshMergeSource> Sources => _sources;

        /// <summary>参数表：属性名与它在参数纹理里的行号。行号稳定，移除的参数保留为空行。</summary>
        public IReadOnlyList<HmMeshMergeParameterEntry> Parameters => _parameters;

        /// <summary>合并后的网格；顶点携带来源索引与外观参数。</summary>
        public Mesh MergedMesh => _mergedMesh;

        /// <summary>合并后的材质：挂载各贴图属性的合并结果与参数查找纹理。</summary>
        public Material Material => _material;

        /// <summary>子树数量；顶点色作索引通道时不超过 256。</summary>
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
