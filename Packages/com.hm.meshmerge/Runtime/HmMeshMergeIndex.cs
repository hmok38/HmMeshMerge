using UnityEngine;

namespace HmMeshMerge
{
    /// <summary>
    /// 将来源索引编码到矩阵 m33，供自定义渲染路径通过同名 Shader 宏读取。
    /// 编码后矩阵不再是标准 TRS，索引 0 会使矩阵奇异；调用方必须自行处理变换、逆矩阵和剔除。
    /// 普通 MeshRenderer 或未经适配的 Unity 实例绘制应使用 _MeshMergeIndex 属性。
    /// </summary>
    public static class HmMeshMergeIndex
    {
        /// <summary>把来源索引写入实例矩阵；索引即合并资产源列表中的下标。未写入的矩阵会被读成 1。</summary>
        public static void WriteToMatrix(ref Matrix4x4 matrix, float index)
        {
            matrix.m33 = index;
        }

        /// <summary>读取实例矩阵中的来源索引，用于校验或调试。</summary>
        public static float ReadFromMatrix(Matrix4x4 matrix)
        {
            return matrix.m33;
        }
    }
}
