using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace AvaBuildInspector
{
    internal enum TextureSettingsTarget
    {
        Default,
        Standalone,
        Android
    }

    internal sealed class TextureCompressionDraft
    {
        public Texture2D Texture;
        public string AssetPath;
        public TextureSettingsTarget Target;
        public bool Override;
        public int MaxSize;
        public TextureResizeAlgorithm ResizeAlgorithm;
        public TextureImporterFormat Format;
        public TextureImporterCompression Compression;
        public bool UseCrunchCompression;
        public int CompressorQuality;
        public bool Pending;
    }

    internal sealed class TextureCompressionQueue
    {
        private readonly Dictionary<string, TextureCompressionDraft> _drafts =
            new Dictionary<string, TextureCompressionDraft>(StringComparer.OrdinalIgnoreCase);

        public int PendingCount => _drafts.Values.Count(draft => draft.Pending);

        public bool HasPending(string assetPath)
        {
            return _drafts.Values.Any(draft => draft.Pending &&
                string.Equals(draft.AssetPath, assetPath, StringComparison.OrdinalIgnoreCase));
        }

        public TextureCompressionDraft GetOrCreate(Texture2D texture, TextureSettingsTarget target)
        {
            if (texture == null) return null;
            var path = AssetDatabase.GetAssetPath(texture);
            if (string.IsNullOrEmpty(path) || !(AssetImporter.GetAtPath(path) is TextureImporter importer)) return null;

            var key = GetKey(path, target);
            if (_drafts.TryGetValue(key, out var existing)) return existing;

            var settings = target == TextureSettingsTarget.Default
                ? importer.GetDefaultPlatformTextureSettings()
                : importer.GetPlatformTextureSettings(GetPlatformName(target));
            var draft = new TextureCompressionDraft
            {
                Texture = texture,
                AssetPath = path,
                Target = target,
                Override = target == TextureSettingsTarget.Default || settings.overridden,
                MaxSize = settings.maxTextureSize,
                ResizeAlgorithm = settings.resizeAlgorithm,
                Format = settings.format,
                Compression = settings.textureCompression,
                UseCrunchCompression = settings.crunchedCompression,
                CompressorQuality = settings.compressionQuality
            };
            _drafts.Add(key, draft);
            return draft;
        }

        public void DiscardPending()
        {
            _drafts.Clear();
        }

        public int ApplyPending(out string errorMessage)
        {
            var pending = _drafts.Values.Where(draft => draft.Pending).ToArray();
            errorMessage = string.Empty;
            if (pending.Length == 0) return 0;

            var appliedAssets = 0;
            var errors = new List<string>();
            var appliedDrafts = new List<TextureCompressionDraft>();
            var groups = pending.GroupBy(draft => draft.AssetPath, StringComparer.OrdinalIgnoreCase).ToArray();
            AssetDatabase.StartAssetEditing();
            try
            {
                for (var index = 0; index < groups.Length; index++)
                {
                    var group = groups[index];
                    EditorUtility.DisplayProgressBar(
                        "AvaBuild Inspector",
                        "テクスチャ圧縮設定を適用中: " + group.Key,
                        groups.Length == 0 ? 1f : (float)index / groups.Length);
                    try
                    {
                        if (!(AssetImporter.GetAtPath(group.Key) is TextureImporter importer))
                        {
                            errors.Add(group.Key + ": TextureImporterを取得できませんでした。");
                            continue;
                        }

                        foreach (var draft in group) ApplyToImporter(importer, draft);
                        EditorUtility.SetDirty(importer);
                        importer.SaveAndReimport();
                        appliedAssets++;
                        appliedDrafts.AddRange(group);
                    }
                    catch (Exception exception)
                    {
                        errors.Add(group.Key + ": " + exception.Message);
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                EditorUtility.ClearProgressBar();
            }

            foreach (var draft in appliedDrafts) draft.Pending = false;
            errorMessage = string.Join("\n", errors);
            return appliedAssets;
        }

        private static void ApplyToImporter(TextureImporter importer, TextureCompressionDraft draft)
        {
            var settings = draft.Target == TextureSettingsTarget.Default
                ? importer.GetDefaultPlatformTextureSettings()
                : importer.GetPlatformTextureSettings(GetPlatformName(draft.Target));
            settings.name = GetPlatformName(draft.Target);
            settings.overridden = draft.Target != TextureSettingsTarget.Default && draft.Override;
            settings.maxTextureSize = draft.MaxSize;
            settings.resizeAlgorithm = draft.ResizeAlgorithm;
            settings.format = draft.Format;
            settings.textureCompression = draft.Compression;
            settings.crunchedCompression = draft.UseCrunchCompression;
            settings.compressionQuality = Mathf.Clamp(draft.CompressorQuality, 0, 100);
            importer.SetPlatformTextureSettings(settings);
        }

        private static string GetKey(string path, TextureSettingsTarget target)
        {
            return path + "|" + target;
        }

        private static string GetPlatformName(TextureSettingsTarget target)
        {
            switch (target)
            {
                case TextureSettingsTarget.Standalone: return "Standalone";
                case TextureSettingsTarget.Android: return "Android";
                default: return "DefaultTexturePlatform";
            }
        }
    }
}
