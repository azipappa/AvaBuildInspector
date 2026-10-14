using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using VRC.SDKBase.Validation.Performance;

namespace AvaBuildInspector
{
    internal sealed partial class AvaBuildInspectorWindow
    {
        private const int InlineContributorLimit = 100;
        private readonly HashSet<AvatarPerformanceCategory> _expandedMetrics = new HashSet<AvatarPerformanceCategory>();

        private static bool TryGetBreakdownTab(AvatarPerformanceCategory category, out BreakdownTab tab)
        {
            switch (category)
            {
                case AvatarPerformanceCategory.TextureMegabytes:
                    tab = BreakdownTab.TextureMemory;
                    return true;
                case AvatarPerformanceCategory.SkinnedMeshCount:
                    tab = BreakdownTab.SkinnedMeshes;
                    return true;
                case AvatarPerformanceCategory.MaterialCount:
                    tab = BreakdownTab.MaterialSlots;
                    return true;
                case AvatarPerformanceCategory.PhysBoneComponentCount:
                case AvatarPerformanceCategory.PhysBoneTransformCount:
                case AvatarPerformanceCategory.PhysBoneColliderCount:
                case AvatarPerformanceCategory.PhysBoneCollisionCheckCount:
                    tab = BreakdownTab.PhysBones;
                    return true;
                default:
                    tab = default;
                    return false;
            }
        }

        private void BuildScanResults()
        {
            var summary = new VisualElement();
            summary.style.paddingLeft = 14;
            summary.style.paddingRight = 14;
            summary.style.paddingTop = 10;
            summary.style.paddingBottom = 8;
            _content.Add(summary);

            var overall = new Label("Overall Performance  " + RatingIcon(_report.OverallRank) + " " + RatingText(_report.OverallRank));
            overall.style.fontSize = 17;
            overall.style.unityFontStyleAndWeight = FontStyle.Bold;
            overall.style.color = RatingColor(_report.OverallRank);
            summary.Add(overall);

            var scroll = new ScrollView(ScrollViewMode.Vertical) { style = { flexGrow = 1 } };
            var metricRows = new Dictionary<AvatarPerformanceCategory, VisualElement>();

            var issues = _report.Metrics.Where(IsIssue).ToArray();
            if (issues.Length > 0)
            {
                var issueRow = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap, alignItems = Align.Center, marginTop = 4 } };
                var issueLabel = new Label("Main Issues:");
                issueLabel.style.marginRight = 4;
                issueRow.Add(issueLabel);
                foreach (var issue in issues)
                {
                    var metric = issue;
                    var chip = new Button(() =>
                    {
                        if (TryGetBreakdownTab(metric.Category, out var tab))
                        {
                            SelectBreakdown(tab);
                            return;
                        }
                        if (metricRows.TryGetValue(metric.Category, out var row))
                        {
                            if (!_expandedMetrics.Contains(metric.Category)) ToggleMetricDetails(metric, row);
                            scroll.schedule.Execute(() => scroll.ScrollTo(row));
                        }
                    })
                    {
                        text = RatingIcon(metric.Rank) + " " + metric.DisplayName + (TryGetBreakdownTab(metric.Category, out _) ? "  ›" : "")
                    };
                    chip.tooltip = metric.DisplayValue + " · " + RatingText(metric.Rank) + "\nクリックで内訳を表示";
                    chip.style.color = RatingColor(metric.Rank);
                    chip.style.marginTop = 1;
                    chip.style.marginBottom = 1;
                    issueRow.Add(chip);
                }
                summary.Add(issueRow);
            }

            foreach (var warning in _report.Warnings)
                summary.Add(new HelpBox(warning, HelpBoxMessageType.Warning));

            var context = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginTop = 5 } };
            var contextLabel = new Label("対象: " + (_report.AvatarRoot == null ? "Missing" : _report.AvatarRoot.name) + " · " +
                                         _report.Platform + " · " + _report.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"));
            contextLabel.style.flexGrow = 1;
            contextLabel.style.flexShrink = 1;
            contextLabel.style.fontSize = 10;
            contextLabel.style.opacity = 0.65f;
            context.Add(contextLabel);
            var auto = new Toggle { text = "自動再スキャン", value = _autoRescan };
            auto.tooltip = "Avatarの変更を検知したら、1秒後に自動で再スキャンします。";
            auto.style.marginRight = 6;
            auto.RegisterValueChangedCallback(evt => SetAutoRescan(evt.newValue));
            context.Add(auto);
            var copy = new Button(CopyReportToClipboard) { text = "レポートをコピー" };
            context.Add(copy);
            summary.Add(context);

            _content.Add(scroll);
            string section = null;
            var rowIndex = 0;
            foreach (var metric in _report.Metrics)
            {
                if (section != metric.Section)
                {
                    section = metric.Section;
                    var sectionLabel = new Label(section);
                    sectionLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
                    sectionLabel.style.marginLeft = 14;
                    sectionLabel.style.marginTop = 9;
                    sectionLabel.style.marginBottom = 3;
                    scroll.Add(sectionLabel);
                    rowIndex = 0;
                }
                var row = CreateMetricRow(metric, rowIndex++);
                metricRows[metric.Category] = row;
                scroll.Add(row);
            }
        }

        private VisualElement CreateMetricRow(MetricResult metric, int index)
        {
            var container = new VisualElement();
            var row = new VisualElement();
            row.AddToClassList("abi-row");
            row.AddToClassList(index % 2 == 0 ? "abi-row--even" : "abi-row--odd");
            row.style.height = 27;
            row.style.paddingLeft = 14;
            row.style.paddingRight = 10;
            if (IsIssue(metric))
            {
                row.style.borderLeftWidth = 3;
                row.style.borderLeftColor = RatingColor(metric.Rank);
                row.style.paddingLeft = 11;
            }

            var metricName = new Label(metric.DisplayName) { style = { flexGrow = 1, flexShrink = 1 } };
            metricName.tooltip = metric.Explanation ?? "";
            row.Add(metricName);

            var accuracy = new Label(metric.Accuracy.ToString());
            accuracy.style.width = 80;
            accuracy.style.opacity = 0.6f;
            row.Add(accuracy);

            var value = new Label(metric.DisplayValue);
            value.style.width = 120;
            value.style.unityTextAlign = TextAnchor.MiddleRight;
            row.Add(value);

            var rank = new Label(RatingIcon(metric.Rank) + " " + RatingText(metric.Rank));
            rank.style.width = 100;
            rank.style.unityTextAlign = TextAnchor.MiddleRight;
            rank.style.color = RatingColor(metric.Rank);
            row.Add(rank);

            var link = new Label { name = "metric-link" };
            link.AddToClassList("abi-link-label");
            row.Add(link);

            var hasBreakdown = TryGetBreakdownTab(metric.Category, out var breakdownTab);
            var canExpand = !hasBreakdown && metric.Contributors.Count > 0;
            if (hasBreakdown)
            {
                link.text = "内訳 ›";
                row.tooltip = "クリックで内訳表示を開きます。" + (string.IsNullOrEmpty(metric.Explanation) ? "" : "\n" + metric.Explanation);
                row.AddToClassList("abi-row--link");
                row.RegisterCallback<ClickEvent>(_ => SelectBreakdown(breakdownTab));
            }
            else if (canExpand)
            {
                row.tooltip = "クリックで対象の一覧を開閉します。" + (string.IsNullOrEmpty(metric.Explanation) ? "" : "\n" + metric.Explanation);
                row.AddToClassList("abi-row--link");
                row.RegisterCallback<ClickEvent>(_ => ToggleMetricDetails(metric, container));
            }
            container.Add(row);

            if (canExpand)
            {
                var details = new VisualElement { name = "metric-details" };
                details.style.display = DisplayStyle.None;
                container.Add(details);
                if (_expandedMetrics.Contains(metric.Category))
                {
                    _expandedMetrics.Remove(metric.Category);
                    ToggleMetricDetails(metric, container);
                }
                else
                {
                    UpdateMetricLink(metric, container);
                }
            }
            return container;
        }

        private void ToggleMetricDetails(MetricResult metric, VisualElement container)
        {
            var details = container.Q<VisualElement>("metric-details");
            if (details == null) return;
            var expand = !_expandedMetrics.Contains(metric.Category);
            if (expand) _expandedMetrics.Add(metric.Category);
            else _expandedMetrics.Remove(metric.Category);
            if (expand && details.childCount == 0) PopulateMetricDetails(details, metric);
            details.style.display = expand ? DisplayStyle.Flex : DisplayStyle.None;
            UpdateMetricLink(metric, container);
        }

        private void UpdateMetricLink(MetricResult metric, VisualElement container)
        {
            var link = container.Q<Label>("metric-link");
            if (link == null) return;
            link.text = _expandedMetrics.Contains(metric.Category)
                ? "閉じる ▾"
                : metric.Contributors.Count.ToString("N0") + "件 ▸";
        }

        private void PopulateMetricDetails(VisualElement details, MetricResult metric)
        {
            details.style.marginLeft = 14;
            details.style.marginRight = 6;
            details.style.marginBottom = 4;
            details.style.paddingLeft = 6;
            details.style.borderLeftWidth = 2;
            details.style.borderLeftColor = AbiTheme.Border;
            var contributors = metric.Contributors.OrderByDescending(contributor => contributor.Value).ToArray();
            for (var index = 0; index < contributors.Length && index < InlineContributorLimit; index++)
            {
                var contributor = contributors[index];
                var line = new VisualElement();
                line.AddToClassList("abi-row");
                line.AddToClassList(index % 2 == 0 ? "abi-row--even" : "abi-row--odd");
                line.AddToClassList("abi-row--link");
                line.style.minHeight = 24;
                line.style.paddingLeft = 4;
                line.style.paddingRight = 6;
                var thumbnail = CreateThumbnail(contributor.Target, 16);
                thumbnail.style.marginRight = 6;
                line.Add(thumbnail);
                var name = new Label(contributor.Name) { style = { flexGrow = 1, flexShrink = 1 } };
                ApplyEllipsis(name);
                name.tooltip = GetHierarchyPath(contributor.Target) + (string.IsNullOrEmpty(contributor.Detail) ? "" : "\n" + contributor.Detail);
                line.Add(name);
                var value = new Label(FormatContributorValue(contributor)) { style = { width = 120, unityTextAlign = TextAnchor.MiddleRight, opacity = 0.82f } };
                line.Add(value);
                var target = contributor.Target;
                if (target == null) line.SetEnabled(false);
                line.RegisterCallback<ClickEvent>(_ => SelectAndPing(target));
                line.AddManipulator(new ContextualMenuManipulator(evt => PopulateContributorMenu(evt.menu, contributor)));
                details.Add(line);
            }
            if (contributors.Length > InlineContributorLimit)
            {
                var more = new Label("…ほか " + (contributors.Length - InlineContributorLimit).ToString("N0") + " 件");
                more.style.opacity = 0.6f;
                more.style.marginTop = 2;
                details.Add(more);
            }
        }

        private void CopyReportToClipboard()
        {
            if (_report == null) return;
            var lines = new List<string>
            {
                "AvaBuild Inspector",
                "Avatar: " + (_report.AvatarRoot == null ? "Missing" : GetHierarchyPath(_report.AvatarRoot)),
                "Platform: " + _report.Platform,
                "Scanned: " + _report.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                "Overall: " + RatingText(_report.OverallRank)
            };
            var issues = _report.Metrics.Where(IsIssue).Select(metric => metric.DisplayName).ToArray();
            lines.Add("Main Issues: " + (issues.Length == 0 ? "None" : string.Join(" / ", issues)));
            foreach (var metric in _report.Metrics)
                lines.Add(metric.DisplayName + ": " + metric.DisplayValue + " (" + metric.Rank + ", " + metric.Contributors.Count + " items, " + metric.Accuracy + ")" +
                          (metric.Accuracy == AnalysisAccuracy.Unavailable ? " — " + metric.Explanation : ""));
            lines.AddRange(_report.Warnings.Select(warning => "Warning: " + warning));
            CopyToClipboard(this, string.Join("\n", lines), "レポート");
        }
    }
}
