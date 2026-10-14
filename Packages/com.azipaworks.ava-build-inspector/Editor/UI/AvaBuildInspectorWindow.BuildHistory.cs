using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using VRC.SDKBase.Validation.Performance;

namespace AvaBuildInspector
{
    internal sealed partial class AvaBuildInspectorWindow
    {
        private const string HistoryPlatformAll = "All";
        private const string HistoryAllAvatars = "（すべてのAvatar）";
        private const int TrendBuildLimit = 15;

        private static readonly (string Section, string Name, string Label)[] TrendMetrics =
        {
            ("SIZE", "Download Size", "Bundle Size"),
            ("SIZE", "Texture Memory", "Texture Memory"),
            ("MESH", "Triangles", "Triangles"),
            ("MESH", "Skinned Meshes", "Skinned Meshes"),
            ("MESH", "Material Slots", "Material Slots"),
            ("PHYSBONE", "Components", "PhysBone Components"),
            ("PHYSBONE", "Collision Checks", "PhysBone Collision Checks")
        };

        [SerializeField] private string _historyAvatarName;
        private bool _historyAvatarExplicit;
        private string _historyPlatformFilter = HistoryPlatformAll;
        private string _selectedBuildHistoryEntryId;
        private string _comparisonBaseEntryId;
        private bool _showBuildHistoryComparison;
        private bool _showChangedOnly = true;

        private void BuildHistoryTab()
        {
            var entries = AvatarBuildHistoryStore.Entries;
            var scroll = new ScrollView(ScrollViewMode.Vertical) { style = { flexGrow = 1 } };
            scroll.style.marginLeft = 12;
            scroll.style.marginRight = 12;
            _content.Add(scroll);

            var header = new VisualElement
            {
                style =
                {
                    marginTop = 10,
                    marginBottom = 6,
                    paddingLeft = 14,
                    paddingRight = 14,
                    paddingTop = 10,
                    paddingBottom = 10,
                    backgroundColor = AbiTheme.HistoryHeader
                }
            };
            var heading = new Label("ビルドレポート") { style = { fontSize = 18, unityFontStyleAndWeight = FontStyle.Bold } };
            header.Add(heading);
            var hint = new Label("VRChat SDKで実行したAvatarビルドの記録です。Avatarごとに推移と前後の差分を確認できます。");
            hint.style.fontSize = 11;
            hint.style.opacity = 0.72f;
            hint.style.marginTop = 3;
            hint.style.whiteSpace = WhiteSpace.Normal;
            header.Add(hint);
            scroll.Add(header);

            if (entries.Count == 0)
            {
                var empty = new VisualElement
                {
                    style =
                    {
                        paddingLeft = 16,
                        paddingRight = 16,
                        paddingTop = 20,
                        paddingBottom = 20,
                        alignItems = Align.Center,
                        backgroundColor = AbiTheme.Card
                    }
                };
                var emptyTitle = new Label("まだビルド履歴はありません");
                emptyTitle.style.fontSize = 14;
                emptyTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
                empty.Add(emptyTitle);
                var emptyHint = new Label("VRChat SDKで次のAvatarビルドを実行すると、結果がここに保存されます。");
                emptyHint.style.opacity = 0.68f;
                emptyHint.style.marginTop = 5;
                empty.Add(emptyHint);
                scroll.Add(empty);
                return;
            }

            var avatarNames = entries
                .Select(entry => GetHistoryAvatarName(entry))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            ResolveHistoryAvatar(avatarNames);
            var allAvatars = _historyAvatarName == HistoryAllAvatars;

            BuildHistoryFilterBar(scroll, entries, avatarNames);

            var avatarEntries = entries
                .Where(entry => allAvatars || string.Equals(GetHistoryAvatarName(entry), _historyAvatarName, StringComparison.OrdinalIgnoreCase))
                .Where(entry => _historyPlatformFilter == HistoryPlatformAll || string.Equals(entry.Platform, _historyPlatformFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (avatarEntries.Count == 0)
            {
                var none = new HelpBox("このAvatar・Platformのビルド履歴はありません。Platformの絞り込みを変更してください。", HelpBoxMessageType.Info);
                none.style.marginTop = 6;
                scroll.Add(none);
                return;
            }

            var selected = avatarEntries.FirstOrDefault(entry => entry.Id == _selectedBuildHistoryEntryId) ?? avatarEntries[0];
            _selectedBuildHistoryEntryId = selected.Id;

            if (!allAvatars) BuildAvatarHistoryOverview(scroll, entries, avatarEntries, selected);

            var listHeading = new Label(allAvatars ? "ビルド履歴（すべてのAvatar）" : "ビルド履歴 · " + _historyAvatarName);
            listHeading.style.unityFontStyleAndWeight = FontStyle.Bold;
            listHeading.style.fontSize = 14;
            listHeading.style.marginTop = 8;
            listHeading.style.marginBottom = 3;
            scroll.Add(listHeading);
            var historyList = new ScrollView(ScrollViewMode.Vertical);
            historyList.style.height = Mathf.Min(230f, Mathf.Max(58f, avatarEntries.Count * 60f));
            historyList.style.flexShrink = 0;
            historyList.style.paddingRight = 4;
            scroll.Add(historyList);
            foreach (var entry in avatarEntries)
                historyList.Add(CreateBuildHistoryCard(entry, entry.Id == selected.Id, FindPreviousBuild(entries, entry), allAvatars));
            var selectedIndexInList = avatarEntries.IndexOf(selected);
            if (selectedIndexInList > 2)
                historyList.schedule.Execute(() => historyList.ScrollTo(historyList.contentContainer[selectedIndexInList]));

            BuildBuildHistoryDetail(scroll, entries, avatarEntries, selected);
        }

        private static string GetHistoryAvatarName(AvatarBuildHistoryEntry entry) =>
            string.IsNullOrEmpty(entry?.AvatarName) ? "Unknown Avatar" : entry.AvatarName;

        private void ResolveHistoryAvatar(List<string> avatarNames)
        {
            if (!_historyAvatarExplicit && _currentAvatar != null)
            {
                var match = avatarNames.FirstOrDefault(name => string.Equals(name, _currentAvatar.name, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    if (!string.Equals(_historyAvatarName, match, StringComparison.Ordinal))
                    {
                        _historyAvatarName = match;
                        _selectedBuildHistoryEntryId = null;
                        _comparisonBaseEntryId = null;
                    }
                    return;
                }
            }
            if (_historyAvatarName == HistoryAllAvatars) return;
            if (string.IsNullOrEmpty(_historyAvatarName) || !avatarNames.Contains(_historyAvatarName, StringComparer.OrdinalIgnoreCase))
                _historyAvatarName = avatarNames[0];
        }

        private void BuildHistoryFilterBar(VisualElement parent, IReadOnlyList<AvatarBuildHistoryEntry> entries, List<string> avatarNames)
        {
            var bar = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    flexWrap = Wrap.Wrap,
                    alignItems = Align.Center,
                    paddingLeft = 7,
                    paddingRight = 7,
                    paddingTop = 4,
                    paddingBottom = 4,
                    backgroundColor = AbiTheme.Band
                }
            };
            AbiTheme.SetRadius(bar.style, 3);

            var counts = entries
                .GroupBy(GetHistoryAvatarName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
            var choices = new List<string>(avatarNames) { HistoryAllAvatars };
            string Format(string name) => name == HistoryAllAvatars
                ? name + "  ·  " + entries.Count.ToString("N0") + "件"
                : name + "  ·  " + (counts.TryGetValue(name, out var count) ? count : 0).ToString("N0") + "件";
            var avatarPopup = new PopupField<string>("Avatar", choices, _historyAvatarName, Format, Format);
            avatarPopup.style.flexGrow = 1;
            avatarPopup.style.minWidth = 220;
            avatarPopup.labelElement.style.minWidth = 50;
            avatarPopup.RegisterValueChangedCallback(evt =>
            {
                _historyAvatarName = evt.newValue;
                _historyAvatarExplicit = true;
                _selectedBuildHistoryEntryId = null;
                _comparisonBaseEntryId = null;
                RebuildSelectedTab();
            });
            bar.Add(avatarPopup);

            var currentName = _currentAvatar == null ? null : avatarNames.FirstOrDefault(name => string.Equals(name, _currentAvatar.name, StringComparison.OrdinalIgnoreCase));
            var current = new Button(() =>
            {
                _historyAvatarName = currentName;
                _historyAvatarExplicit = false;
                _selectedBuildHistoryEntryId = null;
                _comparisonBaseEntryId = null;
                RebuildSelectedTab();
            }) { text = "現在のAvatar" };
            current.tooltip = currentName == null
                ? "選択中のAvatarのビルド履歴はありません。"
                : "ヘッダーで選択中のAvatar（" + currentName + "）の履歴を表示します。";
            current.SetEnabled(currentName != null && !string.Equals(currentName, _historyAvatarName, StringComparison.OrdinalIgnoreCase));
            bar.Add(current);

            var platforms = new VisualElement { style = { flexDirection = FlexDirection.Row, marginLeft = 8 } };
            AddHistoryPlatformButton(platforms, "すべて", HistoryPlatformAll);
            AddHistoryPlatformButton(platforms, "PC", "PC");
            AddHistoryPlatformButton(platforms, "Android", "Android");
            bar.Add(platforms);
            parent.Add(bar);
        }

        private void AddHistoryPlatformButton(VisualElement parent, string label, string platform)
        {
            var button = new Button(() =>
            {
                _historyPlatformFilter = platform;
                EditorPrefsSetString("HistoryPlatform", platform);
                _selectedBuildHistoryEntryId = null;
                _comparisonBaseEntryId = null;
                RebuildSelectedTab();
            }) { text = label };
            button.style.minWidth = 58;
            button.style.marginLeft = 0;
            button.style.marginRight = 1;
            StyleTabButton(button, string.Equals(_historyPlatformFilter, platform, StringComparison.OrdinalIgnoreCase), true);
            parent.Add(button);
        }

        private static void EditorPrefsSetString(string key, string value) => UnityEditor.EditorPrefs.SetString(PrefPrefix + key, value);

        private void BuildAvatarHistoryOverview(VisualElement parent, IReadOnlyList<AvatarBuildHistoryEntry> allEntries,
            List<AvatarBuildHistoryEntry> avatarEntries, AvatarBuildHistoryEntry selected)
        {
            var stats = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap, marginTop = 8 } };
            var successCount = avatarEntries.Count(entry => entry.Succeeded);
            stats.Add(CreateBuildHistoryStatCard("ビルド数", avatarEntries.Count.ToString("N0"),
                "成功 " + successCount.ToString("N0") + " · 失敗 " + (avatarEntries.Count - successCount).ToString("N0"), AbiTheme.BarFill));

            var latest = avatarEntries[0];
            var latestRankCard = CreateBuildHistoryStatCard("最新のOverall", latest.OverallRank ?? "Unavailable",
                latest.FinishedAt + " · " + latest.Platform, GetBuildHistoryRankColor(latest.OverallRank));
            stats.Add(latestRankCard);

            var latestSuccess = avatarEntries.FirstOrDefault(entry => entry.Succeeded);
            var bundleText = "—";
            var bundleCaption = "成功ビルドなし";
            if (latestSuccess != null)
            {
                var bundle = FindMetric(latestSuccess, "SIZE", "Download Size");
                bundleText = bundle == null ? "未取得" : bundle.DisplayValue;
                var previousSuccess = allEntries
                    .SkipWhile(entry => entry.Id != latestSuccess.Id).Skip(1)
                    .FirstOrDefault(entry => entry.Succeeded &&
                                             string.Equals(GetHistoryAvatarName(entry), GetHistoryAvatarName(latestSuccess), StringComparison.OrdinalIgnoreCase) &&
                                             string.Equals(entry.Platform, latestSuccess.Platform, StringComparison.OrdinalIgnoreCase));
                bundleCaption = latestSuccess.Platform;
                if (previousSuccess != null && TryGetBuildMetricValue(bundle, out var now, out var unit) &&
                    TryGetBuildMetricValue(FindMetric(previousSuccess, "SIZE", "Download Size"), out var before, out _))
                    bundleCaption += " · 前回比 " + FormatBuildHistoryDelta(now - before, unit);
            }
            stats.Add(CreateBuildHistoryStatCard("最新のBundle Size", bundleText, bundleCaption, AbiTheme.SuccessBorder));
            parent.Add(stats);

            var trendPlatform = _historyPlatformFilter != HistoryPlatformAll ? _historyPlatformFilter : selected.Platform;
            var trendEntries = avatarEntries
                .Where(entry => string.Equals(entry.Platform, trendPlatform, StringComparison.OrdinalIgnoreCase))
                .Take(TrendBuildLimit)
                .Reverse()
                .ToList();
            if (trendEntries.Count < 2) return;

            var trendCard = new VisualElement
            {
                style =
                {
                    marginTop = 4,
                    paddingLeft = 10,
                    paddingRight = 10,
                    paddingTop = 7,
                    paddingBottom = 8,
                    backgroundColor = AbiTheme.Card
                }
            };
            var trendTitle = new Label("推移（" + trendPlatform + "・直近" + trendEntries.Count + "回）");
            trendTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
            trendTitle.style.marginBottom = 2;
            trendCard.Add(trendTitle);
            var trendHint = new Label("棒をクリックするとそのビルドを選択します。強調表示が選択中のビルドです。左が古いビルドです。");
            trendHint.style.fontSize = 10;
            trendHint.style.opacity = 0.62f;
            trendHint.style.marginBottom = 4;
            trendHint.style.whiteSpace = WhiteSpace.Normal;
            trendCard.Add(trendHint);

            var rowCount = 0;
            foreach (var trend in TrendMetrics)
            {
                var row = CreateTrendRow(trend.Label, trend.Section, trend.Name, trendEntries, selected);
                if (row == null) continue;
                trendCard.Add(row);
                rowCount++;
            }
            if (rowCount > 0) parent.Add(trendCard);
        }

        private VisualElement CreateTrendRow(string label, string section, string metricName,
            List<AvatarBuildHistoryEntry> entries, AvatarBuildHistoryEntry selected)
        {
            var points = entries
                .Select(entry => (entry, ok: TryGetBuildMetricValue(FindMetric(entry, section, metricName), out var value, out var unit), value, unit))
                .ToList();
            var numeric = points.Where(point => point.ok).ToList();
            if (numeric.Count < 2) return null;

            var min = numeric.Min(point => point.value);
            var max = numeric.Max(point => point.value);
            var unitText = numeric.Last().unit;

            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, minHeight = 28 } };
            var name = new Label(label) { style = { width = 150, minWidth = 110, flexShrink = 1 } };
            ApplyEllipsis(name);
            row.Add(name);

            var chart = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    alignItems = Align.FlexEnd,
                    flexGrow = 1,
                    height = 22,
                    marginLeft = 6,
                    marginRight = 8,
                    paddingLeft = 2,
                    paddingRight = 2,
                    backgroundColor = AbiTheme.Track
                }
            };
            foreach (var point in points)
            {
                var entry = point.entry;
                var bar = new VisualElement();
                bar.AddToClassList("abi-trend-bar");
                bar.style.flexGrow = 1;
                bar.style.marginLeft = 1;
                bar.style.marginRight = 1;
                if (point.ok)
                {
                    var ratio = Math.Abs(max - min) < double.Epsilon ? 0.6d : 0.15d + (point.value - min) / (max - min) * 0.85d;
                    bar.style.height = Length.Percent((float)(ratio * 100d));
                    bar.style.backgroundColor = entry.Id == selected.Id ? AbiTheme.BarHighlight : AbiTheme.BarFill;
                    bar.style.opacity = entry.Succeeded ? 1f : 0.45f;
                    bar.tooltip = entry.FinishedAt + (entry.Succeeded ? "" : "（失敗）") + "\n" + label + ": " +
                                  point.value.ToString(IsCountUnit(point.unit) ? "N0" : "N2") + (IsCountUnit(point.unit) ? "" : " " + point.unit);
                }
                else
                {
                    bar.style.height = 2;
                    bar.style.opacity = 0.3f;
                    bar.tooltip = entry.FinishedAt + "\n値なし";
                }
                bar.RegisterCallback<ClickEvent>(_ =>
                {
                    _selectedBuildHistoryEntryId = entry.Id;
                    _comparisonBaseEntryId = null;
                    RebuildSelectedTab();
                });
                chart.Add(bar);
            }
            row.Add(chart);

            var first = numeric.First().value;
            var last = numeric.Last().value;
            var delta = last - first;
            var change = new Label(FormatTrendValue(first, unitText) + " → " + FormatTrendValue(last, unitText));
            change.style.width = 170;
            change.style.minWidth = 120;
            change.style.flexShrink = 1;
            change.style.unityTextAlign = TextAnchor.MiddleRight;
            change.style.fontSize = 11;
            change.style.color = delta < 0d ? AbiTheme.Decrease : delta > 0d ? AbiTheme.Increase : AbiTheme.Neutral;
            change.tooltip = "最初の記録からの変化: " + FormatBuildHistoryDelta(delta, unitText) +
                             (Math.Abs(first) > double.Epsilon ? "（" + (delta / Math.Abs(first) * 100d).ToString("+0.0;-0.0;0") + "%）" : "");
            ApplyEllipsis(change);
            row.Add(change);
            return row;
        }

        private static bool IsCountUnit(string unit) => string.IsNullOrEmpty(unit) || string.Equals(unit, "count", StringComparison.OrdinalIgnoreCase);

        private static string FormatTrendValue(double value, string unit) =>
            IsCountUnit(unit) ? value.ToString("N0") : value.ToString("N1") + " " + unit;

        private static AvatarBuildMetricSnapshot FindMetric(AvatarBuildHistoryEntry entry, string section, string displayName)
        {
            return entry?.Metrics?.FirstOrDefault(metric =>
                string.Equals(metric.DisplayName, displayName, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrEmpty(section) || string.Equals(metric.Section, section, StringComparison.OrdinalIgnoreCase)));
        }

        private void BuildBuildHistoryDetail(VisualElement parent, IReadOnlyList<AvatarBuildHistoryEntry> allEntries,
            List<AvatarBuildHistoryEntry> avatarEntries, AvatarBuildHistoryEntry selected)
        {
            var detailCard = new VisualElement
            {
                style =
                {
                    marginTop = 6,
                    marginBottom = 12,
                    paddingLeft = 12,
                    paddingRight = 12,
                    paddingTop = 10,
                    paddingBottom = 12,
                    backgroundColor = AbiTheme.Card
                }
            };

            var navigation = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };
            var detailTitle = new Label("選択中のビルド結果");
            detailTitle.style.fontSize = 14;
            detailTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
            detailTitle.style.flexGrow = 1;
            navigation.Add(detailTitle);
            var index = avatarEntries.IndexOf(selected);
            var older = index + 1 < avatarEntries.Count ? avatarEntries[index + 1] : null;
            var newer = index > 0 ? avatarEntries[index - 1] : null;
            var olderButton = new Button(() => SelectHistoryEntry(older)) { text = "◀ 前のビルド" };
            olderButton.SetEnabled(older != null);
            olderButton.tooltip = older == null ? "これより古いビルドはありません。" : older.FinishedAt + " · " + older.Platform;
            navigation.Add(olderButton);
            var position = new Label((avatarEntries.Count - index).ToString("N0") + " / " + avatarEntries.Count.ToString("N0"));
            position.style.marginLeft = 4;
            position.style.marginRight = 4;
            position.style.opacity = 0.65f;
            navigation.Add(position);
            var newerButton = new Button(() => SelectHistoryEntry(newer)) { text = "次のビルド ▶" };
            newerButton.SetEnabled(newer != null);
            newerButton.tooltip = newer == null ? "これより新しいビルドはありません。" : newer.FinishedAt + " · " + newer.Platform;
            navigation.Add(newerButton);
            detailCard.Add(navigation);

            var detailHeader = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginTop = 8 } };
            var status = new Label(selected.Succeeded ? "成功" : "失敗");
            status.style.paddingLeft = 9;
            status.style.paddingRight = 9;
            status.style.paddingTop = 3;
            status.style.paddingBottom = 3;
            status.style.unityFontStyleAndWeight = FontStyle.Bold;
            status.style.color = Color.white;
            status.style.backgroundColor = selected.Succeeded ? AbiTheme.SuccessStrong : AbiTheme.FailureStrong;
            detailHeader.Add(status);
            var avatarName = new Label(GetHistoryAvatarName(selected));
            avatarName.style.fontSize = 16;
            avatarName.style.unityFontStyleAndWeight = FontStyle.Bold;
            avatarName.style.flexGrow = 1;
            avatarName.style.flexShrink = 1;
            avatarName.style.marginLeft = 10;
            ApplyEllipsis(avatarName);
            detailHeader.Add(avatarName);
            var rank = new Label("Overall  " + selected.OverallRank);
            rank.style.paddingLeft = 8;
            rank.style.paddingRight = 8;
            rank.style.paddingTop = 4;
            rank.style.paddingBottom = 4;
            rank.style.backgroundColor = AbiTheme.Chip;
            rank.style.color = GetBuildHistoryRankColor(selected.OverallRank);
            detailHeader.Add(rank);
            detailCard.Add(detailHeader);

            var metadata = new Label(selected.Platform + "  ·  " + selected.FinishedAt);
            metadata.style.opacity = 0.68f;
            metadata.style.marginTop = 5;
            detailCard.Add(metadata);

            var downloadSize = FindMetric(selected, "SIZE", "Download Size");
            var detailStats = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap, marginTop = 10 } };
            detailStats.Add(CreateBuildHistoryStatCard("Bundle Size", downloadSize == null ? "未取得" : downloadSize.DisplayValue,
                downloadSize == null ? "" : downloadSize.Accuracy, AbiTheme.BarFill));
            detailStats.Add(CreateBuildHistoryStatCard("性能項目", (selected.Metrics?.Count ?? 0).ToString("N0"), "Metric", AbiTheme.Warning));
            detailCard.Add(detailStats);

            if (!selected.Succeeded && !string.IsNullOrEmpty(selected.Error))
                detailCard.Add(new HelpBox(selected.Error, HelpBoxMessageType.Error));
            if (selected.Warnings != null)
                foreach (var warning in selected.Warnings)
                    detailCard.Add(new HelpBox(warning, HelpBoxMessageType.Warning));

            var detailViews = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    marginTop = 12,
                    marginBottom = 5,
                    paddingBottom = 4,
                    borderBottomWidth = 1,
                    borderBottomColor = AbiTheme.Border
                }
            };
            AddBuildHistoryDetailViewTab(detailViews, "ビルドレポート", false);
            AddBuildHistoryDetailViewTab(detailViews, "前後の比較", true);
            detailCard.Add(detailViews);

            if (_showBuildHistoryComparison) BuildBuildHistoryComparison(detailCard, allEntries, selected);
            else BuildBuildHistoryMetrics(detailCard, selected);
            parent.Add(detailCard);
        }

        private void SelectHistoryEntry(AvatarBuildHistoryEntry entry)
        {
            if (entry == null) return;
            _selectedBuildHistoryEntryId = entry.Id;
            _comparisonBaseEntryId = null;
            RebuildSelectedTab();
        }

        private void BuildBuildHistoryMetrics(VisualElement parent, AvatarBuildHistoryEntry selected)
        {
            var metricsHeader = new VisualElement { style = { flexDirection = FlexDirection.Row, minHeight = 23, alignItems = Align.Center } };
            var nameHeader = new Label("項目") { style = { flexGrow = 1, opacity = 0.58f } };
            metricsHeader.Add(nameHeader);
            var valueHeader = new Label("値") { style = { width = 120, unityTextAlign = TextAnchor.MiddleRight, opacity = 0.58f } };
            metricsHeader.Add(valueHeader);
            var rankHeader = new Label("Rank / 精度") { style = { width = 150, unityTextAlign = TextAnchor.MiddleRight, opacity = 0.58f } };
            metricsHeader.Add(rankHeader);
            parent.Add(metricsHeader);

            if (selected.Metrics == null || selected.Metrics.Count == 0)
            {
                parent.Add(new HelpBox("このビルドには性能Metricが記録されていません。", HelpBoxMessageType.Info));
                return;
            }

            foreach (var section in selected.Metrics.GroupBy(metric => string.IsNullOrEmpty(metric.Section) ? "その他" : metric.Section))
            {
                var foldout = new Foldout { text = section.Key + "  ·  " + section.Count().ToString("N0"), value = true };
                foldout.style.marginTop = 2;
                var rowIndex = 0;
                foreach (var metric in section)
                {
                    var line = new VisualElement();
                    line.AddToClassList("abi-row");
                    line.AddToClassList(rowIndex++ % 2 == 0 ? "abi-row--even" : "abi-row--odd");
                    line.style.minHeight = 25;
                    line.style.paddingLeft = 6;
                    line.style.paddingRight = 4;
                    var name = new Label(metric.DisplayName) { style = { flexGrow = 1, flexShrink = 1 } };
                    name.tooltip = metric.Explanation;
                    line.Add(name);
                    var value = new Label(metric.DisplayValue) { style = { width = 120, unityTextAlign = TextAnchor.MiddleRight } };
                    line.Add(value);
                    var quality = new Label(metric.Rank + " / " + metric.Accuracy)
                    {
                        style = { width = 150, unityTextAlign = TextAnchor.MiddleRight, marginLeft = 8 }
                    };
                    quality.style.color = GetBuildHistoryRankColor(metric.Rank);
                    quality.tooltip = metric.Explanation;
                    line.Add(quality);
                    foldout.Add(line);
                }
                parent.Add(foldout);
            }
        }

        private void AddBuildHistoryDetailViewTab(VisualElement parent, string title, bool comparison)
        {
            var button = new Button(() =>
            {
                _showBuildHistoryComparison = comparison;
                RebuildSelectedTab();
            }) { text = title };
            button.style.minWidth = 130;
            button.style.height = 28;
            button.style.marginRight = 4;
            StyleTabButton(button, _showBuildHistoryComparison == comparison);
            parent.Add(button);
        }

        private static AvatarBuildHistoryEntry FindPreviousBuild(IReadOnlyList<AvatarBuildHistoryEntry> entries,
            AvatarBuildHistoryEntry selected)
        {
            if (entries == null || selected == null) return null;
            var foundSelected = false;
            foreach (var entry in entries)
            {
                if (foundSelected && string.Equals(GetHistoryAvatarName(entry), GetHistoryAvatarName(selected), StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(entry.Platform, selected.Platform, StringComparison.OrdinalIgnoreCase))
                    return entry;
                if (string.Equals(entry.Id, selected.Id, StringComparison.Ordinal)) foundSelected = true;
            }
            return null;
        }

        private void BuildBuildHistoryComparison(VisualElement parent, IReadOnlyList<AvatarBuildHistoryEntry> allEntries,
            AvatarBuildHistoryEntry selected)
        {
            var candidates = allEntries
                .Where(entry => entry.Id != selected.Id &&
                                string.Equals(GetHistoryAvatarName(entry), GetHistoryAvatarName(selected), StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (candidates.Count == 0)
            {
                var noPrevious = new HelpBox("同じAvatarの他のビルドがないため、比較できません。", HelpBoxMessageType.Info);
                noPrevious.style.marginTop = 5;
                parent.Add(noPrevious);
                return;
            }

            var baseEntry = candidates.FirstOrDefault(entry => entry.Id == _comparisonBaseEntryId)
                            ?? FindPreviousBuild(allEntries, selected)
                            ?? candidates[0];

            string FormatEntry(AvatarBuildHistoryEntry entry) =>
                entry.FinishedAt + " · " + entry.Platform + " · " + (entry.Succeeded ? "成功" : "失敗") + " · " + entry.OverallRank;

            var selectorRow = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap, alignItems = Align.Center, marginTop = 6 } };
            var basePopup = new PopupField<AvatarBuildHistoryEntry>("比較元", candidates, baseEntry, FormatEntry, FormatEntry);
            basePopup.style.flexGrow = 1;
            basePopup.style.minWidth = 260;
            basePopup.labelElement.style.minWidth = 50;
            basePopup.tooltip = "比較の基準にするビルドを選びます。既定は同じPlatformの直前のビルドです。";
            basePopup.RegisterValueChangedCallback(evt =>
            {
                _comparisonBaseEntryId = evt.newValue?.Id;
                RebuildSelectedTab();
            });
            selectorRow.Add(basePopup);
            var changedOnly = new Toggle { text = "変化した項目のみ", value = _showChangedOnly };
            changedOnly.style.marginLeft = 8;
            changedOnly.RegisterValueChangedCallback(evt =>
            {
                _showChangedOnly = evt.newValue;
                WriteBool("HistoryChangedOnly", evt.newValue);
                RebuildSelectedTab();
            });
            selectorRow.Add(changedOnly);
            parent.Add(selectorRow);

            var direction = new Label("比較元 " + baseEntry.FinishedAt + "  →  選択中 " + selected.FinishedAt + "（削減は緑、増加は橙）");
            direction.style.fontSize = 10;
            direction.style.opacity = 0.68f;
            direction.style.marginTop = 3;
            direction.style.marginBottom = 4;
            parent.Add(direction);

            if (!string.Equals(baseEntry.Platform, selected.Platform, StringComparison.OrdinalIgnoreCase))
                parent.Add(new HelpBox("比較元とPlatformが異なります（" + baseEntry.Platform + " → " + selected.Platform + "）。Rankの基準や圧縮形式が違うため参考値として見てください。", HelpBoxMessageType.Warning));

            if (!string.Equals(baseEntry.OverallRank, selected.OverallRank, StringComparison.OrdinalIgnoreCase))
            {
                var rankChange = new Label("Overall:  " + baseEntry.OverallRank + "  →  " + selected.OverallRank);
                rankChange.style.unityFontStyleAndWeight = FontStyle.Bold;
                rankChange.style.marginBottom = 4;
                var improved = Enum.TryParse(baseEntry.OverallRank, out PerformanceRating beforeRank) &&
                               Enum.TryParse(selected.OverallRank, out PerformanceRating afterRank) && (int)afterRank < (int)beforeRank;
                rankChange.style.color = improved ? AbiTheme.Decrease : AbiTheme.Increase;
                parent.Add(rankChange);
            }

            var currentMetrics = selected.Metrics ?? new List<AvatarBuildMetricSnapshot>();
            var previousMetrics = baseEntry.Metrics ?? new List<AvatarBuildMetricSnapshot>();
            var pairs = currentMetrics
                .Select(current => new
                {
                    Current = current,
                    Previous = previousMetrics.FirstOrDefault(candidate =>
                        string.Equals(candidate.Section, current.Section, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(candidate.DisplayName, current.DisplayName, StringComparison.OrdinalIgnoreCase))
                })
                .Where(pair => pair.Previous != null &&
                               TryGetBuildMetricValue(pair.Current, out _, out var currentUnit) &&
                               TryGetBuildMetricValue(pair.Previous, out _, out var previousUnit) &&
                               string.Equals(currentUnit, previousUnit, StringComparison.OrdinalIgnoreCase))
                .Select(pair =>
                {
                    TryGetBuildMetricValue(pair.Current, out var currentValue, out var unit);
                    TryGetBuildMetricValue(pair.Previous, out var previousValue, out _);
                    return new { pair.Current, pair.Previous, CurrentValue = currentValue, PreviousValue = previousValue, Unit = unit };
                })
                .ToList();

            var decreased = pairs.Count(pair => pair.CurrentValue < pair.PreviousValue);
            var increased = pairs.Count(pair => pair.CurrentValue > pair.PreviousValue);
            var unchanged = pairs.Count - decreased - increased;
            var summary = new Label("削減 " + decreased + " 項目 · 増加 " + increased + " 項目 · 変化なし " + unchanged + " 項目");
            summary.style.marginBottom = 4;
            parent.Add(summary);

            var visible = _showChangedOnly ? pairs.Where(pair => Math.Abs(pair.CurrentValue - pair.PreviousValue) > double.Epsilon).ToList() : pairs;
            foreach (var section in visible.GroupBy(pair => string.IsNullOrEmpty(pair.Current.Section) ? "その他" : pair.Current.Section))
            {
                var sectionFoldout = new Foldout { text = section.Key + " · " + section.Count().ToString("N0"), value = true };
                sectionFoldout.style.marginTop = 2;
                foreach (var pair in section)
                    sectionFoldout.Add(CreateBuildHistoryComparisonRow(pair.Current, pair.Previous, pair.CurrentValue, pair.PreviousValue, pair.Unit));
                parent.Add(sectionFoldout);
            }

            if (pairs.Count == 0)
            {
                var unavailable = new HelpBox("比較元の記録に比較可能な数値Metricがありません。", HelpBoxMessageType.Info);
                unavailable.style.marginTop = 5;
                parent.Add(unavailable);
            }
            else if (visible.Count == 0)
            {
                var same = new HelpBox("比較できる項目はすべて同じ値です。", HelpBoxMessageType.Info);
                same.style.marginTop = 5;
                parent.Add(same);
            }
        }

        private static VisualElement CreateBuildHistoryComparisonRow(AvatarBuildMetricSnapshot current,
            AvatarBuildMetricSnapshot previous, double currentValue, double previousValue, string unit)
        {
            var delta = currentValue - previousValue;
            var hasPercentage = Math.Abs(previousValue) > double.Epsilon;
            var percentage = hasPercentage ? delta / Math.Abs(previousValue) * 100d : 0d;
            var increased = delta > 0d;
            var decreased = delta < 0d;
            var accent = increased ? AbiTheme.Increase : decreased ? AbiTheme.Decrease : AbiTheme.Neutral;

            var row = new VisualElement();
            row.AddToClassList("abi-row");
            row.AddToClassList("abi-row--even");
            row.style.minHeight = 32;
            row.style.paddingLeft = 6;
            row.style.paddingRight = 5;
            var name = new Label(current.DisplayName) { style = { flexGrow = 1, flexShrink = 1, minWidth = 90 } };
            name.tooltip = current.Explanation;
            ApplyEllipsis(name);
            row.Add(name);

            var values = new Label(previous.DisplayValue + "  →  " + current.DisplayValue)
            {
                style = { width = 190, unityTextAlign = TextAnchor.MiddleRight }
            };
            values.tooltip = "比較元: " + previous.DisplayValue + "\n選択中: " + current.DisplayValue + "\n差分: " + FormatBuildHistoryDelta(delta, unit);
            row.Add(values);

            var bar = new VisualElement
            {
                style =
                {
                    width = 72,
                    minWidth = 48,
                    height = 6,
                    marginLeft = 8,
                    marginRight = 7,
                    backgroundColor = AbiTheme.Track
                }
            };
            var magnitude = hasPercentage ? Mathf.Clamp01((float)(Math.Abs(percentage) / 100d)) : (increased ? 1f : 0f);
            var fill = new VisualElement
            {
                style =
                {
                    width = Length.Percent(magnitude * 100f),
                    height = 6,
                    backgroundColor = accent
                }
            };
            bar.Add(fill);
            var rateText = !increased && !decreased
                ? "変化なし"
                : hasPercentage
                    ? (decreased ? "削減 " : "増加 ") + Math.Abs(percentage).ToString("N1") + "%"
                    : "増加 新規";
            bar.tooltip = hasPercentage
                ? "比較元比 " + (delta >= 0d ? "+" : "") + percentage.ToString("N2") + "%"
                : "比較元の値が0のため割合を計算できません。差分: " + FormatBuildHistoryDelta(delta, unit);
            row.Add(bar);

            var rate = new Label(rateText)
            {
                style = { width = 86, unityTextAlign = TextAnchor.MiddleRight, unityFontStyleAndWeight = FontStyle.Bold }
            };
            rate.style.color = accent;
            row.Add(rate);
            return row;
        }

        private static bool TryGetBuildMetricValue(AvatarBuildMetricSnapshot metric, out double value, out string unit)
        {
            value = 0d;
            unit = string.Empty;
            if (metric == null || string.Equals(metric.Accuracy, AnalysisAccuracy.Unavailable.ToString(), StringComparison.OrdinalIgnoreCase))
                return false;
            if (metric.HasNumericValue)
            {
                value = metric.NumericValue;
                unit = metric.Unit ?? string.Empty;
                return !string.IsNullOrEmpty(unit);
            }

            var display = (metric.DisplayValue ?? string.Empty).Trim();
            var separator = display.IndexOfAny(new[] { ' ', '\t' });
            var numberText = separator < 0 ? display : display.Substring(0, separator);
            if (!double.TryParse(numberText, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value))
                return false;
            unit = separator < 0 ? "count" : display.Substring(separator).Trim();
            return !string.IsNullOrEmpty(unit);
        }

        private static string FormatBuildHistoryDelta(double delta, string unit)
        {
            var isCount = IsCountUnit(unit);
            return (delta >= 0d ? "+" : "") + delta.ToString(isCount ? "N0" : "N2") + (isCount ? "" : " " + unit);
        }

        private static VisualElement CreateBuildHistoryStatCard(string title, string value, string caption, Color accent)
        {
            var card = new VisualElement
            {
                style =
                {
                    flexGrow = 1,
                    minWidth = 145,
                    marginRight = 5,
                    marginBottom = 5,
                    paddingLeft = 10,
                    paddingRight = 10,
                    paddingTop = 7,
                    paddingBottom = 7,
                    backgroundColor = AbiTheme.Card,
                    borderLeftWidth = 3,
                    borderLeftColor = accent
                }
            };
            var titleLabel = new Label(title);
            titleLabel.style.fontSize = 10;
            titleLabel.style.opacity = 0.68f;
            card.Add(titleLabel);
            var valueLabel = new Label(value);
            valueLabel.style.fontSize = 17;
            valueLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            card.Add(valueLabel);
            if (!string.IsNullOrEmpty(caption))
            {
                var captionLabel = new Label(caption);
                captionLabel.style.fontSize = 9;
                captionLabel.style.opacity = 0.62f;
                card.Add(captionLabel);
            }
            return card;
        }

        private VisualElement CreateBuildHistoryCard(AvatarBuildHistoryEntry entry, bool isSelected,
            AvatarBuildHistoryEntry previous, bool showAvatarName)
        {
            var card = new Button(() => SelectHistoryEntry(entry));
            card.style.flexDirection = FlexDirection.Column;
            card.style.alignItems = Align.Stretch;
            card.style.marginBottom = 4;
            card.style.paddingLeft = 10;
            card.style.paddingRight = 10;
            card.style.paddingTop = 6;
            card.style.paddingBottom = 6;
            card.style.backgroundColor = isSelected ? AbiTheme.SelectedCard : AbiTheme.Card;
            card.style.borderLeftWidth = 3;
            card.style.borderLeftColor = entry.Succeeded ? AbiTheme.SuccessBorder : AbiTheme.FailureBorder;

            var top = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };
            var title = new Label(showAvatarName ? GetHistoryAvatarName(entry) : entry.FinishedAt);
            title.style.flexGrow = 1;
            title.style.flexShrink = 1;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.unityTextAlign = TextAnchor.MiddleLeft;
            ApplyEllipsis(title);
            top.Add(title);
            var badge = new Label(entry.Succeeded ? "成功" : "失敗");
            badge.style.color = entry.Succeeded ? AbiTheme.Success : AbiTheme.Failure;
            badge.style.unityFontStyleAndWeight = FontStyle.Bold;
            badge.style.marginLeft = 8;
            top.Add(badge);
            var rank = new Label(entry.OverallRank ?? "Unavailable");
            rank.style.marginLeft = 8;
            rank.style.color = GetBuildHistoryRankColor(entry.OverallRank);
            top.Add(rank);
            card.Add(top);

            var footer = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginTop = 2 } };
            var date = new Label((showAvatarName ? entry.FinishedAt + "  ·  " : "") + entry.Platform);
            date.style.flexGrow = 1;
            date.style.fontSize = 10;
            date.style.opacity = 0.68f;
            date.style.unityTextAlign = TextAnchor.MiddleLeft;
            footer.Add(date);

            var downloadSize = FindMetric(entry, "SIZE", "Download Size");
            var size = new Label(downloadSize == null ? "Bundle —" : "Bundle " + downloadSize.DisplayValue);
            size.style.fontSize = 10;
            footer.Add(size);
            AddCardDelta(footer, entry, previous, "SIZE", "Download Size");
            var texture = FindMetric(entry, "SIZE", "Texture Memory");
            if (texture != null)
            {
                var textureLabel = new Label("Texture " + texture.DisplayValue);
                textureLabel.style.fontSize = 10;
                textureLabel.style.marginLeft = 10;
                footer.Add(textureLabel);
                AddCardDelta(footer, entry, previous, "SIZE", "Texture Memory");
            }
            card.Add(footer);
            card.tooltip = entry.Succeeded ? "クリックで詳細を表示" : entry.Error;
            return card;
        }

        private static void AddCardDelta(VisualElement parent, AvatarBuildHistoryEntry entry, AvatarBuildHistoryEntry previous, string section, string name)
        {
            if (previous == null) return;
            if (!TryGetBuildMetricValue(FindMetric(entry, section, name), out var now, out var unit) ||
                !TryGetBuildMetricValue(FindMetric(previous, section, name), out var before, out _)) return;
            var delta = now - before;
            if (Math.Abs(delta) < 0.005d) return;
            var label = new Label(" (" + FormatBuildHistoryDelta(delta, unit) + ")");
            label.style.fontSize = 10;
            label.style.color = delta < 0 ? AbiTheme.Decrease : AbiTheme.Increase;
            label.tooltip = "同じPlatformの前回ビルド（" + previous.FinishedAt + "）からの変化";
            parent.Add(label);
        }

        private static Color GetBuildHistoryRankColor(string rank)
        {
            return Enum.TryParse(rank, out PerformanceRating parsedRank) ? RatingColor(parsedRank) : AbiTheme.Neutral;
        }
    }
}
