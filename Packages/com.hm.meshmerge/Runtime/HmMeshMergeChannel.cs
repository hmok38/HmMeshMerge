namespace HmMeshMerge
{
    /// <summary>合并数据写入使用的顶点通道；枚举值与网格的 UV 索引一致，顶点色用 -1 表示。</summary>
    public enum HmMeshMergeChannel
    {
        /// <summary>顶点色通道；8 位量化，可承载 0..255 的来源索引。</summary>
        VertexColor = -1,

        TexCoord1 = 1,
        TexCoord2 = 2,
        TexCoord3 = 3,
        TexCoord4 = 4,
        TexCoord5 = 5,
        TexCoord6 = 6,
        TexCoord7 = 7,
    }
}
