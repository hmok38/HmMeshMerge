using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace HmMeshMergeEditor
{
    /// <summary>按源贴图的实际格式和 mip 写入可持久化的数组，依赖源贴图的导入产物。</summary>
    [ScriptedImporter(VERSION, EXTENSION)]
    public sealed class HmMeshMergeTextureArrayImporter : ScriptedImporter
    {
        public const string EXTENSION = "hmtexarray";
        public const string TEXTURES_FIELD = "_textures";
        private const int VERSION = 3;

        [Tooltip("来源贴图，顺序即数组层号；需开启 Read/Write，以便保存实际像素数据。")]
        [SerializeField] private List<Texture2D> _textures = new List<Texture2D>();

        /// <summary>读取来源顺序；修改请通过导入器的序列化设置。</summary>
        public IReadOnlyList<Texture2D> Textures => _textures;

        /// <summary>导入时记录依赖；失败也记录有效来源，修复源贴图后可重新触发导入。</summary>
        public override void OnImportAsset(AssetImportContext ctx)
        {
            foreach (Texture2D texture in _textures)
            {
                if (texture != null)
                {
                    string path = AssetDatabase.GetAssetPath(texture);
                    // 内置贴图也有非空路径和特殊 GUID，但没有可登记的导入产物。
                    if (IsImportedAssetPath(path))
                    {
                        ctx.DependsOnArtifact(path);
                    }
                }
            }

            // 创建空源文件后，Builder 会立即写入引用再重导；此阶段不产生虚假警告。
            if (_textures.Count == 0)
            {
                return;
            }

            // 明细只在点击“执行合并”时由 Builder 报出；重导阶段只说明数组没生成，避免非合并时机刷出长清单。
            if (!ValidateTextures(_textures, out _))
            {
                ctx.LogImportError($"{ctx.assetPath} 的来源贴图参数不一致，未生成数组。" +
                    "在网格合并窗口点击“执行合并”可看到需要修改的项与建议尺寸。");
                return;
            }

            Texture2DArray array = null;
            try
            {
                Texture2D master = _textures[0];
                bool linear = !GraphicsFormatUtility.IsSRGBFormat(master.graphicsFormat);
                array = new Texture2DArray(master.width, master.height, _textures.Count,
                    master.format, master.mipmapCount, linear)
                {
                    name = "TextureArray",
                    filterMode = master.filterMode,
                    wrapModeU = master.wrapModeU,
                    wrapModeV = master.wrapModeV,
                    wrapModeW = TextureWrapMode.Clamp,
                    anisoLevel = master.anisoLevel,
                    mipMapBias = master.mipMapBias
                };
                if (array.graphicsFormat != master.graphicsFormat || array.mipmapCount != master.mipmapCount)
                {
                    throw new InvalidOperationException("当前平台创建数组发生格式回退，或源贴图 mip 链不完整。");
                }

                for (int layer = 0; layer < _textures.Count; layer++)
                {
                    for (int mip = 0; mip < master.mipmapCount; mip++)
                    {
                        // 写入 CPU 数据，保证导入资产保存的内容与上传到 GPU 的内容一致。
                        array.SetPixelData(_textures[layer].GetPixelData<byte>(mip), mip, layer);
                    }
                }

                array.Apply(false, false);
                ctx.AddObjectToAsset("TextureArray", array);
                ctx.SetMainObject(array);
                array = null;
            }
            catch (Exception exception)
            {
                ctx.LogImportError($"纹理数组导入失败：{exception}");
            }
            finally
            {
                if (array != null)
                {
                    DestroyImmediate(array);
                }
            }
        }

        private static bool IsImportedAssetPath(string path)
        {
            return !string.IsNullOrEmpty(path) &&
                (path.StartsWith("Assets/", StringComparison.Ordinal) ||
                path.StartsWith("Packages/", StringComparison.Ordinal));
        }

        internal static bool ValidateTextures(IReadOnlyList<Texture2D> textures, out string reason)
        {
            if (textures.Count == 0 || textures.Count > SystemInfo.maxTextureArraySlices)
            {
                reason = $"数组层数 {textures.Count} 无效，当前设备上限为 {SystemInfo.maxTextureArraySlices}。";
                return false;
            }

            if (textures[0] == null)
            {
                reason = "第 0 层为空或不是 Texture2D。";
                return false;
            }

            for (int i = 0; i < textures.Count; i++)
            {
                if (textures[i] == null)
                {
                    reason = $"第 {i} 层为空或不是 Texture2D；启用数组的每个来源都必须指定二维贴图。";
                    return false;
                }

                if (!IsImportedAssetPath(AssetDatabase.GetAssetPath(textures[i])))
                {
                    reason = $"第 {i} 层 {textures[i].name} 是内置或非资产贴图，无法作为数组的导入来源。" +
                        "请停用该属性的数组化，或为每个来源指定 Assets / Packages 中的实际贴图。";
                    return false;
                }
            }

            Texture2D master = textures[0];
            ResolveTargetSize(textures, out int targetWidth, out int targetHeight);
            var blocks = new List<string>();
            for (int i = 0; i < textures.Count; i++)
            {
                string block = DescribeProblems(i, master, textures[i], targetWidth, targetHeight);
                if (block != null)
                {
                    blocks.Add(block);
                }
            }

            if (blocks.Count == 0)
            {
                reason = string.Empty;
                return true;
            }

            var listing = new List<string>();
            for (int i = 0; i < textures.Count; i++)
            {
                listing.Add(DescribeLayer(i, textures[i]));
            }

            var lines = new List<string>
            {
                "数组各层必须完全一致：宽高、实际格式（含 sRGB）、mip 层数、Read/Write、Crunch 与采样设置。",
                "数组尺寸取自第 0 层，宽高建议统一为各层最大尺寸。",
                string.Empty,
                "建议修改的来源贴图：",
                string.Join("\n\n", blocks),
                string.Empty,
                "全部来源贴图：",
                string.Join("\n\n", listing),
                string.Empty,
                "按上表修改对应源贴图的导入设置后重新合并。"
            };
            reason = string.Join("\n", lines);
            return false;
        }

        /// <summary>数组各层必须同尺寸；取各层最大宽高作为建议目标，任何一层都不必降分辨率。</summary>
        private static void ResolveTargetSize(IReadOnlyList<Texture2D> textures, out int width, out int height)
        {
            width = 0;
            height = 0;
            foreach (Texture2D texture in textures)
            {
                width = Mathf.Max(width, texture.width);
                height = Mathf.Max(height, texture.height);
            }
        }

        /// <summary>列出该层需要手动修改的参数与建议；无需修改时返回 null。</summary>
        private static string DescribeProblems(int index, Texture2D master, Texture2D texture,
            int targetWidth, int targetHeight)
        {
            var problems = new List<string>();
            if (texture.width != targetWidth || texture.height != targetHeight)
            {
                problems.Add($"宽高 {texture.width}×{texture.height}，建议调整为 {targetWidth}×{targetHeight}");
            }

            if (texture.graphicsFormat != master.graphicsFormat)
            {
                problems.Add($"实际格式 {DescribeFormat(texture)}，建议改成与第 0 层相同的 {DescribeFormat(master)}");
            }

            if (texture.mipmapCount != master.mipmapCount)
            {
                problems.Add($"mip {texture.mipmapCount} 层，建议改成与第 0 层相同的 {master.mipmapCount} 层");
            }

            if (texture.filterMode != master.filterMode || texture.wrapModeU != master.wrapModeU ||
                texture.wrapModeV != master.wrapModeV || texture.anisoLevel != master.anisoLevel ||
                texture.mipMapBias != master.mipMapBias)
            {
                problems.Add($"采样设置 {DescribeSampling(texture)}，建议改成与第 0 层相同的 {DescribeSampling(master)}");
            }

            if (!texture.isReadable)
            {
                problems.Add("未开启 Read/Write，建议在导入设置里勾选 Read/Write");
            }

            if (IsCrunched(texture))
            {
                problems.Add($"使用了 {texture.format} 压缩，建议关闭 Crunch 压缩");
            }

            if (problems.Count == 0)
            {
                return null;
            }

            var lines = new List<string>
            {
                $"第 {index} 层 {texture.name}",
                $"  路径  {AssetDatabase.GetAssetPath(texture)}"
            };
            lines.AddRange(problems.ConvertAll(problem => "  " + problem));
            return string.Join("\n", lines);
        }

        /// <summary>逐层列出自查用的实际参数，便于按表修改源贴图导入设置。</summary>
        private static string DescribeLayer(int index, Texture2D texture)
        {
            var lines = new List<string>
            {
                $"{index}  {texture.name}",
                $"  路径  {AssetDatabase.GetAssetPath(texture)}",
                $"  宽高  {texture.width}×{texture.height}",
                $"  格式  {DescribeFormat(texture)}",
                $"  mip   {texture.mipmapCount} 层，Read/Write {(texture.isReadable ? "开" : "关")}，" +
                $"Crunch {(IsCrunched(texture) ? "有" : "无")}",
                $"  采样  {DescribeSampling(texture)}"
            };
            return string.Join("\n", lines);
        }

        private static string DescribeFormat(Texture2D texture)
        {
            bool sRGB = GraphicsFormatUtility.IsSRGBFormat(texture.graphicsFormat);
            return $"{texture.graphicsFormat}（{(sRGB ? "sRGB" : "线性")}）";
        }

        private static string DescribeSampling(Texture2D texture)
        {
            return $"{texture.filterMode}/{texture.wrapModeU}/{texture.wrapModeV}/aniso {texture.anisoLevel}/" +
                $"bias {texture.mipMapBias}";
        }

        private static bool IsCrunched(Texture2D texture)
        {
            return texture.format == TextureFormat.DXT1Crunched || texture.format == TextureFormat.DXT5Crunched ||
                texture.format == TextureFormat.ETC_RGB4Crunched || texture.format == TextureFormat.ETC2_RGBA8Crunched;
        }
    }
}
