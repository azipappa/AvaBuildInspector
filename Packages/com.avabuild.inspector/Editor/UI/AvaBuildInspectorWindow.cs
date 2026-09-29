using System;
using System.Collections.Generic;
using System.Linq;
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
        private enum MainTab { ScanResults, Breakdown }
        private enum BreakdownTab { TextureMemory, SkinnedMeshes, MaterialSlots, PhysBones }

        private ObjectField _avatarField;
        private EnumField _platformField;
        private Button _rescanButton;
        private VisualElement _content;
        private Button _scanResultsTab;
        private Button _breakdownTab;
        private MainTab _selectedTab;
        private BreakdownTab _selectedBreakdown;
        private AvatarReport _report;
        private int _scanRequest;
        private readonly TextureCompressionQueue _textureCompressionQueue = new TextureCompressionQueue();
        private readonly HashSet<string> _selectedTexturePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private TextureSettingsTarget _textureSettingsTarget;
        private GameObject _currentAvatar;
        private bool _showUnusedPhysBonesOnly;

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

            _platformField = new EnumField("Platform", TargetPlatform.PC);
            _platformField.style.width = 180;
            _platformField.style.marginLeft = 8;
            _platformField.RegisterValueChangedCallback(_ =>
            {
                if (_avatarField.value is GameObject avatar) RequestScan(avatar);
            });
            inputRow.Add(_platformField);

            _rescanButton = new Button(() =>
            {
                if (_avatarField.value is GameObject avatar) RequestScan(avatar);
            }) { text = "再スキャン" };
            _rescanButton.style.width = 88;
            _rescanButton.style.height = 21;
            _rescanButton.style.marginLeft = 8;
            _rescanButton.tooltip = "現在のAvatarをもう一度Quick Scanします。";
            inputRow.Add(_rescanButton);
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
            SelectMainTab(MainTab.ScanResults);
        }

        private void OnAvatarChanged(GameObject candidate)
        {
            var avatar = FindAvatarRoot(candidate);
            if (avatar != candidate) _avatarField.SetValueWithoutNotify(avatar);
            if (avatar != _currentAvatar)
            {
                _selectedTexturePaths.Clear();
                _textureCompressionQueue.DiscardPending();
                _currentAvatar = avatar;
            }
            RequestScan(avatar);
        }

        private void RequestScan(GameObject avatar)
        {
            var request = ++_scanRequest;
            _currentAvatar = avatar;
            _report = null;
            if (_rescanButton != null) _rescanButton.SetEnabled(false);
            ShowEmptyState(avatar == null ? "VRCAvatarDescriptorのあるAvatarを指定してください。" : "Quick Scan中...");
            if (avatar == null)
            {
                if (_rescanButton != null) _rescanButton.SetEnabled(false);
                return;
            }

            rootVisualElement.schedule.Execute(() =>
            {
                if (request != _scanRequest || avatar == null) return;
                try
                {
                    _report = AvatarQuickAnalyzer.Analyze(avatar, (TargetPlatform)_platformField.value);
                    if (_rescanButton != null) _rescanButton.SetEnabled(true);
                    RebuildSelectedTab();
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                    if (_rescanButton != null) _rescanButton.SetEnabled(true);
                    ShowEmptyState("Quick Scanに失敗しました。Consoleを確認してください。");
                }
            });
        }

        private void SelectMainTab(MainTab tab)
        {
            _selectedTab = tab;
            if (_scanResultsTab != null)
                _scanResultsTab.style.backgroundColor = tab == MainTab.ScanResults ? new Color(0.24f, 0.46f, 0.68f) : new Color(0.22f, 0.22f, 0.22f);
            if (_breakdownTab != null)
                _breakdownTab.style.backgroundColor = tab == MainTab.Breakdown ? new Color(0.24f, 0.46f, 0.68f) : new Color(0.22f, 0.22f, 0.22f);
            RebuildSelectedTab();
        }

        private void RebuildSelectedTab()
        {
            if (_content == null) return;
            _content.Clear();
            if (_report == null)
            {
                ShowEmptyState("Avatarをドラッグ＆ドロップしてください。");
                return;
            }
            if (_selectedTab == MainTab.ScanResults) BuildScanResults();
            else BuildBreakdown();
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
            row.Add(new Label(metric.DisplayName) { style = { flexGrow = 1 } });

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
            AddBreakdownTab(subTabs, "マテリアルスロット", BreakdownTab.MaterialSlots);
            AddBreakdownTab(subTabs, "PBコンポーネント", BreakdownTab.PhysBones);

            var metric = GetBreakdownMetric();
            var summaryMetric = GetBreakdownSummaryMetric();
            if (metric == null || summaryMetric == null)
            {
                _content.Add(new HelpBox("この内訳は取得できません。", HelpBoxMessageType.Warning));
                return;
            }

            BuildBreakdownSummary(metric, summaryMetric);
            if (_selectedBreakdown == BreakdownTab.TextureMemory) BuildTextureCompressionEditor(metric);
            if (_selectedBreakdown == BreakdownTab.PhysBones) BuildPhysBoneUsagePanel(metric);
            BuildBreakdownColumnHeader();

            var scroll = new ScrollView(ScrollViewMode.Vertical) { style = { flexGrow = 1 } };
            scroll.style.marginLeft = 12;
            scroll.style.marginRight = 12;
            scroll.style.marginBottom = 10;
            _content.Add(scroll);
            var visibleContributors = _selectedBreakdown == BreakdownTab.PhysBones && _showUnusedPhysBonesOnly
                ? metric.Contributors.Where(contributor => contributor.IsUnusedPhysBone).ToArray()
                : metric.Contributors.ToArray();
            for (var index = 0; index < visibleContributors.Length; index++)
            {
                var contributor = visibleContributors[index];
                scroll.Add(_selectedBreakdown == BreakdownTab.TextureMemory
                    ? CreateTextureRow(contributor, index)
                    : CreateDetailRow(contributor, index));
            }
            if (visibleContributors.Length == 0)
            {
                var emptyMessage = _selectedBreakdown == BreakdownTab.PhysBones && _showUnusedPhysBonesOnly
                    ? "未使用候補のPhysBoneはありません。"
                    : "表示できる項目はありません。";
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
            }
        }

        private void AddBreakdownTab(VisualElement parent, string text, BreakdownTab tab)
        {
            var button = new Button(() => { _selectedBreakdown = tab; RebuildSelectedTab(); }) { text = text };
            button.style.flexGrow = 1;
            button.style.height = 32;
            button.style.marginLeft = 1;
            button.style.marginRight = 1;
            button.style.unityFontStyleAndWeight = _selectedBreakdown == tab ? FontStyle.Bold : FontStyle.Normal;
            button.style.backgroundColor = _selectedBreakdown == tab ? new Color(0.25f, 0.47f, 0.68f) : new Color(0.20f, 0.20f, 0.20f);
            parent.Add(button);
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

            var count = new Label(metric.Contributors.Count.ToString("N0") + " 項目");
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

        private void BuildTextureCompressionEditor(MetricResult metric)
        {
            var textureContributors = metric.Contributors
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
            _content.Add(panel);

            var selectionBar = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };
            panel.Add(selectionBar);
            var selectionLabel = new Label("圧縮設定  " + selectedTextures.Length.ToString("N0") + " 枚選択中");
            selectionLabel.style.flexGrow = 1;
            selectionLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            selectionBar.Add(selectionLabel);

            var selectAll = new Button(() =>
            {
                foreach (var contributor in textureContributors)
                {
                    var path = AssetDatabase.GetAssetPath(contributor.Target);
                    if (!string.IsNullOrEmpty(path) && AssetImporter.GetAtPath(path) is TextureImporter)
                        _selectedTexturePaths.Add(path);
                }
                RebuildSelectedTab();
            }) { text = "すべて選択" };
            selectAll.style.width = 82;
            selectionBar.Add(selectAll);

            var clearSelection = new Button(() =>
            {
                _selectedTexturePaths.Clear();
                RebuildSelectedTab();
            }) { text = "選択解除" };
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
            var pending = new Label(_textureCompressionQueue.PendingCount.ToString("N0") + " 設定を保留中");
            pending.style.flexGrow = 1;
            pending.style.opacity = _textureCompressionQueue.PendingCount == 0 ? 0.55f : 1f;
            actionBar.Add(pending);

            var discard = new Button(() =>
            {
                _textureCompressionQueue.DiscardPending();
                RebuildSelectedTab();
            }) { text = "変更を破棄" };
            discard.style.width = 86;
            discard.SetEnabled(_textureCompressionQueue.PendingCount > 0);
            actionBar.Add(discard);

            var apply = new Button(ApplyTextureCompression) { text = "すべてApply" };
            apply.style.width = 110;
            apply.style.marginLeft = 5;
            apply.style.unityFontStyleAndWeight = FontStyle.Bold;
            apply.SetEnabled(_textureCompressionQueue.PendingCount > 0);
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
            var drafts = textures
                .Select(texture => _textureCompressionQueue.GetOrCreate(texture, _textureSettingsTarget))
                .Where(draft => draft != null)
                .ToArray();
            if (drafts.Length == 0)
            {
                parent.Add(new HelpBox("選択した画像にTextureImporterがありません。", HelpBoxMessageType.Warning));
                return;
            }

            if (drafts.Length > 1)
            {
                var multiEditHint = new Label("複数選択中：変更した項目を選択中の全テクスチャへ設定します。");
                multiEditHint.style.fontSize = 10;
                multiEditHint.style.opacity = 0.58f;
                multiEditHint.style.marginBottom = 4;
                parent.Add(multiEditHint);
            }

            var first = drafts[0];
            if (_textureSettingsTarget != TextureSettingsTarget.Default)
            {
                var overrideField = new Toggle("Override") { value = first.Override };
                overrideField.RegisterValueChangedCallback(evt => UpdateTextureDrafts(drafts, draft => draft.Override = evt.newValue));
                parent.Add(overrideField);
            }

            var maxSizes = new List<int> { 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192 };
            if (!maxSizes.Contains(first.MaxSize)) maxSizes.Add(first.MaxSize);
            maxSizes.Sort();
            var maxSizeField = new PopupField<int>("Max Size", maxSizes, maxSizes.IndexOf(first.MaxSize));
            maxSizeField.RegisterValueChangedCallback(evt => UpdateTextureDrafts(drafts, draft => draft.MaxSize = evt.newValue));
            parent.Add(maxSizeField);

            var resizeField = new EnumField("Resize Algorithm", first.ResizeAlgorithm);
            resizeField.RegisterValueChangedCallback(evt => UpdateTextureDrafts(drafts,
                draft => draft.ResizeAlgorithm = (TextureResizeAlgorithm)evt.newValue));
            parent.Add(resizeField);

            var formatField = new EnumField("Format", first.Format);
            formatField.RegisterValueChangedCallback(evt => UpdateTextureDrafts(drafts,
                draft => draft.Format = (TextureImporterFormat)evt.newValue));
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
            compressionField.RegisterValueChangedCallback(evt => UpdateTextureDrafts(drafts,
                draft => draft.Compression = IndexToCompression(compressionLabels.IndexOf(evt.newValue))));
            parent.Add(compressionField);

            var crunchField = new Toggle("Use Crunch Compression") { value = first.UseCrunchCompression };
            crunchField.RegisterValueChangedCallback(evt => UpdateTextureDrafts(drafts,
                draft => draft.UseCrunchCompression = evt.newValue));
            parent.Add(crunchField);

            var qualityField = new SliderInt("Compressor Quality", 0, 100)
            {
                value = Mathf.Clamp(first.CompressorQuality, 0, 100),
                showInputField = true
            };
            qualityField.RegisterValueChangedCallback(evt => UpdateTextureDrafts(drafts,
                draft => draft.CompressorQuality = evt.newValue));
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

        private void UpdateTextureDrafts(IEnumerable<TextureCompressionDraft> drafts,
            Action<TextureCompressionDraft> update)
        {
            foreach (var draft in drafts)
            {
                update(draft);
                draft.Pending = true;
            }
            rootVisualElement.schedule.Execute(RebuildSelectedTab);
        }

        private void ApplyTextureCompression()
        {
            var applied = _textureCompressionQueue.ApplyPending(out var errors);
            if (!string.IsNullOrEmpty(errors))
            {
                Debug.LogError("AvaBuild Inspector: 一部のテクスチャ設定を適用できませんでした。\n" + errors);
                ShowNotification(new GUIContent("一部のApplyに失敗しました。Consoleを確認してください。"));
            }
            else if (applied > 0)
            {
                ShowNotification(new GUIContent(applied.ToString("N0") + "個のテクスチャへ設定を適用しました。"));
            }

            if (_avatarField.value is GameObject avatar) RequestScan(avatar);
            else RebuildSelectedTab();
        }

        private string GetBreakdownTitle()
        {
            switch (_selectedBreakdown)
            {
                case BreakdownTab.TextureMemory: return "テクスチャメモリ内訳";
                case BreakdownTab.SkinnedMeshes: return "スキンドメッシュ内訳";
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
                default: return _report.Find(AvatarPerformanceCategory.PhysBoneCollisionCheckCount);
            }
        }

        private MetricResult GetBreakdownSummaryMetric()
        {
            return _selectedBreakdown == BreakdownTab.PhysBones ? _report.Find(AvatarPerformanceCategory.PhysBoneComponentCount) : GetBreakdownMetric();
        }

        private VisualElement CreateTextureRow(Contributor contributor, int index)
        {
            var row = CreateRowButton(contributor, index);
            row.style.height = 24;
            row.style.minHeight = 24;
            row.style.paddingLeft = 5;
            row.style.paddingRight = 7;

            var assetPath = AssetDatabase.GetAssetPath(contributor.Target);
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

            var name = new Label(contributor.Name);
            name.style.flexGrow = 1;
            name.style.flexShrink = 1;
            name.style.whiteSpace = WhiteSpace.NoWrap;
            name.style.overflow = Overflow.Hidden;
            name.style.textOverflow = TextOverflow.Ellipsis;
            row.Add(name);

            if (_textureCompressionQueue.HasPending(assetPath))
            {
                var pending = new Label("●");
                pending.style.width = 18;
                pending.style.unityTextAlign = TextAnchor.MiddleCenter;
                pending.style.color = new Color(1f, 0.67f, 0.2f);
                pending.tooltip = "Apply待ちの設定があります。";
                row.Add(pending);
            }

            var value = new Label(contributor.Value.ToString("0.00") + " " + contributor.Unit);
            value.style.width = 112;
            value.style.unityTextAlign = TextAnchor.MiddleRight;
            value.style.opacity = 0.82f;
            row.Add(value);
            return row;
        }

        private VisualElement CreateDetailRow(Contributor contributor, int index)
        {
            var row = CreateRowButton(contributor, index);
            row.style.minHeight = 44;
            row.style.paddingLeft = 7;
            row.style.paddingRight = 7;
            if (contributor.IsUnusedPhysBone)
                row.style.backgroundColor = index % 2 == 0
                    ? new Color(0.31f, 0.20f, 0.09f)
                    : new Color(0.37f, 0.24f, 0.10f);

            var previewTarget = contributor.SecondaryTarget != null ? contributor.SecondaryTarget : contributor.Target;
            var thumbnail = CreateThumbnail(previewTarget, 30);
            thumbnail.style.marginRight = 8;
            row.Add(thumbnail);

            var labels = new VisualElement { style = { flexGrow = 1, flexShrink = 1 } };
            var name = new Label(contributor.Name);
            name.style.whiteSpace = WhiteSpace.NoWrap;
            name.style.overflow = Overflow.Hidden;
            name.style.textOverflow = TextOverflow.Ellipsis;
            labels.Add(name);
            if (!string.IsNullOrEmpty(contributor.Detail))
            {
                var detail = new Label(contributor.Detail);
                detail.style.fontSize = 10;
                detail.style.opacity = 0.58f;
                detail.style.whiteSpace = WhiteSpace.NoWrap;
                detail.style.overflow = Overflow.Hidden;
                detail.style.textOverflow = TextOverflow.Ellipsis;
                labels.Add(detail);
            }
            row.Add(labels);

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
            row.tooltip = string.IsNullOrEmpty(contributor.Detail)
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
