using System;
using UnityEngine;

namespace HmMeshMerge
{
    /// <summary>一个参与合并的源：网格与其材质。显示名取网格名，索引即资产源列表中的下标。</summary>
    [Serializable]
    public sealed class HmMeshMergeSource
    {
        public Mesh mesh;
        public Material material;
    }
}
