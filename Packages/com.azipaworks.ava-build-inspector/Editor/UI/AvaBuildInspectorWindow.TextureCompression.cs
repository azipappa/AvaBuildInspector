using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using VRC.SDKBase.Validation.Performance;

namespace AvaBuildInspector
{
    internal sealed partial class AvaBuildInspectorWindow
    {
        private sealed class ApplyComparison
        {
            public TargetPlatform Platform;
            public HashSet<string> Paths;
            public Dictionary<string, double> BeforeByPath;
            public double BeforeSelectedMegabytes;
            public double? BeforeTotalMegabytes;
            public double EstimatedBeforeMegabytes;
            public double EstimatedAfterMegabytes;
        }

        private readonly TextureCompressionSettingsApplier _textureCompressionSettings = new TextureCompressionSettingsApplier();
        private readonly HashSet<string> _selectedTexturePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TextureImportInfo> _textureImportInfoCache = new Dictionary<string, TextureImportInfo>(StringComparer.OrdinalIgnoreCase);
        private TextureSettingsTarget _textureSettingsTarget;
        private VisualElement _compressionPanel;
        private Label _compressionEstimateLabel;
        private Label _compressionEstimateNote;
        private IVisualElementScheduledItem _compressionRefreshItem;
        private ApplyComparison _pendingApplyComparison;

        private TargetPlatform ReportPlatform => _report?.Platform ?? CurrentPlatform;
        private string ReportPlatformLabel => ReportPlatform == TargetPlatform.Android ? "Android" : "PC";

        private TextureImportInfo GetTextureImportInfo(Contributor contributor) =>
            contributor?.Target is Texture2D ? GetTextureImportInfo(GetAssetPath(contributor.Target)) : null;

        private TextureImportInfo GetTextureImportInfo(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;
            if (_textureImportInfoCache.TryGetValue(assetPath, out var cached)) return cached;
            TextureImportInfo info = null;
            try { info = TextureMemoryEstimator.Read(assetPath, ReportPlatform); }
            catch (Exception exception) { Debug.LogWarning("[AvaBuild Inspector] Import設定を読み込めません: " + assetPath + " " + exception.Message); }
            _textureImportInfoCache[assetPath] = info;
            return info;
        }

        private Texture2D[] GetCheckedTextures()
        {
            var metric = _report?.Find(AvatarPerformanceCategory.TextureMegabytes);
            if (metric == null) return Array.Empty<Texture2D>();
            return metric.Contributors
                .Select(contributor => contributor.Target as Texture2D)
                .Where(texture => texture != null && _selectedTexturePaths.Contains(AssetDatabase.GetAssetPath(texture)))
                .Distinct()
                .ToArray();
        }

        private void ScheduleCompressionPanelRefresh()
        {
            if (_compressionPanel == null) return;
            _compressionRefreshItem?.Pause();
            _compressionRefreshItem = _compressionPanel.schedule.Execute(RefreshCompressionPanel);
            _compressionRefreshItem.ExecuteLater(40);
        }

        private void RefreshCompressionPanel()
        {
            if (_compressionPanel == null || _report == null) return;
            _compressionPanel.Clear();
            _compressionEstimateLabel = null;
            _compressionEstimateNote = null;

            var selectedTextures = GetCheckedTextures();
            _compressionPanel.style.display = selectedTextures.Length == 0 ? DisplayStyle.None : DisplayStyle.Flex;
            if (selectedTextures.Length == 0) return;

            var panel = _compressionPanel;
            panel.style.flexShrink = 0;
            panel.style.maxHeight = Length.Percent(48);
            panel.style.marginLeft = 12;
            panel.style.marginRight = 12;
            panel.style.marginBottom = 8;
            panel.style.paddingLeft = 8;
            panel.style.paddingRight = 8;
            panel.style.paddingTop = 7;
            panel.style.paddingBottom = 8;
            panel.style.backgroundColor = AbiTheme.Card;
            panel.style.borderTopWidth = 2;
            panel.style.borderTopColor = AbiTheme.TabSelected;

            var visibleTargets = new HashSet<UnityEngine.Object>(_visibleContributors.Select(contributor => contributor.Target));
            var hiddenSelectedCount = selectedTextures.Count(texture => !visibleTargets.Contains(texture));

            var selectionBar = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, flexShrink = 0 } };
            var selectionText = "圧縮設定  " + selectedTextures.Length.ToString("N0") + " 枚チェック中";
            if (hiddenSelectedCount > 0) selectionText += "（うち検索/フィルターで非表示 " + hiddenSelectedCount.ToString("N0") + " 枚）";
            var selectionLabel = new Label(selectionText);
            selectionLabel.style.flexGrow = 1;
            selectionLabel.style.flexShrink = 1;
            selectionLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            ApplyEllipsis(selectionLabel);
            selectionBar.Add(selectionLabel);

            var selectAll = new Button(() =>
            {
                foreach (var path in GetVisibleEditableTexturePaths()) SetTextureChecked(path, true);
                OnTextureChecksChanged(true);
            }) { text = "表示中をチェック" };
            selectionBar.Add(selectAll);
            var clearSelection = new Button(() =>
            {
                _selectedTexturePaths.Clear();
                OnTextureChecksChanged(true);
            }) { text = "チェックを解除" };
            clearSelection.style.marginLeft = 4;
            selectionBar.Add(clearSelection);
            panel.Add(selectionBar);

            var platformTabs = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 7, marginBottom = 6, flexShrink = 0 } };
            AddTexturePlatformTab(platformTabs, "Default", TextureSettingsTarget.Default);
            AddTexturePlatformTab(platformTabs, "PC", TextureSettingsTarget.Standalone);
            AddTexturePlatformTab(platformTabs, "Android", TextureSettingsTarget.Android);
            panel.Add(platformTabs);

            var fields = new ScrollView(ScrollViewMode.Vertical) { style = { flexShrink = 1, minHeight = 60 } };
            panel.Add(fields);
            BuildTextureSettingFields(fields, selectedTextures);

            var estimate = new VisualElement
            {
                style =
                {
                    flexShrink = 0,
                    marginTop = 6,
                    paddingLeft = 7,
                    paddingRight = 7,
                    paddingTop = 4,
                    paddingBottom = 4,
                    backgroundColor = AbiTheme.CardDeep
                }
            };
            AbiTheme.SetRadius(estimate.style, 3);
            _compressionEstimateLabel = new Label { style = { unityFontStyleAndWeight = FontStyle.Bold, whiteSpace = WhiteSpace.Normal } };
            _compressionEstimateNote = new Label { style = { fontSize = 10, opacity = 0.7f, whiteSpace = WhiteSpace.Normal, marginTop = 1 } };
            estimate.Add(_compressionEstimateLabel);
            estimate.Add(_compressionEstimateNote);
            panel.Add(estimate);
            RefreshCompressionEstimate(selectedTextures);

            var actionBar = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginTop = 7, flexShrink = 0 } };
            var targetCount = new Label("チェック中の " + selectedTextures.Length.ToString("N0") + " 枚が適用対象です");
            targetCount.style.flexGrow = 1;
            targetCount.style.flexShrink = 1;
            targetCount.style.opacity = 0.72f;
            actionBar.Add(targetCount);

            if (_textureCompressionSettings.HasBackup)
            {
                var restore = new Button(RestoreLastTextureSettings)
                {
                    text = "直前の設定に戻す (" + _textureCompressionSettings.BackupAssetCount.ToString("N0") + ")"
                };
                restore.tooltip = "直前に適用する前のTexture Importer設定に戻します。変更したPlatform設定だけが対象です。";
                actionBar.Add(restore);
            }

            var apply = new Button(() => ApplyTextureCompression(selectedTextures)) { text = "選択中に適用" };
            apply.style.minWidth = 110;
            apply.style.marginLeft = 5;
            apply.style.unityFontStyleAndWeight = FontStyle.Bold;
            actionBar.Add(apply);
            panel.Add(actionBar);
        }

        private void AddTexturePlatformTab(VisualElement parent, string label, TextureSettingsTarget target)
        {
            var affectsScan = target == TextureSettingsTarget.Default || target == TextureMemoryEstimator.PlatformTarget(ReportPlatform);
            var button = new Button(() =>
            {
                _textureSettingsTarget = target;
                WriteEnum("TextureSettingsTarget", target);
                RefreshCompressionPanel();
            }) { text = label + (target == TextureMemoryEstimator.PlatformTarget(ReportPlatform) ? "  ●" : "") };
            button.tooltip = affectsScan
                ? "スキャン中のPlatform（" + ReportPlatformLabel + "）のメモリ推定に反映されます。"
                : "スキャン中のPlatform（" + ReportPlatformLabel + "）とは別Platformの設定です。";
            button.style.flexGrow = 1;
            button.style.height = 25;
            StyleTabButton(button, _textureSettingsTarget == target, true);
            parent.Add(button);
        }

        private void BuildTextureSettingFields(VisualElement parent, Texture2D[] textures)
        {
            var settings = textures
                .Select(texture => _textureCompressionSettings.GetOrCreateSettings(texture, _textureSettingsTarget))
                .Where(setting => setting != null)
                .ToArray();
            if (settings.Length == 0)
            {
                parent.Add(new HelpBox("選択した画像にTextureImporterがありません。", HelpBoxMessageType.Warning));
                return;
            }

            if (settings.Length > 1)
            {
                var multiEditHint = new Label("複数選択中：変更した項目を選択中の全テクスチャへ設定します。「—」は値が混在している項目です。");
                multiEditHint.style.fontSize = 10;
                multiEditHint.style.opacity = 0.58f;
                multiEditHint.style.marginBottom = 4;
                multiEditHint.style.whiteSpace = WhiteSpace.Normal;
                parent.Add(multiEditHint);
            }

            var first = settings[0];
            bool IsMixed<T>(Func<TextureCompressionSettings, T> selector)
            {
                var comparer = EqualityComparer<T>.Default;
                var value = selector(first);
                return settings.Skip(1).Any(setting => !comparer.Equals(value, selector(setting)));
            }

            void Changed<T>(BaseField<T> field, Action<TextureCompressionSettings> update)
            {
                field.showMixedValue = false;
                foreach (var setting in settings) update(setting);
                RefreshCompressionEstimate(textures);
            }

            if (_textureSettingsTarget != TextureSettingsTarget.Default)
            {
                var overrideField = new Toggle("Override") { value = first.Override };
                overrideField.showMixedValue = IsMixed(setting => setting.Override);
                overrideField.RegisterValueChangedCallback(evt => Changed(overrideField, setting => setting.Override = evt.newValue));
                parent.Add(overrideField);
            }

            var maxSizes = new List<int> { 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192 };
            if (!maxSizes.Contains(first.MaxSize)) maxSizes.Add(first.MaxSize);
            maxSizes.Sort();
            var maxSizeField = new PopupField<int>("Max Size", maxSizes, maxSizes.IndexOf(first.MaxSize));
            maxSizeField.showMixedValue = IsMixed(setting => setting.MaxSize);
            maxSizeField.RegisterValueChangedCallback(evt => Changed(maxSizeField, setting => setting.MaxSize = evt.newValue));
            parent.Add(maxSizeField);

            var resizeField = new EnumField("Resize Algorithm", first.ResizeAlgorithm);
            resizeField.showMixedValue = IsMixed(setting => setting.ResizeAlgorithm);
            resizeField.RegisterValueChangedCallback(evt => Changed(resizeField,
                setting => setting.ResizeAlgorithm = (TextureResizeAlgorithm)evt.newValue));
            parent.Add(resizeField);

            var formatField = new EnumField("Format", first.Format);
            formatField.showMixedValue = IsMixed(setting => setting.Format);
            formatField.RegisterValueChangedCallback(evt => Changed(formatField,
                setting => setting.Format = (TextureImporterFormat)evt.newValue));
            parent.Add(formatField);

            var compressionLabels = new List<string> { "None", "Low Quality", "Normal Quality", "High Quality" };
            var compressionField = new PopupField<string>("Compression", compressionLabels, CompressionToIndex(first.Compression));
            compressionField.showMixedValue = IsMixed(setting => setting.Compression);
            compressionField.RegisterValueChangedCallback(evt => Changed(compressionField,
                setting => setting.Compression = IndexToCompression(compressionLabels.IndexOf(evt.newValue))));
            parent.Add(compressionField);

            var crunchField = new Toggle("Use Crunch Compression") { value = first.UseCrunchCompression };
            crunchField.showMixedValue = IsMixed(setting => setting.UseCrunchCompression);
            crunchField.RegisterValueChangedCallback(evt => Changed(crunchField, setting => setting.UseCrunchCompression = evt.newValue));
            parent.Add(crunchField);

            var qualityField = new SliderInt("Compressor Quality", 0, 100)
            {
                value = Mathf.Clamp(first.CompressorQuality, 0, 100),
                showInputField = true
            };
            qualityField.showMixedValue = IsMixed(setting => setting.CompressorQuality);
            qualityField.RegisterValueChangedCallback(evt => Changed(qualityField, setting => setting.CompressorQuality = evt.newValue));
            parent.Add(qualityField);
        }

        private void RefreshCompressionEstimate(Texture2D[] textures)
        {
            if (_compressionEstimateLabel == null) return;
            var estimate = EstimateSelection(textures, out var count, out var affected, out var unaffectedByOverride);
            var before = estimate.before;
            var after = estimate.after;
            var delta = after - before;
            var percent = before > 0d ? delta / before * 100d : 0d;
            _compressionEstimateLabel.text = "推定メモリ（" + ReportPlatformLabel + "・" + count.ToString("N0") + "枚）:  " +
                                             before.ToString("N1") + " MB  →  " + after.ToString("N1") + " MB" +
                                             (Math.Abs(delta) < 0.05d ? "   変化なし" :
                                                 "   " + (delta > 0 ? "+" : "−") + Math.Abs(delta).ToString("N1") + " MB (" +
                                                 (delta > 0 ? "+" : "−") + Math.Abs(percent).ToString("N0") + "%)");
            _compressionEstimateLabel.style.color = delta < -0.05d ? AbiTheme.Decrease : delta > 0.05d ? AbiTheme.Increase : AbiTheme.Neutral;

            var targetLabel = _textureSettingsTarget == TextureSettingsTarget.Default ? "Default"
                : _textureSettingsTarget == TextureSettingsTarget.Standalone ? "PC" : "Android";
            string note;
            if (affected == 0 && _textureSettingsTarget == TextureSettingsTarget.Default && unaffectedByOverride > 0)
                note = "チェック中のTextureは" + ReportPlatformLabel + "のOverrideが有効なため、Default設定の変更はスキャン中の" + ReportPlatformLabel + "に影響しません。";
            else if (affected == 0 && _textureSettingsTarget != TextureSettingsTarget.Default)
                note = targetLabel + "の設定はスキャン中の" + ReportPlatformLabel + "には影響しません。ヘッダーのPlatformを" + targetLabel + "に切り替えると推定できます。";
            else if (unaffectedByOverride > 0)
                note = unaffectedByOverride.ToString("N0") + "枚は" + ReportPlatformLabel + "のOverrideが有効なため変化しません。";
            else
                note = string.Empty;
            note += (note.Length > 0 ? "\n" : "") + "元画像サイズ・Max Size・Format・Mipmapからの概算です（Crunchはダウンロードサイズのみ削減）。実測値は適用後の再スキャンで確認できます。";
            _compressionEstimateNote.text = note;
        }

        private (double before, double after) EstimateSelection(Texture2D[] textures, out int count, out int affected, out int unaffectedByOverride)
        {
            long beforeBytes = 0L;
            long afterBytes = 0L;
            count = 0;
            affected = 0;
            unaffectedByOverride = 0;
            foreach (var texture in textures)
            {
                var info = GetTextureImportInfo(AssetDatabase.GetAssetPath(texture));
                var draft = _textureCompressionSettings.GetOrCreateSettings(texture, _textureSettingsTarget);
                if (info == null || draft == null) continue;
                count++;
                beforeBytes += info.EstimatedBytes;
                afterBytes += TextureMemoryEstimator.EstimateAfter(info, draft, out var affects);
                if (affects) affected++;
                else if (_textureSettingsTarget == TextureSettingsTarget.Default && info.PlatformOverridden) unaffectedByOverride++;
            }
            return (beforeBytes / 1024d / 1024d, afterBytes / 1024d / 1024d);
        }

        private static int CompressionToIndex(TextureImporterCompression compression)
        {
            switch (compression)
            {
                case TextureImporterCompression.Uncompressed: return 0;
                case TextureImporterCompression.CompressedLQ: return 1;
                case TextureImporterCompression.CompressedHQ: return 3;
                default: return 2;
            }
        }

        private static TextureImporterCompression IndexToCompression(int index)
        {
            switch (index)
            {
                case 0: return TextureImporterCompression.Uncompressed;
                case 1: return TextureImporterCompression.CompressedLQ;
                case 3: return TextureImporterCompression.CompressedHQ;
                default: return TextureImporterCompression.Compressed;
            }
        }

        private void ApplyTextureCompression(Texture2D[] selectedTextures)
        {
            var selectedSettings = (selectedTextures ?? Array.Empty<Texture2D>())
                .Select(texture => _textureCompressionSettings.GetOrCreateSettings(texture, _textureSettingsTarget))
                .Where(settings => settings != null)
                .ToArray();
            var targets = selectedSettings.Select(settings => settings.Target.ToString()).Distinct().ToArray();
            var assetPaths = selectedSettings.Select(settings => settings.AssetPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var estimate = EstimateSelection(selectedTextures ?? Array.Empty<Texture2D>(), out _, out _, out _);
            var pathsPreview = string.Join("\n", assetPaths.Take(8));
            if (assetPaths.Length > 8) pathsPreview += "\n…ほか " + (assetPaths.Length - 8).ToString("N0") + " 件";
            var applySummary = assetPaths.Length + " 個のチェック済みTextureへ圧縮設定を適用します。\n対象Platform: " + string.Join(", ", targets) +
                               "\n推定メモリ（" + ReportPlatformLabel + "）: " + estimate.before.ToString("N1") + " MB → " + estimate.after.ToString("N1") + " MB" +
                               "\n\n注意: Import設定はAvatar専用ではありません。同じTextureを使う他のAvatar/Materialにも影響します。" +
                               "\n\n対象Asset:\n" + pathsPreview;
            if (!EditorUtility.DisplayDialog("Texture設定を適用", applySummary, "適用", "キャンセル")) return;

            var metric = _report?.Find(AvatarPerformanceCategory.TextureMegabytes);
            if (metric != null)
            {
                var paths = new HashSet<string>(assetPaths, StringComparer.OrdinalIgnoreCase);
                var beforeByPath = metric.Contributors
                    .Where(contributor => paths.Contains(GetAssetPath(contributor.Target)))
                    .GroupBy(contributor => GetAssetPath(contributor.Target), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.OrdinalIgnoreCase);
                _pendingApplyComparison = new ApplyComparison
                {
                    Platform = _report.Platform,
                    Paths = paths,
                    BeforeByPath = beforeByPath,
                    BeforeSelectedMegabytes = beforeByPath.Values.Sum(),
                    BeforeTotalMegabytes = metric.NumericValue,
                    EstimatedBeforeMegabytes = estimate.before,
                    EstimatedAfterMegabytes = estimate.after
                };
            }

            var applied = _textureCompressionSettings.ApplySelected(selectedSettings, out var errors);
            if (!string.IsNullOrEmpty(errors))
            {
                Debug.LogError("AvaBuild Inspector: 一部のテクスチャ設定を適用できませんでした。\n" + errors);
                ShowNotification(new GUIContent("一部のApplyに失敗しました。Consoleを確認してください。"));
            }
            else if (applied > 0)
            {
                ShowNotification(new GUIContent(applied.ToString("N0") + "個のテクスチャへ設定を適用しました。再スキャン中…"));
            }
            if (applied == 0) _pendingApplyComparison = null;

            _textureCompressionSettings.ClearSettings();
            _textureImportInfoCache.Clear();
            _searchTextCache.Clear();
            if (_currentAvatar != null) RequestScan(_currentAvatar);
            else RebuildSelectedTab();
        }

        /// <summary>
        /// 適用後の再スキャン完了時に、Profiler実測値で前後を比較して通知する。
        /// </summary>
        private void ShowApplyComparisonResult()
        {
            var comparison = _pendingApplyComparison;
            _pendingApplyComparison = null;
            if (comparison == null || _report == null || _report.Platform != comparison.Platform) return;
            var metric = _report.Find(AvatarPerformanceCategory.TextureMegabytes);
            if (metric == null) return;

            var afterByPath = metric.Contributors
                .Where(contributor => comparison.Paths.Contains(GetAssetPath(contributor.Target)))
                .GroupBy(contributor => GetAssetPath(contributor.Target), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.OrdinalIgnoreCase);
            var afterSelected = afterByPath.Values.Sum();
            var delta = afterSelected - comparison.BeforeSelectedMegabytes;
            var message = "適用したTexture: " + comparison.BeforeSelectedMegabytes.ToString("N1") + " → " + afterSelected.ToString("N1") + " MB (" +
                          (delta > 0 ? "+" : delta < 0 ? "−" : "±") + Math.Abs(delta).ToString("N1") + " MB)";
            if (comparison.BeforeTotalMegabytes.HasValue && metric.NumericValue.HasValue)
                message += "\nTexture Memory合計: " + comparison.BeforeTotalMegabytes.Value.ToString("N1") + " → " + metric.NumericValue.Value.ToString("N1") + " MB";
            ShowNotification(new GUIContent(message), 6d);

            var lines = comparison.BeforeByPath
                .OrderByDescending(pair => pair.Value)
                .Select(pair => pair.Key + ": " + pair.Value.ToString("N2") + " → " +
                                (afterByPath.TryGetValue(pair.Key, out var after) ? after.ToString("N2") : "?") + " MB");
            Debug.Log("[AvaBuild Inspector] Texture設定の適用結果（" + comparison.Platform + "、Profiler推定値）\n" + message +
                      "\n適用前の概算: " + comparison.EstimatedBeforeMegabytes.ToString("N1") + " → " + comparison.EstimatedAfterMegabytes.ToString("N1") + " MB\n" +
                      string.Join("\n", lines));
        }

        private void RestoreLastTextureSettings()
        {
            var backupPaths = _textureCompressionSettings.BackupAssetPaths;
            var pathsPreview = string.Join("\n", backupPaths.Take(8));
            if (backupPaths.Count > 8) pathsPreview += "\n…ほか " + (backupPaths.Count - 8).ToString("N0") + " 件";
            if (!_textureCompressionSettings.HasBackup ||
                !EditorUtility.DisplayDialog("Texture設定を復元", _textureCompressionSettings.BackupAssetCount + " 個のTextureについて、直前のApply前に保存した設定へ戻します。対象Platformの設定も復元されます。\n対象Asset:\n" + pathsPreview, "復元", "キャンセル")) return;

            var restored = _textureCompressionSettings.RestoreLastBackup(out var errors);
            if (!string.IsNullOrEmpty(errors))
            {
                Debug.LogError("AvaBuild Inspector: Texture設定の復元に一部失敗しました。\n" + errors);
                ShowNotification(new GUIContent("一部の復元に失敗しました。Consoleを確認してください。"));
            }
            else if (restored > 0)
            {
                ShowNotification(new GUIContent(restored.ToString("N0") + " 個のTexture設定を復元しました。"));
            }

            _textureCompressionSettings.ClearSettings();
            _textureImportInfoCache.Clear();
            _searchTextCache.Clear();
            if (_currentAvatar != null) RequestScan(_currentAvatar);
            else RebuildSelectedTab();
        }
    }
}
