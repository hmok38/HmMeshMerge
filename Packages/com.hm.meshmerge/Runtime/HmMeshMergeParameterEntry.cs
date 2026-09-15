using System;

namespace HmMeshMerge
{
    /// <summary>
    /// 参数表的一项：源着色器属性名与它在参数纹理里的行号。
    /// 行号一旦分配就保持稳定——移除的参数只把该行置为未使用（active = false）并保留空行，
    /// 不让后续参数的行号前移，避免使用者已复制到自有着色器里的读取行号失效。
    /// </summary>
    [Serializable]
    public struct HmMeshMergeParameterEntry
    {
        /// <summary>源着色器里的属性名，例如 _BaseColor。</summary>
        public string propertyName;

        /// <summary>参数纹理中的行号。</summary>
        public int row;

        /// <summary>该行当前是否仍在使用；false 表示空出的保留行。</summary>
        public bool active;
    }
}
