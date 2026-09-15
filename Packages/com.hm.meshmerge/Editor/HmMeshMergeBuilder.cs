using System.Collections.Generic;
using System.IO;
using HmMeshMerge;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace HmMeshMergeEditor
{
    /// <summary>
    /// 按合并资产的配置执行合并：校验源与参数表，把各源网格拼成一个网格（顶点带来源索引）、
    /// 把各贴图属性拼成纹理数组、把数值参数写成查找纹理，并把结果写回同一个资产。
    /// 资产既是配置也是结果，窗口只编辑它，不另存一份设置。
    /// </summary>
    internal static class HmMeshMergeBuilder
    {
        /// <summary>校验资产的配置；返回空列表表示可以合并。</summary>
        public static List<string> Validate(HmMeshMergeAsset asset)
        {
            var errors = new List<string>();
            ValidateSources(asset, errors);
            ValidateChannels(asset, errors);
            ValidateTextures(asset, errors);
            return errors;
        }

        /// <summary>执行合并并把结果写回资产；输出文件按资产名放在资产同目录。调用前应先通过 Validate。</summary>
        public static void Build(HmMeshMergeAsset asset)
        {
            string assetPath = AssetDatabase.GetAssetPath(asset);
            string folder = Path.GetDirectoryName(assetPath).Replace('\\', '/');
            string name = Path.GetFileNameWithoutExtension(assetPath);
            var sources = new List<HmMeshMergeSource>(asset.Sources);

            // 首次合并（参数表为空）时按源材质的差异自动填表：
            // 取值不同的属性才需要按来源区分，取值相同的排在后且不启用。
            if (asset.Parameters.Count == 0)
            {
                asset.SetParameters(HmMeshMergeParameterWriter.BuildInitialTable(sources));
            }

            List<HmMeshMergeTextureSet> textureSets = BuildTextureSets(asset, sources, folder, name);
            Mesh mergedMesh = BuildMergedMesh(sources, asset, name);
            Mesh savedMesh = SaveMeshAsset(mergedMesh, $"{folder}/{name}_Mesh.asset");

            Texture2D parameterTexture = HmMeshMergeParameterWriter.Build(sources, asset.Parameters);
            Texture2D savedParameters = HmMeshMergeParameterWriter.Save(parameterTexture, name, folder);

            Shader shader = ResolveOutputShader(asset, sources, textureSets, $"{folder}/{name}_Shader.shader", name);
            Material savedMaterial = SaveMaterialAsset(BuildMaterial(shader, textureSets, savedParameters),
                $"{folder}/{name}_Material.mat");

            asset.SetResult(savedMesh, savedMaterial);
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
        }

        private static void ValidateSources(HmMeshMergeAsset asset, List<string> errors)
        {
            if (asset.Sources.Count == 0)
            {
                errors.Add("至少需要一个源。");
                return;
            }

            Shader sharedShader = null;
            for (int i = 0; i < asset.Sources.Count; i++)
            {
                HmMeshMergeSource source = asset.Sources[i];
                if (source.mesh == null || source.material == null)
                {
                    errors.Add($"第 {i} 个源缺少网格或材质。");
                    continue;
                }

                ValidateMeshShape(source.mesh, errors);
                if (i == 0)
                {
                    sharedShader = source.material.shader;
                }
                else if (source.material.shader != sharedShader)
                {
                    errors.Add($"{source.mesh.name}：所有源必须使用同一个 Shader。");
                }
            }
        }

        private static void ValidateMeshShape(Mesh mesh, List<string> errors)
        {
            if (mesh.subMeshCount != 1)
            {
                errors.Add($"{mesh.name}：只支持单子网格的网格（当前 {mesh.subMeshCount} 个）。");
            }

            if (mesh.bindposes.Length > 0)
            {
                errors.Add($"{mesh.name}：不支持蒙皮网格。");
            }

            if (mesh.vertexCount == 0 || mesh.normals.Length == 0 || mesh.tangents.Length == 0)
            {
                errors.Add($"{mesh.name}：网格缺少顶点、法线或切线数据。");
            }

            if (mesh.uv.Length != mesh.vertexCount)
            {
                errors.Add($"{mesh.name}：网格缺少 UV0，或 UV 数量与顶点数不一致。");
            }
        }

        private static void ValidateChannels(HmMeshMergeAsset asset, List<string> errors)
        {
            if (asset.Sources.Count > HmMeshMergeParameterWriter.MAX_SOURCE_COUNT)
            {
                errors.Add($"来源数量不能超过 {HmMeshMergeParameterWriter.MAX_SOURCE_COUNT}。");
            }

            if (asset.indexChannel == HmMeshMergeChannel.VertexColor && asset.Sources.Count > 256)
            {
                errors.Add("索引写入顶点色时源数量不能超过 256。");
            }

            VertexAttribute indexAttribute = ToVertexAttribute(asset.indexChannel);
            foreach (HmMeshMergeSource source in asset.Sources)
            {
                if (source.mesh == null)
                {
                    continue;
                }

                if (source.mesh.HasVertexAttribute(indexAttribute))
                {
                    errors.Add($"{source.mesh.name}：索引通道 {asset.indexChannel} 已被源网格占用。");
                }

                ReportDiscardedChannels(source.mesh, indexAttribute, errors);
            }
        }

        /// <summary>参数表里启用且类型为贴图的属性名，按表内顺序。</summary>
        private static List<string> TextureParameters(HmMeshMergeAsset asset)
        {
            var names = new List<string>();
            Shader shader = asset.Sources[0].material.shader;
            foreach (HmMeshMergeParameterEntry entry in asset.Parameters)
            {
                if (!entry.active)
                {
                    continue;
                }

                int index = shader.FindPropertyIndex(entry.propertyName);
                if (index < 0 || shader.GetPropertyType(index) != ShaderPropertyType.Texture)
                {
                    continue;
                }

                // 指向同一批贴图的别名只保留第一个，例如 URP Lit 同时声明了 _BaseMap 与 _MainTex。
                if (!IsSameTextures(entry.propertyName, names, asset))
                {
                    names.Add(entry.propertyName);
                }
            }

            return names;
        }

        /// <summary>该属性在所有源上的贴图是否与已收录的某个属性完全相同。</summary>
        private static bool IsSameTextures(string candidate, List<string> accepted, HmMeshMergeAsset asset)
        {
            foreach (string name in accepted)
            {
                bool same = true;
                foreach (HmMeshMergeSource source in asset.Sources)
                {
                    if (source.material.GetTexture(candidate) != source.material.GetTexture(name))
                    {
                        same = false;
                        break;
                    }
                }

                if (same)
                {
                    return true;
                }
            }

            return false;
        }

        private static void ValidateTextures(HmMeshMergeAsset asset, List<string> errors)
        {
            foreach (string propertyName in TextureParameters(asset))
            {
                ValidateTextureProperty(asset, propertyName, errors);
            }
        }

        /// <summary>该贴图属性在各源上的贴图必须存在、尺寸一致，且合并后放得进纹理数组。</summary>
        private static void ValidateTextureProperty(HmMeshMergeAsset asset, string propertyName, List<string> errors)
        {
            Texture2D first = null;
            foreach (HmMeshMergeSource source in asset.Sources)
            {
                if (source.material == null)
                {
                    continue;
                }

                if (!source.material.HasProperty(propertyName))
                {
                    errors.Add($"{source.material.name}：材质上没有贴图属性 {propertyName}。");
                    continue;
                }

                Texture2D texture = source.material.GetTexture(propertyName) as Texture2D;
                if (texture == null)
                {
                    errors.Add($"{source.material.name}：贴图属性 {propertyName} 不是 Texture2D。");
                    continue;
                }

                if (first == null)
                {
                    first = texture;
                    continue;
                }

                // 尺寸不一致不在这里报错：Max Size 可以把它对齐（只能往小的方向），
                // 由窗口询问使用者后调用 AlignTextureSizes 处理。
            }

            if (first != null && asset.Sources.Count > SystemInfo.maxTextureArraySlices)
            {
                errors.Add($"{propertyName}：来源数量 {asset.Sources.Count} 超过当前设备的纹理数组层数上限 " +
                    $"{SystemInfo.maxTextureArraySlices}。");
            }
        }

        /// <summary>同一贴图属性下各源贴图的最大边长。</summary>
        private static int MaxWidth(IReadOnlyList<HmMeshMergeSource> sources, string propertyName)
        {
            int maxWidth = 0;
            foreach (HmMeshMergeSource source in sources)
            {
                Texture2D texture = source.material.GetTexture(propertyName) as Texture2D;
                if (texture != null && texture.width > maxWidth)
                {
                    maxWidth = texture.width;
                }
            }

            return maxWidth;
        }

        /// <summary>是否存在同组贴图尺寸不一致；这些贴图可以提高导入 Max Size 来对齐。</summary>
        public static bool HasSizeMismatch(HmMeshMergeAsset asset)
        {
            foreach (string propertyName in TextureParameters(asset))
            {
                int maxWidth = MaxWidth(asset.Sources, propertyName);
                foreach (HmMeshMergeSource source in asset.Sources)
                {
                    Texture2D texture = source.material.GetTexture(propertyName) as Texture2D;
                    if (texture != null && texture.width < maxWidth)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// 把同一贴图属性下各源贴图的导入 Max Size 统一到最小尺寸并重新导入，使数组各层同尺寸。
        /// Max Size 只是上限、不能放大小图，所以对齐方向只能是"把大的压到小的"，较大贴图的画质会下降；
        /// 反过来放大需要重新采样，会失去平台压缩。会修改这些源贴图资产，返回处理数量。
        /// </summary>
        public static int AlignTextureSizes(HmMeshMergeAsset asset)
        {
            var aligned = new HashSet<Texture2D>();
            foreach (string propertyName in TextureParameters(asset))
            {
                int target = MinWidth(asset.Sources, propertyName);
                if (target == 0)
                {
                    continue;
                }

                foreach (HmMeshMergeSource source in asset.Sources)
                {
                    Texture2D texture = source.material.GetTexture(propertyName) as Texture2D;
                    if (texture == null || texture.width <= target || !aligned.Add(texture))
                    {
                        continue;
                    }

                    var importer = (TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture));
                    if (importer == null)
                    {
                        continue;
                    }

                    importer.maxTextureSize = target;
                    EditorUtility.SetDirty(importer);
                    importer.SaveAndReimport();
                }
            }

            return aligned.Count;
        }

        /// <summary>同一贴图属性下各源贴图的最小边长。</summary>
        private static int MinWidth(IReadOnlyList<HmMeshMergeSource> sources, string propertyName)
        {
            int minWidth = 0;
            foreach (HmMeshMergeSource source in sources)
            {
                Texture2D texture = source.material.GetTexture(propertyName) as Texture2D;
                if (texture != null && (minWidth == 0 || texture.width < minWidth))
                {
                    minWidth = texture.width;
                }
            }

            return minWidth;
        }

        /// <summary>合并后只保留基础通道与索引通道，源网格其余通道会被丢弃，这里逐项报出。</summary>
        private static void ReportDiscardedChannels(Mesh mesh, VertexAttribute indexAttribute, List<string> errors)
        {
            if (mesh.HasVertexAttribute(VertexAttribute.Color) && indexAttribute != VertexAttribute.Color)
            {
                errors.Add($"{mesh.name}：源网格的顶点色在合并后不会保留。");
            }

            if (mesh.HasVertexAttribute(VertexAttribute.BlendWeight) ||
                mesh.HasVertexAttribute(VertexAttribute.BlendIndices))
            {
                errors.Add($"{mesh.name}：源网格的骨骼权重/骨骼索引在合并后不会保留。");
            }

            for (int i = 1; i <= 7; i++)
            {
                VertexAttribute attribute = (VertexAttribute)((int)VertexAttribute.TexCoord0 + i);
                if (!mesh.HasVertexAttribute(attribute) || attribute == indexAttribute)
                {
                    continue;
                }

                errors.Add($"{mesh.name}：源网格的 UV{i} 在合并后不会保留。");
            }
        }

        /// <summary>
        /// 每个贴图属性生成一份纹理数组资产：源文件是空的 .hmtexarray，导入器把各源贴图按顺序装进各层。
        /// 尺寸与格式由导入器跟随源贴图，因此切换平台时数组会随源贴图一起按平台重导。
        /// </summary>
        private static List<HmMeshMergeTextureSet> BuildTextureSets(HmMeshMergeAsset asset,
            List<HmMeshMergeSource> sources, string folder, string name)
        {
            var sets = new List<HmMeshMergeTextureSet>();
            foreach (string propertyName in TextureParameters(asset))
            {
                string path = $"{folder}/{name}_Array_{TrimProperty(propertyName)}" +
                    $".{HmMeshMergeTextureArrayImporter.EXTENSION}";
                WriteArraySource(path, sources, propertyName);
                var array = AssetDatabase.LoadAssetAtPath<Texture2DArray>(path);
                if (array == null)
                {
                    // 用全名而不引入 using System：该文件里的 Object 必须是 UnityEngine.Object。
                    throw new System.InvalidOperationException(
                        $"未能从 {path} 导入纹理数组。请检查该资产的导入设置与源贴图是否一致。");
                }

                sets.Add(new HmMeshMergeTextureSet { propertyName = propertyName, texture = array });
            }

            return sets;
        }

        /// <summary>写出源文件、把引用的贴图列表写进导入器，再让它重新导入生成数组。</summary>
        private static void WriteArraySource(string path, List<HmMeshMergeSource> sources, string propertyName)
        {
            if (!File.Exists(path))
            {
                File.WriteAllText(path, string.Empty);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            }

            var importer = (HmMeshMergeTextureArrayImporter)AssetImporter.GetAtPath(path);
            var textures = new List<Texture2D>();
            foreach (HmMeshMergeSource source in sources)
            {
                textures.Add(source.material.GetTexture(propertyName) as Texture2D);
            }

            // 走 SerializedObject 写入并标脏：直接给导入器的属性赋值不会持久化进 .meta。
            var serialized = new SerializedObject(importer);
            SerializedProperty list = serialized.FindProperty(HmMeshMergeTextureArrayImporter.TEXTURES_FIELD);
            list.arraySize = textures.Count;
            for (int i = 0; i < textures.Count; i++)
            {
                list.GetArrayElementAtIndex(i).objectReferenceValue = textures[i];
            }

            serialized.ApplyModifiedProperties();
            // 必须显式标脏：SaveAndReimport 只在导入器为脏时才把设置写进 .meta。
            EditorUtility.SetDirty(importer);
            importer.SaveAndReimport();
        }

        private static string TrimProperty(string propertyName)
        {
            return propertyName.TrimStart('_');
        }

        private static Mesh BuildMergedMesh(List<HmMeshMergeSource> sources, HmMeshMergeAsset asset, string name)
        {
            var data = new MeshData();
            for (int i = 0; i < sources.Count; i++)
            {
                data.Append(sources[i].mesh, i);
            }

            return data.ToMesh($"{name}_Mesh", asset.indexChannel);
        }

        /// <summary>
        /// 确定输出材质的 Shader：指向别的 Shader 时直接用它；否则生成（同路径覆盖，不产生多余文件）
        /// 并回填到资产，下次合并即可直接引用。
        /// </summary>
        private static Shader ResolveOutputShader(HmMeshMergeAsset asset, List<HmMeshMergeSource> sources,
            List<HmMeshMergeTextureSet> textureSets, string shaderPath, string name)
        {
            if (asset.outputShader != null && AssetDatabase.GetAssetPath(asset.outputShader) != shaderPath)
            {
                return asset.outputShader;
            }

            File.WriteAllText(shaderPath, HmMeshMergeShaderWriter.Write(sources, asset.Parameters,
                textureSets, asset.indexChannel, $"HmMeshMerge/{name}"));
            AssetDatabase.Refresh();
            Shader generated = AssetDatabase.LoadAssetAtPath<Shader>(shaderPath);
            asset.outputShader = generated;
            return generated;
        }

        /// <summary>创建材质：各贴图属性的合并结果 + 各来源参数的查找纹理。名字在保存时按文件名设置。</summary>
        private static Material BuildMaterial(Shader shader, List<HmMeshMergeTextureSet> textureSets, Texture2D parameters)
        {
            var material = new Material(shader);
            foreach (HmMeshMergeTextureSet set in textureSets)
            {
                material.SetTexture(set.propertyName, set.texture);
            }

            material.SetTexture(HmMeshMergeParameterWriter.TEXTURE_NAME, parameters);
            return material;
        }

        private static VertexAttribute ToVertexAttribute(HmMeshMergeChannel channel)
        {
            return channel == HmMeshMergeChannel.VertexColor
                ? VertexAttribute.Color
                : (VertexAttribute)((int)VertexAttribute.TexCoord0 + (int)channel);
        }

        /// <summary>写入合并网格；同路径已有资产会被替换。</summary>
        private static Mesh SaveMeshAsset(Mesh mesh, string path)
        {
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(mesh, path);
            return mesh;
        }

        /// <summary>写入材质；同路径已有资产时原地更新内容，保持其 GUID。</summary>
        private static Material SaveMaterialAsset(Material material, string path)
        {
            // 资产名要与文件名一致，否则 Unity 会提示 Main Object Name 不匹配。
            material.name = Path.GetFileNameWithoutExtension(path);
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing == null)
            {
                AssetDatabase.CreateAsset(material, path);
                return material;
            }

            EditorUtility.CopySerialized(material, existing);
            Object.DestroyImmediate(material);
            return existing;
        }

        /// <summary>合并过程中的顶点数据累积器；索引按顶点逐份写入，UV 原样保留。</summary>
        private sealed class MeshData
        {
            private readonly List<Vector3> _vertices = new List<Vector3>();
            private readonly List<Vector3> _normals = new List<Vector3>();
            private readonly List<Vector4> _tangents = new List<Vector4>();
            private readonly List<Vector2> _uv0 = new List<Vector2>();
            private readonly List<int> _triangles = new List<int>();
            private readonly List<Vector2> _sourceIndices = new List<Vector2>();

            /// <summary>追加一个源的几何：顶点、法线、切线、UV 与偏移后的索引。</summary>
            public void Append(Mesh source, int sourceIndex)
            {
                int vertexOffset = _vertices.Count;
                _vertices.AddRange(source.vertices);
                _normals.AddRange(source.normals);
                _tangents.AddRange(source.tangents);
                _uv0.AddRange(source.uv);

                int[] sourceTriangles = source.triangles;
                for (int i = 0; i < sourceTriangles.Length; i++)
                {
                    _triangles.Add(sourceTriangles[i] + vertexOffset);
                }

                for (int i = 0; i < source.vertexCount; i++)
                {
                    _sourceIndices.Add(new Vector2(sourceIndex, 0f));
                }
            }

            public Mesh ToMesh(string name, HmMeshMergeChannel indexChannel)
            {
                var mesh = new Mesh { name = name };
                mesh.indexFormat = _vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
                mesh.SetVertices(_vertices);
                mesh.SetNormals(_normals);
                mesh.SetTangents(_tangents);
                mesh.SetUVs(0, _uv0);
                mesh.SetTriangles(_triangles, 0);
                WriteIndexChannel(mesh, indexChannel);
                mesh.RecalculateBounds();
                return mesh;
            }

            private void WriteIndexChannel(Mesh mesh, HmMeshMergeChannel channel)
            {
                if (channel == HmMeshMergeChannel.VertexColor)
                {
                    var colors = new Color32[_sourceIndices.Count];
                    for (int i = 0; i < colors.Length; i++)
                    {
                        colors[i] = new Color32((byte)_sourceIndices[i].x, 0, 0, 0);
                    }

                    mesh.colors32 = colors;
                    return;
                }

                // 索引只用一个分量，按 2 分量写入；生成的着色器也按 float2 声明。
                mesh.SetUVs((int)channel, _sourceIndices);
            }
        }
    }
}
