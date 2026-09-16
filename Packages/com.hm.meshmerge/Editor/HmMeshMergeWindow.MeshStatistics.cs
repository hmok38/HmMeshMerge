using System.Collections.Generic;
using HmMeshMerge;
using UnityEditor;
using UnityEngine;

namespace HmMeshMergeEditor
{
    internal sealed partial class HmMeshMergeWindow
    {
        private readonly HashSet<Mesh> _statisticsMeshes = new HashSet<Mesh>();

        /// <summary>读取正在编辑的来源列表，按 Mesh 引用去重；不复制顶点和索引缓冲。</summary>
        private void DrawMeshStatistics()
        {
            _statisticsMeshes.Clear();
            long vertexCount = 0;
            long triangleCount = 0;
            bool hasNonTriangles = false;
            for (int i = 0; i < _sourcesProperty.arraySize; i++)
            {
                SerializedProperty source = _sourcesProperty.GetArrayElementAtIndex(i);
                var mesh = source.FindPropertyRelative(nameof(HmMeshMergeSource.mesh)).objectReferenceValue as Mesh;
                if (mesh == null || !_statisticsMeshes.Add(mesh))
                {
                    continue;
                }

                vertexCount += mesh.vertexCount;
                for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
                {
                    if (mesh.GetTopology(subMesh) != MeshTopology.Triangles)
                    {
                        hasNonTriangles = true;
                        continue;
                    }

                    triangleCount += mesh.GetIndexCount(subMesh) / 3;
                }
            }

            EditorGUILayout.LabelField("网格种数（按引用去重）", _statisticsMeshes.Count.ToString("N0"));
            EditorGUILayout.LabelField("合并后顶点数", vertexCount.ToString("N0"));
            EditorGUILayout.LabelField("合并后三角形数", triangleCount.ToString("N0"));
            if (hasNonTriangles)
            {
                EditorGUILayout.HelpBox("存在非三角形子网格：三角形统计未计入这些子网格，执行时将报错。",
                    MessageType.Warning);
            }
        }
    }
}
