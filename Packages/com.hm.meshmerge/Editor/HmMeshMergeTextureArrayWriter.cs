using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEditor.PackageManager;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Object = UnityEngine.Object;

namespace HmMeshMergeEditor
{
    /// <summary>生成分格源图，由 Unity 原生 TextureImporter 导入为二维数组。</summary>
    internal static class HmMeshMergeTextureArrayWriter
    {
        /// <summary>按材质顺序拼图并同步导入；源图变化后由用户再次执行合并。</summary>
        internal static Texture2DArray Build(IReadOnlyList<Texture2D> textures, string outputPrefix)
        {
            if (!ValidateTextures(textures, out string reason, out _))
            {
                throw new InvalidOperationException(reason);
            }

            Texture2D master = textures[0];
            TryGetLayout(master.width, master.height, textures.Count, out int columns, out int rows, out _);
            bool hdr = NeedsFloatSource(master);
            string path = outputPrefix + (hdr ? ".exr" : ".png");
            bool existed = File.Exists(path);
            Texture2D sheet = null;
            string temporaryFolder = "Assets/__HmMeshMergeTextureRead_" + Guid.NewGuid().ToString("N");
            try
            {
                AssetDatabase.CreateFolder("Assets", Path.GetFileName(temporaryFolder));
                sheet = new Texture2D(master.width * columns, master.height * rows,
                    hdr ? TextureFormat.RGBAFloat : TextureFormat.RGBA32, false, true);
                for (int layer = 0; layer < textures.Count; layer++)
                {
                    Color[] pixels = ReadSourcePixels(textures[layer], temporaryFolder);
                    // 原生 flipbook 从左上角逐行取层；SetPixels 的原点在左下角。
                    int x = layer % columns * master.width;
                    int y = (rows - 1 - layer / columns) * master.height;
                    sheet.SetPixels(x, y, master.width, master.height, pixels);
                }

                byte[] bytes = hdr
                    ? sheet.EncodeToEXR(Texture2D.EXRFlags.OutputAsFloat | Texture2D.EXRFlags.CompressZIP)
                    : sheet.EncodeToPNG();
                File.WriteAllBytes(path, bytes);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport |
                    ImportAssetOptions.ForceUpdate);
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null)
                {
                    throw new InvalidOperationException($"未找到原生贴图导入器：{path}");
                }

                TextureImporterSettings settings = ReadArraySettings(master);
                settings.flipbookColumns = columns;
                settings.flipbookRows = rows;
                importer.SetTextureSettings(settings);
                if (!existed)
                {
                    CopyPlatformSettings(GetImporter(master), importer, Mathf.Max(sheet.width, sheet.height));
                }

                importer.SaveAndReimport();
                CheckImportLog(path);
                var array = AssetDatabase.LoadAssetAtPath<Texture2DArray>(path);
                if (array == null || array.width != master.width || array.height != master.height ||
                    array.depth != textures.Count || array.mipmapCount != master.mipmapCount ||
                    GraphicsFormatUtility.IsSRGBFormat(array.graphicsFormat) !=
                    GraphicsFormatUtility.IsSRGBFormat(master.graphicsFormat))
                {
                    throw new InvalidOperationException($"原生数组 {path} 的宽高、层数、mip 或色彩空间不符。" +
                        "请检查该数组的 Max Size、平台覆盖设置和导入日志；插件不会自动缩小来源贴图。");
                }

                return array;
            }
            finally
            {
                if (sheet != null)
                {
                    Object.DestroyImmediate(sheet);
                }

                if (AssetDatabase.IsValidFolder(temporaryFolder) && !AssetDatabase.DeleteAsset(temporaryFolder))
                {
                    Debug.LogWarning($"临时贴图目录清理失败，请手动删除：{temporaryFolder}");
                }
            }
        }

        /// <summary>读取未压缩、未做法线/通道处理的源像素；只导入临时副本，不改来源资产。</summary>
        private static Color[] ReadSourcePixels(Texture2D source, string temporaryFolder)
        {
            string sourcePath = AssetDatabase.GetAssetPath(source);
            string path = temporaryFolder + "/Source" + Path.GetExtension(sourcePath);
            PackageInfo package = PackageInfo.FindForAssetPath(sourcePath);
            string sourceFile = package == null ? sourcePath :
                Path.Combine(package.resolvedPath, sourcePath.Substring(package.assetPath.Length + 1));
            TextureImporter original = GetImporter(source);
            try
            {
                File.Copy(sourceFile, path);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null)
                {
                    throw new InvalidOperationException($"无法读取原生贴图源文件：{sourcePath}");
                }

                var settings = new TextureImporterSettings();
                settings.ApplyTextureType(TextureImporterType.Default);
                settings.textureShape = TextureImporterShape.Texture2D;
                settings.readable = true;
                settings.sRGBTexture = original.sRGBTexture;
                settings.ignorePngGamma = original.ignorePngGamma;
                settings.npotScale = original.npotScale;
                settings.mipmapEnabled = false;
                settings.ignoreMipmapLimit = true;
                settings.alphaSource = TextureImporterAlphaSource.FromInput;
                settings.alphaIsTransparency = false;
                importer.SetTextureSettings(settings);
                ClearPlatformOverrides(importer);
                TextureImporterPlatformSettings platform = original.GetPlatformTextureSettings(
                    BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget).ToString());
                var defaults = importer.GetDefaultPlatformTextureSettings();
                defaults.maxTextureSize = Mathf.NextPowerOfTwo(Mathf.Max(source.width, source.height));
                defaults.resizeAlgorithm = platform.overridden ? platform.resizeAlgorithm :
                    original.GetDefaultPlatformTextureSettings().resizeAlgorithm;
                defaults.format = NeedsFloatSource(source) ? TextureImporterFormat.RGBAFloat :
                    TextureImporterFormat.RGBA32;
                defaults.textureCompression = TextureImporterCompression.Uncompressed;
                defaults.crunchedCompression = false;
                importer.SetPlatformTextureSettings(defaults);
                importer.SaveAndReimport();
                CheckImportLog(path);
                var readable = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (readable == null || readable.width != source.width || readable.height != source.height)
                {
                    throw new InvalidOperationException($"{sourcePath} 的源图解码尺寸与当前导入尺寸不符，" +
                        "请检查源贴图的 Max Size、NPOT 和 Mipmap Limit 设置后重新合并。");
                }

                return readable.GetPixels();
            }
            finally
            {
                if (File.Exists(path) && !AssetDatabase.DeleteAsset(path))
                {
                    Debug.LogWarning($"临时贴图清理失败，请手动删除：{path}");
                }
            }
        }

        private static TextureImporter GetImporter(Texture2D texture)
        {
            return (TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture));
        }

        private static bool NeedsFloatSource(Texture2D texture)
        {
            GraphicsFormat format = texture.graphicsFormat;
            return GraphicsFormatUtility.IsHDRFormat(format) ||
                !GraphicsFormatUtility.IsCompressedFormat(format) &&
                GraphicsFormatUtility.GetBlockSize(format) > GraphicsFormatUtility.GetComponentCount(format);
        }

        private static TextureImporterSettings ReadArraySettings(Texture2D texture)
        {
            var settings = new TextureImporterSettings();
            GetImporter(texture).ReadTextureSettings(settings);
            settings.textureShape = TextureImporterShape.Texture2DArray;
            settings.flipbookColumns = 1;
            settings.flipbookRows = 1;
            settings.npotScale = TextureImporterNPOTScale.None;
            settings.readable = false;
            settings.streamingMipmaps = false;
            settings.streamingMipmapsPriority = 0;
            settings.ignoreMipmapLimit = true;
            settings.wrapModeW = TextureWrapMode.Clamp;
            return settings;
        }

        /// <summary>首次生成继承第 0 层的所有平台格式；以后保留用户在数组 Inspector 中的覆盖设置。</summary>
        private static void CopyPlatformSettings(TextureImporter source, TextureImporter target, int sheetSize)
        {
            ClearPlatformOverrides(target);
            using (var serialized = new SerializedObject(source))
            {
                SerializedProperty platforms = serialized.FindProperty("m_PlatformSettings");
                for (int i = 0; i < platforms.arraySize; i++)
                {
                    string name = platforms.GetArrayElementAtIndex(i).FindPropertyRelative("m_BuildTarget").stringValue;
                    TextureImporterPlatformSettings settings = source.GetPlatformTextureSettings(name);
                    settings.maxTextureSize = Mathf.NextPowerOfTwo(sheetSize);
                    settings.crunchedCompression = false;
                    // Crunch 是二维贴图容器格式；数组交给原生导入器选择对应平台压缩格式。
                    if (settings.format.ToString().Contains("Crunched"))
                    {
                        settings.format = TextureImporterFormat.Automatic;
                    }

                    target.SetPlatformTextureSettings(settings);
                }
            }
        }

        private static void ClearPlatformOverrides(TextureImporter importer)
        {
            var names = new List<string>();
            using (var serialized = new SerializedObject(importer))
            {
                SerializedProperty platforms = serialized.FindProperty("m_PlatformSettings");
                for (int i = 0; i < platforms.arraySize; i++)
                {
                    names.Add(platforms.GetArrayElementAtIndex(i).FindPropertyRelative("m_BuildTarget").stringValue);
                }
            }

            foreach (string name in names)
            {
                if (name != importer.GetDefaultPlatformTextureSettings().name)
                {
                    importer.ClearPlatformTextureSettings(name);
                }
            }
        }

        private static void CheckImportLog(string path)
        {
            ImportLog log = AssetImporter.GetImportLog(path);
            if (log == null)
            {
                return;
            }

            foreach (ImportLog.ImportLogEntry entry in log.logEntries)
            {
                if ((entry.flags & ImportLogFlags.Error) != 0)
                {
                    throw new InvalidOperationException($"贴图导入失败：{path}\n{entry.message}");
                }
            }
        }

        /// <summary>只使用恰好容纳所有层的网格，避免补空层改变数组深度。</summary>
        private static bool TryGetLayout(int width, int height, int count, out int columns, out int rows,
            out string reason)
        {
            columns = 0;
            rows = 0;
            int limit = Mathf.Min(16384, SystemInfo.maxTextureSize);
            int bestSide = int.MaxValue;
            for (int candidate = 1; candidate <= count; candidate++)
            {
                if (count % candidate != 0 || width > limit / candidate || height > limit / (count / candidate))
                {
                    continue;
                }

                int side = Mathf.Max(width * candidate, height * (count / candidate));
                if (side <= bestSide)
                {
                    columns = candidate;
                    rows = count / candidate;
                    bestSide = side;
                }
            }

            reason = columns > 0 ? string.Empty :
                $"{count} 层 {width}×{height} 贴图无法在 {limit}×{limit} 内组成无空层的分格源图。" +
                "请减少本次合并的材质数量，或手工统一为更小的源贴图尺寸。";
            return columns > 0;
        }

        /// <summary>校验各层能否组成数组：reason 为汇总文本，problems 为需要修改的贴图及其建议，供调用方合并后输出。</summary>
        internal static bool ValidateTextures(IReadOnlyList<Texture2D> textures, out string reason,
            out List<(Texture2D texture, string advice)> problems)
        {
            problems = new List<(Texture2D texture, string advice)>();
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
                    reason = $"第 {i} 层为空或不是 Texture2D；启用数组的每个材质都必须指定二维贴图。";
                    return false;
                }

                if (!(AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(textures[i])) is TextureImporter))
                {
                    reason = $"第 {i} 层 {textures[i].name} 没有原生 TextureImporter，无法读取数组源图。" +
                        "请停用该属性的数组化，或为每个材质指定 Assets / Packages 中的实际贴图。";
                    return false;
                }
            }

            Texture2D master = textures[0];
            ResolveTargetSize(textures, out int targetWidth, out int targetHeight);
            bool sizesAligned = SizesAligned(textures, targetWidth, targetHeight);
            for (int i = 0; i < textures.Count; i++)
            {
                foreach (string advice in DescribeAdvice(master, textures[i], targetWidth, targetHeight,
                    sizesAligned))
                {
                    problems.Add((textures[i], advice));
                }
            }

            if (problems.Count == 0)
            {
                return TryGetLayout(master.width, master.height, textures.Count, out _, out _, out reason);
            }

            var listing = new List<string>();
            for (int i = 0; i < textures.Count; i++)
            {
                listing.Add(DescribeLayer(i, master, textures[i], targetWidth, targetHeight, sizesAligned));
            }

            var lines = new List<string>
            {
                "数组各层必须统一：实际宽高、格式（含 sRGB）、mip、采样与像素处理设置。",
                "数组尺寸取自第 0 层，宽高建议统一为各层最大尺寸。",
                string.Empty,
                "全部层贴图（单击对应的一条日志即可在 Project 中定位该贴图；带“建议”的层需要修改）：",
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

        /// <summary>各层尺寸是否都已等于目标尺寸；不一致时 mip 层数差异由尺寸决定，不再单独给 mip 建议。</summary>
        private static bool SizesAligned(IReadOnlyList<Texture2D> textures, int targetWidth, int targetHeight)
        {
            foreach (Texture2D texture in textures)
            {
                if (texture.width != targetWidth || texture.height != targetHeight)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>列出一层与其它层不一致、需要手动统一的参数与改法。</summary>
        private static List<string> DescribeAdvice(Texture2D master, Texture2D texture,
            int targetWidth, int targetHeight, bool sizesAligned)
        {
            var advice = new List<string>();
            if (texture.width != targetWidth || texture.height != targetHeight)
            {
                advice.Add($"宽高 {texture.width}×{texture.height} 与其他层不一致，需调整为 {targetWidth}×{targetHeight}");
            }

            if (texture.graphicsFormat != master.graphicsFormat)
            {
                advice.Add($"格式 {DescribeFormat(texture)} 与其他层不一致，需改成 {DescribeFormat(master)}");
            }

            if (sizesAligned && texture.mipmapCount != master.mipmapCount)
            {
                advice.Add($"mip {texture.mipmapCount} 层与其他层不一致，需改成 {master.mipmapCount} 层" +
                    "（检查 Generate Mip Maps 与 Mipmap Limit）");
            }

            if (texture.filterMode != master.filterMode || texture.wrapModeU != master.wrapModeU ||
                texture.wrapModeV != master.wrapModeV || texture.anisoLevel != master.anisoLevel ||
                texture.mipMapBias != master.mipMapBias)
            {
                advice.Add($"采样 {DescribeSampling(texture)} 与其他层不一致，需改成 {DescribeSampling(master)}");
            }

            TextureImporterSettings settings = ReadArraySettings(texture);
            if (settings.textureType != TextureImporterType.Default &&
                settings.textureType != TextureImporterType.NormalMap &&
                settings.textureType != TextureImporterType.SingleChannel)
            {
                advice.Add("原生数组仅支持 Default、Normal Map 或 Single Channel 类型");
            }

            if (!TextureImporterSettings.Equal(ReadArraySettings(master), settings))
            {
                advice.Add("像素处理设置需与第 0 层统一（类型、sRGB、Alpha、法线、Swizzle、mip 与采样设置）");
            }

            return advice;
        }

        /// <summary>逐层列出实际参数与需要修改的建议，便于按表修改源贴图导入设置。</summary>
        private static string DescribeLayer(int index, Texture2D master, Texture2D texture,
            int targetWidth, int targetHeight, bool sizesAligned)
        {
            var lines = new List<string>
            {
                $"{index}  {AssetDatabase.GetAssetPath(texture)}",
                $"  宽高  {texture.width}×{texture.height}",
                $"  格式  {DescribeFormat(texture)}",
                $"  mip   {texture.mipmapCount} 层",
                $"  采样  {DescribeSampling(texture)}"
            };
            foreach (string advice in DescribeAdvice(master, texture, targetWidth, targetHeight, sizesAligned))
            {
                lines.Add("  建议  " + advice);
            }

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
    }
}
