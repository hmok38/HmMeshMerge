using System;
using System.Collections.Generic;
using System.IO;
using HmMeshMerge;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace HmMeshMergeEditor
{
    /// <summary>校验配置，组织网格、纹理与 Shader 生成，并把结果写回配置资产。</summary>
    internal static class HmMeshMergeBuilder
    {
        public static List<string> Validate(HmMeshMergeAsset asset)
        {
            var errors = new List<string>();
            if (asset == null)
            {
                errors.Add("请选择合并配置资产。");
                return errors;
            }

            if (!AssetDatabase.GetAssetPath(asset).StartsWith("Assets/", StringComparison.Ordinal))
            {
                errors.Add("配置资产必须保存在 Assets 内。");
            }

            ValidateSources(asset, errors);
            string outputPrefix = Path.ChangeExtension(AssetDatabase.GetAssetPath(asset), null);
            foreach (HmMeshMergeSource source in asset.Sources)
            {
                if (source == null)
                {
                    continue;
                }

                if (source.mesh != null && AssetDatabase.GetAssetPath(source.mesh) == outputPrefix + "_Mesh.asset" ||
                    source.material != null && AssetDatabase.GetAssetPath(source.material) == outputPrefix + "_Material.mat")
                {
                    errors.Add("不能把本配置的输出 Mesh 或 Material 同时作为来源，否则会覆盖输入。");
                }
            }

            if (errors.Count > 0)
            {
                return errors;
            }

            Shader sourceShader = asset.Sources[0].material.shader;
            for (int i = 0; i < sourceShader.GetPropertyCount(); i++)
            {
                string propertyName = sourceShader.GetPropertyName(i);
                if (propertyName == "_MeshMergeIndex" || propertyName == HmMeshMergeParameterWriter.TEXTURE_NAME)
                {
                    errors.Add($"源 Shader 的 {propertyName} 与插件保留属性冲突。");
                }

                if (!System.Text.RegularExpressions.Regex.IsMatch(propertyName, @"^[A-Za-z_][A-Za-z0-9_]*$"))
                {
                    errors.Add($"源属性 {propertyName} 不能直接生成 HLSL 标识符。");
                }
            }

            HmMeshMergeParameterWriter.Validate(asset.Sources, asset.Parameters, errors);
            if (errors.Count > 0)
            {
                return errors;
            }

            // 引用同一组贴图的属性共用一次校验与报告，避免同一问题重复输出。
            var problems = new List<(Texture2D texture, string block)>();
            foreach ((List<string> names, List<Texture2D> textures) group in GroupTextureParameters(asset))
            {
                if (!HmMeshMergeTextureArrayImporter.ValidateTextures(group.textures, out string reason,
                    out var layerProblems))
                {
                    problems.AddRange(layerProblems);
                    errors.Add($"{string.Join("、", group.names)}：{reason}");
                }
            }

            LogTextureProblems(problems);
            return errors;
        }

        /// <summary>按来源贴图序列分组属性名；序列相同的属性共用一次报告，顺序不同视为不同数组。</summary>
        private static List<(List<string> names, List<Texture2D> textures)> GroupTextureParameters(
            HmMeshMergeAsset asset)
        {
            var groups = new List<(List<string> names, List<Texture2D> textures)>();
            foreach (string name in TextureParameters(asset))
            {
                List<Texture2D> textures = CollectTextures(asset, name);
                int index = groups.FindIndex(group => SameTextures(group.textures, textures));
                if (index < 0)
                {
                    groups.Add((new List<string> { name }, textures));
                    continue;
                }

                List<string> names = groups[index].names;
                names.Add(name);
            }

            return groups;
        }

        private static List<Texture2D> CollectTextures(HmMeshMergeAsset asset, string name)
        {
            var textures = new List<Texture2D>();
            foreach (HmMeshMergeSource source in asset.Sources)
            {
                textures.Add(source.material.GetTexture(name) as Texture2D);
            }

            return textures;
        }

        /// <summary>按顺序比较两组来源贴图，顺序不同即数组内容不同。</summary>
        private static bool SameTextures(IReadOnlyList<Texture2D> left, IReadOnlyList<Texture2D> right)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            for (int i = 0; i < left.Count; i++)
            {
                if (left[i] != right[i])
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>逐条输出需要修改的来源贴图；条目携带贴图对象，单击 Console 条目即可在 Project 中定位。</summary>
        private static void LogTextureProblems(List<(Texture2D texture, string block)> problems)
        {
            var logged = new HashSet<(Texture2D texture, string block)>();
            foreach ((Texture2D texture, string block) problem in problems)
            {
                if (!logged.Add(problem))
                {
                    continue;
                }

                Debug.LogError($"[HmMeshMerge] {problem.block}", problem.texture);
            }
        }

        private static void ValidateSources(HmMeshMergeAsset asset, List<string> errors)
        {
            if (asset.Sources.Count == 0 || asset.Sources.Count > Mathf.Min(HmMeshMergeParameterWriter.MAX_SOURCE_COUNT, SystemInfo.maxTextureSize))
            {
                errors.Add($"来源数量必须在 1 到 {HmMeshMergeParameterWriter.MAX_SOURCE_COUNT} 之间。");
                return;
            }

            if (!Enum.IsDefined(typeof(HmMeshMergeChannel), asset.indexChannel))
            {
                errors.Add("索引通道无效。");
                return;
            }

            if (asset.indexChannel == HmMeshMergeChannel.VertexColor && asset.Sources.Count > 256)
            {
                errors.Add("索引写入顶点色时来源数量不能超过 256。");
            }

            VertexAttribute attribute = asset.indexChannel == HmMeshMergeChannel.VertexColor
                ? VertexAttribute.Color
                : (VertexAttribute)((int)VertexAttribute.TexCoord0 + (int)asset.indexChannel);
            Shader sharedShader = null;
            foreach (HmMeshMergeSource source in asset.Sources)
            {
                if (source == null || source.mesh == null || source.material == null || source.material.shader == null)
                {
                    errors.Add("来源缺少网格、材质或 Shader。");
                    continue;
                }

                Mesh mesh = source.mesh;
                if (sharedShader == null)
                {
                    sharedShader = source.material.shader;
                }
                else if (source.material.shader != sharedShader)
                {
                    errors.Add($"{source.material.name}：所有来源必须使用同一个 Shader。");
                }

                if (mesh.vertexCount == 0)
                {
                    errors.Add($"{mesh.name}：网格没有顶点。");
                }

                if (mesh.HasVertexAttribute(attribute))
                {
                    errors.Add($"{mesh.name}：索引通道 {asset.indexChannel} 已被源数据占用。");
                }

                if (mesh.bindposes.Length > 0 || mesh.blendShapeCount > 0 ||
                    mesh.HasVertexAttribute(VertexAttribute.BlendWeight) ||
                    mesh.HasVertexAttribute(VertexAttribute.BlendIndices))
                {
                    errors.Add($"{mesh.name}：请先把蒙皮或 BlendShape 烘焙成静态网格。");
                }

                for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
                {
                    if (mesh.GetTopology(subMesh) != MeshTopology.Triangles)
                    {
                        errors.Add($"{mesh.name}：子网格 {subMesh} 不是三角形拓扑。");
                    }
                }
            }
        }

        /// <summary>启用且各来源引用不同的贴图才生成数组；不同属性各自保留绑定。</summary>
        private static List<string> TextureParameters(HmMeshMergeAsset asset)
        {
            var names = new List<string>();
            Shader shader = asset.Sources[0].material.shader;
            foreach (HmMeshMergeParameterEntry entry in asset.Parameters)
            {
                if (entry.active &&
                    HmMeshMergeParameterWriter.TryGetType(shader, entry.propertyName, out ShaderPropertyType type) &&
                    type == ShaderPropertyType.Texture &&
                    HmMeshMergeParameterWriter.SourcesDiffer(asset.Sources, entry.propertyName))
                {
                    names.Add(entry.propertyName);
                }
            }

            return names;
        }

        /// <summary>先自行校验；只在所有生成步骤完成后发布结果，失败时释放临时对象。</summary>
        public static void Build(HmMeshMergeAsset asset)
        {
            List<string> errors = Validate(asset);
            if (errors.Count > 0)
            {
                throw new InvalidOperationException(string.Join("\n", errors));
            }

            if (asset.Parameters.Count == 0)
            {
                Undo.RecordObject(asset, "初始化参数表");
                asset.SetParameters(HmMeshMergeParameterWriter.BuildInitialTable(asset.Sources));
                EditorUtility.SetDirty(asset);
            }

            errors = Validate(asset);
            if (errors.Count > 0)
            {
                throw new InvalidOperationException(string.Join("\n", errors));
            }

            string assetPath = AssetDatabase.GetAssetPath(asset);
            string folder = Path.GetDirectoryName(assetPath).Replace('\\', '/');
            string name = Path.GetFileNameWithoutExtension(assetPath);
            var sources = new List<HmMeshMergeSource>(asset.Sources);
            Mesh mesh = null;
            Texture2D parameters = null;
            Material material = null;
            try
            {
                List<HmMeshMergeTextureSet> sets = BuildTextureSets(asset, folder, name);
                mesh = BuildMergedMesh(sources, asset.indexChannel, name);
                parameters = HmMeshMergeParameterWriter.Build(sources, asset.Parameters);
                Shader shader = ResolveOutputShader(asset, sources, sets, folder, name);
                material = BuildMaterial(shader, sources[0].material, sets, parameters);
                Mesh savedMesh = SaveMeshAsset(mesh, $"{folder}/{name}_Mesh.asset");
                Texture2D savedParameters = HmMeshMergeParameterWriter.Save(parameters, name, folder);
                if (material.HasProperty(HmMeshMergeParameterWriter.TEXTURE_NAME))
                {
                    material.SetTexture(HmMeshMergeParameterWriter.TEXTURE_NAME, savedParameters);
                }

                Material savedMaterial = SaveMaterialAsset(material, $"{folder}/{name}_Material.mat");
                asset.outputShader = shader;
                asset.SetResult(savedMesh, savedMaterial);
                EditorUtility.SetDirty(asset);
                AssetDatabase.SaveAssets();
            }
            finally
            {
                if (mesh != null && !AssetDatabase.Contains(mesh))
                {
                    Object.DestroyImmediate(mesh);
                }

                if (parameters != null && !AssetDatabase.Contains(parameters))
                {
                    Object.DestroyImmediate(parameters);
                }

                if (material != null && !AssetDatabase.Contains(material))
                {
                    Object.DestroyImmediate(material);
                }
            }
        }

        private static List<HmMeshMergeTextureSet> BuildTextureSets(HmMeshMergeAsset asset, string folder, string name)
        {
            var sets = new List<HmMeshMergeTextureSet>();
            List<string> properties = TextureParameters(asset);
            foreach (string propertyName in properties)
            {
                string suffix = propertyName.TrimStart('_');
                string path = $"{folder}/{name}_Array_{suffix}.{HmMeshMergeTextureArrayImporter.EXTENSION}";
                foreach (string other in properties)
                {
                    if (other != propertyName && other.TrimStart('_').Equals(suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        // 同名文件冲突时按属性行区分；普通属性沿用既有路径和 GUID。
                        int propertyIndex = asset.Sources[0].material.shader.FindPropertyIndex(propertyName);
                        path = $"{folder}/{name}_Array_{suffix}_{propertyIndex}." +
                            HmMeshMergeTextureArrayImporter.EXTENSION;
                        break;
                    }
                }
                if (!File.Exists(path))
                {
                    File.WriteAllText(path, string.Empty);
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                }

                var importer = AssetImporter.GetAtPath(path) as HmMeshMergeTextureArrayImporter;
                if (importer == null)
                {
                    throw new InvalidOperationException($"未找到纹理数组导入器：{path}");
                }

                using (var serialized = new SerializedObject(importer))
                {
                    SerializedProperty list = serialized.FindProperty(HmMeshMergeTextureArrayImporter.TEXTURES_FIELD);
                    list.arraySize = asset.Sources.Count;
                    for (int i = 0; i < asset.Sources.Count; i++)
                    {
                        list.GetArrayElementAtIndex(i).objectReferenceValue =
                            asset.Sources[i].material.GetTexture(propertyName);
                    }

                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }

                EditorUtility.SetDirty(importer);
                importer.SaveAndReimport();
                ImportLog importLog = AssetImporter.GetImportLog(path);
                if (importLog != null)
                {
                    foreach (ImportLog.ImportLogEntry entry in importLog.logEntries)
                    {
                        if ((entry.flags & ImportLogFlags.Error) != 0)
                        {
                            throw new InvalidOperationException($"纹理数组导入失败：{path}\n{entry.message}");
                        }
                    }
                }

                var array = AssetDatabase.LoadAssetAtPath<Texture2DArray>(path);
                if (array == null)
                {
                    throw new InvalidOperationException($"纹理数组导入失败：{path}。请查看 Console。");
                }

                sets.Add(new HmMeshMergeTextureSet { propertyName = propertyName, texture = array });
            }

            return sets;
        }

        private static Mesh BuildMergedMesh(List<HmMeshMergeSource> sources, HmMeshMergeChannel channel, string name)
        {
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var tangents = new List<Vector4>();
            var colors = new List<Color>();
            var triangles = new List<int>();
            var indices = new List<Vector2>();
            bool hasNormals = false;
            bool hasTangents = false;
            bool hasColors = false;
            foreach (HmMeshMergeSource source in sources)
            {
                hasNormals |= source.mesh.HasVertexAttribute(VertexAttribute.Normal);
                hasTangents |= source.mesh.HasVertexAttribute(VertexAttribute.Tangent);
                hasColors |= source.mesh.HasVertexAttribute(VertexAttribute.Color);
            }

            for (int sourceIndex = 0; sourceIndex < sources.Count; sourceIndex++)
            {
                Mesh source = sources[sourceIndex].mesh;
                int offset = vertices.Count;
                vertices.AddRange(source.vertices);
                Vector3[] sourceNormals = source.normals;
                Vector4[] sourceTangents = source.tangents;
                Color[] sourceColors = source.colors;
                for (int i = 0; i < source.vertexCount; i++)
                {
                    indices.Add(new Vector2(sourceIndex, 0f));
                    if (hasNormals)
                    {
                        normals.Add(sourceNormals.Length > i ? sourceNormals[i] : Vector3.zero);
                    }

                    if (hasTangents)
                    {
                        tangents.Add(sourceTangents.Length > i ? sourceTangents[i] : Vector4.zero);
                    }

                    if (hasColors)
                    {
                        colors.Add(sourceColors.Length > i ? sourceColors[i] : Color.white);
                    }
                }

                foreach (int index in source.triangles)
                {
                    triangles.Add(offset + index);
                }
            }

            var mesh = new Mesh
            {
                name = name + "_Mesh",
                indexFormat = vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16
            };
            try
            {
                mesh.SetVertices(vertices);
                if (hasNormals)
                {
                    mesh.SetNormals(normals);
                }

                if (hasTangents)
                {
                    mesh.SetTangents(tangents);
                }

                if (hasColors)
                {
                    mesh.SetColors(colors);
                }

                CopyUvChannels(sources, mesh);
                if (channel == HmMeshMergeChannel.VertexColor)
                {
                    var indexColors = new Color32[indices.Count];
                    for (int i = 0; i < indices.Count; i++)
                    {
                        indexColors[i] = new Color32((byte)indices[i].x, 0, 0, 0);
                    }

                    mesh.colors32 = indexColors;
                }
                else
                {
                    mesh.SetUVs((int)channel, indices);
                }

                mesh.SetTriangles(triangles, 0);
                mesh.RecalculateBounds();
                return mesh;
            }
            catch
            {
                Object.DestroyImmediate(mesh);
                throw;
            }
        }

        private static void CopyUvChannels(List<HmMeshMergeSource> sources, Mesh target)
        {
            for (int channel = 0; channel < 8; channel++)
            {
                var attribute = (VertexAttribute)((int)VertexAttribute.TexCoord0 + channel);
                int dimension = 0;
                foreach (HmMeshMergeSource source in sources)
                {
                    if (source.mesh.HasVertexAttribute(attribute))
                    {
                        dimension = Mathf.Max(dimension, source.mesh.GetVertexAttributeDimension(attribute));
                    }
                }

                if (dimension == 0)
                {
                    continue;
                }

                var values = new List<Vector4>();
                var sourceValues = new List<Vector4>();
                foreach (HmMeshMergeSource source in sources)
                {
                    sourceValues.Clear();
                    source.mesh.GetUVs(channel, sourceValues);
                    for (int i = 0; i < source.mesh.vertexCount; i++)
                    {
                        values.Add(i < sourceValues.Count ? sourceValues[i] : Vector4.zero);
                    }
                }

                if (dimension <= 2)
                {
                    target.SetUVs(channel, values.ConvertAll(value => new Vector2(value.x, value.y)));
                }
                else if (dimension == 3)
                {
                    target.SetUVs(channel, values.ConvertAll(value => new Vector3(value.x, value.y, value.z)));
                }
                else
                {
                    target.SetUVs(channel, values);
                }
            }
        }

        private static Shader ResolveOutputShader(HmMeshMergeAsset asset, List<HmMeshMergeSource> sources,
            List<HmMeshMergeTextureSet> sets, string folder, string name)
        {
            string path = $"{folder}/{name}_Shader.shader";
            if (asset.outputShader != null && AssetDatabase.GetAssetPath(asset.outputShader) != path)
            {
                return asset.outputShader;
            }

            string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(asset));
            File.WriteAllText(path, HmMeshMergeShaderWriter.Write(sources, asset.Parameters, sets,
                asset.indexChannel, $"HmMeshMerge/{name}_{guid}"));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            if (shader == null)
            {
                throw new InvalidOperationException($"未能加载生成 Shader：{path}");
            }

            return shader;
        }

        private static Material BuildMaterial(Shader shader, Material source,
            List<HmMeshMergeTextureSet> sets, Texture2D parameters)
        {
            if (ShaderUtil.ShaderHasError(shader))
            {
                throw new InvalidOperationException(
                    $"输出 Shader {shader.name} 存在编译错误，请先处理 Console 中的 Shader 错误。未写入材质。");
            }

            var material = new Material(shader);
            try
            {
                // 停用参数明确使用第一来源的值；启用项由数组和 LUT 覆盖。
                CopySourceProperties(source, material, sets);
                material.renderQueue = -1;
                foreach (HmMeshMergeTextureSet set in sets)
                {
                    int index = shader.FindPropertyIndex(set.propertyName);
                    if (index < 0 || shader.GetPropertyType(index) != ShaderPropertyType.Texture ||
                        shader.GetPropertyTextureDimension(index) != TextureDimension.Tex2DArray)
                    {
                        throw new InvalidOperationException($"输出 Shader 必须声明 2DArray 属性 {set.propertyName}。");
                    }

                    material.SetTexture(set.propertyName, set.texture);
                }

                int parameterIndex = shader.FindPropertyIndex(HmMeshMergeParameterWriter.TEXTURE_NAME);
                if (parameters != null && (parameterIndex < 0 ||
                    shader.GetPropertyType(parameterIndex) != ShaderPropertyType.Texture ||
                    shader.GetPropertyTextureDimension(parameterIndex) != TextureDimension.Tex2D))
                {
                    throw new InvalidOperationException("输出 Shader 必须声明 2D 贴图属性 _HmMeshMergeParams。");
                }

                if (material.HasProperty(HmMeshMergeParameterWriter.TEXTURE_NAME))
                {
                    material.SetTexture(HmMeshMergeParameterWriter.TEXTURE_NAME, parameters);
                }

                return material;
            }
            catch
            {
                Object.DestroyImmediate(material);
                throw;
            }
        }

        /// <summary>只复制输出声明中的普通属性；数组槽位跳过源贴图，保留其 Tiling/Offset。</summary>
        private static void CopySourceProperties(Material source, Material target, List<HmMeshMergeTextureSet> sets)
        {
            Shader sourceShader = source.shader;
            Shader targetShader = target.shader;
            for (int i = 0; i < sourceShader.GetPropertyCount(); i++)
            {
                string name = sourceShader.GetPropertyName(i);
                if (HmMeshMergeParameterWriter.IsEngineProperty(name))
                {
                    continue;
                }

                int targetIndex = targetShader.FindPropertyIndex(name);
                if (targetIndex < 0)
                {
                    continue;
                }

                ShaderPropertyType type = sourceShader.GetPropertyType(i);
                ShaderPropertyType targetType = targetShader.GetPropertyType(targetIndex);
                bool scalar = type == ShaderPropertyType.Float || type == ShaderPropertyType.Range;
                bool targetScalar = targetType == ShaderPropertyType.Float || targetType == ShaderPropertyType.Range;
                if (type != targetType && !(scalar && targetScalar))
                {
                    throw new InvalidOperationException($"输出 Shader 的 {name} 类型与源 Shader 不一致。");
                }

                switch (type)
                {
                    case ShaderPropertyType.Texture:
                        if (!sets.Exists(set => set.propertyName == name))
                        {
                            Texture texture = source.GetTexture(name);
                            TextureDimension dimension = targetShader.GetPropertyTextureDimension(targetIndex);
                            if (sourceShader.GetPropertyTextureDimension(i) != dimension ||
                                texture != null && texture.dimension != dimension)
                            {
                                throw new InvalidOperationException($"普通贴图 {name} 与输出 Shader 的维度不一致。");
                            }

                            target.SetTexture(name, texture);
                        }

                        target.SetTextureScale(name, source.GetTextureScale(name));
                        target.SetTextureOffset(name, source.GetTextureOffset(name));
                        break;
                    case ShaderPropertyType.Color:
                        target.SetColor(name, source.GetColor(name));
                        break;
                    case ShaderPropertyType.Vector:
                        target.SetVector(name, source.GetVector(name));
                        break;
                    case ShaderPropertyType.Int:
                        target.SetInteger(name, source.GetInteger(name));
                        break;
                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range:
                        target.SetFloat(name, source.GetFloat(name));
                        break;
                }
            }
        }

        private static Mesh SaveMeshAsset(Mesh mesh, string path)
        {
            Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing == null)
            {
                AssetDatabase.CreateAsset(mesh, path);
                return mesh;
            }

            EditorUtility.CopySerialized(mesh, existing);
            EditorUtility.SetDirty(existing);
            return existing;
        }

        private static Material SaveMaterialAsset(Material material, string path)
        {
            material.name = Path.GetFileNameWithoutExtension(path);
            Material existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing == null)
            {
                AssetDatabase.CreateAsset(material, path);
                return material;
            }

            EditorUtility.CopySerialized(material, existing);
            EditorUtility.SetDirty(existing);
            return existing;
        }
    }
}
