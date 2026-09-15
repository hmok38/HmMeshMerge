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

            if (!ValidateTextures(_textures, out string reason))
            {
                ctx.LogImportError(reason);
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

            Texture2D master = textures[0];
            for (int i = 0; i < textures.Count; i++)
            {
                Texture2D texture = textures[i];
                if (texture == null)
                {
                    reason = $"第 {i} 层为空或不是 Texture2D；启用数组的每个来源都必须指定二维贴图。";
                    return false;
                }

                if (!IsImportedAssetPath(AssetDatabase.GetAssetPath(texture)))
                {
                    reason = $"第 {i} 层 {texture.name} 是内置或非资产贴图，无法作为数组的导入来源。" +
                        "请停用该属性的数组化，或为每个来源指定 Assets / Packages 中的实际贴图。";
                    return false;
                }

                if (texture.width != master.width || texture.height != master.height ||
                    texture.graphicsFormat != master.graphicsFormat || texture.mipmapCount != master.mipmapCount)
                {
                    reason = $"第 {i} 层 {texture.name} 为 {texture.width}×{texture.height} / " +
                        $"{texture.graphicsFormat} / {texture.mipmapCount} mip；第 0 层为 " +
                        $"{master.width}×{master.height} / {master.graphicsFormat} / {master.mipmapCount} mip。" +
                        "请手动统一宽高、实际格式（含 sRGB）和 mip 层数。";
                    return false;
                }

                if (!texture.isReadable)
                {
                    reason = $"{texture.name}：请手动开启 Read/Write；生成数组需要读取可保存的像素数据。";
                    return false;
                }

                if (texture.format == TextureFormat.DXT1Crunched || texture.format == TextureFormat.DXT5Crunched ||
                    texture.format == TextureFormat.ETC_RGB4Crunched || texture.format == TextureFormat.ETC2_RGBA8Crunched)
                {
                    reason = $"{texture.name}：纹理数组不能直接使用 Crunch 数据，请关闭 Crunch。";
                    return false;
                }

                if (texture.filterMode != master.filterMode || texture.wrapModeU != master.wrapModeU ||
                    texture.wrapModeV != master.wrapModeV || texture.anisoLevel != master.anisoLevel ||
                    texture.mipMapBias != master.mipMapBias)
                {
                    reason = $"{texture.name}：采样设置与第 0 层不同；同一数组只能使用一组采样设置。";
                    return false;
                }
            }

            reason = string.Empty;
            return true;
        }
    }
}
