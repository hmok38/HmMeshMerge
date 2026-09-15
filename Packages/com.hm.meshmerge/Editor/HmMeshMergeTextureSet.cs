using UnityEngine;

namespace HmMeshMergeEditor
{
    /// <summary>
    /// 一个贴图属性合并后的结果：属性名与它的纹理数组资产。
    /// 数组的层号即来源索引，所以 UV 保持原样、不需要重映射。
    /// </summary>
    internal sealed class HmMeshMergeTextureSet
    {
        public string propertyName;
        public Texture2DArray texture;
    }
}
