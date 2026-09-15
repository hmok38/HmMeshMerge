using UnityEngine;

namespace HmMeshMerge
{
    /// <summary>
    /// 把激活索引编码进实例矩阵的 m33 分量（标准 TRS 下恒为 1），供着色器从
    /// unity_ObjectToWorld._m33 读取；适用于自己提供实例矩阵的绘制路径。
    /// 位置变换取结果的 xyz 分量即可（m33 只影响 w），法线变换取矩阵左上 3x3，
    /// 因此编码索引不影响常规顶点变换。
    /// </summary>
    public static class HmMeshMergeIndex
    {
        /// <summary>把子树索引写入实例矩阵；索引即合并资产源列表中的下标。未写入的矩阵会被读成 1。</summary>
        public static void WriteToMatrix(ref Matrix4x4 matrix, float index)
        {
            matrix.m33 = index;
        }

        /// <summary>读取实例矩阵中的子树索引，用于校验或调试。</summary>
        public static float ReadFromMatrix(Matrix4x4 matrix)
        {
            return matrix.m33;
        }
    }
}
