using System.Collections.Generic;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace HmMeshMergeEditor
{
    /// <summary>
    /// 把若干张独立贴图导入成一个纹理数组资产（.hmtexarray 源文件）。
    /// 层号即贴图中的来源顺序，合并网格的 UV 不需要重映射。
    ///
    /// 格式与尺寸跟随各层源贴图：对每张源贴图声明 DependsOnArtifact 建立平台依赖，
    /// 切换 Build Target 时源贴图先按新平台重导，本数组随之重导，因此 Android 得到 ASTC、桌面得到 BC。
    /// 这条依赖是必须的——只声明 DependsOnSourceAsset 的话不会随平台重导。
    /// </summary>
    [ScriptedImporter(VERSION, EXTENSION)]
    public sealed class HmMeshMergeTextureArrayImporter : ScriptedImporter
    {
        /// <summary>源文件扩展名；内容为空，导入设置保存在它对应的 .meta 里。</summary>
        public const string EXTENSION = "hmtexarray";

        /// <summary>序列化字段名，供工具用 SerializedProperty 写入贴图列表。</summary>
        public const string TEXTURES_FIELD = "_textures";

        private const int VERSION = 1;

        [Tooltip("参与数组的贴图，顺序即数组的层号")]
        [SerializeField] private List<Texture2D> _textures = new List<Texture2D>();

        /// <summary>参与数组的贴图，顺序即层号。</summary>
        public List<Texture2D> Textures
        {
            get => _textures;
            set => _textures = value;
        }

        public override void OnImportAsset(AssetImportContext ctx)
        {
            // 正常只发生在"源文件刚建、列表还没写入"的那一次导入；写入失败时这里是唯一的线索。
            if (_textures.Count == 0)
            {
                ctx.LogImportWarning("贴图列表为空，暂不产出纹理数组；合并工具写入列表后会重新导入。");
                return;
            }

            Texture2D master = _textures[0];
            if (master == null)
            {
                ctx.LogImportError("第 0 层贴图为空。");
                return;
            }

            for (int i = 1; i < _textures.Count; i++)
            {
                if (!Matches(master, _textures[i], out string reason))
                {
                    ctx.LogImportError($"第 {i} 层与第 0 层不一致：{reason}。纹理数组要求所有层同尺寸、同格式、同 mip 层数。");
                    return;
                }
            }

            foreach (Texture2D texture in _textures)
            {
                ctx.DependsOnArtifact(AssetDatabase.GetAssetPath(texture));
            }

            var importer = (TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(master));
            bool hasMipmaps = importer != null ? importer.mipmapEnabled : master.mipmapCount > 1;
            bool linear = importer != null && !importer.sRGBTexture;

            var array = new Texture2DArray(master.width, master.height, _textures.Count,
                master.format, hasMipmaps, linear);
            for (int i = 0; i < _textures.Count; i++)
            {
                Graphics.CopyTexture(_textures[i], 0, array, i);
            }

            array.name = "TextureArray";
            ctx.AddObjectToAsset("TextureArray", array);
            ctx.SetMainObject(array);
        }

        /// <summary>
        /// 层之间必须能合成同一个数组：尺寸、mip 层数一致，导入设置（压缩方式、Alpha 来源、sRGB）也要一致。
        /// 不直接比 Texture2D.format——编辑器里是未压缩格式，RGBA32 与 RGB24 的差别只在于有没有 Alpha 通道，
        /// 打包后真正的格式由压缩设置决定，比它会把"打包后其实一致"的贴图误判为冲突。
        /// </summary>
        private static bool Matches(Texture2D master, Texture2D other, out string reason)
        {
            if (other == null)
            {
                reason = "为空";
                return false;
            }

            if (other.width != master.width || other.height != master.height)
            {
                reason = $"尺寸不同（{other.width}x{other.height} 与 {master.width}x{master.height}）";
                return false;
            }

            if (other.mipmapCount != master.mipmapCount)
            {
                reason = $"mip 层数不同（{other.mipmapCount} 与 {master.mipmapCount}）";
                return false;
            }

            TextureImporter masterImporter = GetImporter(master);
            TextureImporter otherImporter = GetImporter(other);
            if (masterImporter == null || otherImporter == null)
            {
                reason = "读不到导入设置";
                return false;
            }

            if (masterImporter.textureCompression != otherImporter.textureCompression ||
                masterImporter.crunchedCompression != otherImporter.crunchedCompression ||
                masterImporter.sRGBTexture != otherImporter.sRGBTexture ||
                masterImporter.alphaSource != otherImporter.alphaSource)
            {
                reason = "导入设置不同（压缩方式 / Crunch / sRGB / Alpha 来源）";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        private static TextureImporter GetImporter(Texture2D texture)
        {
            return (TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture));
        }
    }
}
