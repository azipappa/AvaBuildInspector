using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase.Validation.Performance;

namespace AvaBuildInspector
{
    internal sealed class AvaBuildInspectorWindow : EditorWindow
    {
        private enum MainTab { ScanResults, Breakdown, BuildHistory }
        private enum BreakdownTab { TextureMemory, SkinnedMeshes, Materials, MaterialSlots, PhysBones }

        private ObjectField _avatarField;
        private EnumField _platformField;
        private Button _rescanButton;
        private VisualElement _content;
        private Button _scanResultsTab;
        private Button _breakdownTab;
        private Button _buildHistoryTab;
        private Label _breakdownCountLabel;
        private MainTab _selectedTab;
        private BreakdownTab _selectedBreakdown;
        private AvatarReport _report;
        private int _scanRequest;
        private readonly TextureCompressionSettingsApplier _textureCompressionSettings = new TextureCompressionSettingsApplier();
        private readonly HashSet<string> _selectedTexturePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private TextureSettingsTarget _textureSettingsTarget;
        private GameObject _currentAvatar;
        private bool _showUnusedPhysBonesOnly;
        private string _contributorSearch = string.Empty;
        private bool _compactRows;
        private bool _sortByValue;
        private bool _groupTexturesByDirectory;
        private bool _showTextureDirectoryPercentages = true;
        private double _totalTextureEstimateMegabytes;
        private bool _groupSkinnedMeshesByDirectory;
        private bool _groupMaterialsByDirectory;
        private bool _isReportStale;
        private string _scannedHierarchySignature;
        private AvatarAnalysisOperation _scanOperation;
        private int _scanOperationRequest;
        private readonly Dictionary<string, bool> _groupExpanded = new Dictionary<string, bool>(StringComparer.Ordinal);
        private readonly Dictionary<BreakdownTab, string> _filterByTab = new Dictionary<BreakdownTab, string>();
        private Vector2 _breakdownScrollOffset;
        private string _selectedBuildHistoryEntryId;

        [MenuItem("Tools/AvaBuild Inspector/Open Inspector")]
        public static void Open()
        {
            var window = GetWindow<AvaBuildInspectorWindow>();
            window.titleContent = new GUIContent("AvaBuild Inspector");
            window.minSize = new Vector2(620, 420);
        }

        public void CreateGUI()
        {
            rootVisualElement.Clear();
            rootVisualElement.style.flexDirection = FlexDirection.Column;
            BuildHeader();
            BuildMainTabs();

            _content = new VisualElement { style = { flexGrow = 1 } };
            rootVisualElement.Add(_content);

            var initialAvatar = FindSelectedAvatar();
            _avatarField.SetValueWithoutNotify(initialAvatar);
            if (initialAvatar != null) RequestScan(initialAvatar);
            else ShowEmptyState("Avatarをドラッグ＆ドロップしてください。");
        }

        private void OnEnable()
        {
            EditorApplication.hierarchyChanged += OnEditorHierarchyChanged;
            Undo.postprocessModifications += OnPostprocessModifications;
            AvatarBuildHistoryStore.Changed += OnBuildHistoryChanged;
        }

        private void OnDisable()
        {
            EditorApplication.hierarchyChanged -= OnEditorHierarchyChanged;
            Undo.postprocessModifications -= OnPostprocessModifications;
            AvatarBuildHistoryStore.Changed -= OnBuildHistoryChanged;
            StopAvatarScan();
        }

        private void OnEditorHierarchyChanged()
        {
            if (_report == null || _report.AvatarRoot == null) return;
            var currentSignature = GetHierarchySignature(_report.AvatarRoot);
            if (string.Equals(currentSignature, _scannedHierarchySignature, StringComparison.Ordinal)) return;
            MarkReportStale();
        }

        private UndoPropertyModification[] OnPostprocessModifications(UndoPropertyModification[] modifications)
        {
            if (modifications == null || modifications.Length == 0 || _report == null || _report.AvatarRoot == null) return modifications;
            var avatarTransform = _report.AvatarRoot.transform;
            var affectsAvatar = modifications.Any(modification =>
            {
                var target = modification.currentValue == null ? null : modification.currentValue.target;
                if (target == null && modification.previousValue != null) target = modification.previousValue.target;
                var gameObject = target as GameObject;
                if (gameObject == null && target is Component component) gameObject = component.gameObject;
                return gameObject != null && (gameObject.transform == avatarTransform || gameObject.transform.IsChildOf(avatarTransform));
            });
            if (affectsAvatar && rootVisualElement != null) rootVisualElement.schedule.Execute(MarkReportStale);
            return modifications;
        }

        private static string GetHierarchySignature(GameObject avatar)
        {
            if (avatar == null) return string.Empty;
            var signature = new StringBuilder();
            foreach (var transform in avatar.GetComponentsInChildren<Transform>(true))
            {
                signature.Append(transform.GetInstanceID()).Append(':')
                    .Append(transform.name).Append(':')
                    .Append(transform.parent == null ? 0 : transform.parent.GetInstanceID()).Append(':')
                    .Append(transform.gameObject.activeSelf ? '1' : '0').Append(':');
                foreach (var component in transform.GetComponents<Component>())
                    signature.Append(component == null ? "Missing" : component.GetType().FullName).Append(',');
                signature.Append(';');
            }
            return signature.ToString();
        }

        private void MarkReportStale()
        {
            if (_report == null || _report.AvatarRoot == null || _isReportStale) return;
            _isReportStale = true;
            RebuildSelectedTab();
        }

        private void BuildHeader()
        {
            var header = new VisualElement();
            header.style.paddingLeft = 12;
            header.style.paddingRight = 12;
            header.style.paddingTop = 10;
            header.style.paddingBottom = 10;
            rootVisualElement.Add(header);

            var title = new Label("AvaBuild Inspector");
            title.style.fontSize = 18;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            header.Add(title);

            var inputRow = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 8 } };
            header.Add(inputRow);

            _avatarField = new ObjectField("Avatar") { objectType = typeof(GameObject), allowSceneObjects = true };
            _avatarField.style.flexGrow = 1;
            _avatarField.RegisterValueChangedCallback(evt => OnAvatarChanged(evt.newValue as GameObject));
            inputRow.Add(_avatarField);

            _rescanButton = new Button(() =>
            {
                if (_avatarField.value is GameObject avatar) RequestScan(avatar);
            }) { text = "再スキャン" };
            _rescanButton.style.width = 88;
            _rescanButton.style.height = 21;
            _rescanButton.style.marginLeft = 4;
            _rescanButton.tooltip = "現在のAvatarをもう一度Quick Scanします。";
            inputRow.Add(_rescanButton);

            _platformField = new EnumField("Platform", TargetPlatform.PC);
            _platformField.style.width = 180;
            _platformField.style.marginLeft = 8;
            _platformField.RegisterValueChangedCallback(evt =>
            {
                if (_avatarField.value is GameObject avatar) RequestScan(avatar);
            });
            inputRow.Add(_platformField);
        }

        private void BuildMainTabs()
        {
            var tabs = new VisualElement { style = { flexDirection = FlexDirection.Row, paddingLeft = 12, paddingRight = 12 } };
            tabs.style.borderBottomWidth = 1;
            tabs.style.borderBottomColor = new Color(0.28f, 0.28f, 0.28f);
            rootVisualElement.Add(tabs);

            _scanResultsTab = new Button(() => SelectMainTab(MainTab.ScanResults)) { text = "スキャン結果表示" };
            _scanResultsTab.style.flexGrow = 1;
            _scanResultsTab.style.height = 32;
            tabs.Add(_scanResultsTab);

            _breakdownTab = new Button(() => SelectMainTab(MainTab.Breakdown)) { text = "内訳表示" };
            _breakdownTab.style.flexGrow = 1;
            _breakdownTab.style.height = 32;
            tabs.Add(_breakdownTab);

            _buildHistoryTab = new Button(() => SelectMainTab(MainTab.BuildHistory)) { text = "ビルド履歴" };
            _buildHistoryTab.style.flexGrow = 1;
            _buildHistoryTab.style.height = 32;
            tabs.Add(_buildHistoryTab);
            SelectMainTab(MainTab.ScanResults);
        }

        private void OnAvatarChanged(GameObject candidate)
        {
            var avatar = FindAvatarRoot(candidate);
            if (avatar != candidate) _avatarField.SetValueWithoutNotify(avatar);
            if (avatar != _currentAvatar)
            {
                _selectedTexturePaths.Clear();
                _textureCompressionSettings.ClearSettings();
                _currentAvatar = avatar;
            }
            RequestScan(avatar);
        }

        private void RequestScan(GameObject avatar)
        {
            StopAvatarScan();
            var request = ++_scanRequest;
            _currentAvatar = avatar;
            _report = null;
            _isReportStale = false;
            if (_rescanButton != null) _rescanButton.SetEnabled(false);
            ShowEmptyState(avatar == null ? "VRCAvatarDescriptorのあるAvatarを指定してください。" : "Quick Scan中...");
            if (avatar == null)
            {
                if (_rescanButton != null) _rescanButton.SetEnabled(false);
                return;
            }

            try
            {
                _scanOperation = AvatarQuickAnalyzer.BeginAnalyze(avatar, (TargetPlatform)_platformField.value);
                _scanOperationRequest = request;
                EditorApplication.update += AdvanceAvatarScan;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                if (_rescanButton != null) _rescanButton.SetEnabled(true);
                ShowEmptyState("Quick Scanを開始できませんでした。Consoleを確認してください。");
            }
        }

        private void AdvanceAvatarScan()
        {
            var operation = _scanOperation;
            if (operation == null) return;
            if (_scanOperationRequest != _scanRequest || _currentAvatar == null)
            {
                StopAvatarScan();
                return;
            }

            var progressText = (operation.CompletedStageCount + 1).ToString("N0") + "/" + operation.StageCount.ToString("N0") + " · " + operation.CurrentStage;
            if (EditorUtility.DisplayCancelableProgressBar("AvaBuild Inspector - Quick Scan", progressText, operation.Progress))
            {
                StopAvatarScan();
                ShowEmptyState("Quick Scanをキャンセルしました。再スキャンできます。");
                return;
            }

            try
            {
                var hasMoreStages = operation.Advance();
                if (hasMoreStages) return;

                EditorApplication.update -= AdvanceAvatarScan;
                _scanOperation = null;
                EditorUtility.ClearProgressBar();
                _report = operation.Report;
                _isReportStale = false;
                _scannedHierarchySignature = GetHierarchySignature(_currentAvatar);
                if (_rescanButton != null) _rescanButton.SetEnabled(true);
                RebuildSelectedTab();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                StopAvatarScan();
                ShowEmptyState("Quick Scanに失敗しました。Consoleを確認してください。");
            }
        }

        private void StopAvatarScan()
        {
            if (_scanOperation == null) return;
            EditorApplication.update -= AdvanceAvatarScan;
            _scanOperation = null;
            EditorUtility.ClearProgressBar();
            if (_rescanButton != null) _rescanButton.SetEnabled(true);
        }

        private void SelectMainTab(MainTab tab)
        {
            _selectedTab = tab;
            if (_scanResultsTab != null)
                _scanResultsTab.style.backgroundColor = tab == MainTab.ScanResults ? new Color(0.24f, 0.46f, 0.68f) : new Color(0.22f, 0.22f, 0.22f);
            if (_breakdownTab != null)
                _breakdownTab.style.backgroundColor = tab == MainTab.Breakdown ? new Color(0.24f, 0.46f, 0.68f) : new Color(0.22f, 0.22f, 0.22f);
            if (_buildHistoryTab != null)
                _buildHistoryTab.style.backgroundColor = tab == MainTab.BuildHistory ? new Color(0.24f, 0.46f, 0.68f) : new Color(0.22f, 0.22f, 0.22f);
            RebuildSelectedTab();
        }

        private void RebuildSelectedTab()
        {
            if (_content == null) return;
            _content.Query<Foldout>().ForEach(foldout =>
            {
                if (foldout.userData is string stateKey)
                    _groupExpanded[stateKey] = foldout.value;
            });
            var existingScroll = _content.Q<ScrollView>();
            if (existingScroll != null) _breakdownScrollOffset = existingScroll.scrollOffset;
            _content.Clear();
            if (_selectedTab == MainTab.BuildHistory)
            {
                BuildHistoryTab();
                return;
            }
            if (_report == null)
            {
                ShowEmptyState("Avatarをドラッグ＆ドロップしてください。");
                return;
            }
            if (_isReportStale)
                _content.Add(new HelpBox("Hierarchyが変更されました。表示中の結果は古い可能性があります。再スキャンしてください。", HelpBoxMessageType.Warning));
            if (_selectedTab == MainTab.ScanResults) BuildScanResults();
            else BuildBreakdown();
        }

        private void OnBuildHistoryChanged()
        {
            if (_selectedTab == MainTab.BuildHistory && rootVisualElement != null)
                rootVisualElement.schedule.Execute(RebuildSelectedTab);
        }

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
                    marginBottom = 5,
                    paddingLeft = 14,
                    paddingRight = 14,
                    paddingTop = 12,
                    paddingBottom = 12,
                    backgroundColor = new Color(0.13f, 0.17f, 0.22f)
                }
            };
            var heading = new Label("ビルドレポート")
            {
                style = { fontSize = 18, unityFontStyleAndWeight = FontStyle.Bold }
            };
            header.Add(heading);
            var hint = new Label("VRChat SDKで実行したAvatarビルドの記録です。Textureメモリは合計値のみを表示します。");
            hint.style.fontSize = 11;
            hint.style.opacity = 0.72f;
            hint.style.marginTop = 3;
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
                        backgroundColor = new Color(0.17f, 0.17f, 0.17f)
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

            var selected = entries.FirstOrDefault(entry => entry.Id == _selectedBuildHistoryEntryId) ?? entries[0];
            _selectedBuildHistoryEntryId = selected.Id;

            var listHeading = new Label("ビルド履歴");
            listHeading.style.unityFontStyleAndWeight = FontStyle.Bold;
            listHeading.style.fontSize = 14;
            listHeading.style.marginTop = 0;
            listHeading.style.marginBottom = 3;
            scroll.Add(listHeading);
            var historyList = new ScrollView(ScrollViewMode.Vertical);
            historyList.style.height = Mathf.Min(230f, Mathf.Max(54f, entries.Count * 58f));
            historyList.style.flexShrink = 0;
            historyList.style.paddingRight = 4;
            scroll.Add(historyList);
            foreach (var entry in entries)
                historyList.Add(CreateBuildHistoryCard(entry, entry.Id == selected.Id));

            var detailCard = new VisualElement
            {
                style =
                {
                    marginTop = 5,
                    marginBottom = 12,
                    paddingLeft = 12,
                    paddingRight = 12,
                    paddingTop = 10,
                    paddingBottom = 12,
                    backgroundColor = new Color(0.16f, 0.16f, 0.16f)
                }
            };
            var detailTitle = new Label("選択中のビルド結果");
            detailTitle.style.fontSize = 14;
            detailTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
            detailCard.Add(detailTitle);

            var detailHeader = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginTop = 8 } };
            var status = new Label(selected.Succeeded ? "成功" : "失敗");
            status.style.paddingLeft = 9;
            status.style.paddingRight = 9;
            status.style.paddingTop = 3;
            status.style.paddingBottom = 3;
            status.style.unityFontStyleAndWeight = FontStyle.Bold;
            status.style.color = Color.white;
            status.style.backgroundColor = selected.Succeeded ? new Color(0.18f, 0.48f, 0.30f) : new Color(0.62f, 0.25f, 0.25f);
            detailHeader.Add(status);
            var avatarName = new Label(selected.AvatarName);
            avatarName.style.fontSize = 16;
            avatarName.style.unityFontStyleAndWeight = FontStyle.Bold;
            avatarName.style.flexGrow = 1;
            avatarName.style.marginLeft = 10;
            detailHeader.Add(avatarName);
            var rank = new Label("Overall  " + selected.OverallRank);
            rank.style.paddingLeft = 8;
            rank.style.paddingRight = 8;
            rank.style.paddingTop = 4;
            rank.style.paddingBottom = 4;
            rank.style.backgroundColor = new Color(0.24f, 0.24f, 0.24f);
            rank.style.color = GetBuildHistoryRankColor(selected.OverallRank);
            detailHeader.Add(rank);
            detailCard.Add(detailHeader);

            var metadata = new Label(selected.Platform + "  ·  " + selected.FinishedAt);
            metadata.style.opacity = 0.68f;
            metadata.style.marginTop = 5;
            detailCard.Add(metadata);

            var downloadSize = selected.Metrics.FirstOrDefault(metric => metric.DisplayName == "Download Size");
            var metricCount = selected.Metrics.Count;
            var detailStats = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap, marginTop = 10 } };
            detailStats.Add(CreateBuildHistoryStatCard("Bundle Size", downloadSize == null ? "未取得" : downloadSize.DisplayValue,
                downloadSize == null ? "" : downloadSize.Accuracy, new Color(0.22f, 0.35f, 0.48f)));
            detailStats.Add(CreateBuildHistoryStatCard("性能項目", metricCount.ToString("N0"), "Metric", new Color(0.32f, 0.30f, 0.20f)));
            detailCard.Add(detailStats);

            if (!selected.Succeeded && !string.IsNullOrEmpty(selected.Error))
                detailCard.Add(new HelpBox(selected.Error, HelpBoxMessageType.Error));
            if (selected.Warnings != null)
                foreach (var warning in selected.Warnings)
                    detailCard.Add(new HelpBox(warning, HelpBoxMessageType.Warning));

            var metricsHeading = new Label("性能レポート");
            metricsHeading.style.unityFontStyleAndWeight = FontStyle.Bold;
            metricsHeading.style.marginTop = 12;
            detailCard.Add(metricsHeading);
            var metricsHeader = new VisualElement { style = { flexDirection = FlexDirection.Row, minHeight = 23, alignItems = Align.Center } };
            var nameHeader = new Label("項目") { style = { flexGrow = 1 } };
            nameHeader.style.opacity = 0.58f;
            metricsHeader.Add(nameHeader);
            var valueHeader = new Label("値") { style = { width = 120, unityTextAlign = TextAnchor.MiddleRight } };
            valueHeader.style.opacity = 0.58f;
            metricsHeader.Add(valueHeader);
            var rankHeader = new Label("Rank / 精度") { style = { width = 150, unityTextAlign = TextAnchor.MiddleRight } };
            rankHeader.style.opacity = 0.58f;
            metricsHeader.Add(rankHeader);
            detailCard.Add(metricsHeader);

            if (selected.Metrics != null)
            {
                foreach (var section in selected.Metrics.GroupBy(metric => string.IsNullOrEmpty(metric.Section) ? "その他" : metric.Section))
                {
                    var foldout = new Foldout { text = section.Key + "  ·  " + section.Count().ToString("N0"), value = true };
                    foldout.style.marginTop = 2;
                    var rowIndex = 0;
                    foreach (var metric in section)
                    {
                        var line = new VisualElement
                        {
                            style =
                            {
                                flexDirection = FlexDirection.Row,
                                alignItems = Align.Center,
                                minHeight = 25,
                                paddingLeft = 6,
                                paddingRight = 4,
                                backgroundColor = rowIndex++ % 2 == 0 ? new Color(0.19f, 0.19f, 0.19f) : new Color(0.15f, 0.15f, 0.15f)
                            }
                        };
                        var name = new Label(metric.DisplayName) { style = { flexGrow = 1 } };
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
                    detailCard.Add(foldout);
                }
            }
            scroll.Add(detailCard);
        }

        private VisualElement CreateBuildHistoryStatCard(string title, string value, string caption, Color accent)
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
                    backgroundColor = new Color(0.17f, 0.17f, 0.17f),
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
                captionLabel.style.opacity = 0.58f;
                card.Add(captionLabel);
            }
            return card;
        }

        private VisualElement CreateBuildHistoryCard(AvatarBuildHistoryEntry entry, bool isSelected)
        {
            var card = new Button(() =>
            {
                _selectedBuildHistoryEntryId = entry.Id;
                RebuildSelectedTab();
            });
            card.style.flexDirection = FlexDirection.Column;
            card.style.alignItems = Align.Stretch;
            card.style.marginBottom = 4;
            card.style.paddingLeft = 10;
            card.style.paddingRight = 10;
            card.style.paddingTop = 7;
            card.style.paddingBottom = 7;
            card.style.backgroundColor = isSelected ? new Color(0.22f, 0.36f, 0.49f) : new Color(0.17f, 0.17f, 0.17f);
            card.style.borderLeftWidth = 3;
            card.style.borderLeftColor = entry.Succeeded ? new Color(0.25f, 0.65f, 0.4f) : new Color(0.78f, 0.34f, 0.32f);

            var top = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };
            var avatar = new Label(string.IsNullOrEmpty(entry.AvatarName) ? "Unknown Avatar" : entry.AvatarName);
            avatar.style.flexGrow = 1;
            avatar.style.unityFontStyleAndWeight = FontStyle.Bold;
            avatar.style.whiteSpace = WhiteSpace.NoWrap;
            avatar.style.overflow = Overflow.Hidden;
            avatar.style.textOverflow = TextOverflow.Ellipsis;
            top.Add(avatar);
            var badge = new Label(entry.Succeeded ? "成功" : "失敗");
            badge.style.color = entry.Succeeded ? new Color(0.48f, 0.88f, 0.64f) : new Color(1f, 0.58f, 0.54f);
            badge.style.unityFontStyleAndWeight = FontStyle.Bold;
            badge.style.marginLeft = 8;
            top.Add(badge);
            card.Add(top);

            var footer = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginTop = 3 } };
            var date = new Label(entry.FinishedAt + "  ·  " + entry.Platform);
            date.style.flexGrow = 1;
            date.style.fontSize = 10;
            date.style.opacity = 0.68f;
            footer.Add(date);
            var downloadSize = entry.Metrics == null ? null : entry.Metrics.FirstOrDefault(metric => metric.DisplayName == "Download Size");
            var size = new Label(downloadSize == null ? "Bundle: —" : "Bundle: " + downloadSize.DisplayValue);
            size.style.fontSize = 10;
            size.style.marginRight = 8;
            footer.Add(size);
            var rank = new Label(entry.OverallRank ?? "Unavailable");
            rank.style.fontSize = 10;
            rank.style.color = GetBuildHistoryRankColor(entry.OverallRank);
            footer.Add(rank);
            card.Add(footer);
            card.tooltip = entry.Error;
            return card;
        }

        private static Color GetBuildHistoryRankColor(string rank)
        {
            return Enum.TryParse(rank, out PerformanceRating parsedRank) ? RatingColor(parsedRank) : new Color(0.72f, 0.72f, 0.72f);
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

            var issues = _report.Metrics.Where(IsIssue).Select(metric => metric.DisplayName).Take(5).ToArray();
            if (issues.Length > 0)
            {
                var issueLabel = new Label("Main Issues: " + string.Join(" / ", issues));
                issueLabel.style.marginTop = 4;
                summary.Add(issueLabel);
            }
            foreach (var warning in _report.Warnings)
                summary.Add(new HelpBox(warning, HelpBoxMessageType.Warning));
            var context = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginTop = 5 } };
            var contextLabel = new Label("対象: " + (_report.AvatarRoot == null ? "Missing" : _report.AvatarRoot.name) + " · " + _report.Platform + " · " + _report.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"));
            contextLabel.style.flexGrow = 1;
            contextLabel.style.fontSize = 10;
            contextLabel.style.opacity = 0.65f;
            context.Add(contextLabel);
            var copy = new Button(CopyReportToClipboard) { text = "レポートをコピー" };
            context.Add(copy);
            summary.Add(context);

            var scroll = new ScrollView(ScrollViewMode.Vertical) { style = { flexGrow = 1 } };
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
                scroll.Add(CreateMetricRow(metric, rowIndex++));
            }
        }

        private VisualElement CreateMetricRow(MetricResult metric, int index)
        {
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };
            row.style.height = 27;
            row.style.paddingLeft = 14;
            row.style.paddingRight = 14;
            row.style.backgroundColor = index % 2 == 0 ? new Color(0.18f, 0.18f, 0.18f) : new Color(0.22f, 0.22f, 0.22f);
            var metricName = new Label(metric.DisplayName) { style = { flexGrow = 1 } };
            metricName.tooltip = metric.Explanation ?? "";
            row.Add(metricName);

            var accuracy = new Label(metric.Accuracy.ToString());
            accuracy.style.width = 85;
            accuracy.style.opacity = 0.6f;
            row.Add(accuracy);

            var value = new Label(metric.DisplayValue);
            value.style.width = 120;
            value.style.unityTextAlign = TextAnchor.MiddleRight;
            row.Add(value);

            var rank = new Label(RatingIcon(metric.Rank) + " " + RatingText(metric.Rank));
            rank.style.width = 105;
            rank.style.unityTextAlign = TextAnchor.MiddleRight;
            rank.style.color = RatingColor(metric.Rank);
            row.Add(rank);
            return row;
        }

        private void BuildBreakdown()
        {
            var subTabs = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    paddingLeft = 12,
                    paddingRight = 12,
                    paddingTop = 10,
                    paddingBottom = 8
                }
            };
            _content.Add(subTabs);
            AddBreakdownTab(subTabs, "テクスチャメモリ", BreakdownTab.TextureMemory);
            AddBreakdownTab(subTabs, "スキンドメッシュ", BreakdownTab.SkinnedMeshes);
            AddBreakdownTab(subTabs, "マテリアル", BreakdownTab.Materials);
            AddBreakdownTab(subTabs, "PBコンポーネント", BreakdownTab.PhysBones);
            if (_selectedBreakdown == BreakdownTab.Materials || _selectedBreakdown == BreakdownTab.MaterialSlots)
            {
                var materialSubTabs = new VisualElement { style = { flexDirection = FlexDirection.Row, marginLeft = 12, marginRight = 12, marginBottom = 6 } };
                AddBreakdownTab(materialSubTabs, "マテリアル", BreakdownTab.Materials);
                AddBreakdownTab(materialSubTabs, "マテリアルスロット", BreakdownTab.MaterialSlots);
                _content.Add(materialSubTabs);
            }

            var listTools = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap, alignItems = Align.Center, marginLeft = 12, marginRight = 12, marginBottom = 6 } };
            var search = new TextField { value = _contributorSearch, isDelayed = true };
            search.style.flexGrow = 1;
            search.style.minWidth = 140;
            search.tooltip = "名前、詳細、Hierarchyパスを検索します。";
            search.RegisterValueChangedCallback(evt => { _contributorSearch = evt.newValue; RebuildSelectedTab(); });
            listTools.Add(search);
            var filterChoices = GetContributorFilterChoices();
            var activeFilter = _filterByTab.TryGetValue(_selectedBreakdown, out var savedFilter) ? savedFilter : "すべて";
            if (!filterChoices.Contains(activeFilter)) activeFilter = "すべて";
            var filterField = new PopupField<string>(filterChoices, filterChoices.IndexOf(activeFilter));
            filterField.style.width = 170;
            filterField.tooltip = "タブ別の条件で一覧を絞り込みます。";
            filterField.RegisterValueChangedCallback(evt => { _filterByTab[_selectedBreakdown] = evt.newValue; RebuildSelectedTab(); });
            listTools.Add(filterField);
            var sort = new PopupField<string>(new List<string> { "既定順", "値の大きい順" }, _sortByValue ? 1 : 0);
            sort.style.width = 120;
            sort.RegisterValueChangedCallback(evt => { _sortByValue = evt.newValue == "値の大きい順"; RebuildSelectedTab(); });
            listTools.Add(sort);
            var compact = new Toggle("コンパクト") { value = _compactRows };
            compact.style.marginLeft = 6;
            compact.RegisterValueChangedCallback(evt => { _compactRows = evt.newValue; RebuildSelectedTab(); });
            listTools.Add(compact);
            if (_selectedBreakdown == BreakdownTab.TextureMemory ||
                _selectedBreakdown == BreakdownTab.SkinnedMeshes ||
                _selectedBreakdown == BreakdownTab.Materials)
            {
                var groupByDirectory = new Toggle("親フォルダーでグループ化")
                {
                    value = _selectedBreakdown == BreakdownTab.TextureMemory
                        ? _groupTexturesByDirectory
                        : _selectedBreakdown == BreakdownTab.SkinnedMeshes
                            ? _groupSkinnedMeshesByDirectory
                        : _groupMaterialsByDirectory
                };
                groupByDirectory.tooltip = "Assetの場所を3階層（Assets直下 / 親フォルダー / 一覧）でまとめて表示します。";
                groupByDirectory.RegisterValueChangedCallback(evt =>
                {
                    if (_selectedBreakdown == BreakdownTab.TextureMemory) _groupTexturesByDirectory = evt.newValue;
                    else if (_selectedBreakdown == BreakdownTab.SkinnedMeshes) _groupSkinnedMeshesByDirectory = evt.newValue;
                    else _groupMaterialsByDirectory = evt.newValue;
                    RebuildSelectedTab();
                });
                listTools.Add(groupByDirectory);
                if (_selectedBreakdown == BreakdownTab.TextureMemory)
                {
                    var showPercentages = new Toggle("容量の割合を表示") { value = _showTextureDirectoryPercentages };
                    showPercentages.tooltip = "ディレクトリまたはTextureの推定容量が全Texture容量に占める割合を表示します。";
                    showPercentages.RegisterValueChangedCallback(evt =>
                    {
                        _showTextureDirectoryPercentages = evt.newValue;
                        RebuildSelectedTab();
                    });
                    listTools.Add(showPercentages);
                }
            }
            _breakdownCountLabel = new Label();
            _breakdownCountLabel.style.marginLeft = 6;
            _breakdownCountLabel.style.opacity = 0.65f;
            listTools.Add(_breakdownCountLabel);
            _content.Add(listTools);

            var metric = GetBreakdownMetric();
            var summaryMetric = GetBreakdownSummaryMetric();
            if (metric == null || summaryMetric == null)
            {
                _content.Add(new HelpBox("この内訳は取得できません。", HelpBoxMessageType.Warning));
                return;
            }

            if (_selectedBreakdown == BreakdownTab.TextureMemory)
                _totalTextureEstimateMegabytes = metric.Contributors.Sum(contributor => Math.Max(0d, contributor.Value));

            BuildBreakdownSummary(metric, summaryMetric);
            if (_selectedBreakdown == BreakdownTab.PhysBones) BuildPhysBoneUsagePanel(metric);
            BuildBreakdownColumnHeader();

            var scroll = new ScrollView(ScrollViewMode.Vertical) { style = { flexGrow = 1 } };
            scroll.style.marginLeft = 12;
            scroll.style.marginRight = 12;
            scroll.style.marginBottom = 10;
            _content.Add(scroll);
            var restoreOffset = _breakdownScrollOffset;
            scroll.schedule.Execute(() => scroll.scrollOffset = restoreOffset);
            var allContributors = (_selectedBreakdown == BreakdownTab.Materials ? _report.Materials : metric.Contributors).AsEnumerable();
            if (_selectedBreakdown == BreakdownTab.PhysBones && _showUnusedPhysBonesOnly)
                allContributors = allContributors.Where(contributor => contributor.IsUnusedPhysBone);
            if (!string.IsNullOrWhiteSpace(_contributorSearch))
                allContributors = allContributors.Where(contributor => ((contributor.Name ?? "") + " " + (contributor.Group ?? "") + " " + (contributor.Detail ?? "") + " " + GetHierarchyPath(contributor.Target)).IndexOf(_contributorSearch, StringComparison.OrdinalIgnoreCase) >= 0);
            var activeRowFilter = _filterByTab.TryGetValue(_selectedBreakdown, out var rowFilter) ? rowFilter : "すべて";
            switch (activeRowFilter)
            {
                case "1 MB以上": allContributors = allContributors.Where(contributor => contributor.Value >= 1d); break;
                case "4 MB以上": allContributors = allContributors.Where(contributor => contributor.Value >= 4d); break;
                case "Blend Shapeあり": allContributors = allContributors.Where(contributor => contributor.SecondaryTarget is Mesh mesh && mesh.blendShapeCount > 0); break;
                case "10,000 triangles以上": allContributors = allContributors.Where(contributor => contributor.Value >= 10000d); break;
                case "50,000 triangles以上": allContributors = allContributors.Where(contributor => contributor.Value >= 50000d); break;
                case "5 slots以上": allContributors = allContributors.Where(contributor => contributor.Value >= 5d); break;
                case "10 slots以上": allContributors = allContributors.Where(contributor => contributor.Value >= 10d); break;
                case "Missing Slotあり": allContributors = allContributors.Where(contributor => contributor.Detail != null && contributor.Detail.IndexOf("Missing", StringComparison.OrdinalIgnoreCase) >= 0); break;
            }
            if (_sortByValue) allContributors = allContributors.OrderByDescending(contributor => contributor.Value);
            var visibleContributors = allContributors.ToArray();
            if (_breakdownCountLabel != null)
                _breakdownCountLabel.text = visibleContributors.Length.ToString("N0") + " / " +
                    (_selectedBreakdown == BreakdownTab.Materials ? _report.Materials.Count : metric.Contributors.Count).ToString("N0") + " 件";
            if (_selectedBreakdown == BreakdownTab.TextureMemory)
            {
                if (_groupTexturesByDirectory)
                    BuildAssetDirectoryGroups(scroll, visibleContributors, CreateTextureRow, metric.Contributors.ToArray());
                else
                {
                    for (var index = 0; index < visibleContributors.Length; index++)
                        scroll.Add(CreateTextureRow(visibleContributors[index], index));
                }
            }
            else
            {
                var useAssetDirectoryGroups =
                    (_selectedBreakdown == BreakdownTab.SkinnedMeshes && _groupSkinnedMeshesByDirectory) ||
                    (_selectedBreakdown == BreakdownTab.Materials && _groupMaterialsByDirectory);
                if (useAssetDirectoryGroups)
                {
                    BuildAssetDirectoryGroups(scroll, visibleContributors, CreateDetailRow);
                }
                else foreach (var group in visibleContributors.GroupBy(contributor => string.IsNullOrWhiteSpace(contributor.Group) ? "Other" : contributor.Group))
                {
                    var unusedCount = group.Count(contributor => contributor.IsUnusedPhysBone);
                    var missingCount = group.Count(contributor => contributor.Detail != null && contributor.Detail.IndexOf("Missing", StringComparison.OrdinalIgnoreCase) >= 0);
                    var groupTitle = group.Key + " · " + group.Count().ToString("N0") + " 件" +
                                     (missingCount > 0 ? " · Missing " + missingCount.ToString("N0") : "") +
                                     (unusedCount > 0 ? " · 未使用候補 " + unusedCount.ToString("N0") : "");
                    var stateKey = _selectedBreakdown + "|" + group.Key;
                    var foldout = new Foldout { text = groupTitle, value = _groupExpanded.TryGetValue(stateKey, out var expanded) && expanded };
                    foldout.RegisterValueChangedCallback(evt => _groupExpanded[stateKey] = evt.newValue);
                    foldout.style.marginLeft = 4;
                    foldout.style.marginRight = 4;
                    scroll.Add(foldout);
                    var rowIndex = 0;
                    foreach (var contributor in group)
                        foldout.Add(CreateDetailRow(contributor, rowIndex++));
                }
            }
            if (_selectedBreakdown == BreakdownTab.TextureMemory)
                BuildTextureCompressionEditor(metric, _content, visibleContributors);
            if (visibleContributors.Length == 0)
            {
                var hasFilters = _showUnusedPhysBonesOnly || activeRowFilter != "すべて" || !string.IsNullOrWhiteSpace(_contributorSearch);
                var emptyMessage = hasFilters ? "条件に一致する項目はありません。検索・フィルターを解除してください。" : "表示できる項目はありません。";
                var empty = new Label(emptyMessage)
                {
                    style =
                    {
                        height = 80,
                        unityTextAlign = TextAnchor.MiddleCenter,
                        opacity = 0.65f
                    }
                };
                scroll.Add(empty);
                if (hasFilters)
                {
                    var clearFilters = new Button(() =>
                    {
                        _contributorSearch = string.Empty;
                        _filterByTab[_selectedBreakdown] = "すべて";
                        _showUnusedPhysBonesOnly = false;
                        RebuildSelectedTab();
                    }) { text = "検索・フィルターを解除" };
                    clearFilters.style.alignSelf = Align.Center;
                    scroll.Add(clearFilters);
                }
            }
        }

        private void AddBreakdownTab(VisualElement parent, string text, BreakdownTab tab)
        {
            var button = new Button(() => { _selectedBreakdown = tab; RebuildSelectedTab(); }) { text = text };
            button.style.flexGrow = 1;
            button.style.height = 32;
            button.style.marginLeft = 1;
            button.style.marginRight = 1;
            var selected = _selectedBreakdown == tab || (tab == BreakdownTab.Materials && _selectedBreakdown == BreakdownTab.MaterialSlots);
            button.style.unityFontStyleAndWeight = selected ? FontStyle.Bold : FontStyle.Normal;
            button.style.backgroundColor = selected ? new Color(0.25f, 0.47f, 0.68f) : new Color(0.20f, 0.20f, 0.20f);
            parent.Add(button);
        }

        private List<string> GetContributorFilterChoices()
        {
            switch (_selectedBreakdown)
            {
                case BreakdownTab.TextureMemory: return new List<string> { "すべて", "1 MB以上", "4 MB以上" };
                case BreakdownTab.SkinnedMeshes: return new List<string> { "すべて", "10,000 triangles以上", "50,000 triangles以上", "Blend Shapeあり" };
                case BreakdownTab.MaterialSlots: return new List<string> { "すべて", "5 slots以上", "10 slots以上", "Missing Slotあり" };
                case BreakdownTab.Materials: return new List<string> { "すべて" };
                default: return new List<string> { "すべて" };
            }
        }

        private void BuildBreakdownSummary(MetricResult metric, MetricResult summaryMetric)
        {
            var summary = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    alignItems = Align.Center,
                    minHeight = 54,
                    marginLeft = 12,
                    marginRight = 12,
                    paddingLeft = 10,
                    paddingRight = 10,
                    paddingTop = 7,
                    paddingBottom = 7,
                    backgroundColor = new Color(0.16f, 0.16f, 0.16f)
                }
            };

            var labels = new VisualElement { style = { flexGrow = 1 } };
            var title = new Label(GetBreakdownTitle());
            title.style.fontSize = 15;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            labels.Add(title);

            var hint = new Label(GetBreakdownHint());
            hint.style.fontSize = 11;
            hint.style.opacity = 0.58f;
            hint.style.marginTop = 2;
            labels.Add(hint);
            summary.Add(labels);

            var totals = new VisualElement { style = { alignItems = Align.FlexEnd } };
            var total = new Label(summaryMetric.DisplayValue);
            total.style.fontSize = 17;
            total.style.unityFontStyleAndWeight = FontStyle.Bold;
            totals.Add(total);

            var itemCount = _selectedBreakdown == BreakdownTab.Materials ? _report.Materials.Count : metric.Contributors.Count;
            var count = new Label(itemCount.ToString("N0") + (_selectedBreakdown == BreakdownTab.Materials ? " Material" : " 項目"));
            count.style.fontSize = 11;
            count.style.opacity = 0.58f;
            totals.Add(count);
            summary.Add(totals);
            _content.Add(summary);
        }

        private void BuildBreakdownColumnHeader()
        {
            var header = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    alignItems = Align.Center,
                    height = 24,
                    marginLeft = 12,
                    marginRight = 12,
                    marginTop = 7,
                    paddingLeft = 7,
                    paddingRight = 8,
                    backgroundColor = new Color(0.12f, 0.12f, 0.12f)
                }
            };

            if (_selectedBreakdown == BreakdownTab.TextureMemory)
            {
                var selectionSpace = new VisualElement { style = { width = 22, minWidth = 22 } };
                header.Add(selectionSpace);
            }

            var name = new Label(_selectedBreakdown == BreakdownTab.TextureMemory ? "テクスチャ" : "オブジェクト");
            name.style.flexGrow = 1;
            name.style.fontSize = 10;
            name.style.opacity = 0.58f;
            header.Add(name);

            var value = new Label(GetValueColumnName());
            value.style.width = 112;
            value.style.fontSize = 10;
            value.style.opacity = 0.58f;
            value.style.unityTextAlign = TextAnchor.MiddleRight;
            header.Add(value);
            _content.Add(header);
        }

        private void BuildPhysBoneUsagePanel(MetricResult metric)
        {
            var unusedCount = metric.Contributors.Count(contributor => contributor.IsUnusedPhysBone);
            var panel = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    alignItems = Align.Center,
                    minHeight = 42,
                    marginLeft = 12,
                    marginRight = 12,
                    marginTop = 8,
                    paddingLeft = 9,
                    paddingRight = 9,
                    backgroundColor = unusedCount > 0
                        ? new Color(0.34f, 0.23f, 0.10f)
                        : new Color(0.15f, 0.25f, 0.17f)
                }
            };

            var labels = new VisualElement { style = { flexGrow = 1, flexShrink = 1 } };
            var title = new Label(unusedCount > 0
                ? "未使用候補  " + unusedCount.ToString("N0") + " 件"
                : "未使用候補は見つかりませんでした");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            labels.Add(title);
            var detail = new Label("PhysBone対象TransformがSkinnedMeshRenderer.bonesに1本も含まれない設定です。");
            detail.style.fontSize = 10;
            detail.style.opacity = 0.68f;
            detail.style.whiteSpace = WhiteSpace.NoWrap;
            detail.style.overflow = Overflow.Hidden;
            detail.style.textOverflow = TextOverflow.Ellipsis;
            labels.Add(detail);
            panel.Add(labels);

            var filter = new Toggle("候補のみ表示") { value = _showUnusedPhysBonesOnly };
            filter.style.marginLeft = 10;
            filter.SetEnabled(unusedCount > 0);
            filter.RegisterValueChangedCallback(evt =>
            {
                _showUnusedPhysBonesOnly = evt.newValue;
                RebuildSelectedTab();
            });
            panel.Add(filter);
            _content.Add(panel);
        }

        private void BuildTextureCompressionEditor(MetricResult metric, VisualElement parentElement, Contributor[] visibleContributors)
        {
            var textureContributors = metric.Contributors
                .Where(contributor => contributor.Target is Texture2D)
                .ToArray();
            var visibleTextureContributors = visibleContributors
                .Where(contributor => contributor.Target is Texture2D)
                .ToArray();
            var selectedTextures = textureContributors
                .Select(contributor => contributor.Target as Texture2D)
                .Where(texture => texture != null && _selectedTexturePaths.Contains(AssetDatabase.GetAssetPath(texture)))
                .Distinct()
                .ToArray();

            var panel = new VisualElement
            {
                style =
                {
                    marginLeft = 12,
                    marginRight = 12,
                    marginTop = 8,
                    paddingLeft = 8,
                    paddingRight = 8,
                    paddingTop = 7,
                    paddingBottom = 8,
                    backgroundColor = new Color(0.19f, 0.19f, 0.19f)
                }
            };
            panel.style.flexShrink = 0;
            panel.style.maxHeight = new Length(42, LengthUnit.Percent);
            parentElement.Add(panel);

            var selectionBar = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };
            panel.Add(selectionBar);
            var hiddenSelectedCount = selectedTextures.Count(texture => !visibleTextureContributors.Any(contributor => contributor.Target == texture));
            var selectionText = "圧縮設定  " + selectedTextures.Length.ToString("N0") + " 枚選択中";
            if (hiddenSelectedCount > 0) selectionText += "（検索/フィルターで非表示 " + hiddenSelectedCount.ToString("N0") + " 枚）";
            var selectionLabel = new Label(selectionText);
            selectionLabel.style.flexGrow = 1;
            selectionLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            selectionBar.Add(selectionLabel);

            var platformName = new Label("対象: " + (_textureSettingsTarget == TextureSettingsTarget.Default ? "Default" : _textureSettingsTarget == TextureSettingsTarget.Standalone ? "PC (Standalone)" : "Android"));
            platformName.style.marginRight = 8;
            platformName.style.opacity = 0.72f;
            selectionBar.Add(platformName);

            var selectAll = new Button(() =>
            {
                foreach (var contributor in visibleTextureContributors)
                {
                    var path = AssetDatabase.GetAssetPath(contributor.Target);
                    if (!string.IsNullOrEmpty(path) && AssetImporter.GetAtPath(path) is TextureImporter)
                        _selectedTexturePaths.Add(path);
                }
                RebuildSelectedTab();
            }) { text = "表示中をチェック" };
            selectAll.style.width = 82;
            selectionBar.Add(selectAll);

            var clearSelection = new Button(() =>
            {
                _selectedTexturePaths.Clear();
                RebuildSelectedTab();
            }) { text = "チェックを解除" };
            clearSelection.style.width = 76;
            clearSelection.style.marginLeft = 4;
            selectionBar.Add(clearSelection);

            var platformTabs = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, marginTop = 7, marginBottom = 6 }
            };
            panel.Add(platformTabs);
            AddTexturePlatformTab(platformTabs, "Default", TextureSettingsTarget.Default);
            AddTexturePlatformTab(platformTabs, "PC", TextureSettingsTarget.Standalone);
            AddTexturePlatformTab(platformTabs, "Android", TextureSettingsTarget.Android);

            panel.style.display = selectedTextures.Length == 0 ? DisplayStyle.None : DisplayStyle.Flex;
            if (selectedTextures.Length == 0)
            {
                var help = new Label("下の一覧から設定するテクスチャを選択してください。設定変更はApplyするまで圧縮を開始しません。");
                help.style.opacity = 0.68f;
                help.style.marginTop = 4;
                help.style.marginBottom = 5;
                panel.Add(help);
            }
            else
            {
                BuildTextureSettingFields(panel, selectedTextures);
            }

            var actionBar = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginTop = 7 }
            };
            panel.Add(actionBar);
            var targetCount = new Label("チェック中の " + selectedTextures.Length.ToString("N0") + " 枚が適用対象です");
            targetCount.style.flexGrow = 1;
            targetCount.style.opacity = 0.72f;
            actionBar.Add(targetCount);

            if (_textureCompressionSettings.HasBackup)
            {
                var restore = new Button(RestoreLastTextureSettings)
                {
                    text = "直前の設定に戻す (" + _textureCompressionSettings.BackupAssetCount.ToString("N0") + ")"
                };
                restore.style.width = 152;
                restore.tooltip = "直前に適用する前のTexture Importer設定に戻します。変更したPlatform設定だけが対象です。";
                actionBar.Add(restore);
            }

            var apply = new Button(() => ApplyTextureCompression(selectedTextures)) { text = "選択中に適用" };
            apply.style.width = 120;
            apply.style.marginLeft = 5;
            apply.style.unityFontStyleAndWeight = FontStyle.Bold;
            apply.SetEnabled(selectedTextures.Length > 0);
            actionBar.Add(apply);
        }

        private void AddTexturePlatformTab(VisualElement parent, string label, TextureSettingsTarget target)
        {
            var button = new Button(() =>
            {
                _textureSettingsTarget = target;
                RebuildSelectedTab();
            }) { text = label };
            button.style.flexGrow = 1;
            button.style.height = 25;
            button.style.unityFontStyleAndWeight = _textureSettingsTarget == target ? FontStyle.Bold : FontStyle.Normal;
            button.style.backgroundColor = _textureSettingsTarget == target
                ? new Color(0.30f, 0.42f, 0.54f)
                : new Color(0.15f, 0.15f, 0.15f);
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
                var multiEditHint = new Label("複数選択中：変更した項目を選択中の全テクスチャへ設定します。");
                multiEditHint.style.fontSize = 10;
                multiEditHint.style.opacity = 0.58f;
                multiEditHint.style.marginBottom = 4;
                parent.Add(multiEditHint);
            }

            var first = settings[0];
            bool IsMixed<T>(Func<TextureCompressionSettings, T> selector)
            {
                var comparer = EqualityComparer<T>.Default;
                var value = selector(first);
                return settings.Skip(1).Any(setting => !comparer.Equals(value, selector(setting)));
            }

            if (_textureSettingsTarget != TextureSettingsTarget.Default)
            {
                var overrideField = new Toggle("Override") { value = first.Override };
                overrideField.showMixedValue = IsMixed(setting => setting.Override);
                overrideField.RegisterValueChangedCallback(evt => UpdateTextureSettings(settings, setting => setting.Override = evt.newValue));
                parent.Add(overrideField);
            }

            var maxSizes = new List<int> { 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192 };
            if (!maxSizes.Contains(first.MaxSize)) maxSizes.Add(first.MaxSize);
            maxSizes.Sort();
            var maxSizeField = new PopupField<int>("Max Size", maxSizes, maxSizes.IndexOf(first.MaxSize));
            maxSizeField.showMixedValue = IsMixed(setting => setting.MaxSize);
            maxSizeField.RegisterValueChangedCallback(evt => UpdateTextureSettings(settings, setting => setting.MaxSize = evt.newValue));
            parent.Add(maxSizeField);

            var resizeField = new EnumField("Resize Algorithm", first.ResizeAlgorithm);
            resizeField.showMixedValue = IsMixed(setting => setting.ResizeAlgorithm);
            resizeField.RegisterValueChangedCallback(evt => UpdateTextureSettings(settings,
                setting => setting.ResizeAlgorithm = (TextureResizeAlgorithm)evt.newValue));
            parent.Add(resizeField);

            var formatField = new EnumField("Format", first.Format);
            formatField.showMixedValue = IsMixed(setting => setting.Format);
            formatField.RegisterValueChangedCallback(evt => UpdateTextureSettings(settings,
                setting => setting.Format = (TextureImporterFormat)evt.newValue));
            parent.Add(formatField);

            var compressionLabels = new List<string>
            {
                "None",
                "Low Quality",
                "Normal Quality",
                "High Quality"
            };
            var compressionField = new PopupField<string>(
                "Compression",
                compressionLabels,
                CompressionToIndex(first.Compression));
            compressionField.showMixedValue = IsMixed(setting => setting.Compression);
            compressionField.RegisterValueChangedCallback(evt => UpdateTextureSettings(settings,
                setting => setting.Compression = IndexToCompression(compressionLabels.IndexOf(evt.newValue))));
            parent.Add(compressionField);

            var crunchField = new Toggle("Use Crunch Compression") { value = first.UseCrunchCompression };
            crunchField.showMixedValue = IsMixed(setting => setting.UseCrunchCompression);
            crunchField.RegisterValueChangedCallback(evt => UpdateTextureSettings(settings,
                setting => setting.UseCrunchCompression = evt.newValue));
            parent.Add(crunchField);

            var qualityField = new SliderInt("Compressor Quality", 0, 100)
            {
                value = Mathf.Clamp(first.CompressorQuality, 0, 100),
                showInputField = true
            };
            qualityField.showMixedValue = IsMixed(setting => setting.CompressorQuality);
            qualityField.RegisterValueChangedCallback(evt => UpdateTextureSettings(settings,
                setting => setting.CompressorQuality = evt.newValue));
            parent.Add(qualityField);
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

        private void UpdateTextureSettings(IEnumerable<TextureCompressionSettings> settings,
            Action<TextureCompressionSettings> update)
        {
            foreach (var setting in settings)
            {
                update(setting);
            }
            rootVisualElement.schedule.Execute(RebuildSelectedTab);
        }

        private List<(Renderer renderer, Material material, int slot, string property)> FindTextureUsages(Texture2D texture)
        {
            var uses = new List<(Renderer renderer, Material material, int slot, string property)>();
            if (_currentAvatar == null) return uses;
            foreach (var renderer in _currentAvatar.GetComponentsInChildren<Renderer>(true))
            {
                var materials = renderer.sharedMaterials;
                for (var slot = 0; slot < materials.Length; slot++)
                {
                    var material = materials[slot];
                    if (material == null || material.shader == null) continue;
                    var propertyCount = ShaderUtil.GetPropertyCount(material.shader);
                    for (var propertyIndex = 0; propertyIndex < propertyCount; propertyIndex++)
                    {
                        if (ShaderUtil.GetPropertyType(material.shader, propertyIndex) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                        var property = ShaderUtil.GetPropertyName(material.shader, propertyIndex);
                        if (material.GetTexture(property) == texture) uses.Add((renderer, material, slot, property));
                    }
                }
            }
            return uses;
        }

        private void PopulateTextureUsagePanel(VisualElement parent, Texture2D texture,
            List<(Renderer renderer, Material material, int slot, string property)> uses)
        {
            if (_currentAvatar == null)
            {
                parent.Add(new HelpBox("Avatarが選択されていません。", HelpBoxMessageType.Info));
                return;
            }
            if (uses.Count == 0)
            {
                parent.Add(new HelpBox("現在のAvatar配下のRenderer Materialから参照されていません。", HelpBoxMessageType.Info));
                return;
            }
            if (_groupTexturesByDirectory)
            {
                foreach (var group in uses.GroupBy(GetTextureUsageParent).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var stateKey = "TextureUsage|" + AssetDatabase.GetAssetPath(texture) + "|" + group.Key;
                    var expanded = !_groupExpanded.TryGetValue(stateKey, out var savedState) || savedState;
                    var foldout = new Foldout { text = group.Key + " · " + group.Count().ToString("N0") + " 件", value = expanded, userData = stateKey };
                    foldout.style.marginTop = 3;
                    foldout.RegisterValueChangedCallback(evt => _groupExpanded[stateKey] = evt.newValue);
                    parent.Add(foldout);
                    var rowIndex = 0;
                    foreach (var use in group) foldout.Add(CreateTextureUsageRow(use, rowIndex++));
                }
            }
            else
            {
                var rowIndex = 0;
                foreach (var use in uses) parent.Add(CreateTextureUsageRow(use, rowIndex++));
            }
        }

        private static string GetTextureUsageParent((Renderer renderer, Material material, int slot, string property) use)
        {
            var parent = use.renderer == null ? null : use.renderer.transform.parent;
            return parent == null ? "(ルート)" : GetHierarchyPath(parent.gameObject);
        }

        private VisualElement CreateTextureUsageRow((Renderer renderer, Material material, int slot, string property) use, int index)
        {
            var line = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    alignItems = Align.Center,
                    minHeight = 28,
                    marginTop = 1,
                    paddingLeft = 4,
                    paddingRight = 4,
                    backgroundColor = index % 2 == 0 ? new Color(0.17f, 0.17f, 0.17f) : new Color(0.225f, 0.225f, 0.225f)
                }
            };
            line.tooltip = "クリックしてHierarchyで選択・Ping";
            if (use.renderer != null)
                line.AddManipulator(new Clickable(() => SelectAndPing(use.renderer.gameObject)));
            line.Add(CreateThumbnail(use.renderer == null ? null : use.renderer.gameObject, 16));
            var labels = new VisualElement { style = { flexGrow = 1, marginLeft = 6 } };
            var objectName = new Label(use.renderer == null ? "Missing" : GetHierarchyPath(use.renderer.gameObject));
            objectName.style.unityTextAlign = TextAnchor.MiddleLeft;
            objectName.style.whiteSpace = WhiteSpace.NoWrap;
            objectName.style.overflow = Overflow.Hidden;
            objectName.style.textOverflow = TextOverflow.Ellipsis;
            labels.Add(objectName);
            line.Add(labels);
            return line;
        }

        private void CopyReportToClipboard()
        {
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
            EditorGUIUtility.systemCopyBuffer = string.Join("\n", lines);
            ShowNotification(new GUIContent("レポートをクリップボードへコピーしました。"));
        }

        private void ApplyTextureCompression(Texture2D[] selectedTextures)
        {
            var selectedSettings = (selectedTextures ?? Array.Empty<Texture2D>())
                .Select(texture => _textureCompressionSettings.GetOrCreateSettings(texture, _textureSettingsTarget))
                .Where(settings => settings != null)
                .ToArray();
            var targets = selectedSettings.Select(settings => settings.Target.ToString()).Distinct().ToArray();
            var assetPaths = selectedSettings.Select(settings => settings.AssetPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var pathsPreview = string.Join("\n", assetPaths.Take(8));
            if (assetPaths.Length > 8) pathsPreview += "\n…ほか " + (assetPaths.Length - 8).ToString("N0") + " 件";
            var applySummary = assetPaths.Length + " 個のチェック済みTextureへ圧縮設定を適用します。\n対象Platform: " + string.Join(", ", targets) + "\n対象Asset:\n" + pathsPreview;
            if (!EditorUtility.DisplayDialog("Texture設定を適用", applySummary, "適用", "キャンセル")) return;
            var applied = _textureCompressionSettings.ApplySelected(selectedSettings, out var errors);
            if (!string.IsNullOrEmpty(errors))
            {
                Debug.LogError("AvaBuild Inspector: 一部のテクスチャ設定を適用できませんでした。\n" + errors);
                ShowNotification(new GUIContent("一部のApplyに失敗しました。Consoleを確認してください。"));
            }
            else if (applied > 0)
            {
                ShowNotification(new GUIContent(applied.ToString("N0") + "個のテクスチャへ設定を適用しました。"));
            }

            _textureCompressionSettings.ClearSettings();
            if (_avatarField.value is GameObject avatar) RequestScan(avatar);
            else RebuildSelectedTab();
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
            if (_avatarField.value is GameObject avatar) RequestScan(avatar);
            else RebuildSelectedTab();
        }

        private string GetBreakdownTitle()
        {
            switch (_selectedBreakdown)
            {
                case BreakdownTab.TextureMemory: return "テクスチャメモリ内訳";
                case BreakdownTab.SkinnedMeshes: return "スキンドメッシュ内訳";
                case BreakdownTab.Materials: return "マテリアル内訳";
                case BreakdownTab.MaterialSlots: return "マテリアルスロット内訳";
                default: return "PhysBoneコンポーネント内訳";
            }
        }

        private string GetBreakdownHint()
        {
            switch (_selectedBreakdown)
            {
                case BreakdownTab.TextureMemory: return "推定使用量の大きい順・クリックでProject内のアセットを表示";
                case BreakdownTab.SkinnedMeshes: return "三角形数の多い順・クリックでHierarchy内のオブジェクトを選択";
                case BreakdownTab.Materials: return "アバター内で使用されているMaterial一覧・クリックでProject上のMaterialを選択";
                case BreakdownTab.MaterialSlots: return "スロット数の多い順・クリックでHierarchy内のオブジェクトを選択";
                default: return "推定Collision Check数とメッシュ未使用候補を確認・クリックでHierarchy内を選択";
            }
        }

        private string GetValueColumnName()
        {
            switch (_selectedBreakdown)
            {
                case BreakdownTab.TextureMemory: return "推定メモリ";
                case BreakdownTab.SkinnedMeshes: return "三角形数";
                case BreakdownTab.Materials: return "使用スロット数";
                case BreakdownTab.MaterialSlots: return "スロット数";
                default: return "推定Checks";
            }
        }

        private MetricResult GetBreakdownMetric()
        {
            switch (_selectedBreakdown)
            {
                case BreakdownTab.TextureMemory: return _report.Find(AvatarPerformanceCategory.TextureMegabytes);
                case BreakdownTab.SkinnedMeshes: return _report.Find(AvatarPerformanceCategory.SkinnedMeshCount);
                case BreakdownTab.MaterialSlots: return _report.Find(AvatarPerformanceCategory.MaterialCount);
                case BreakdownTab.Materials: return _report.Find(AvatarPerformanceCategory.MaterialCount);
                default: return _report.Find(AvatarPerformanceCategory.PhysBoneCollisionCheckCount);
            }
        }

        private MetricResult GetBreakdownSummaryMetric()
        {
            return _selectedBreakdown == BreakdownTab.PhysBones ? _report.Find(AvatarPerformanceCategory.PhysBoneComponentCount) : GetBreakdownMetric();
        }

        private VisualElement CreateTextureRow(Contributor contributor, int index)
        {
            var container = new VisualElement();
            container.style.flexDirection = FlexDirection.Column;
            var row = CreateRowButton(contributor, index);
            row.style.height = StyleKeyword.Auto;
            row.style.minHeight = _compactRows ? 20 : 34;
            row.style.paddingLeft = 5;
            row.style.paddingRight = 7;

            var assetPath = AssetDatabase.GetAssetPath(contributor.Target);
            var contextPath = GetTargetContext(contributor.Target) + " · " + (string.IsNullOrEmpty(assetPath) ? "Asset path unavailable" : assetPath);
            var editable = contributor.Target is Texture2D &&
                !string.IsNullOrEmpty(assetPath) && AssetImporter.GetAtPath(assetPath) is TextureImporter;
            var selected = editable && _selectedTexturePaths.Contains(assetPath);
            var selection = new Toggle { value = selected };
            selection.style.width = 22;
            selection.style.minWidth = 22;
            selection.tooltip = editable ? "圧縮設定の編集対象に追加" : "このTextureはインポート設定を変更できません。";
            selection.SetEnabled(editable);
            selection.RegisterCallback<ClickEvent>(evt => evt.StopPropagation());
            selection.RegisterValueChangedCallback(evt =>
            {
                if (evt.newValue) _selectedTexturePaths.Add(assetPath);
                else _selectedTexturePaths.Remove(assetPath);
                rootVisualElement.schedule.Execute(RebuildSelectedTab);
            });
            row.Add(selection);

            var thumbnail = CreateThumbnail(contributor.Target, 16);
            thumbnail.style.marginRight = 6;
            row.Add(thumbnail);

            var labels = new VisualElement { style = { flexGrow = 1, flexShrink = 1, alignItems = Align.FlexStart } };
            var name = new Label(contributor.Name);
            name.style.flexGrow = 1;
            name.style.unityTextAlign = TextAnchor.MiddleLeft;
            name.style.whiteSpace = WhiteSpace.NoWrap;
            name.style.overflow = Overflow.Hidden;
            name.style.textOverflow = TextOverflow.Ellipsis;
            name.tooltip = contributor.Name + "\n" + contextPath;
            labels.Add(name);
            VisualElement usagePanel = null;
            Button usageButton = null;
            Button materialLocationsButton = null;
            if (contributor.Target is Texture2D texture)
            {
                usagePanel = new VisualElement
                {
                    style =
                    {
                        display = DisplayStyle.None,
                        flexDirection = FlexDirection.Column,
                        marginTop = 3,
                        marginLeft = 27,
                        marginBottom = 4,
                        paddingLeft = 6,
                        paddingRight = 6,
                        paddingTop = 4,
                        paddingBottom = 4,
                        backgroundColor = new Color(0.13f, 0.13f, 0.13f)
                    }
                };
                usageButton = new Button { text = "参照元オブジェクト" };
                usageButton.style.width = 138;
                usageButton.style.minWidth = 138;
                usageButton.style.height = 22;
                usageButton.style.marginLeft = 3;
                usageButton.style.marginRight = 5;
                var populated = false;
                List<(Renderer renderer, Material material, int slot, string property)> usages = null;
                usageButton.RegisterCallback<ClickEvent>(evt => evt.StopPropagation());
                usageButton.clicked += () =>
                {
                    var expanded = usagePanel.style.display == DisplayStyle.None;
                    usagePanel.style.display = expanded ? DisplayStyle.Flex : DisplayStyle.None;
                    if (expanded && !populated)
                    {
                        usages = FindTextureUsages(texture);
                        PopulateTextureUsagePanel(usagePanel, texture, usages);
                        populated = true;
                    }
                    usageButton.text = expanded
                        ? "参照元オブジェクト・閉じる"
                        : "参照元オブジェクト";
                };
                materialLocationsButton = new Button { text = "Location" };
                materialLocationsButton.style.width = 68;
                materialLocationsButton.style.minWidth = 68;
                materialLocationsButton.style.height = 22;
                materialLocationsButton.style.marginRight = 3;
                materialLocationsButton.tooltip = "参照中のMaterialをProjectで選択・Pingします。";
                materialLocationsButton.RegisterCallback<ClickEvent>(evt => evt.StopPropagation());
                materialLocationsButton.clicked += () =>
                {
                    if (usages == null) usages = FindTextureUsages(texture);
                    var materials = usages.Select(use => use.material)
                        .Where(material => material != null && !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(material)))
                        .Distinct().Cast<UnityEngine.Object>().ToArray();
                    if (materials.Length == 0)
                    {
                        ShowNotification(new GUIContent("Project内の参照Materialがありません。"));
                        return;
                    }
                    EditorUtility.FocusProjectWindow();
                    Selection.objects = materials;
                    EditorGUIUtility.PingObject(materials[0]);
                };
            }
            row.Add(labels);

            if (materialLocationsButton != null) row.Add(materialLocationsButton);
            if (usageButton != null) row.Add(usageButton);

            var valueText = contributor.Value.ToString("0.00") + " " + contributor.Unit;
            if (_selectedBreakdown == BreakdownTab.TextureMemory && !_groupTexturesByDirectory &&
                _showTextureDirectoryPercentages && _totalTextureEstimateMegabytes > 0d)
                valueText += " · " + (Math.Max(0d, contributor.Value) / _totalTextureEstimateMegabytes * 100d).ToString("N1") + "%";
            var value = new Label(valueText);
            value.style.width = _selectedBreakdown == BreakdownTab.TextureMemory ? 142 : 112;
            value.style.unityTextAlign = TextAnchor.MiddleRight;
            value.style.opacity = 0.82f;
            row.Add(value);
            container.Add(row);
            if (_selectedBreakdown == BreakdownTab.TextureMemory && !_groupTexturesByDirectory &&
                _showTextureDirectoryPercentages && _totalTextureEstimateMegabytes > 0d)
                container.Add(CreateTextureShareBar(Math.Max(0d, contributor.Value), _totalTextureEstimateMegabytes));
            if (usagePanel != null) container.Add(usagePanel);
            return container;
        }

        private void BuildAssetDirectoryGroups(VisualElement parent, Contributor[] contributors,
            Func<Contributor, int, VisualElement> createRow, Contributor[] aggregateContributors = null)
        {
            var usageContributors = aggregateContributors ?? contributors;
            var totalUsage = usageContributors.Sum(contributor => Math.Max(0d, contributor.Value));
            var rootGroups = contributors
                .GroupBy(GetAssetRootGroup, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => string.Equals(group.Key, "Assets以外", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase);
            foreach (var rootGroup in rootGroups)
            {
                var rootStateKey = _selectedBreakdown + "|asset-root|" + rootGroup.Key;
                var rootExpanded = !_groupExpanded.TryGetValue(rootStateKey, out var savedRootExpanded) || savedRootExpanded;
                var externalAssets = string.Equals(rootGroup.Key, "Assets以外", StringComparison.OrdinalIgnoreCase);
                var rootUsage = usageContributors.Where(contributor =>
                    string.Equals(GetAssetRootGroup(contributor), rootGroup.Key, StringComparison.OrdinalIgnoreCase)).ToArray();
                var rootMegabytes = rootUsage.Sum(contributor => Math.Max(0d, contributor.Value));
                var rootPercentage = totalUsage <= 0d ? 0d : rootMegabytes / totalUsage * 100d;
                var rootFoldout = new Foldout
                {
                    text = rootGroup.Key + " · " + rootGroup.Count().ToString("N0") + " 項目 · " + rootMegabytes.ToString("N1") + " MB" +
                           (_selectedBreakdown == BreakdownTab.TextureMemory && _showTextureDirectoryPercentages ? " · " + rootPercentage.ToString("N1") + "%" : ""),
                    value = rootExpanded,
                    userData = rootStateKey
                };
                rootFoldout.RegisterValueChangedCallback(evt => _groupExpanded[rootStateKey] = evt.newValue);
                rootFoldout.style.marginLeft = externalAssets ? 16 : 4;
                rootFoldout.style.marginRight = 4;
                rootFoldout.style.marginTop = externalAssets ? 12 : 0;
                if (_selectedBreakdown == BreakdownTab.TextureMemory && _showTextureDirectoryPercentages)
                    parent.Add(CreateTextureShareBar(rootMegabytes, totalUsage));
                parent.Add(rootFoldout);

                var parentGroups = rootGroup
                    .GroupBy(contributor => GetAssetParentSubgroup(contributor, rootGroup.Key), StringComparer.OrdinalIgnoreCase)
                    .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase);
                foreach (var parentGroup in parentGroups)
                {
                    var parentStateKey = rootStateKey + "|parent|" + parentGroup.Key;
                    var parentExpanded = !_groupExpanded.TryGetValue(parentStateKey, out var savedParentExpanded) || savedParentExpanded;
                    var parentUsage = rootUsage.Where(contributor =>
                        string.Equals(GetAssetParentSubgroup(contributor, rootGroup.Key), parentGroup.Key, StringComparison.OrdinalIgnoreCase)).ToArray();
                    var parentMegabytes = parentUsage.Sum(contributor => Math.Max(0d, contributor.Value));
                    var parentPercentage = totalUsage <= 0d ? 0d : parentMegabytes / totalUsage * 100d;
                    var parentFoldout = new Foldout
                    {
                        text = parentGroup.Key + " · " + parentGroup.Count().ToString("N0") + " 項目" +
                               (_selectedBreakdown == BreakdownTab.TextureMemory
                                   ? " · " + parentMegabytes.ToString("N1") + " MB" +
                                     (_showTextureDirectoryPercentages ? " · " + parentPercentage.ToString("N1") + "%" : "")
                                   : ""),
                        value = parentExpanded,
                        userData = parentStateKey
                    };
                    parentFoldout.RegisterValueChangedCallback(evt => _groupExpanded[parentStateKey] = evt.newValue);
                    parentFoldout.style.marginLeft = 18;
                    parentFoldout.style.marginRight = 4;
                    if (_selectedBreakdown == BreakdownTab.TextureMemory && _showTextureDirectoryPercentages)
                        rootFoldout.Add(CreateTextureShareBar(parentMegabytes, totalUsage));
                    rootFoldout.Add(parentFoldout);
                    var rowIndex = 0;
                    foreach (var contributor in parentGroup)
                        parentFoldout.Add(createRow(contributor, rowIndex++));
                }
            }
        }

        private static VisualElement CreateTextureShareBar(double shareMegabytes, double totalMegabytes)
        {
            var percentage = totalMegabytes <= 0d ? 0f : Mathf.Clamp01((float)(shareMegabytes / totalMegabytes));
            var track = new VisualElement
            {
                style =
                {
                    height = 5,
                    marginLeft = 6,
                    marginRight = 8,
                    marginTop = 1,
                    marginBottom = 4,
                    backgroundColor = new Color(0.10f, 0.10f, 0.10f)
                }
            };
            var fill = new VisualElement
            {
                style =
                {
                    width = Length.Percent(percentage * 100f),
                    height = 5,
                    backgroundColor = new Color(0.30f, 0.62f, 0.86f)
                }
            };
            track.tooltip = "推定容量: " + shareMegabytes.ToString("N2") + " MB / 全Texture: " + totalMegabytes.ToString("N2") + " MB";
            track.Add(fill);
            return track;
        }

        private string GetAssetGroupingPath(Contributor contributor)
        {
            var asset = _selectedBreakdown == BreakdownTab.TextureMemory || _selectedBreakdown == BreakdownTab.Materials
                ? contributor.Target
                : contributor.SecondaryTarget != null ? contributor.SecondaryTarget : contributor.Target;
            return NormalizeAssetPath(AssetDatabase.GetAssetPath(asset));
        }

        private string GetAssetRootGroup(Contributor contributor)
        {
            var path = GetAssetGroupingPath(contributor);
            if (string.IsNullOrEmpty(path)) return "(パスなし)";
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) return "Assets以外";
            var relativePath = path.Substring("Assets/".Length);
            var directoryEnd = relativePath.LastIndexOf('/');
            var directory = directoryEnd < 0 ? string.Empty : relativePath.Substring(0, directoryEnd);
            if (string.IsNullOrEmpty(directory)) return "(ルート)";
            var firstDirectoryEnd = directory.IndexOf('/');
            return firstDirectoryEnd < 0 ? directory : directory.Substring(0, firstDirectoryEnd);
        }

        private string GetAssetParentSubgroup(Contributor contributor, string rootGroup)
        {
            var path = GetAssetGroupingPath(contributor);
            if (string.IsNullOrEmpty(path)) return "(パスなし)";
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                var externalDirectoryEnd = path.LastIndexOf('/');
                return externalDirectoryEnd < 0 ? "(ルート直下)" : path.Substring(0, externalDirectoryEnd);
            }

            var relativePath = path.Substring("Assets/".Length);
            var directoryEnd = relativePath.LastIndexOf('/');
            var directory = directoryEnd < 0 ? string.Empty : relativePath.Substring(0, directoryEnd);
            if (string.Equals(rootGroup, "(ルート)", StringComparison.OrdinalIgnoreCase)) return "(直下)";
            if (string.IsNullOrEmpty(directory)) return "(直下)";
            if (directory.Length <= rootGroup.Length) return "(直下)";
            return directory.Substring(rootGroup.Length).TrimStart('/');
        }

        private static string NormalizeAssetPath(string path)
        {
            return string.IsNullOrEmpty(path) ? string.Empty : path.Replace('\\', '/');
        }

        private VisualElement CreateDetailRow(Contributor contributor, int index)
        {
            var row = CreateRowButton(contributor, index);
            row.style.minHeight = _compactRows ? 26 : 44;
            row.style.paddingLeft = 7;
            row.style.paddingRight = 7;
            if (contributor.IsUnusedPhysBone)
                row.style.backgroundColor = index % 2 == 0
                    ? new Color(0.31f, 0.20f, 0.09f)
                    : new Color(0.37f, 0.24f, 0.10f);

            var previewTarget = _selectedBreakdown == BreakdownTab.Materials
                ? contributor.Target
                : contributor.SecondaryTarget != null ? contributor.SecondaryTarget : contributor.Target;
            var showAssetPreview =
                (_selectedBreakdown == BreakdownTab.Materials && previewTarget is Material) ||
                (_selectedBreakdown == BreakdownTab.MaterialSlots && previewTarget is Material) ||
                (_selectedBreakdown == BreakdownTab.SkinnedMeshes && previewTarget is Mesh);
            var thumbnail = showAssetPreview
                ? CreateAssetPreviewThumbnail(previewTarget, 30)
                : CreateThumbnail(previewTarget, 30);
            thumbnail.style.marginRight = 8;
            row.Add(thumbnail);

            var labels = new VisualElement { style = { flexGrow = 1, flexShrink = 1 } };
            var name = new Label(contributor.Name);
            name.style.whiteSpace = WhiteSpace.NoWrap;
            name.style.overflow = Overflow.Hidden;
            name.style.textOverflow = TextOverflow.Ellipsis;
            name.tooltip = contributor.Name;
            labels.Add(name);
            if (!_compactRows && !string.IsNullOrEmpty(contributor.Detail))
            {
                var detail = new Label(contributor.Detail);
                detail.style.fontSize = 10;
                detail.style.opacity = 0.58f;
                detail.style.whiteSpace = WhiteSpace.NoWrap;
                detail.style.overflow = Overflow.Hidden;
                detail.style.textOverflow = TextOverflow.Ellipsis;
                detail.tooltip = detail.text;
                labels.Add(detail);
            }
            row.Add(labels);

            if (contributor.SecondaryTarget != null && contributor.SecondaryTarget != contributor.Target)
            {
                var relatedTarget = contributor.SecondaryTarget;
                var relatedButton = new Button(() => SelectAndPing(relatedTarget))
                {
                    text = relatedTarget is Material ? "Material" : relatedTarget is Mesh ? "Mesh" : "Component"
                };
                relatedButton.style.width = 76;
                relatedButton.tooltip = GetTargetContext(relatedTarget) + " · " + GetHierarchyPath(relatedTarget);
                relatedButton.RegisterCallback<ClickEvent>(evt => evt.StopPropagation());
                row.Add(relatedButton);
            }

            if (contributor.IsUnusedPhysBone)
            {
                var warning = new Label("未使用候補");
                warning.style.width = 78;
                warning.style.unityTextAlign = TextAnchor.MiddleCenter;
                warning.style.color = new Color(1f, 0.72f, 0.28f);
                warning.style.unityFontStyleAndWeight = FontStyle.Bold;
                warning.tooltip = "対象ボーンがどのSkinnedMeshRendererのbonesにも登録されていません。";
                row.Add(warning);
            }

            var value = new Label(contributor.Value.ToString("N0") + " " + contributor.Unit);
            value.style.width = 112;
            value.style.unityTextAlign = TextAnchor.MiddleRight;
            value.style.opacity = 0.82f;
            row.Add(value);
            return row;
        }

        private static Button CreateRowButton(Contributor contributor, int index)
        {
            var row = new Button(() => SelectAndPing(contributor.Target));
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginTop = 0;
            row.style.marginBottom = 0;
            row.style.borderTopLeftRadius = 0;
            row.style.borderTopRightRadius = 0;
            row.style.borderBottomLeftRadius = 0;
            row.style.borderBottomRightRadius = 0;
            row.style.borderTopWidth = 0;
            row.style.borderBottomWidth = 0;
            row.style.borderLeftWidth = 0;
            row.style.borderRightWidth = 0;
            row.style.backgroundColor = index % 2 == 0 ? new Color(0.17f, 0.17f, 0.17f) : new Color(0.225f, 0.225f, 0.225f);
            row.SetEnabled(contributor.Target != null);
            row.tooltip = contributor.Target == null
                ? "対象がMissingまたは削除済みのため移動できません。"
                : string.IsNullOrEmpty(contributor.Detail)
                ? "クリックして選択"
                : contributor.Detail + "\nクリックして選択";
            return row;
        }

        private static Image CreateThumbnail(UnityEngine.Object target, float size)
        {
            var thumbnail = new Image
            {
                image = target == null ? null : AssetPreview.GetMiniThumbnail(target),
                scaleMode = ScaleMode.ScaleToFit
            };
            thumbnail.style.width = size;
            thumbnail.style.height = size;
            thumbnail.style.minWidth = size;
            thumbnail.style.minHeight = size;
            return thumbnail;
        }

        private static Image CreateAssetPreviewThumbnail(UnityEngine.Object target, float size)
        {
            var thumbnail = CreateThumbnail(target, size);
            if (target == null) return thumbnail;

            var preview = AssetPreview.GetAssetPreview(target);
            if (preview != null)
            {
                thumbnail.image = preview;
                thumbnail.tooltip = target.name;
                return thumbnail;
            }

            var startedAt = EditorApplication.timeSinceStartup;
            EditorApplication.CallbackFunction updatePreview = null;
            updatePreview = () =>
            {
                if (thumbnail == null || thumbnail.panel == null || target == null || EditorApplication.timeSinceStartup - startedAt > 5d)
                {
                    EditorApplication.update -= updatePreview;
                    return;
                }

                var loadedPreview = AssetPreview.GetAssetPreview(target);
                if (loadedPreview != null)
                {
                    thumbnail.image = loadedPreview;
                    thumbnail.tooltip = target.name;
                    thumbnail.MarkDirtyRepaint();
                    EditorApplication.update -= updatePreview;
                }
            };
            EditorApplication.update += updatePreview;
            return thumbnail;
        }

        private void ShowEmptyState(string message)
        {
            if (_content == null) return;
            _content.Clear();
            var label = new Label(message);
            label.style.flexGrow = 1;
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            label.style.fontSize = 15;
            label.style.opacity = 0.7f;
            _content.Add(label);
        }

        private static GameObject FindAvatarRoot(GameObject candidate)
        {
            if (candidate == null) return null;
            var descriptor = candidate.GetComponent<VRCAvatarDescriptor>() ?? candidate.GetComponentInParent<VRCAvatarDescriptor>();
            if (descriptor == null) descriptor = candidate.GetComponentInChildren<VRCAvatarDescriptor>(true);
            return descriptor == null ? null : descriptor.gameObject;
        }

        private static GameObject FindSelectedAvatar()
        {
            var selected = FindAvatarRoot(Selection.activeGameObject);
            if (selected != null) return selected;
            var descriptors = FindObjectsOfType<VRCAvatarDescriptor>(true);
            return descriptors.Length == 1 ? descriptors[0].gameObject : null;
        }

        private static void SelectAndPing(UnityEngine.Object target)
        {
            if (target == null) return;
            Selection.activeObject = target;
            EditorGUIUtility.PingObject(target);
        }

        private static string GetHierarchyPath(UnityEngine.Object target)
        {
            if (!(target is GameObject gameObject))
            {
                if (target is Component component) gameObject = component.gameObject;
                else return target == null ? "Missing" : AssetDatabase.GetAssetPath(target);
            }
            var parts = new Stack<string>();
            for (var current = gameObject.transform; current != null; current = current.parent) parts.Push(current.name);
            return string.Join("/", parts);
        }

        private static string GetTargetContext(UnityEngine.Object target)
        {
            if (target == null) return "Missing";
            if (target is GameObject || target is Component) return "Scene Object";
            return string.IsNullOrEmpty(AssetDatabase.GetAssetPath(target)) ? target.GetType().Name : "Project Asset";
        }

        private static bool IsIssue(MetricResult metric) => metric.Rank == PerformanceRating.Poor || metric.Rank == PerformanceRating.VeryPoor;

        private static string RatingText(PerformanceRating rating)
        {
            switch (rating)
            {
                case PerformanceRating.Excellent: return "Excellent";
                case PerformanceRating.Good: return "Good";
                case PerformanceRating.Medium: return "Medium";
                case PerformanceRating.Poor: return "Poor";
                case PerformanceRating.VeryPoor: return "Very Poor";
                default: return "Unavailable";
            }
        }

        private static string RatingIcon(PerformanceRating rating)
        {
            switch (rating)
            {
                case PerformanceRating.VeryPoor: return "✕";
                case PerformanceRating.Poor: return "⚠";
                case PerformanceRating.Medium: return "!";
                case PerformanceRating.Good:
                case PerformanceRating.Excellent: return "✓";
                default: return "—";
            }
        }

        private static Color RatingColor(PerformanceRating rating)
        {
            switch (rating)
            {
                case PerformanceRating.VeryPoor: return new Color(1f, 0.28f, 0.28f);
                case PerformanceRating.Poor: return new Color(1f, 0.62f, 0.2f);
                case PerformanceRating.Medium: return new Color(1f, 0.85f, 0.25f);
                case PerformanceRating.Good: return new Color(0.48f, 0.82f, 0.36f);
                case PerformanceRating.Excellent: return new Color(0.3f, 0.75f, 0.55f);
                default: return Color.gray;
            }
        }
    }
}
