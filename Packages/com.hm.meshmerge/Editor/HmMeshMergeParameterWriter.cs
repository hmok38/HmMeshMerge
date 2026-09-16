using System;
using System.Collections.Generic;
using HmMeshMerge;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace HmMeshMergeEditor
{
    /// <summary>维护稳定参数行，并把启用的数值写入线性、无压缩的浮点查找纹理。</summary>
    internal static class HmMeshMergeParameterWriter
    {
        public const string TEXTURE_NAME = "_HmMeshMergeParams";
        public const int MAX_SOURCE_COUNT = 2048;

        /// <summary>首次按差异排序；不把数值恰好相同的不同属性当作别名。</summary>
        public static List<HmMeshMergeParameterEntry> BuildInitialTable(IReadOnlyList<HmMeshMergeSource> sources)
        {
            var differing = new List<string>();
            var identical = new List<string>();
            var table = new List<HmMeshMergeParameterEntry>();
            if (sources.Count == 0 || sources[0] == null || sources[0].material == null)
            {
                return table;
            }

            Shader shader = sources[0].material.shader;
            if (shader == null)
            {
                return table;
            }

            for (int i = 0; i < shader.GetPropertyCount(); i++)
            {
                string name = shader.GetPropertyName(i);
                if (IsEngineProperty(name))
                {
                    continue;
                }

                if (HasPropertyOnAll(sources, name))
                {
                    (SourcesDiffer(sources, name) ? differing : identical).Add(name);
                }

                if (shader.GetPropertyType(i) == ShaderPropertyType.Texture &&
                    shader.GetPropertyTextureDimension(i) == TextureDimension.Tex2D &&
                    shader.FindPropertyIndex(name + "_ST") < 0 && HasPropertyOnAll(sources, name + "_ST"))
                {
                    string transformName = name + "_ST";
                    (SourcesDiffer(sources, transformName) ? differing : identical).Add(transformName);
                }
            }

            foreach (string name in differing)
            {
                table.Add(new HmMeshMergeParameterEntry { propertyName = name, row = table.Count, active = true });
            }

            foreach (string name in identical)
            {
                table.Add(new HmMeshMergeParameterEntry { propertyName = name, row = table.Count, active = false });
            }

            return table;
        }

        /// <summary>保留已有行号和选择，只在末尾追加新属性；失效项停用但保留行。</summary>
        public static List<HmMeshMergeParameterEntry> RefreshTable(IReadOnlyList<HmMeshMergeSource> sources,
            IReadOnlyList<HmMeshMergeParameterEntry> previous)
        {
            var table = new List<HmMeshMergeParameterEntry>();
            var names = new HashSet<string>();
            int nextRow = 0;
            foreach (HmMeshMergeParameterEntry old in previous)
            {
                HmMeshMergeParameterEntry entry = old;
                if (!HasPropertyOnAll(sources, entry.propertyName))
                {
                    entry.active = false;
                }

                table.Add(entry);
                names.Add(entry.propertyName);
                nextRow = Mathf.Max(nextRow, entry.row + 1);
            }

            foreach (HmMeshMergeParameterEntry candidate in BuildInitialTable(sources))
            {
                if (!names.Add(candidate.propertyName))
                {
                    continue;
                }

                HmMeshMergeParameterEntry entry = candidate;
                entry.row = nextRow++;
                table.Add(entry);
            }

            return table;
        }

        internal static bool IsEngineProperty(string name)
        {
            return name != null && name.StartsWith("unity_", StringComparison.Ordinal);
        }

        public static bool TryGetType(Shader shader, string name, out ShaderPropertyType type)
        {
            type = ShaderPropertyType.Vector;
            if (shader == null || string.IsNullOrEmpty(name) || IsEngineProperty(name))
            {
                return false;
            }

            int index = shader.FindPropertyIndex(name);
            if (index >= 0)
            {
                type = shader.GetPropertyType(index);
                return true;
            }

            if (!name.EndsWith("_ST", StringComparison.Ordinal))
            {
                return false;
            }

            index = shader.FindPropertyIndex(name.Substring(0, name.Length - 3));
            return index >= 0 && shader.GetPropertyType(index) == ShaderPropertyType.Texture &&
                shader.GetPropertyTextureDimension(index) == TextureDimension.Tex2D;
        }

        private static bool HasPropertyOnAll(IReadOnlyList<HmMeshMergeSource> sources, string name)
        {
            if (sources.Count == 0)
            {
                return false;
            }

            foreach (HmMeshMergeSource source in sources)
            {
                if (source == null || source.material == null || !TryGetType(source.material.shader, name, out _))
                {
                    return false;
                }
            }

            return true;
        }

        public static bool SourcesDiffer(IReadOnlyList<HmMeshMergeSource> sources, string name)
        {
            TryGetType(sources[0].material.shader, name, out ShaderPropertyType type);
            Material first = sources[0].material;
            for (int i = 1; i < sources.Count; i++)
            {
                Material other = sources[i].material;
                bool same = type == ShaderPropertyType.Texture
                    ? first.GetTexture(name) == other.GetTexture(name)
                    : ReadValue(first, name).Equals(ReadValue(other, name));
                if (!same)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>按原始精度读取；颜色保持材质 API 的值，写 LUT 时再做颜色空间转换。</summary>
        public static Vector4 ReadValue(Material material, string name)
        {
            if (!TryGetType(material.shader, name, out ShaderPropertyType type))
            {
                throw new ArgumentException($"材质 {material.name} 不存在参数 {name}。");
            }

            if (material.shader.FindPropertyIndex(name) < 0)
            {
                string textureName = name.Substring(0, name.Length - 3);
                Vector2 scale = material.GetTextureScale(textureName);
                Vector2 offset = material.GetTextureOffset(textureName);
                return new Vector4(scale.x, scale.y, offset.x, offset.y);
            }

            switch (type)
            {
                case ShaderPropertyType.Color:
                    return material.GetColor(name);
                case ShaderPropertyType.Vector:
                    return material.GetVector(name);
                case ShaderPropertyType.Int:
                    return new Vector4(material.GetInteger(name), 0f, 0f, 0f);
                case ShaderPropertyType.Float:
                case ShaderPropertyType.Range:
                    return new Vector4(material.GetFloat(name), 0f, 0f, 0f);
                default:
                    throw new ArgumentException($"{name} 是纹理，不能作为数值读取。");
            }
        }

        public static void Validate(IReadOnlyList<HmMeshMergeSource> sources,
            IReadOnlyList<HmMeshMergeParameterEntry> table, List<string> errors)
        {
            var rows = new HashSet<int>();
            var names = new HashSet<string>();
            int limit = Mathf.Min(MAX_SOURCE_COUNT, SystemInfo.maxTextureSize);
            foreach (HmMeshMergeParameterEntry entry in table)
            {
                if (entry.row < 0 || entry.row >= limit || !rows.Add(entry.row))
                {
                    errors.Add($"参数 {entry.propertyName} 的行号 {entry.row} 无效、重复或超过上限 {limit - 1}。");
                }

                if (!entry.active)
                {
                    continue;
                }

                if (!names.Add(entry.propertyName) || !HasPropertyOnAll(sources, entry.propertyName))
                {
                    errors.Add($"启用参数 {entry.propertyName} 重复，或源材质中不存在；请刷新参数表。");
                    continue;
                }

                TryGetType(sources[0].material.shader, entry.propertyName, out ShaderPropertyType type);
                if (type == ShaderPropertyType.Texture)
                {
                    continue;
                }

                foreach (HmMeshMergeSource source in sources)
                {
                    Vector4 value = ReadValue(source.material, entry.propertyName);
                    for (int component = 0; component < 4; component++)
                    {
                        if (float.IsNaN(value[component]) || float.IsInfinity(value[component]))
                        {
                            errors.Add($"{source.material.name}：{entry.propertyName} 含非有限数值。");
                            break;
                        }
                    }

                    if (type == ShaderPropertyType.Int &&
                        (double)source.material.GetInteger(entry.propertyName) != value.x)
                    {
                        errors.Add($"{source.material.name}：整数 {entry.propertyName} 无法用浮点 LUT 精确表示。");
                    }
                }
            }
        }

        /// <summary>按去重后的材质列出数值列。无启用数值时返回 null；保留全部已分配行的高度，空行写零。</summary>
        public static Texture2D Build(IReadOnlyList<Material> materials,
            IReadOnlyList<HmMeshMergeParameterEntry> table)
        {
            int height = 0;
            bool hasValues = false;
            Shader shader = materials[0].shader;
            foreach (HmMeshMergeParameterEntry entry in table)
            {
                height = Mathf.Max(height, entry.row + 1);
                hasValues |= entry.active && TryGetType(shader, entry.propertyName, out ShaderPropertyType type) &&
                    type != ShaderPropertyType.Texture;
            }

            if (!hasValues)
            {
                return null;
            }

            int width = materials.Count;
            var pixels = new Color[width * height];
            foreach (HmMeshMergeParameterEntry entry in table)
            {
                if (!entry.active || !TryGetType(shader, entry.propertyName, out ShaderPropertyType type) ||
                    type == ShaderPropertyType.Texture)
                {
                    continue;
                }

                for (int i = 0; i < materials.Count; i++)
                {
                    Color value = ReadValue(materials[i], entry.propertyName);
                    if (type == ShaderPropertyType.Color && QualitySettings.activeColorSpace == ColorSpace.Linear)
                    {
                        value = value.linear;
                    }

                    pixels[entry.row * width + i] = value;
                }
            }

            var texture = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            try
            {
                texture.SetPixels(pixels);
                texture.Apply(false, false);
                return texture;
            }
            catch
            {
                Object.DestroyImmediate(texture);
                throw;
            }
        }

        /// <summary>把参数 LUT 保存为「{配置名}_ParamLut.asset」并保持既有 GUID；调用方负责释放尚未移交给资产库的临时纹理。</summary>
        public static Texture2D Save(Texture2D texture, string assetName, string assetFolder)
        {
            if (texture == null)
            {
                return null;
            }

            string path = $"{assetFolder}/{assetName}_ParamLut.asset";
            texture.name = assetName + "_ParamLut";
            Texture2D existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (existing == null)
            {
                AssetDatabase.CreateAsset(texture, path);
                return texture;
            }

            EditorUtility.CopySerialized(texture, existing);
            EditorUtility.SetDirty(existing);
            return existing;
        }
    }
}
