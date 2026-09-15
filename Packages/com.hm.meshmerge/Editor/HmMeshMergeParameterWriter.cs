using System.Collections.Generic;
using System.IO;
using HmMeshMerge;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace HmMeshMergeEditor
{
    /// <summary>
    /// 管理参数表并把各来源的参数写进查找纹理。
    /// 纹理横轴是来源索引，纵轴是参数表的行；每行一个参数，行号一旦分配就保持稳定，
    /// 使用者复制到自己着色器里的读取行号不会因为增删参数而失效。
    /// </summary>
    internal static class HmMeshMergeParameterWriter
    {
        /// <summary>参数纹理在着色器里的名字。</summary>
        public const string TEXTURE_NAME = "_HmMeshMergeParams";

        /// <summary>纹理宽度上限；来源数量超过它就无法用索引列表示。</summary>
        public const int MAX_SOURCE_COUNT = 2048;

        private static readonly Color32 EMPTY_PIXEL = new Color32(255, 255, 255, 255);

        /// <summary>按参数表生成查找纹理：每行一个参数，横向为来源索引；空行填中性值。</summary>
        public static Texture2D Build(List<HmMeshMergeSource> sources, IReadOnlyList<HmMeshMergeParameterEntry> table)
        {
            int width = Mathf.NextPowerOfTwo(sources.Count);
            int height = Mathf.Max(1, CountRows(table));
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var pixels = new Color32[width * height];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = EMPTY_PIXEL;
            }

            foreach (HmMeshMergeParameterEntry entry in table)
            {
                // 贴图属性由纹理数组承载，这里只写数值参数。
                if (!entry.active || IsTextureProperty(sources, entry.propertyName))
                {
                    continue;
                }

                for (int i = 0; i < sources.Count; i++)
                {
                    pixels[entry.row * width + i] = ReadValue(sources[i].material, entry.propertyName);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply();
            return texture;
        }

        /// <summary>写入参数纹理并配置导入参数；返回导入后的纹理资产。</summary>
        public static Texture2D Save(Texture2D texture, string assetName, string assetFolder)
        {
            string path = $"{assetFolder}/{assetName}_Params.png";
            File.WriteAllBytes(path, texture.EncodeToPNG());
            Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = false;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Point;
            importer.npotScale = TextureImporterNPOTScale.None;
            // 关键：查找纹理必须逐像素精确。ASTC 6x6 会让 6 个相邻来源共用一块而互相污染，
            // 有损压缩也会改变裁剪阈值这类需要精确的数值；纹理很小，不压缩无负担。
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        /// <summary>
        /// 首次填表：按源材质的差异生成参数表——各源取值不同的属性排在前并启用，取值相同的排在后且不启用。
        /// 源材质缺少的属性不列入（合并时会另行报错）。只有取值不同的属性才需要按来源区分。
        /// </summary>
        public static List<HmMeshMergeParameterEntry> BuildInitialTable(IReadOnlyList<HmMeshMergeSource> sources)
        {
            var table = new List<HmMeshMergeParameterEntry>();
            if (sources.Count == 0 || sources[0].material == null)
            {
                return table;
            }

            Shader shader = sources[0].material.shader;

            // 先收集可直接比较的属性，并跳过"与已收录项取值完全相同"的别名
            // （URP Lit 同时声明 _BaseMap/_MainTex、_BaseColor/_Color，指向同一份数据）。
            var candidates = new List<string>();
            var types = new Dictionary<string, ShaderPropertyType>();
            for (int i = 0; i < shader.GetPropertyCount(); i++)
            {
                string propertyName = shader.GetPropertyName(i);
                if (!HasPropertyOnAll(sources, propertyName))
                {
                    continue;
                }

                ShaderPropertyType type = shader.GetPropertyType(i);
                if (IsAliasOfAccepted(sources, propertyName, candidates, types))
                {
                    continue;
                }

                candidates.Add(propertyName);
                types[propertyName] = type;
            }

            var differing = new List<string>();
            var identical = new List<string>();
            foreach (string propertyName in candidates)
            {
                if (SourcesDiffer(sources, propertyName, types[propertyName]))
                {
                    differing.Add(propertyName);
                }
                else
                {
                    identical.Add(propertyName);
                }
            }

            AppendRows(table, differing, true);
            AppendRows(table, identical, false);
            return table;
        }

        /// <summary>该属性是否与已收录的某个属性取值完全相同（贴图比引用，数值比读出的值）。</summary>
        private static bool IsAliasOfAccepted(IReadOnlyList<HmMeshMergeSource> sources, string candidate,
            List<string> accepted, Dictionary<string, ShaderPropertyType> types)
        {
            foreach (string name in accepted)
            {
                bool same = true;
                foreach (HmMeshMergeSource source in sources)
                {
                    Material material = source.material;
                    same = types[name] == ShaderPropertyType.Texture
                        ? material.GetTexture(candidate) == material.GetTexture(name)
                        : ReadValue(material, candidate).Equals(ReadValue(material, name));
                    if (!same)
                    {
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

        private static void AppendRows(List<HmMeshMergeParameterEntry> table, List<string> names, bool active)
        {
            foreach (string name in names)
            {
                table.Add(new HmMeshMergeParameterEntry { propertyName = name, row = table.Count, active = active });
            }
        }

        private static bool HasPropertyOnAll(IReadOnlyList<HmMeshMergeSource> sources, string propertyName)
        {
            foreach (HmMeshMergeSource source in sources)
            {
                if (source.material == null || !source.material.HasProperty(propertyName))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>各源在该属性上取值是否不同；贴图比较引用，数值按类型读取后比较。</summary>
        private static bool SourcesDiffer(IReadOnlyList<HmMeshMergeSource> sources, string propertyName, ShaderPropertyType type)
        {
            if (type == ShaderPropertyType.Texture)
            {
                Texture firstTexture = sources[0].material.GetTexture(propertyName);
                for (int i = 1; i < sources.Count; i++)
                {
                    if (sources[i].material.GetTexture(propertyName) != firstTexture)
                    {
                        return true;
                    }
                }

                return false;
            }

            Color32 first = ReadValue(sources[0].material, propertyName);
            for (int i = 1; i < sources.Count; i++)
            {
                if (!ReadValue(sources[i].material, propertyName).Equals(first))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>贴图属性由纹理数组承载，不进数值参数纹理。</summary>
        private static bool IsTextureProperty(List<HmMeshMergeSource> sources, string propertyName)
        {
            if (sources.Count == 0 || sources[0].material == null)
            {
                return false;
            }

            Shader shader = sources[0].material.shader;
            int index = shader.FindPropertyIndex(propertyName);
            return index >= 0 && shader.GetPropertyType(index) == ShaderPropertyType.Texture;
        }

        /// <summary>按源材质的属性类型取值；属性不存在时返回中性值。</summary>
        public static Color32 ReadValue(Material material, string propertyName)
        {
            if (!material.HasProperty(propertyName))
            {
                return EMPTY_PIXEL;
            }

            int index = material.shader.FindPropertyIndex(propertyName);
            ShaderPropertyType type = index < 0 ? ShaderPropertyType.Float : material.shader.GetPropertyType(index);
            switch (type)
            {
                case ShaderPropertyType.Color:
                    Color color = material.GetColor(propertyName);
                    return new Color32(ToByte(color.r), ToByte(color.g), ToByte(color.b), ToByte(color.a));
                case ShaderPropertyType.Vector:
                    Vector4 vector = material.GetVector(propertyName);
                    return new Color32(ToByte(vector.x), ToByte(vector.y), ToByte(vector.z), ToByte(vector.w));
                default:
                    return new Color32(ToByte(material.GetFloat(propertyName)), 0, 0, 0);
            }
        }

        private static int CountRows(IReadOnlyList<HmMeshMergeParameterEntry> table)
        {
            int rows = 0;
            foreach (HmMeshMergeParameterEntry entry in table)
            {
                if (entry.row >= rows)
                {
                    rows = entry.row + 1;
                }
            }

            return rows;
        }

        private static byte ToByte(float value)
        {
            return (byte)(Mathf.Clamp01(value) * 255f);
        }
    }
}
