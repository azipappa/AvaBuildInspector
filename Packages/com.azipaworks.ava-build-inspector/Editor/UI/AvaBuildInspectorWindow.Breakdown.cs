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
        private enum BreakdownTab { TextureMemory, SkinnedMeshes, Materials, MaterialSlots, PhysBones }
        private enum SortColumn { Default, Name, Value }
        private enum BreakdownItemKind { GroupHeader, Contributor, TextureUsage, Message }

        private const string FilterAll = "すべて";
        private const string FilterTexture1Mb = "1 MB以上";
        private const string FilterTexture4Mb = "4 MB以上";
        private const string FilterUncompressed = "非圧縮";
        private const string FilterMaxSize4096 = "Max Size 4096以上";
        private const string FilterNoOverride = "Platform Overrideなし";
        private const string FilterChecked = "チェック中のみ";
        private const string FilterTriangles10K = "10,000 triangles以上";
        private const string FilterTriangles50K = "50,000 triangles以上";
        private const string FilterBlendShapes = "Blend Shapeあり";
        private const string FilterSlots5 = "5 slots以上";
        private const string FilterSlots10 = "10 slots以上";
        private const string FilterMissingSlots = "Missing Slotあり";
        private const float TextureValueWidth = 142f;
        private const float DetailValueWidth = 112f;
        private const float ImportBadgeWidth = 128f;

        private sealed class TextureUsageRef
        {
            public Renderer Renderer;
            public Material Material;
            public int Slot;
            public string Property;
        }

        private sealed class BreakdownItem
        {
            public BreakdownItemKind Kind;
            public int Depth;
            public int RowIndex;
            public string StateKey;
            public string Title;
            public bool Expanded;
            public bool Spaced;
            public double ShareValue = -1d;
            public Contributor Contributor;
            public TextureUsageRef Usage;
        }

        private BreakdownTab _selectedBreakdown;
        private BreakdownTab _listTab;
        private SortColumn _sortColumn;
        private bool _sortDescending = true;
        private bool _groupTexturesByDirectory;
        private bool _groupSkinnedMeshesByDirectory;
        private bool _groupMaterialsByDirectory;
        private bool _showTextureDirectoryPercentages = true;
        private bool _showUnusedPhysBonesOnly;
        private double _totalTextureEstimateMegabytes;
        private readonly Dictionary<BreakdownTab, string> _searchByTab = new Dictionary<BreakdownTab, string>();
        private readonly Dictionary<BreakdownTab, string> _filterByTab = new Dictionary<BreakdownTab, string>();
        private readonly Dictionary<BreakdownTab, Vector2> _scrollByTab = new Dictionary<BreakdownTab, Vector2>();
        private readonly Dictionary<string, bool> _groupExpanded = new Dictionary<string, bool>(StringComparer.Ordinal);
        private readonly HashSet<string> _expandedTextureUsages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<Texture2D, List<TextureUsageRef>> _usageCache = new Dictionary<Texture2D, List<TextureUsageRef>>();
        private readonly Dictionary<Contributor, string> _searchTextCache = new Dictionary<Contributor, string>();
        private readonly Dictionary<Contributor, string[]> _contributorGroupKeys = new Dictionary<Contributor, string[]>();
        private readonly List<BreakdownItem> _breakdownItems = new List<BreakdownItem>();
        private Contributor[] _visibleContributors = Array.Empty<Contributor>();

        private ListView _breakdownList;
        private VisualElement _breakdownEmpty;
        private Label _breakdownCountLabel;
        private Label _nameSortHeader;
        private Label _valueSortHeader;
        private Toggle _headerCheckToggle;
        private IVisualElementScheduledItem _searchDebounce;
        private int _checkAnchorIndex = -1;
        private bool? _checkPaintState;

        private string CurrentSearch => _searchByTab.TryGetValue(_selectedBreakdown, out var search) ? search ?? string.Empty : string.Empty;

        private void ResetBreakdownReferences()
        {
            _breakdownList = null;
            _breakdownEmpty = null;
            _breakdownCountLabel = null;
            _nameSortHeader = null;
            _valueSortHeader = null;
            _headerCheckToggle = null;
            _compressionPanel = null;
            _compressionEstimateLabel = null;
            _compressionEstimateNote = null;
            _searchDebounce?.Pause();
            _searchDebounce = null;
            _checkAnchorIndex = -1;
        }

        #region Layout

        private void BuildBreakdown()
        {
            var subTabs = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, paddingLeft = 12, paddingRight = 12, paddingTop = 10, paddingBottom = 8 }
            };
            _content.Add(subTabs);
            AddBreakdownTab(subTabs, "テクスチャメモリ", BreakdownTab.TextureMemory, false);
            AddBreakdownTab(subTabs, "スキンドメッシュ", BreakdownTab.SkinnedMeshes, false);
            AddBreakdownTab(subTabs, "マテリアル", BreakdownTab.Materials, false);
            AddBreakdownTab(subTabs, "PBコンポーネント", BreakdownTab.PhysBones, false);
            if (_selectedBreakdown == BreakdownTab.Materials || _selectedBreakdown == BreakdownTab.MaterialSlots)
            {
                var materialSubTabs = new VisualElement { style = { flexDirection = FlexDirection.Row, marginLeft = 12, marginRight = 12, marginBottom = 6 } };
                AddBreakdownTab(materialSubTabs, "マテリアル", BreakdownTab.Materials, true);
                AddBreakdownTab(materialSubTabs, "マテリアルスロット", BreakdownTab.MaterialSlots, true);
                _content.Add(materialSubTabs);
            }

            BuildBreakdownToolbar();

            var metric = GetBreakdownMetric();
            var summaryMetric = GetBreakdownSummaryMetric();
            if (metric == null || summaryMetric == null)
            {
                _content.Add(new HelpBox("この内訳は取得できません。", HelpBoxMessageType.Warning));
                return;
            }

            BuildBreakdownSummary(metric, summaryMetric);
            if (_selectedBreakdown == BreakdownTab.PhysBones) BuildPhysBoneUsagePanel(metric);
            BuildBreakdownColumnHeader();

            _listTab = _selectedBreakdown;
            _breakdownList = CreateBreakdownListView();
            _content.Add(_breakdownList);
            _breakdownEmpty = new VisualElement { style = { flexGrow = 1, alignItems = Align.Center, paddingTop = 24, display = DisplayStyle.None } };
            _content.Add(_breakdownEmpty);

            if (_selectedBreakdown == BreakdownTab.TextureMemory)
            {
                _compressionPanel = new VisualElement();
                _content.Add(_compressionPanel);
            }

            // 固定バー（サブタブ・検索・見出し等）は縮めず、縮むのは一覧だけにする。
            // 既定のflexShrink=1のままだと、圧縮パネル表示時にバーが潰れて重なる。
            foreach (var child in _content.Children())
            {
                if (child == _breakdownList || child == _breakdownEmpty) continue;
                child.style.flexShrink = 0;
            }
            _breakdownList.style.flexShrink = 1;
            _breakdownList.style.minHeight = 90;
            _breakdownEmpty.style.flexShrink = 1;
            _content.style.overflow = Overflow.Hidden;

            RefreshBreakdownList();
            RestoreBreakdownScroll();
            RefreshCompressionPanel();
        }

        private void AddBreakdownTab(VisualElement parent, string text, BreakdownTab tab, bool subTab)
        {
            var button = new Button(() =>
            {
                if (_selectedBreakdown == tab) return;
                SaveBreakdownScroll();
                _selectedBreakdown = tab;
                WriteEnum("BreakdownTab", tab);
                RebuildSelectedTab();
            }) { text = text };
            button.style.flexGrow = 1;
            button.style.height = subTab ? 24 : 32;
            button.style.marginLeft = 1;
            button.style.marginRight = 1;
            var selected = _selectedBreakdown == tab ||
                           (!subTab && tab == BreakdownTab.Materials && _selectedBreakdown == BreakdownTab.MaterialSlots);
            StyleTabButton(button, selected, subTab);
            parent.Add(button);
        }

        private void BuildBreakdownToolbar()
        {
            var listTools = new VisualElement { style = { flexDirection = FlexDirection.Column, marginLeft = 12, marginRight = 12, marginBottom = 6 } };
            var filterTools = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap, alignItems = Align.Center } };

            var search = new ToolbarSearchField();
            search.SetValueWithoutNotify(CurrentSearch);
            search.style.flexGrow = 1;
            search.style.flexShrink = 1;
            search.style.width = StyleKeyword.Auto;
            search.style.minWidth = 140;
            search.style.marginLeft = 0;
            search.tooltip = "名前・詳細・Hierarchyパス・Assetパス・Import設定を検索します。スペース区切りで複数語のAND検索ができます。";
            search.RegisterValueChangedCallback(evt =>
            {
                _searchByTab[_selectedBreakdown] = evt.newValue ?? string.Empty;
                _searchDebounce?.Pause();
                _searchDebounce = search.schedule.Execute(() =>
                {
                    RefreshBreakdownList();
                    RefreshCompressionPanel();
                });
                _searchDebounce.ExecuteLater(200);
            });
            filterTools.Add(search);

            var filterChoices = GetContributorFilterChoices();
            var activeFilter = GetActiveFilter(filterChoices);
            var filterField = new PopupField<string>(filterChoices, filterChoices.IndexOf(activeFilter));
            filterField.style.width = 170;
            filterField.tooltip = "タブ別の条件で一覧を絞り込みます。";
            filterField.SetEnabled(filterChoices.Count > 1);
            filterField.RegisterValueChangedCallback(evt =>
            {
                _filterByTab[_selectedBreakdown] = evt.newValue;
                RefreshBreakdownList();
                RefreshCompressionPanel();
            });
            filterTools.Add(filterField);
            listTools.Add(filterTools);

            var displayOptions = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    flexWrap = Wrap.Wrap,
                    alignItems = Align.Center,
                    marginTop = 5,
                    paddingLeft = 7,
                    paddingRight = 7,
                    paddingTop = 4,
                    paddingBottom = 4,
                    backgroundColor = AbiTheme.Band
                }
            };
            AbiTheme.SetRadius(displayOptions.style, 3);
            var displayLabel = new Label("表示オプション");
            displayLabel.style.fontSize = 10;
            displayLabel.style.opacity = 0.68f;
            displayLabel.style.marginRight = 7;
            displayOptions.Add(displayLabel);
            if (_selectedBreakdown == BreakdownTab.TextureMemory ||
                _selectedBreakdown == BreakdownTab.SkinnedMeshes ||
                _selectedBreakdown == BreakdownTab.Materials)
            {
                var groupByDirectoryValue = _selectedBreakdown == BreakdownTab.TextureMemory
                    ? _groupTexturesByDirectory
                    : _selectedBreakdown == BreakdownTab.SkinnedMeshes
                        ? _groupSkinnedMeshesByDirectory
                        : _groupMaterialsByDirectory;
                displayOptions.Add(CreateDisplayOptionToggle("親フォルダーでグループ化", groupByDirectoryValue,
                    "Assetの場所を3階層（Assets直下 / 親フォルダー / 一覧）でまとめて表示します。", value =>
                    {
                        if (_selectedBreakdown == BreakdownTab.TextureMemory)
                        {
                            _groupTexturesByDirectory = value;
                            WriteBool("GroupTextures", value);
                        }
                        else if (_selectedBreakdown == BreakdownTab.SkinnedMeshes)
                        {
                            _groupSkinnedMeshesByDirectory = value;
                            WriteBool("GroupSkinnedMeshes", value);
                        }
                        else
                        {
                            _groupMaterialsByDirectory = value;
                            WriteBool("GroupMaterials", value);
                        }
                        RefreshBreakdownList();
                    }));
                if (_selectedBreakdown == BreakdownTab.TextureMemory)
                {
                    displayOptions.Add(CreateDisplayOptionToggle("容量の割合を表示", _showTextureDirectoryPercentages,
                        "ディレクトリまたはTextureの推定容量が全Texture容量に占める割合を表示します。", value =>
                        {
                            _showTextureDirectoryPercentages = value;
                            WriteBool("ShowTexturePercentages", value);
                            RefreshBreakdownList();
                        }));
                }
            }
            _breakdownCountLabel = new Label();
            _breakdownCountLabel.style.marginLeft = StyleKeyword.Auto;
            _breakdownCountLabel.style.minWidth = 76;
            _breakdownCountLabel.style.unityTextAlign = TextAnchor.MiddleRight;
            _breakdownCountLabel.style.opacity = 0.65f;
            displayOptions.Add(_breakdownCountLabel);
            listTools.Add(displayOptions);
            _content.Add(listTools);
        }

        private static VisualElement CreateDisplayOptionToggle(string label, bool value, string tooltip, Action<bool> onChanged)
        {
            var chip = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    alignItems = Align.Center,
                    marginRight = 5,
                    marginTop = 1,
                    marginBottom = 1,
                    paddingLeft = 5,
                    paddingRight = 6,
                    paddingTop = 2,
                    paddingBottom = 2,
                    backgroundColor = AbiTheme.Chip
                }
            };
            AbiTheme.SetRadius(chip.style, 3);
            var toggle = new Toggle { text = label, value = value };
            toggle.tooltip = tooltip;
            toggle.style.marginLeft = 0;
            toggle.style.marginRight = 0;
            toggle.RegisterValueChangedCallback(evt => onChanged(evt.newValue));
            chip.Add(toggle);
            chip.tooltip = tooltip;
            return chip;
        }

        private List<string> GetContributorFilterChoices()
        {
            switch (_selectedBreakdown)
            {
                case BreakdownTab.TextureMemory:
                    return new List<string> { FilterAll, FilterTexture1Mb, FilterTexture4Mb, FilterUncompressed, FilterMaxSize4096, FilterNoOverride, FilterChecked };
                case BreakdownTab.SkinnedMeshes:
                    return new List<string> { FilterAll, FilterTriangles10K, FilterTriangles50K, FilterBlendShapes };
                case BreakdownTab.MaterialSlots:
                    return new List<string> { FilterAll, FilterSlots5, FilterSlots10, FilterMissingSlots };
                default:
                    return new List<string> { FilterAll };
            }
        }

        private string GetActiveFilter(List<string> choices = null)
        {
            choices = choices ?? GetContributorFilterChoices();
            var active = _filterByTab.TryGetValue(_selectedBreakdown, out var saved) ? saved : FilterAll;
            return choices.Contains(active) ? active : FilterAll;
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
                    backgroundColor = AbiTheme.Card
                }
            };

            var labels = new VisualElement { style = { flexGrow = 1, flexShrink = 1 } };
            var title = new Label(GetBreakdownTitle());
            title.style.fontSize = 15;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            labels.Add(title);

            var hint = new Label(GetBreakdownHint());
            hint.style.fontSize = 11;
            hint.style.opacity = 0.58f;
            hint.style.marginTop = 2;
            hint.style.whiteSpace = WhiteSpace.Normal;
            labels.Add(hint);
            summary.Add(labels);

            var totals = new VisualElement { style = { alignItems = Align.FlexEnd } };
            var total = new Label(summaryMetric.DisplayValue);
            total.style.fontSize = 17;
            total.style.unityFontStyleAndWeight = FontStyle.Bold;
            total.style.color = RatingColor(summaryMetric.Rank);
            total.tooltip = summaryMetric.DisplayName + " · " + RatingText(summaryMetric.Rank);
            totals.Add(total);

            var itemCount = _selectedBreakdown == BreakdownTab.Materials ? _report.Materials.Count : metric.Contributors.Count;
            var count = new Label(itemCount.ToString("N0") + (_selectedBreakdown == BreakdownTab.Materials ? " Material" : " 項目"));
            count.style.fontSize = 11;
            count.style.opacity = 0.58f;
            totals.Add(count);
            summary.Add(totals);
            _content.Add(summary);
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
                    backgroundColor = unusedCount > 0 ? AbiTheme.WarningPanel : AbiTheme.SuccessPanel
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
            ApplyEllipsis(detail);
            labels.Add(detail);
            panel.Add(labels);

            var filter = new Toggle { text = "候補のみ表示", value = _showUnusedPhysBonesOnly };
            filter.style.marginLeft = 10;
            filter.SetEnabled(unusedCount > 0);
            filter.RegisterValueChangedCallback(evt =>
            {
                _showUnusedPhysBonesOnly = evt.newValue;
                RefreshBreakdownList();
            });
            panel.Add(filter);
            _content.Add(panel);
        }

        private void BuildBreakdownColumnHeader()
        {
            var isTexture = _selectedBreakdown == BreakdownTab.TextureMemory;
            var header = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    alignItems = Align.Center,
                    minHeight = 26,
                    marginLeft = 12,
                    marginRight = 12,
                    marginTop = 7,
                    paddingLeft = 5,
                    paddingRight = 7,
                    backgroundColor = AbiTheme.ColumnHeader
                }
            };

            if (isTexture)
            {
                var cell = new VisualElement();
                cell.AddToClassList("abi-check-cell");
                _headerCheckToggle = new Toggle();
                MakeIgnorePointer(_headerCheckToggle);
                cell.Add(_headerCheckToggle);
                cell.tooltip = "表示中のテクスチャをすべてチェック / 解除";
                cell.RegisterCallback<PointerDownEvent>(evt =>
                {
                    if (evt.button != 0) return;
                    ToggleAllVisibleTextures();
                    evt.StopPropagation();
                });
                header.Add(cell);
                header.Add(new VisualElement { style = { width = 22, minWidth = 22 } });
            }
            else
            {
                header.Add(new VisualElement { style = { width = 40, minWidth = 40 } });
            }

            _nameSortHeader = CreateSortHeaderLabel(isTexture ? "テクスチャ" : "オブジェクト", SortColumn.Name);
            _nameSortHeader.style.flexGrow = 1;
            _nameSortHeader.style.flexShrink = 1;
            header.Add(_nameSortHeader);

            if (isTexture)
            {
                var importHeader = new Label("Import設定 (" + (ReportPlatform == TargetPlatform.Android ? "Android" : "PC") + ")");
                importHeader.style.width = ImportBadgeWidth;
                importHeader.style.fontSize = 10;
                importHeader.style.opacity = 0.58f;
                importHeader.tooltip = "スキャン中のPlatformで実際に使われるMax Size・Format・Crunch設定です。";
                header.Add(importHeader);
            }

            _valueSortHeader = CreateSortHeaderLabel(GetValueColumnName(), SortColumn.Value);
            _valueSortHeader.style.width = isTexture ? TextureValueWidth : DetailValueWidth;
            _valueSortHeader.style.unityTextAlign = TextAnchor.MiddleRight;
            header.Add(_valueSortHeader);
            _content.Add(header);
            UpdateSortHeaders();
        }

        private Label CreateSortHeaderLabel(string text, SortColumn column)
        {
            var label = new Label(text) { userData = text };
            label.AddToClassList("abi-sort-header");
            label.style.fontSize = 10;
            label.style.opacity = 0.7f;
            label.tooltip = "クリックで並べ替え（もう一度で逆順、3回目で既定順）";
            label.RegisterCallback<ClickEvent>(_ => CycleSort(column));
            return label;
        }

        private void CycleSort(SortColumn column)
        {
            var firstDirectionDescending = column == SortColumn.Value;
            if (_sortColumn != column)
            {
                _sortColumn = column;
                _sortDescending = firstDirectionDescending;
            }
            else if (_sortDescending == firstDirectionDescending)
            {
                _sortDescending = !_sortDescending;
            }
            else
            {
                _sortColumn = SortColumn.Default;
            }
            WriteEnum("SortColumn", _sortColumn);
            WriteBool("SortDescending", _sortDescending);
            UpdateSortHeaders();
            RefreshBreakdownList();
        }

        private void UpdateSortHeaders()
        {
            void Apply(Label label, SortColumn column)
            {
                if (label == null) return;
                var active = _sortColumn == column;
                label.text = (string)label.userData + (active ? (_sortDescending ? "  ▼" : "  ▲") : "");
                label.style.unityFontStyleAndWeight = active ? FontStyle.Bold : FontStyle.Normal;
                label.style.opacity = active ? 0.95f : 0.7f;
            }
            Apply(_nameSortHeader, SortColumn.Name);
            Apply(_valueSortHeader, SortColumn.Value);
        }

        private ListView CreateBreakdownListView()
        {
            var list = new ListView
            {
                itemsSource = _breakdownItems,
                makeItem = () => new VisualElement(),
                bindItem = BindBreakdownItem,
                selectionType = SelectionType.Single,
                virtualizationMethod = CollectionVirtualizationMethod.DynamicHeight,
                reorderable = false,
                showBorder = false,
                horizontalScrollingEnabled = false
            };
            list.style.flexGrow = 1;
            list.style.marginLeft = 12;
            list.style.marginRight = 12;
            list.style.marginBottom = 8;
            list.selectionChanged += OnBreakdownSelectionChanged;
            list.itemsChosen += OnBreakdownItemsChosen;
            list.RegisterCallback<KeyDownEvent>(OnBreakdownKeyDown);
            return list;
        }

        private void SaveBreakdownScroll()
        {
            var scroll = _breakdownList?.Q<ScrollView>();
            if (scroll != null) _scrollByTab[_listTab] = scroll.scrollOffset;
        }

        private void RestoreBreakdownScroll()
        {
            if (_breakdownList == null || !_scrollByTab.TryGetValue(_selectedBreakdown, out var offset)) return;
            var list = _breakdownList;
            void Apply()
            {
                var scroll = list.Q<ScrollView>();
                if (scroll != null) scroll.scrollOffset = offset;
            }
            list.schedule.Execute(Apply);
            list.schedule.Execute(Apply).ExecuteLater(50);
        }

        #endregion

        #region List data

        private void RefreshBreakdownList()
        {
            if (_breakdownList == null || _report == null) return;
            var metric = GetBreakdownMetric();
            if (metric == null) return;

            var selectedContributor = (_breakdownList.selectedItem as BreakdownItem)?.Contributor;
            if (_selectedBreakdown == BreakdownTab.TextureMemory)
                _totalTextureEstimateMegabytes = metric.Contributors.Sum(contributor => Math.Max(0d, contributor.Value));

            _visibleContributors = FilterAndSortContributors(metric).ToArray();
            var totalCount = _selectedBreakdown == BreakdownTab.Materials ? _report.Materials.Count : metric.Contributors.Count;
            if (_breakdownCountLabel != null)
                _breakdownCountLabel.text = _visibleContributors.Length.ToString("N0") + " / " + totalCount.ToString("N0") + " 件";

            BuildBreakdownItems(metric);

            var isEmpty = _visibleContributors.Length == 0;
            _breakdownList.style.display = isEmpty ? DisplayStyle.None : DisplayStyle.Flex;
            if (_breakdownEmpty != null)
            {
                _breakdownEmpty.style.display = isEmpty ? DisplayStyle.Flex : DisplayStyle.None;
                if (isEmpty) PopulateBreakdownEmpty();
            }

            _breakdownList.RefreshItems();
            var selectedIndex = selectedContributor == null
                ? -1
                : _breakdownItems.FindIndex(item => item.Kind == BreakdownItemKind.Contributor && item.Contributor == selectedContributor);
            _breakdownList.SetSelectionWithoutNotify(selectedIndex >= 0 ? new[] { selectedIndex } : Array.Empty<int>());
            UpdateHeaderCheckToggle();
        }

        private void PopulateBreakdownEmpty()
        {
            _breakdownEmpty.Clear();
            var hasFilters = _showUnusedPhysBonesOnly || GetActiveFilter() != FilterAll || !string.IsNullOrWhiteSpace(CurrentSearch);
            var message = new Label(hasFilters ? "条件に一致する項目はありません。" : "表示できる項目はありません。");
            message.style.opacity = 0.65f;
            message.style.marginBottom = 8;
            _breakdownEmpty.Add(message);
            if (!hasFilters) return;
            var clearFilters = new Button(() =>
            {
                _searchByTab[_selectedBreakdown] = string.Empty;
                _filterByTab[_selectedBreakdown] = FilterAll;
                _showUnusedPhysBonesOnly = false;
                RebuildSelectedTab();
            }) { text = "検索・フィルターを解除" };
            _breakdownEmpty.Add(clearFilters);
        }

        private IEnumerable<Contributor> FilterAndSortContributors(MetricResult metric)
        {
            IEnumerable<Contributor> contributors = _selectedBreakdown == BreakdownTab.Materials ? _report.Materials : metric.Contributors;
            if (_selectedBreakdown == BreakdownTab.PhysBones && _showUnusedPhysBonesOnly)
                contributors = contributors.Where(contributor => contributor.IsUnusedPhysBone);

            var search = CurrentSearch;
            if (!string.IsNullOrWhiteSpace(search))
            {
                var tokens = search.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                contributors = contributors.Where(contributor =>
                {
                    var haystack = GetSearchText(contributor);
                    return tokens.All(token => haystack.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0);
                });
            }

            switch (GetActiveFilter())
            {
                case FilterTexture1Mb: contributors = contributors.Where(contributor => contributor.Value >= 1d); break;
                case FilterTexture4Mb: contributors = contributors.Where(contributor => contributor.Value >= 4d); break;
                case FilterUncompressed: contributors = contributors.Where(contributor => GetTextureImportInfo(contributor)?.IsUncompressed == true); break;
                case FilterMaxSize4096: contributors = contributors.Where(contributor => (GetTextureImportInfo(contributor)?.MaxSize ?? 0) >= 4096); break;
                case FilterNoOverride: contributors = contributors.Where(contributor => GetTextureImportInfo(contributor) is TextureImportInfo info && !info.PlatformOverridden); break;
                case FilterChecked: contributors = contributors.Where(contributor => _selectedTexturePaths.Contains(GetAssetPath(contributor.Target))); break;
                case FilterBlendShapes: contributors = contributors.Where(contributor => contributor.SecondaryTarget is Mesh mesh && mesh.blendShapeCount > 0); break;
                case FilterTriangles10K: contributors = contributors.Where(contributor => contributor.Value >= 10000d); break;
                case FilterTriangles50K: contributors = contributors.Where(contributor => contributor.Value >= 50000d); break;
                case FilterSlots5: contributors = contributors.Where(contributor => contributor.Value >= 5d); break;
                case FilterSlots10: contributors = contributors.Where(contributor => contributor.Value >= 10d); break;
                case FilterMissingSlots: contributors = contributors.Where(contributor => contributor.Detail != null && contributor.Detail.IndexOf("Missing", StringComparison.OrdinalIgnoreCase) >= 0); break;
            }

            switch (_sortColumn)
            {
                case SortColumn.Name:
                    contributors = _sortDescending
                        ? contributors.OrderByDescending(contributor => contributor.Name, StringComparer.OrdinalIgnoreCase)
                        : contributors.OrderBy(contributor => contributor.Name, StringComparer.OrdinalIgnoreCase);
                    break;
                case SortColumn.Value:
                    contributors = _sortDescending
                        ? contributors.OrderByDescending(contributor => contributor.Value)
                        : contributors.OrderBy(contributor => contributor.Value);
                    break;
            }
            return contributors;
        }

        private string GetSearchText(Contributor contributor)
        {
            if (_searchTextCache.TryGetValue(contributor, out var cached)) return cached;
            var text = (contributor.Name ?? "") + " " + (contributor.Group ?? "") + " " + (contributor.Detail ?? "") + " " +
                       GetHierarchyPath(contributor.Target);
            if (contributor.SecondaryTarget != null && !IsSceneObject(contributor.SecondaryTarget))
                text += " " + GetAssetPath(contributor.SecondaryTarget);
            if (contributor.Target is Texture2D && GetTextureImportInfo(contributor) is TextureImportInfo info)
                text += " " + info.Summary + (info.IsUncompressed ? " 非圧縮 Uncompressed" : "");
            _searchTextCache[contributor] = text;
            return text;
        }

        private bool IsGroupExpanded(string key, bool defaultExpanded, bool searching) =>
            _groupExpanded.TryGetValue(key, out var expanded) ? expanded : searching || defaultExpanded;

        private void BuildBreakdownItems(MetricResult metric)
        {
            _breakdownItems.Clear();
            _contributorGroupKeys.Clear();
            var searching = !string.IsNullOrWhiteSpace(CurrentSearch);

            if (_selectedBreakdown == BreakdownTab.TextureMemory)
            {
                if (_groupTexturesByDirectory)
                {
                    AddAssetDirectoryItems(_visibleContributors, metric.Contributors.ToArray(), searching);
                }
                else
                {
                    for (var index = 0; index < _visibleContributors.Length; index++)
                        AddContributorItem(_visibleContributors[index], index, 0);
                }
                return;
            }

            var useAssetDirectoryGroups =
                (_selectedBreakdown == BreakdownTab.SkinnedMeshes && _groupSkinnedMeshesByDirectory) ||
                (_selectedBreakdown == BreakdownTab.Materials && _groupMaterialsByDirectory);
            if (useAssetDirectoryGroups)
            {
                AddAssetDirectoryItems(_visibleContributors, _visibleContributors, searching);
                return;
            }

            // スキンドメッシュは親フォルダーグループ化OFFのとき、Hierarchy階層で入れ子にせずフラットに表示する。
            if (_selectedBreakdown == BreakdownTab.SkinnedMeshes)
            {
                for (var index = 0; index < _visibleContributors.Length; index++)
                    AddContributorItem(_visibleContributors[index], index, 0);
                return;
            }

            foreach (var group in _visibleContributors.GroupBy(contributor => string.IsNullOrWhiteSpace(contributor.Group) ? "Other" : contributor.Group))
            {
                var unusedCount = group.Count(contributor => contributor.IsUnusedPhysBone);
                var missingCount = group.Count(contributor => contributor.Detail != null && contributor.Detail.IndexOf("Missing", StringComparison.OrdinalIgnoreCase) >= 0);
                var stateKey = _selectedBreakdown + "|" + group.Key;
                var expanded = IsGroupExpanded(stateKey, false, searching);
                _breakdownItems.Add(new BreakdownItem
                {
                    Kind = BreakdownItemKind.GroupHeader,
                    StateKey = stateKey,
                    Expanded = expanded,
                    Title = group.Key + " · " + group.Count().ToString("N0") + " 件 · " + FormatValueTotal(group) +
                            (missingCount > 0 ? " · Missing " + missingCount.ToString("N0") : "") +
                            (unusedCount > 0 ? " · 未使用候補 " + unusedCount.ToString("N0") : "")
                });
                var keys = new[] { stateKey };
                var rowIndex = 0;
                foreach (var contributor in group)
                {
                    _contributorGroupKeys[contributor] = keys;
                    if (expanded) AddContributorItem(contributor, rowIndex++, 1);
                }
            }
        }

        private void AddAssetDirectoryItems(Contributor[] contributors, Contributor[] aggregateContributors, bool searching)
        {
            var isTexture = _selectedBreakdown == BreakdownTab.TextureMemory;
            var showShare = isTexture && _showTextureDirectoryPercentages;
            var totalUsage = aggregateContributors.Sum(contributor => Math.Max(0d, contributor.Value));
            var rootGroups = contributors
                .GroupBy(GetAssetRootGroup, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => string.Equals(group.Key, "Assets以外", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase);
            foreach (var rootGroup in rootGroups)
            {
                var rootKey = _selectedBreakdown + "|asset-root|" + rootGroup.Key;
                var rootExpanded = IsGroupExpanded(rootKey, true, searching);
                var rootUsage = aggregateContributors.Where(contributor =>
                    string.Equals(GetAssetRootGroup(contributor), rootGroup.Key, StringComparison.OrdinalIgnoreCase)).ToArray();
                var rootTotal = rootUsage.Sum(contributor => Math.Max(0d, contributor.Value));
                _breakdownItems.Add(new BreakdownItem
                {
                    Kind = BreakdownItemKind.GroupHeader,
                    StateKey = rootKey,
                    Expanded = rootExpanded,
                    Spaced = string.Equals(rootGroup.Key, "Assets以外", StringComparison.OrdinalIgnoreCase),
                    Title = rootGroup.Key + " · " + rootGroup.Count().ToString("N0") + " 項目 · " + FormatValueTotal(rootUsage) +
                            (showShare ? " · " + FormatPercent(rootTotal, totalUsage) : ""),
                    ShareValue = showShare ? rootTotal : -1d
                });

                var parentGroups = rootGroup
                    .GroupBy(contributor => GetAssetParentSubgroup(contributor, rootGroup.Key), StringComparer.OrdinalIgnoreCase)
                    .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase);
                foreach (var parentGroup in parentGroups)
                {
                    var parentKey = rootKey + "|parent|" + parentGroup.Key;
                    var parentExpanded = IsGroupExpanded(parentKey, true, searching);
                    var parentUsage = rootUsage.Where(contributor =>
                        string.Equals(GetAssetParentSubgroup(contributor, rootGroup.Key), parentGroup.Key, StringComparison.OrdinalIgnoreCase)).ToArray();
                    var parentTotal = parentUsage.Sum(contributor => Math.Max(0d, contributor.Value));
                    if (rootExpanded)
                    {
                        _breakdownItems.Add(new BreakdownItem
                        {
                            Kind = BreakdownItemKind.GroupHeader,
                            Depth = 1,
                            StateKey = parentKey,
                            Expanded = parentExpanded,
                            Title = parentGroup.Key + " · " + parentGroup.Count().ToString("N0") + " 項目 · " + FormatValueTotal(parentUsage) +
                                    (showShare ? " · " + FormatPercent(parentTotal, totalUsage) : ""),
                            ShareValue = showShare ? parentTotal : -1d
                        });
                    }

                    var keys = new[] { rootKey, parentKey };
                    var rowIndex = 0;
                    foreach (var contributor in parentGroup)
                    {
                        _contributorGroupKeys[contributor] = keys;
                        if (rootExpanded && parentExpanded) AddContributorItem(contributor, rowIndex++, 2);
                    }
                }
            }
        }

        private void AddContributorItem(Contributor contributor, int rowIndex, int depth)
        {
            _breakdownItems.Add(new BreakdownItem
            {
                Kind = BreakdownItemKind.Contributor,
                Contributor = contributor,
                RowIndex = rowIndex,
                Depth = depth
            });

            if (!(contributor.Target is Texture2D texture)) return;
            var path = GetAssetPath(texture);
            if (!_expandedTextureUsages.Contains(path)) return;

            var usages = GetTextureUsages(texture);
            if (usages.Count == 0)
            {
                _breakdownItems.Add(new BreakdownItem
                {
                    Kind = BreakdownItemKind.Message,
                    Depth = depth + 1,
                    Title = "現在のAvatar配下のRenderer Materialから参照されていません。"
                });
                return;
            }

            if (_groupTexturesByDirectory)
            {
                foreach (var group in usages.GroupBy(GetTextureUsageParent).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var stateKey = "TextureUsage|" + path + "|" + group.Key;
                    var expanded = IsGroupExpanded(stateKey, true, false);
                    _breakdownItems.Add(new BreakdownItem
                    {
                        Kind = BreakdownItemKind.GroupHeader,
                        Depth = depth + 1,
                        StateKey = stateKey,
                        Expanded = expanded,
                        Title = group.Key + " · " + group.Count().ToString("N0") + " 件"
                    });
                    if (!expanded) continue;
                    var usageIndex = 0;
                    foreach (var usage in group)
                        _breakdownItems.Add(new BreakdownItem { Kind = BreakdownItemKind.TextureUsage, Usage = usage, RowIndex = usageIndex++, Depth = depth + 2 });
                }
            }
            else
            {
                for (var index = 0; index < usages.Count; index++)
                    _breakdownItems.Add(new BreakdownItem { Kind = BreakdownItemKind.TextureUsage, Usage = usages[index], RowIndex = index, Depth = depth + 1 });
            }
        }

        private string FormatValueTotal(IEnumerable<Contributor> contributors)
        {
            var list = contributors as ICollection<Contributor> ?? contributors.ToArray();
            var total = list.Sum(contributor => Math.Max(0d, contributor.Value));
            var unit = list.Select(contributor => contributor.Unit).FirstOrDefault(value => !string.IsNullOrEmpty(value)) ?? "";
            return _selectedBreakdown == BreakdownTab.TextureMemory
                ? total.ToString("N1") + " MB"
                : total.ToString("N0") + " " + unit;
        }

        private static string FormatPercent(double value, double total) =>
            (total <= 0d ? 0d : value / total * 100d).ToString("N1") + "%";

        private string FormatContributorValue(Contributor contributor)
        {
            return string.Equals(contributor.Unit, "MB", StringComparison.OrdinalIgnoreCase) || string.Equals(contributor.Unit, "m", StringComparison.Ordinal)
                ? contributor.Value.ToString("0.00") + " " + contributor.Unit
                : contributor.Value.ToString("N0") + " " + contributor.Unit;
        }

        #endregion

        #region Item binding

        private void BindBreakdownItem(VisualElement element, int index)
        {
            element.Clear();
            if (index < 0 || index >= _breakdownItems.Count) return;
            var item = _breakdownItems[index];
            switch (item.Kind)
            {
                case BreakdownItemKind.GroupHeader:
                    element.Add(CreateGroupHeaderElement(item));
                    break;
                case BreakdownItemKind.Contributor:
                    element.Add(_listTab == BreakdownTab.TextureMemory ? CreateTextureRow(item, index) : CreateDetailRow(item));
                    break;
                case BreakdownItemKind.TextureUsage:
                    element.Add(CreateTextureUsageRow(item));
                    break;
                default:
                    var message = new Label(item.Title);
                    message.style.paddingLeft = 8 + item.Depth * 14;
                    message.style.paddingTop = 5;
                    message.style.paddingBottom = 5;
                    message.style.opacity = 0.65f;
                    element.Add(message);
                    break;
            }
        }

        private VisualElement CreateGroupHeaderElement(BreakdownItem item)
        {
            var container = new VisualElement();
            if (item.Spaced) container.style.marginTop = 12;
            var header = new VisualElement();
            header.AddToClassList("abi-group-header");
            header.style.paddingLeft = 4 + item.Depth * 14;
            var arrow = new Label(item.Expanded ? "▼" : "▶") { style = { width = 14, fontSize = 8, opacity = 0.7f, unityTextAlign = TextAnchor.MiddleCenter } };
            header.Add(arrow);
            var title = new Label(item.Title) { style = { flexGrow = 1, flexShrink = 1 } };
            ApplyEllipsis(title);
            title.tooltip = item.Title;
            if (item.Depth == 0) title.style.unityFontStyleAndWeight = FontStyle.Bold;
            header.Add(title);
            header.RegisterCallback<ClickEvent>(evt =>
            {
                ToggleGroup(item.StateKey, item.Expanded);
                evt.StopPropagation();
            });
            container.Add(header);
            if (item.ShareValue >= 0d)
            {
                var bar = CreateShareBar(item.ShareValue, _totalTextureEstimateMegabytes);
                bar.style.marginLeft = 18 + item.Depth * 14;
                container.Add(bar);
            }
            return container;
        }

        private void ToggleGroup(string stateKey, bool currentlyExpanded)
        {
            if (string.IsNullOrEmpty(stateKey)) return;
            _groupExpanded[stateKey] = !currentlyExpanded;
            RefreshBreakdownList();
        }

        private VisualElement CreateContributorRowBase(Contributor contributor, int rowIndex, int depth)
        {
            var row = new VisualElement();
            row.AddToClassList("abi-row");
            row.AddToClassList(contributor.IsUnusedPhysBone ? "abi-row--warning" : rowIndex % 2 == 0 ? "abi-row--even" : "abi-row--odd");
            row.style.paddingLeft = 5 + depth * 14;
            row.style.paddingRight = 7;
            var target = contributor.Target;
            if (target == null)
            {
                row.style.opacity = 0.5f;
                row.tooltip = "対象がMissingまたは削除済みのため移動できません。";
            }
            row.RegisterCallback<ClickEvent>(evt =>
            {
                // 選択済みの行をもう一度クリックした場合もPingする。
                if (target != null && evt.button == 0 && Selection.activeObject == target) EditorGUIUtility.PingObject(target);
            });
            row.AddManipulator(new ContextualMenuManipulator(evt => PopulateContributorMenu(evt.menu, contributor)));
            return row;
        }

        private VisualElement CreateTextureRow(BreakdownItem item, int itemIndex)
        {
            var contributor = item.Contributor;
            var container = new VisualElement();
            var row = CreateContributorRowBase(contributor, item.RowIndex, item.Depth);
            row.style.minHeight = 34;

            var assetPath = GetAssetPath(contributor.Target);
            var editable = IsEditableTexture(contributor.Target, assetPath);
            var isChecked = editable && _selectedTexturePaths.Contains(assetPath);
            if (isChecked) row.AddToClassList("abi-row--checked");

            var cell = new VisualElement();
            cell.AddToClassList("abi-check-cell");
            var toggle = new Toggle { value = isChecked };
            MakeIgnorePointer(toggle);
            toggle.SetEnabled(editable);
            cell.Add(toggle);
            cell.tooltip = editable
                ? "クリックで圧縮対象にチェック\nShift+クリック: 範囲をまとめてチェック\nドラッグ: なぞった行を連続でチェック\nSpaceキー: 選択行をチェック"
                : "このTextureはインポート設定を変更できません。";
            if (editable)
            {
                void Apply(bool state)
                {
                    SetTextureChecked(assetPath, state);
                    toggle.SetValueWithoutNotify(state);
                    row.EnableInClassList("abi-row--checked", state);
                }

                cell.RegisterCallback<PointerDownEvent>(evt =>
                {
                    if (evt.button != 0) return;
                    var newState = !_selectedTexturePaths.Contains(assetPath);
                    if (evt.shiftKey && _checkAnchorIndex >= 0 && _checkAnchorIndex != itemIndex)
                    {
                        SetTextureRangeChecked(_checkAnchorIndex, itemIndex, newState);
                    }
                    else
                    {
                        Apply(newState);
                        OnTextureChecksChanged(false);
                    }
                    _checkAnchorIndex = itemIndex;
                    _checkPaintState = newState;
                    evt.StopPropagation();
                });
                cell.RegisterCallback<PointerUpEvent>(evt => evt.StopPropagation());
                cell.RegisterCallback<ClickEvent>(evt => evt.StopPropagation());
                cell.RegisterCallback<PointerEnterEvent>(evt =>
                {
                    if (!_checkPaintState.HasValue || (evt.pressedButtons & 1) == 0) return;
                    if (_selectedTexturePaths.Contains(assetPath) == _checkPaintState.Value) return;
                    Apply(_checkPaintState.Value);
                    OnTextureChecksChanged(false);
                });
            }
            row.Add(cell);

            var thumbnail = CreateThumbnail(contributor.Target, 16);
            thumbnail.style.marginRight = 6;
            row.Add(thumbnail);

            var nameCell = new VisualElement();
            nameCell.AddToClassList("abi-row-cell");
            var name = new Label(contributor.Name) { style = { flexGrow = 1, flexShrink = 1, unityTextAlign = TextAnchor.MiddleLeft } };
            ApplyEllipsis(name);
            name.tooltip = contributor.Name + "\n" + GetTargetContext(contributor.Target) + " · " +
                           (string.IsNullOrEmpty(assetPath) ? "Asset path unavailable" : assetPath) +
                           (string.IsNullOrEmpty(contributor.Detail) ? "" : "\n" + contributor.Detail) + "\n右クリックでメニュー";
            nameCell.Add(name);

            if (contributor.Target is Texture2D texture)
            {
                var usageExpanded = _expandedTextureUsages.Contains(assetPath);
                var actions = new VisualElement();
                actions.AddToClassList("abi-row-actions");
                actions.Add(CreateIconButton(LoadEditorIcon("Material Icon"), "Mat", "参照中のMaterialをProjectで選択・Ping",
                    () => SelectTextureMaterials(texture)));
                var usageButton = CreateIconButton(LoadEditorIcon("UnityEditor.SceneHierarchyWindow"), "参照",
                    usageExpanded ? "参照元オブジェクトの一覧を閉じる" : "このTextureを参照しているオブジェクトを一覧表示",
                    () => ToggleTextureUsage(assetPath));
                if (usageExpanded)
                {
                    usageButton.AddToClassList("abi-icon-button--active");
                    row.AddToClassList("abi-row--actions-pinned");
                }
                actions.Add(usageButton);
                nameCell.Add(actions);
            }
            row.Add(nameCell);

            var info = GetTextureImportInfo(assetPath);
            var badge = new Label(info == null ? "—" : info.Summary);
            badge.style.width = ImportBadgeWidth;
            badge.style.minWidth = 60;
            badge.style.flexShrink = 1;
            badge.style.fontSize = 10;
            badge.style.opacity = 0.85f;
            badge.style.marginLeft = 6;
            ApplyEllipsis(badge);
            if (info != null && info.IsUncompressed) badge.style.color = AbiTheme.Warning;
            badge.tooltip = BuildImportTooltip(info);
            row.Add(badge);

            var showPercent = !_groupTexturesByDirectory && _showTextureDirectoryPercentages && _totalTextureEstimateMegabytes > 0d;
            var valueText = contributor.Value.ToString("0.00") + " " + contributor.Unit;
            if (showPercent) valueText += " · " + FormatPercent(Math.Max(0d, contributor.Value), _totalTextureEstimateMegabytes);
            var value = new Label(valueText);
            value.style.width = TextureValueWidth;
            value.style.minWidth = TextureValueWidth;
            value.style.unityTextAlign = TextAnchor.MiddleRight;
            value.style.opacity = 0.82f;
            row.Add(value);

            container.Add(row);
            if (showPercent)
            {
                var bar = CreateShareBar(Math.Max(0d, contributor.Value), _totalTextureEstimateMegabytes);
                bar.style.marginLeft = 40 + item.Depth * 14;
                container.Add(bar);
            }
            return container;
        }

        private VisualElement CreateDetailRow(BreakdownItem item)
        {
            var contributor = item.Contributor;
            var row = CreateContributorRowBase(contributor, item.RowIndex, item.Depth);
            row.style.minHeight = 44;

            var previewTarget = _listTab == BreakdownTab.Materials
                ? contributor.Target
                : contributor.SecondaryTarget != null ? contributor.SecondaryTarget : contributor.Target;
            var showAssetPreview =
                (_listTab == BreakdownTab.Materials && previewTarget is Material) ||
                (_listTab == BreakdownTab.MaterialSlots && previewTarget is Material) ||
                (_listTab == BreakdownTab.SkinnedMeshes && previewTarget is Mesh);
            var thumbnail = showAssetPreview ? CreateAssetPreviewThumbnail(previewTarget, 30) : CreateThumbnail(previewTarget, 30);
            thumbnail.style.marginRight = 8;
            row.Add(thumbnail);

            var nameCell = new VisualElement();
            nameCell.AddToClassList("abi-row-cell");
            var labels = new VisualElement { style = { flexGrow = 1, flexShrink = 1 } };
            var name = new Label(contributor.Name);
            ApplyEllipsis(name);
            name.tooltip = contributor.Name + "\n" + GetTargetContext(contributor.Target) + " · " + GetHierarchyPath(contributor.Target) + "\n右クリックでメニュー";
            labels.Add(name);
            if (!string.IsNullOrEmpty(contributor.Detail))
            {
                var detail = new Label(contributor.Detail);
                detail.style.fontSize = 10;
                detail.style.opacity = 0.58f;
                ApplyEllipsis(detail);
                detail.tooltip = detail.text;
                labels.Add(detail);
            }
            nameCell.Add(labels);

            if (contributor.SecondaryTarget != null && contributor.SecondaryTarget != contributor.Target)
            {
                var relatedTarget = contributor.SecondaryTarget;
                var allRelated = contributor.RelatedTargets == null
                    ? Array.Empty<UnityEngine.Object>()
                    : contributor.RelatedTargets.Where(related => related != null).ToArray();
                var selectAll = allRelated.Length > 1;
                var actions = new VisualElement();
                actions.AddToClassList("abi-row-actions");
                actions.Add(CreateIconButton(AssetPreview.GetMiniThumbnail(relatedTarget), GetRelatedLabel(relatedTarget),
                    selectAll
                        ? "このMaterialを参照している " + allRelated.Length.ToString("N0") + " 個のRendererをすべて選択"
                        : GetRelatedLabel(relatedTarget) + "を選択: " + GetTargetContext(relatedTarget) + " · " + GetHierarchyPath(relatedTarget),
                    () =>
                    {
                        if (selectAll) SelectAndPing(allRelated);
                        else SelectAndPing(relatedTarget);
                    }));
                nameCell.Add(actions);
            }
            row.Add(nameCell);

            if (contributor.IsUnusedPhysBone)
            {
                var warning = new Label("未使用候補");
                warning.style.width = 72;
                warning.style.unityTextAlign = TextAnchor.MiddleCenter;
                warning.style.color = AbiTheme.Warning;
                warning.style.unityFontStyleAndWeight = FontStyle.Bold;
                warning.tooltip = "対象ボーンがどのSkinnedMeshRendererのbonesにも登録されていません。";
                row.Add(warning);
            }

            var value = new Label(FormatContributorValue(contributor));
            value.style.width = DetailValueWidth;
            value.style.minWidth = DetailValueWidth;
            value.style.unityTextAlign = TextAnchor.MiddleRight;
            value.style.opacity = 0.82f;
            row.Add(value);
            return row;
        }

        private VisualElement CreateTextureUsageRow(BreakdownItem item)
        {
            var usage = item.Usage;
            var row = new VisualElement();
            row.AddToClassList("abi-row");
            row.AddToClassList(item.RowIndex % 2 == 0 ? "abi-row--even" : "abi-row--odd");
            row.style.minHeight = 26;
            row.style.paddingLeft = 8 + item.Depth * 14;
            row.style.paddingRight = 6;
            var rendererObject = usage.Renderer == null ? null : usage.Renderer.gameObject;
            row.Add(CreateThumbnail(rendererObject, 16));
            var path = new Label(rendererObject == null ? "Missing" : GetHierarchyPath(rendererObject))
            {
                style = { flexGrow = 1, flexShrink = 1, marginLeft = 6, unityTextAlign = TextAnchor.MiddleLeft }
            };
            ApplyEllipsis(path);
            row.Add(path);
            var slot = new Label((usage.Material == null ? "Missing" : usage.Material.name) + " · slot " + usage.Slot + " · " + usage.Property);
            slot.style.fontSize = 10;
            slot.style.opacity = 0.55f;
            slot.style.maxWidth = 220;
            slot.style.marginLeft = 6;
            ApplyEllipsis(slot);
            row.Add(slot);
            row.tooltip = "クリックでHierarchyのRendererを選択・Ping\nMaterial: " + (usage.Material == null ? "Missing" : usage.Material.name) +
                          " / Slot " + usage.Slot + " / " + usage.Property;
            row.AddManipulator(new ContextualMenuManipulator(evt =>
            {
                evt.menu.AppendAction("Rendererを選択", _ => SelectAndPing(rendererObject),
                    rendererObject != null ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
                evt.menu.AppendAction("Materialを選択", _ => SelectAndPing(usage.Material),
                    usage.Material != null ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
                evt.menu.AppendAction("Hierarchyパスをコピー", _ => CopyToClipboard(this, GetHierarchyPath(rendererObject), "パス"),
                    rendererObject != null ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            }));
            return row;
        }

        private Button CreateIconButton(Texture icon, string fallbackText, string tooltip, Action action)
        {
            var button = new Button(action);
            button.AddToClassList("abi-icon-button");
            button.tooltip = tooltip;
            if (icon != null)
            {
                var image = new Image { image = icon, scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
                image.style.width = 16;
                image.style.height = 16;
                button.Add(image);
            }
            else
            {
                button.text = fallbackText;
                button.style.width = StyleKeyword.Auto;
                button.style.fontSize = 10;
            }
            button.RegisterCallback<ClickEvent>(evt => evt.StopPropagation());
            button.RegisterCallback<PointerDownEvent>(evt => evt.StopPropagation());
            return button;
        }

        private static string GetRelatedLabel(UnityEngine.Object target)
        {
            if (target is Material) return "Material";
            if (target is Mesh) return "Mesh";
            if (target is GameObject) return "Renderer";
            return target == null ? "対象" : target.GetType().Name;
        }

        private string BuildImportTooltip(TextureImportInfo info)
        {
            if (info == null) return "TextureImporterがないため、Import設定を表示できません。";
            var platformLabel = info.Platform == TargetPlatform.Android ? "Android" : "PC";
            return platformLabel + "の実効設定（" + (info.PlatformOverridden ? "Platform Override" : "Default設定を使用") + "）\n" +
                   "Max Size: " + info.MaxSize + "\n" +
                   "Format: " + TextureMemoryEstimator.FormatLabel(info.ResolvedFormat) +
                   (info.Format == TextureImporterFormat.Automatic ? "（Automatic）" : "") + "\n" +
                   "Compression: " + info.Compression + (info.Crunch ? " · Crunch" : "") + "\n" +
                   "元画像: " + info.SourceWidth + " × " + info.SourceHeight + (info.Mipmaps ? " · Mipmapあり" : "") + "\n" +
                   "推定メモリ: " + (info.EstimatedBytes / 1024d / 1024d).ToString("N2") + " MB（概算）";
        }

        #endregion

        #region Interaction

        private void OnBreakdownSelectionChanged(IEnumerable<object> selection)
        {
            var item = selection?.OfType<BreakdownItem>().FirstOrDefault();
            if (item == null) return;
            if (item.Kind == BreakdownItemKind.Contributor && item.Contributor.Target != null)
                SelectAndPing(item.Contributor.Target);
            else if (item.Kind == BreakdownItemKind.TextureUsage && item.Usage.Renderer != null)
                SelectAndPing(item.Usage.Renderer.gameObject);
        }

        private void OnBreakdownItemsChosen(IEnumerable<object> chosen)
        {
            var item = chosen?.OfType<BreakdownItem>().FirstOrDefault();
            if (item == null) return;
            if (item.Kind == BreakdownItemKind.GroupHeader)
            {
                ToggleGroup(item.StateKey, item.Expanded);
                return;
            }
            var target = item.Kind == BreakdownItemKind.TextureUsage
                ? (item.Usage.Renderer == null ? null : item.Usage.Renderer.gameObject)
                : item.Contributor?.Target;
            if (target == null) return;
            SelectAndPing(target);
            if (IsSceneObject(target) && SceneView.lastActiveSceneView != null) SceneView.lastActiveSceneView.FrameSelected();
        }

        private void OnBreakdownKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode != KeyCode.Space || _breakdownList == null) return;
            if (!(_breakdownList.selectedItem is BreakdownItem item) || item.Kind != BreakdownItemKind.Contributor) return;
            var path = GetAssetPath(item.Contributor.Target);
            if (!IsEditableTexture(item.Contributor.Target, path)) return;
            SetTextureChecked(path, !_selectedTexturePaths.Contains(path));
            _checkAnchorIndex = _breakdownList.selectedIndex;
            OnTextureChecksChanged(true);
            evt.StopPropagation();
        }

        private static bool IsEditableTexture(UnityEngine.Object target, string assetPath) =>
            target is Texture2D && !string.IsNullOrEmpty(assetPath) && AssetImporter.GetAtPath(assetPath) is TextureImporter;

        private void SetTextureChecked(string assetPath, bool state)
        {
            if (string.IsNullOrEmpty(assetPath)) return;
            if (state) _selectedTexturePaths.Add(assetPath);
            else _selectedTexturePaths.Remove(assetPath);
        }

        private void SetTextureRangeChecked(int fromIndex, int toIndex, bool state)
        {
            var start = Math.Max(0, Math.Min(fromIndex, toIndex));
            var end = Math.Min(_breakdownItems.Count - 1, Math.Max(fromIndex, toIndex));
            for (var index = start; index <= end; index++)
            {
                var item = _breakdownItems[index];
                if (item.Kind != BreakdownItemKind.Contributor) continue;
                var path = GetAssetPath(item.Contributor.Target);
                if (IsEditableTexture(item.Contributor.Target, path)) SetTextureChecked(path, state);
            }
            OnTextureChecksChanged(true);
        }

        private void ToggleAllVisibleTextures()
        {
            var paths = GetVisibleEditableTexturePaths();
            if (paths.Count == 0) return;
            var allChecked = paths.All(_selectedTexturePaths.Contains);
            foreach (var path in paths) SetTextureChecked(path, !allChecked);
            OnTextureChecksChanged(true);
        }

        private List<string> GetVisibleEditableTexturePaths()
        {
            return _visibleContributors
                .Select(contributor => (contributor, path: GetAssetPath(contributor.Target)))
                .Where(pair => IsEditableTexture(pair.contributor.Target, pair.path))
                .Select(pair => pair.path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private void OnTextureChecksChanged(bool refreshRows)
        {
            if (refreshRows) _breakdownList?.RefreshItems();
            UpdateHeaderCheckToggle();
            ScheduleCompressionPanelRefresh();
        }

        private void UpdateHeaderCheckToggle()
        {
            if (_headerCheckToggle == null) return;
            var paths = GetVisibleEditableTexturePaths();
            var checkedCount = paths.Count(_selectedTexturePaths.Contains);
            _headerCheckToggle.SetValueWithoutNotify(paths.Count > 0 && checkedCount == paths.Count);
            _headerCheckToggle.showMixedValue = checkedCount > 0 && checkedCount < paths.Count;
            _headerCheckToggle.SetEnabled(paths.Count > 0);
        }

        private void ToggleTextureUsage(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return;
            if (!_expandedTextureUsages.Remove(assetPath)) _expandedTextureUsages.Add(assetPath);
            RefreshBreakdownList();
        }

        private void SelectTextureMaterials(Texture2D texture)
        {
            var materials = GetTextureUsages(texture).Select(use => use.Material)
                .Where(material => material != null && !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(material)))
                .Distinct().Cast<UnityEngine.Object>().ToArray();
            if (materials.Length == 0)
            {
                ShowNotification(new GUIContent("Project内の参照Materialがありません。"));
                return;
            }
            EditorUtility.FocusProjectWindow();
            SelectAndPing(materials);
        }

        private void PopulateContributorMenu(DropdownMenu menu, Contributor contributor)
        {
            var target = contributor.Target;
            var hasTarget = target != null;
            DropdownMenuAction.Status Enabled(bool enabled) => enabled ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled;

            menu.AppendAction(IsSceneObject(target) ? "Hierarchyで選択" : "Projectで選択", _ => SelectAndPing(target), Enabled(hasTarget));
            menu.AppendAction("Inspectorで開く", _ => EditorUtility.OpenPropertyEditor(target), Enabled(hasTarget));
            if (IsSceneObject(target))
            {
                menu.AppendAction("Sceneビューでフォーカス", _ =>
                {
                    SelectAndPing(target);
                    if (SceneView.lastActiveSceneView != null) SceneView.lastActiveSceneView.FrameSelected();
                }, Enabled(SceneView.lastActiveSceneView != null));
            }
            menu.AppendSeparator();
            menu.AppendAction("名前をコピー", _ => CopyToClipboard(this, contributor.Name, "名前"));
            menu.AppendAction(IsSceneObject(target) ? "Hierarchyパスをコピー" : "Assetパスをコピー",
                _ => CopyToClipboard(this, GetHierarchyPath(target), "パス"), Enabled(hasTarget));
            var assetPath = GetAssetPath(target);
            if (!string.IsNullOrEmpty(assetPath))
                menu.AppendAction("エクスプローラーで表示", _ => EditorUtility.RevealInFinder(assetPath));

            if (contributor.SecondaryTarget != null && contributor.SecondaryTarget != target && !(target is Texture2D))
            {
                var related = contributor.SecondaryTarget;
                menu.AppendSeparator();
                var allRelated = contributor.RelatedTargets == null
                    ? Array.Empty<UnityEngine.Object>()
                    : contributor.RelatedTargets.Where(item => item != null).ToArray();
                if (allRelated.Length > 1)
                    menu.AppendAction("参照しているRendererをすべて選択 (" + allRelated.Length.ToString("N0") + ")", _ => SelectAndPing(allRelated));
                else
                    menu.AppendAction(GetRelatedLabel(related) + "を選択", _ => SelectAndPing(related));
                if (!IsSceneObject(related) && !string.IsNullOrEmpty(GetAssetPath(related)))
                    menu.AppendAction(GetRelatedLabel(related) + "のAssetパスをコピー", _ => CopyToClipboard(this, GetAssetPath(related), "パス"));
            }

            if (target is Texture2D texture)
            {
                var editable = IsEditableTexture(texture, assetPath);
                var isChecked = editable && _selectedTexturePaths.Contains(assetPath);
                menu.AppendSeparator();
                menu.AppendAction(isChecked ? "圧縮対象のチェックを外す" : "圧縮対象にチェック", _ =>
                {
                    SetTextureChecked(assetPath, !isChecked);
                    OnTextureChecksChanged(true);
                }, Enabled(editable));
                menu.AppendAction("参照中のMaterialを選択", _ => SelectTextureMaterials(texture));
                menu.AppendAction(_expandedTextureUsages.Contains(assetPath) ? "参照元オブジェクトを閉じる" : "参照元オブジェクトを表示",
                    _ => ToggleTextureUsage(assetPath));
            }
        }

        /// <summary>
        /// Hierarchy / Projectで選択されたObjectに対応する行を一覧で強調する。
        /// </summary>
        private void RevealInBreakdown(UnityEngine.Object selected)
        {
            if (_breakdownList == null || _report == null || selected == null) return;

            bool Matches(Contributor contributor) =>
                contributor != null &&
                (contributor.Target == selected ||
                 contributor.SecondaryTarget == selected ||
                 (contributor.Target is Component component && component.gameObject == selected));

            int FindIndex() => _breakdownItems.FindIndex(item => item.Kind == BreakdownItemKind.Contributor && Matches(item.Contributor));

            var index = FindIndex();
            if (index < 0)
            {
                var hidden = _visibleContributors.FirstOrDefault(Matches);
                if (hidden == null || !_contributorGroupKeys.TryGetValue(hidden, out var keys)) return;
                foreach (var key in keys) _groupExpanded[key] = true;
                RefreshBreakdownList();
                index = FindIndex();
                if (index < 0) return;
            }
            _breakdownList.SetSelectionWithoutNotify(new[] { index });
            _breakdownList.ScrollToItem(index);
        }

        #endregion

        #region Texture usage

        private List<TextureUsageRef> GetTextureUsages(Texture2D texture)
        {
            if (texture == null) return new List<TextureUsageRef>();
            if (_usageCache.TryGetValue(texture, out var cached)) return cached;
            var uses = new List<TextureUsageRef>();
            if (_currentAvatar != null)
            {
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
                            if (material.GetTexture(property) == texture)
                                uses.Add(new TextureUsageRef { Renderer = renderer, Material = material, Slot = slot, Property = property });
                        }
                    }
                }
            }
            _usageCache[texture] = uses;
            return uses;
        }

        private static string GetTextureUsageParent(TextureUsageRef use)
        {
            var parent = use.Renderer == null ? null : use.Renderer.transform.parent;
            return parent == null ? "(ルート)" : GetHierarchyPath(parent.gameObject);
        }

        #endregion

        #region Directory grouping

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

        #endregion

        #region Shared elements

        private static VisualElement CreateShareBar(double share, double total)
        {
            var percentage = total <= 0d ? 0f : Mathf.Clamp01((float)(share / total));
            var track = new VisualElement
            {
                style =
                {
                    height = 4,
                    marginLeft = 6,
                    marginRight = 8,
                    marginTop = 1,
                    marginBottom = 3,
                    backgroundColor = AbiTheme.Track
                }
            };
            var fill = new VisualElement
            {
                style =
                {
                    width = Length.Percent(percentage * 100f),
                    height = 4,
                    backgroundColor = AbiTheme.BarFill
                }
            };
            track.tooltip = "推定容量: " + share.ToString("N2") + " MB / 全Texture: " + total.ToString("N2") + " MB";
            track.Add(fill);
            return track;
        }

        private static void ApplyEllipsis(Label label)
        {
            label.style.whiteSpace = WhiteSpace.NoWrap;
            label.style.overflow = Overflow.Hidden;
            label.style.textOverflow = TextOverflow.Ellipsis;
        }

        private static void MakeIgnorePointer(VisualElement element)
        {
            element.pickingMode = PickingMode.Ignore;
            element.Query<VisualElement>().ForEach(child => child.pickingMode = PickingMode.Ignore);
        }

        private static Image CreateThumbnail(UnityEngine.Object target, float size)
        {
            var thumbnail = new Image
            {
                image = target == null ? null : AssetPreview.GetMiniThumbnail(target),
                scaleMode = ScaleMode.ScaleToFit,
                pickingMode = PickingMode.Ignore
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
                return thumbnail;
            }

            var startedAt = EditorApplication.timeSinceStartup;
            EditorApplication.CallbackFunction updatePreview = null;
            updatePreview = () =>
            {
                if (thumbnail.panel == null || target == null || EditorApplication.timeSinceStartup - startedAt > 5d)
                {
                    EditorApplication.update -= updatePreview;
                    return;
                }

                var loadedPreview = AssetPreview.GetAssetPreview(target);
                if (loadedPreview == null) return;
                thumbnail.image = loadedPreview;
                thumbnail.MarkDirtyRepaint();
                EditorApplication.update -= updatePreview;
            };
            EditorApplication.update += updatePreview;
            return thumbnail;
        }

        #endregion

        #region Tab metadata

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
                case BreakdownTab.TextureMemory: return "クリックでProjectのTextureを表示 · 左端をクリック/ドラッグで圧縮対象をチェック · 右クリックでメニュー";
                case BreakdownTab.SkinnedMeshes: return "クリックでHierarchyのオブジェクトを選択 · ダブルクリックでSceneビューにフォーカス · 右クリックでメニュー";
                case BreakdownTab.Materials: return "Avatar内で使用されているMaterial一覧 · クリックでProjectのMaterialを選択 · 右クリックでメニュー";
                case BreakdownTab.MaterialSlots: return "クリックでHierarchyのオブジェクトを選択 · ダブルクリックでSceneビューにフォーカス · 右クリックでメニュー";
                default: return "推定Collision Check数とメッシュ未使用候補を確認 · クリックでHierarchyを選択 · 右クリックでメニュー";
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
            if (_report == null) return null;
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

        #endregion
    }
}
