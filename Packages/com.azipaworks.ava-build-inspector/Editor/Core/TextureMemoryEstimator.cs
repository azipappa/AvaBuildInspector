using System;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace AvaBuildInspector
{
    /// <summary>
    /// 1 Textureの、指定Platformで実際に使われるImport設定。
    /// </summary>
    internal sealed class TextureImportInfo
    {
        public string AssetPath;
        public TargetPlatform Platform;
        public int SourceWidth;
        public int SourceHeight;
        public bool HasAlpha;
        public bool IsNormalMap;
        public bool Mipmaps;
        public TextureImporterNPOTScale NpotScale;
        public bool PlatformOverridden;
        public int MaxSize;
        public TextureImporterFormat Format;
        public TextureImporterFormat ResolvedFormat;
        public TextureImporterCompression Compression;
        public bool Crunch;
        public int DefaultMaxSize;
        public TextureImporterFormat DefaultFormat;
        public TextureImporterCompression DefaultCompression;
        public bool DefaultCrunch;
        public long EstimatedBytes;

        public bool IsUncompressed => TextureMemoryEstimator.IsUncompressedFormat(ResolvedFormat);

        public string Summary => MaxSize.ToString(CultureInfo.InvariantCulture) + " · " +
                                 TextureMemoryEstimator.FormatLabel(ResolvedFormat) + (Crunch ? " · Crunch" : "");
    }

    /// <summary>
    /// 元画像サイズ・Max Size・Format・MipmapからTextureのGPUメモリを概算する。
    /// Profiler値とは算出方法が異なるため、変更前後の比較に使う。
    /// </summary>
    internal static class TextureMemoryEstimator
    {
        private static readonly Regex BlockSizePattern = new Regex(@"(\d+)x(\d+)", RegexOptions.Compiled);

        public static string PlatformName(TargetPlatform platform) => platform == TargetPlatform.Android ? "Android" : "Standalone";

        public static TextureSettingsTarget PlatformTarget(TargetPlatform platform) =>
            platform == TargetPlatform.Android ? TextureSettingsTarget.Android : TextureSettingsTarget.Standalone;

        public static TextureImportInfo Read(string assetPath, TargetPlatform platform)
        {
            if (string.IsNullOrEmpty(assetPath) || !(AssetImporter.GetAtPath(assetPath) is TextureImporter importer)) return null;

            var info = new TextureImportInfo { AssetPath = assetPath, Platform = platform };
            importer.GetSourceTextureWidthAndHeight(out info.SourceWidth, out info.SourceHeight);
            info.HasAlpha = importer.DoesSourceTextureHaveAlpha() && importer.alphaSource != TextureImporterAlphaSource.None;
            info.IsNormalMap = importer.textureType == TextureImporterType.NormalMap;
            info.Mipmaps = importer.mipmapEnabled;
            info.NpotScale = importer.npotScale;

            var defaults = importer.GetDefaultPlatformTextureSettings();
            info.DefaultMaxSize = defaults.maxTextureSize;
            info.DefaultFormat = defaults.format;
            info.DefaultCompression = defaults.textureCompression;
            info.DefaultCrunch = defaults.crunchedCompression;

            var platformName = PlatformName(platform);
            var platformSettings = importer.GetPlatformTextureSettings(platformName);
            info.PlatformOverridden = platformSettings.overridden;
            var effective = platformSettings.overridden ? platformSettings : defaults;
            info.MaxSize = effective.maxTextureSize;
            info.Format = effective.format;
            info.Compression = effective.textureCompression;
            info.Crunch = effective.crunchedCompression;

            var resolved = info.Format;
            if (resolved == TextureImporterFormat.Automatic)
            {
                try { resolved = importer.GetAutomaticFormat(platformName); }
                catch (Exception) { resolved = TextureImporterFormat.Automatic; }
                if (resolved == TextureImporterFormat.Automatic)
                    resolved = ResolveAutomaticFormat(platform, info.Compression, info.Crunch, info.HasAlpha, info.IsNormalMap);
            }
            info.ResolvedFormat = resolved;
            info.EstimatedBytes = EstimateBytes(info, info.MaxSize, resolved);
            return info;
        }

        /// <summary>
        /// Draft設定を適用した後の推定バイト数。スキャン中のPlatformに影響しない設定なら現在値を返す。
        /// </summary>
        public static long EstimateAfter(TextureImportInfo info, TextureCompressionSettings draft, out bool affectsPlatform)
        {
            affectsPlatform = false;
            if (info == null) return 0L;
            if (draft == null) return info.EstimatedBytes;

            int maxSize;
            TextureImporterFormat format;
            TextureImporterCompression compression;
            bool crunch;
            if (draft.Target == PlatformTarget(info.Platform))
            {
                if (draft.Override)
                {
                    maxSize = draft.MaxSize;
                    format = draft.Format;
                    compression = draft.Compression;
                    crunch = draft.UseCrunchCompression;
                }
                else
                {
                    maxSize = info.DefaultMaxSize;
                    format = info.DefaultFormat;
                    compression = info.DefaultCompression;
                    crunch = info.DefaultCrunch;
                }
            }
            else if (draft.Target == TextureSettingsTarget.Default && !info.PlatformOverridden)
            {
                maxSize = draft.MaxSize;
                format = draft.Format;
                compression = draft.Compression;
                crunch = draft.UseCrunchCompression;
            }
            else
            {
                return info.EstimatedBytes;
            }

            affectsPlatform = true;
            TextureImporterFormat resolved;
            if (format != TextureImporterFormat.Automatic) resolved = format;
            else if (info.Format == TextureImporterFormat.Automatic && compression == info.Compression && crunch == info.Crunch)
                resolved = info.ResolvedFormat;
            else resolved = ResolveAutomaticFormat(info.Platform, compression, crunch, info.HasAlpha, info.IsNormalMap);

            if (maxSize == info.MaxSize && resolved == info.ResolvedFormat) return info.EstimatedBytes;
            return EstimateBytes(info, maxSize, resolved);
        }

        public static TextureImporterFormat ResolveAutomaticFormat(TargetPlatform platform, TextureImporterCompression compression,
            bool crunch, bool hasAlpha, bool normalMap)
        {
            if (compression == TextureImporterCompression.Uncompressed)
                return hasAlpha || normalMap ? TextureImporterFormat.RGBA32 : TextureImporterFormat.RGB24;

            if (platform == TargetPlatform.PC)
            {
                if (crunch) return hasAlpha || normalMap ? TextureImporterFormat.DXT5Crunched : TextureImporterFormat.DXT1Crunched;
                if (normalMap) return compression == TextureImporterCompression.CompressedHQ ? TextureImporterFormat.BC5 : TextureImporterFormat.DXT5;
                if (compression == TextureImporterCompression.CompressedHQ) return TextureImporterFormat.BC7;
                return hasAlpha ? TextureImporterFormat.DXT5 : TextureImporterFormat.DXT1;
            }

            if (crunch) return hasAlpha ? TextureImporterFormat.ETC2_RGBA8Crunched : TextureImporterFormat.ETC_RGB4Crunched;
            switch (compression)
            {
                case TextureImporterCompression.CompressedLQ: return TextureImporterFormat.ASTC_8x8;
                case TextureImporterCompression.CompressedHQ: return TextureImporterFormat.ASTC_4x4;
                default: return TextureImporterFormat.ASTC_6x6;
            }
        }

        public static long EstimateBytes(TextureImportInfo info, int maxSize, TextureImporterFormat format)
        {
            if (info == null) return 0L;
            var width = Math.Max(1, info.SourceWidth);
            var height = Math.Max(1, info.SourceHeight);
            if (info.NpotScale != TextureImporterNPOTScale.None && (!Mathf.IsPowerOfTwo(width) || !Mathf.IsPowerOfTwo(height)))
            {
                width = ScaleToPowerOfTwo(width, info.NpotScale);
                height = ScaleToPowerOfTwo(height, info.NpotScale);
            }

            var largest = Math.Max(width, height);
            if (maxSize > 0 && largest > maxSize)
            {
                var scale = (double)maxSize / largest;
                width = Math.Max(1, (int)Math.Round(width * scale));
                height = Math.Max(1, (int)Math.Round(height * scale));
            }

            GetBlockInfo(format, out var blockWidth, out var blockHeight, out var bytesPerBlock);
            var total = 0d;
            while (true)
            {
                var blocksX = (width + blockWidth - 1) / blockWidth;
                var blocksY = (height + blockHeight - 1) / blockHeight;
                total += (double)blocksX * blocksY * bytesPerBlock;
                if (!info.Mipmaps || (width == 1 && height == 1)) break;
                width = Math.Max(1, width / 2);
                height = Math.Max(1, height / 2);
            }
            return (long)Math.Ceiling(total);
        }

        public static bool IsUncompressedFormat(TextureImporterFormat format)
        {
            if (format == TextureImporterFormat.Automatic) return false;
            GetBlockInfo(format, out var blockWidth, out _, out var bytesPerBlock);
            return blockWidth == 1 && bytesPerBlock >= 1d;
        }

        public static string FormatLabel(TextureImporterFormat format)
        {
            var name = format.ToString();
            if (name.EndsWith("Crunched", StringComparison.Ordinal)) name = name.Substring(0, name.Length - "Crunched".Length);
            return name.Replace('_', ' ');
        }

        private static int ScaleToPowerOfTwo(int value, TextureImporterNPOTScale scale)
        {
            switch (scale)
            {
                case TextureImporterNPOTScale.ToLarger: return Mathf.NextPowerOfTwo(value);
                case TextureImporterNPOTScale.ToSmaller: return Math.Max(1, Mathf.NextPowerOfTwo(value) / (Mathf.IsPowerOfTwo(value) ? 1 : 2));
                default: return Mathf.ClosestPowerOfTwo(value);
            }
        }

        private static void GetBlockInfo(TextureImporterFormat format, out int blockWidth, out int blockHeight, out double bytesPerBlock)
        {
            var name = format.ToString();
            blockWidth = 4;
            blockHeight = 4;
            if (name.StartsWith("ASTC", StringComparison.Ordinal))
            {
                var match = BlockSizePattern.Match(name);
                if (match.Success)
                {
                    blockWidth = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                    blockHeight = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                }
                bytesPerBlock = 16d;
                return;
            }

            if (name.StartsWith("DXT1", StringComparison.Ordinal) || name == "BC4" ||
                name.StartsWith("ETC_RGB4", StringComparison.Ordinal) || name.StartsWith("ETC2_RGB4", StringComparison.Ordinal) ||
                name == "ETC2_RGBA1" || name == "EAC_R" || name == "EAC_R_SIGNED")
            {
                bytesPerBlock = 8d;
                return;
            }

            if (name.StartsWith("DXT5", StringComparison.Ordinal) || name == "BC5" || name == "BC6H" || name == "BC7" ||
                name.StartsWith("ETC2_RGBA8", StringComparison.Ordinal) || name.StartsWith("EAC_RG", StringComparison.Ordinal))
            {
                bytesPerBlock = 16d;
                return;
            }

            blockWidth = 1;
            blockHeight = 1;
            if (name.StartsWith("PVRTC", StringComparison.Ordinal))
            {
                bytesPerBlock = name.EndsWith("2", StringComparison.Ordinal) ? 0.25d : 0.5d;
                return;
            }

            switch (name)
            {
                case "Alpha8":
                case "R8":
                    bytesPerBlock = 1d;
                    break;
                case "RGB16":
                case "RGBA16":
                case "ARGB16":
                case "R16":
                case "RHalf":
                case "RG16":
                    bytesPerBlock = 2d;
                    break;
                case "RGB24":
                    bytesPerBlock = 3d;
                    break;
                case "RGB48":
                    bytesPerBlock = 6d;
                    break;
                case "RGBA64":
                case "RGBAHalf":
                case "RGFloat":
                    bytesPerBlock = 8d;
                    break;
                case "RGBAFloat":
                    bytesPerBlock = 16d;
                    break;
                default:
                    bytesPerBlock = 4d;
                    break;
            }
        }
    }
}
