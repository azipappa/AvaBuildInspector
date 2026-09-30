using System;
using System.Collections.Generic;
using System.IO;
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

    internal sealed class TextureCompressionSettings
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
    }

    [Serializable]
    internal sealed class TextureCompressionBackupFile
    {
        public List<TextureCompressionBackupEntry> Entries = new List<TextureCompressionBackupEntry>();
    }

    [Serializable]
    internal sealed class TextureCompressionBackupEntry
    {
        public string AssetPath;
        public List<TextureCompressionBackupSettings> Settings = new List<TextureCompressionBackupSettings>();
    }

    [Serializable]
    internal sealed class TextureCompressionBackupSettings
    {
        public TextureSettingsTarget Target;
        public bool Override;
        public int MaxSize;
        public TextureResizeAlgorithm ResizeAlgorithm;
        public TextureImporterFormat Format;
        public TextureImporterCompression Compression;
        public bool UseCrunchCompression;
        public int CompressorQuality;
    }

internal sealed class TextureCompressionSettingsApplier
    {
        private readonly Dictionary<string, TextureCompressionSettings> _settings =
            new Dictionary<string, TextureCompressionSettings>(StringComparer.OrdinalIgnoreCase);

        public bool HasBackup => File.Exists(GetBackupPath());
        public int BackupAssetCount => LoadBackup()?.Entries.Count ?? 0;
        public IReadOnlyList<string> BackupAssetPaths => LoadBackup()?.Entries.Select(entry => entry.AssetPath).ToArray() ?? Array.Empty<string>();

        public TextureCompressionSettings GetOrCreateSettings(Texture2D texture, TextureSettingsTarget target)
        {
            if (texture == null) return null;
            var path = AssetDatabase.GetAssetPath(texture);
            if (string.IsNullOrEmpty(path) || !(AssetImporter.GetAtPath(path) is TextureImporter importer)) return null;

            var key = GetKey(path, target);
            if (_settings.TryGetValue(key, out var existing)) return existing;

            var settings = target == TextureSettingsTarget.Default
                ? importer.GetDefaultPlatformTextureSettings()
                : importer.GetPlatformTextureSettings(GetPlatformName(target));
            var setting = new TextureCompressionSettings
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
            _settings.Add(key, setting);
            return setting;
        }

        public void ClearSettings() => _settings.Clear();

        public int ApplySelected(IEnumerable<TextureCompressionSettings> selectedSettings, out string errorMessage)
        {
            var selected = (selectedSettings ?? Enumerable.Empty<TextureCompressionSettings>())
                .Where(setting => setting != null && !string.IsNullOrEmpty(setting.AssetPath))
                .GroupBy(setting => GetKey(setting.AssetPath, setting.Target), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .ToArray();
            errorMessage = string.Empty;
            if (selected.Length == 0) return 0;

            var appliedAssets = 0;
            var errors = new List<string>();
            var groups = selected.GroupBy(setting => setting.AssetPath, StringComparer.OrdinalIgnoreCase).ToArray();

            var backup = new TextureCompressionBackupFile();
            foreach (var group in groups)
            {
                if (!(AssetImporter.GetAtPath(group.Key) is TextureImporter importer)) continue;
                backup.Entries.Add(new TextureCompressionBackupEntry
                {
                    AssetPath = group.Key,
                    Settings = group.Select(setting => setting.Target).Distinct()
                        .Select(target => CaptureSettings(importer, target)).ToList()
                });
            }
            if (backup.Entries.Count == 0)
            {
                errorMessage = "バックアップ対象のTextureImporterがありません。Applyを中止しました。";
                return 0;
            }
            try
            {
                SaveBackup(backup);
            }
            catch (Exception exception)
            {
                errorMessage = "Apply前バックアップを保存できませんでした。Applyを中止しました: " + exception.Message;
                return 0;
            }

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

                        foreach (var setting in group) ApplyToImporter(importer, setting);
                        EditorUtility.SetDirty(importer);
                        importer.SaveAndReimport();
                        appliedAssets++;
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

            errorMessage = string.Join("\n", errors);
            return appliedAssets;
        }

        public int RestoreLastBackup(out string errorMessage)
        {
            errorMessage = string.Empty;
            var backup = LoadBackup();
            if (backup == null || backup.Entries.Count == 0)
            {
                errorMessage = "Texture設定バックアップが見つかりません。";
                return 0;
            }

            var restored = 0;
            var errors = new List<string>();
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var entry in backup.Entries)
                {
                    try
                    {
                        if (!(AssetImporter.GetAtPath(entry.AssetPath) is TextureImporter importer))
                        {
                            errors.Add(entry.AssetPath + ": TextureImporterを取得できませんでした。");
                            continue;
                        }
                        foreach (var settings in entry.Settings)
                        {
                            ApplyToImporter(importer, new TextureCompressionSettings
                            {
                                AssetPath = entry.AssetPath,
                                Target = settings.Target,
                                Override = settings.Override,
                                MaxSize = settings.MaxSize,
                                ResizeAlgorithm = settings.ResizeAlgorithm,
                                Format = settings.Format,
                                Compression = settings.Compression,
                                UseCrunchCompression = settings.UseCrunchCompression,
                                CompressorQuality = settings.CompressorQuality
                            });
                        }
                        EditorUtility.SetDirty(importer);
                        importer.SaveAndReimport();
                        restored++;
                    }
                    catch (Exception exception)
                    {
                        errors.Add(entry.AssetPath + ": " + exception.Message);
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            if (errors.Count == 0)
            {
                try { File.Delete(GetBackupPath()); }
                catch (Exception exception) { errors.Add("設定は復元されましたが、バックアップファイルを削除できませんでした: " + exception.Message); }
            }
            errorMessage = string.Join("\n", errors);
            return restored;
        }

        private static TextureCompressionBackupSettings CaptureSettings(TextureImporter importer, TextureSettingsTarget target)
        {
            var settings = target == TextureSettingsTarget.Default
                ? importer.GetDefaultPlatformTextureSettings()
                : importer.GetPlatformTextureSettings(GetPlatformName(target));
            return new TextureCompressionBackupSettings
            {
                Target = target,
                Override = target == TextureSettingsTarget.Default || settings.overridden,
                MaxSize = settings.maxTextureSize,
                ResizeAlgorithm = settings.resizeAlgorithm,
                Format = settings.format,
                Compression = settings.textureCompression,
                UseCrunchCompression = settings.crunchedCompression,
                CompressorQuality = settings.compressionQuality
            };
        }

        private static string GetBackupPath()
        {
            var projectRoot = Directory.GetParent(Application.dataPath).FullName;
            return Path.Combine(projectRoot, "Library", "AvaBuildInspector", "texture-settings-backup.json");
        }

        private static TextureCompressionBackupFile LoadBackup()
        {
            var path = GetBackupPath();
            if (!File.Exists(path)) return null;
            try { return JsonUtility.FromJson<TextureCompressionBackupFile>(File.ReadAllText(path)); }
            catch (Exception exception)
            {
                Debug.LogError("AvaBuild Inspector: Texture設定バックアップを読み込めません: " + exception.Message);
                return null;
            }
        }

        private static void SaveBackup(TextureCompressionBackupFile backup)
        {
            var path = GetBackupPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(backup, true));
        }

        private static void ApplyToImporter(TextureImporter importer, TextureCompressionSettings setting)
        {
            var settings = setting.Target == TextureSettingsTarget.Default
                ? importer.GetDefaultPlatformTextureSettings()
                : importer.GetPlatformTextureSettings(GetPlatformName(setting.Target));
            settings.name = GetPlatformName(setting.Target);
            settings.overridden = setting.Target != TextureSettingsTarget.Default && setting.Override;
            settings.maxTextureSize = setting.MaxSize;
            settings.resizeAlgorithm = setting.ResizeAlgorithm;
            settings.format = setting.Format;
            settings.textureCompression = setting.Compression;
            settings.crunchedCompression = setting.UseCrunchCompression;
            settings.compressionQuality = Mathf.Clamp(setting.CompressorQuality, 0, 100);
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
