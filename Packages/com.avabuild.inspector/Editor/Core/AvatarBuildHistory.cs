using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;
using VRC.SDK3A.Editor;

namespace AvaBuildInspector
{
    [Serializable]
    internal sealed class AvatarBuildHistoryFile
    {
        public List<AvatarBuildHistoryEntry> Entries = new List<AvatarBuildHistoryEntry>();
    }

    [Serializable]
    internal sealed class AvatarBuildHistoryEntry
    {
        public string Id;
        public string AvatarName;
        public string Platform;
        public string FinishedAt;
        public bool Succeeded;
        public string Error;
        public string OverallRank;
        public List<AvatarBuildMetricSnapshot> Metrics = new List<AvatarBuildMetricSnapshot>();
        public List<string> Warnings = new List<string>();
    }

    [Serializable]
    internal sealed class AvatarBuildMetricSnapshot
    {
        public string Section;
        public string DisplayName;
        public string DisplayValue;
        public string Rank;
        public string Accuracy;
        public string Explanation;
    }

    internal static class AvatarBuildHistoryStore
    {
        private const int MaxEntries = 100;
        private static readonly string HistoryPath = Path.Combine("Library", "AvaBuildInspector", "build-history.json");
        private static AvatarBuildHistoryFile _history;

        public static event Action Changed;

        public static IReadOnlyList<AvatarBuildHistoryEntry> Entries
        {
            get
            {
                EnsureLoaded();
                return _history.Entries;
            }
        }

        public static void Add(AvatarBuildHistoryEntry entry)
        {
            EnsureLoaded();
            _history.Entries.Insert(0, entry);
            if (_history.Entries.Count > MaxEntries)
                _history.Entries.RemoveRange(MaxEntries, _history.Entries.Count - MaxEntries);
            Save();
            Changed?.Invoke();
        }

        private static void EnsureLoaded()
        {
            if (_history != null) return;
            try
            {
                if (File.Exists(HistoryPath))
                    _history = JsonUtility.FromJson<AvatarBuildHistoryFile>(File.ReadAllText(HistoryPath));
            }
            catch (Exception exception)
            {
                Debug.LogWarning("AvaBuild Inspector: ビルド履歴を読み込めませんでした。" + exception.Message);
            }
            if (_history == null) _history = new AvatarBuildHistoryFile();
            if (_history.Entries == null) _history.Entries = new List<AvatarBuildHistoryEntry>();
        }

        private static void Save()
        {
            try
            {
                var directory = Path.GetDirectoryName(HistoryPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(HistoryPath, JsonUtility.ToJson(_history, true));
            }
            catch (Exception exception)
            {
                Debug.LogError("AvaBuild Inspector: ビルド履歴を保存できませんでした。" + exception);
            }
        }
    }

    [InitializeOnLoad]
    internal static class AvatarBuildHistoryRecorder
    {
        private static IVRCSdkAvatarBuilderApi _builder;
        private static AvatarReport _pendingReport;
        private static string _pendingAvatarName;
        private static string _pendingPlatform;

        static AvatarBuildHistoryRecorder()
        {
            VRCSdkControlPanel.OnSdkPanelEnable += OnSdkPanelEnable;
            EditorApplication.delayCall += AttachToBuilder;
        }

        private static void OnSdkPanelEnable(object sender, EventArgs args)
        {
            AttachToBuilder();
        }

        private static void AttachToBuilder()
        {
            if (VRCSdkControlPanel.window == null) return;
            if (!VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>(out var builder) || builder == null) return;
            if (_builder == builder) return;
            DetachFromBuilder();
            _builder = builder;
            _builder.OnSdkBuildStart += OnBuildStart;
            _builder.OnSdkBuildSuccess += OnBuildSuccess;
            _builder.OnSdkBuildError += OnBuildError;
        }

        private static void DetachFromBuilder()
        {
            if (_builder == null) return;
            _builder.OnSdkBuildStart -= OnBuildStart;
            _builder.OnSdkBuildSuccess -= OnBuildSuccess;
            _builder.OnSdkBuildError -= OnBuildError;
            _builder = null;
        }

        private static void OnBuildStart(object sender, object target)
        {
            var avatar = target as GameObject;
            if (avatar == null && target is Component component) avatar = component.gameObject;
            if (avatar == null)
            {
                _pendingReport = null;
                _pendingAvatarName = target == null ? "Unknown Avatar" : target.ToString();
                _pendingPlatform = GetPlatformName();
                return;
            }

            _pendingAvatarName = avatar.name;
            _pendingPlatform = GetPlatformName();
            try
            {
                var platform = EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android
                    ? TargetPlatform.Android
                    : TargetPlatform.PC;
                _pendingReport = AvatarQuickAnalyzer.Analyze(avatar, platform);
            }
            catch (Exception exception)
            {
                _pendingReport = null;
                Debug.LogWarning("AvaBuild Inspector: ビルド履歴用の性能解析に失敗しました。" + exception.Message);
            }
        }

        private static void OnBuildSuccess(object sender, string bundlePath)
        {
            var entry = CreateEntry(true, string.Empty);
            if (_pendingReport != null && !string.IsNullOrEmpty(bundlePath) && File.Exists(bundlePath))
            {
                try
                {
                    AvatarQuickAnalyzer.ApplyExactDownloadSize(_pendingReport, new FileInfo(bundlePath).Length);
                }
                catch (Exception exception)
                {
                    Debug.LogWarning("AvaBuild Inspector: Bundleサイズを履歴へ反映できませんでした。" + exception.Message);
                }
            }
            CopyMetrics(entry);
            AvatarBuildHistoryStore.Add(entry);
            ClearPending();
        }

        private static void OnBuildError(object sender, string error)
        {
            var entry = CreateEntry(false, error);
            CopyMetrics(entry);
            AvatarBuildHistoryStore.Add(entry);
            ClearPending();
        }

        private static AvatarBuildHistoryEntry CreateEntry(bool succeeded, string error)
        {
            return new AvatarBuildHistoryEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                AvatarName = string.IsNullOrEmpty(_pendingAvatarName) ? "Unknown Avatar" : _pendingAvatarName,
                Platform = string.IsNullOrEmpty(_pendingPlatform) ? GetPlatformName() : _pendingPlatform,
                FinishedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Succeeded = succeeded,
                Error = error ?? string.Empty,
                OverallRank = _pendingReport == null ? "Unavailable" : _pendingReport.OverallRank.ToString()
            };
        }

        private static void CopyMetrics(AvatarBuildHistoryEntry entry)
        {
            if (_pendingReport == null) return;
            entry.OverallRank = _pendingReport.OverallRank.ToString();
            entry.Warnings.AddRange(_pendingReport.Warnings);
            foreach (var metric in _pendingReport.Metrics)
            {
                entry.Metrics.Add(new AvatarBuildMetricSnapshot
                {
                    Section = metric.Section,
                    DisplayName = metric.DisplayName,
                    DisplayValue = metric.DisplayValue,
                    Rank = metric.Rank.ToString(),
                    Accuracy = metric.Accuracy.ToString(),
                    Explanation = metric.Explanation
                });
            }
        }

        private static string GetPlatformName()
        {
            return EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android ? "Android" : "PC";
        }

        private static void ClearPending()
        {
            _pendingReport = null;
            _pendingAvatarName = null;
            _pendingPlatform = null;
        }
    }
}
