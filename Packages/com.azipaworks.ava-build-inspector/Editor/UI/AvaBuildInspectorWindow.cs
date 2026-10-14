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
    internal sealed partial class AvaBuildInspectorWindow : EditorWindow
    {
        private const string AvatarDropPrompt = "VRCAvatarDescriptorのあるAvatarを指定してください。";
        private const string EditorRoot = "Packages/com.azipaworks.ava-build-inspector/Editor/";
        private const string AvatarDropIconPath = EditorRoot + "Resources/AvatarDropIcon.png";
        private const string AppIconPath = EditorRoot + "Resources/AvaBuildInspectorIcon.png";
        private const string StyleSheetPath = EditorRoot + "UI/AvaBuildInspector.uss";
        private const string PrefPrefix = "AvaBuildInspector.";
        private const long AutoRescanDelayMilliseconds = 1000;
        private const double ScanFrameBudgetSeconds = 0.03;
        private static Texture2D _avatarDropIcon;
        private static Texture2D _appIcon;

        private enum MainTab { ScanResults, Breakdown, BuildHistory }

        private ObjectField _avatarField;
        private Button _avatarPickerButton;
        private EnumField _platformField;
        private Button _rescanButton;
        private Button _scanResultsTab;
        private Button _breakdownTab;
        private Button _buildHistoryTab;
        private VisualElement _statusArea;
        private VisualElement _progressFill;
        private Label _progressLabel;
        private VisualElement _content;

        [SerializeField] private GameObject _currentAvatar;
        private MainTab _selectedTab;
        private AvatarReport _report;
        private int _scanRequest;
        private AvatarAnalysisOperation _scanOperation;
        private int _scanOperationRequest;
        private string _scanMessage;
        private bool _isReportStale;
        private string _staleReason;
        private string _scannedHierarchySignature;
        private bool _autoRescan;
        private IVisualElementScheduledItem _autoRescanItem;
        private IVisualElementScheduledItem _avatarCountItem;
        private UnityEngine.Object _lastWindowSelection;

        private bool IsScanning => _scanOperation != null;
        private TargetPlatform CurrentPlatform => _platformField == null ? GetInitialPlatform() : (TargetPlatform)_platformField.value;

        [MenuItem("Tools/Azipa Tools/AvaBuild Inspector", false, 916)]
        public static void Open()
        {
            var window = GetWindow<AvaBuildInspectorWindow>();
            window.titleContent = new GUIContent("AvaBuild Inspector", GetAppIcon());
            window.minSize = new Vector2(620, 420);
        }

        public void CreateGUI()
        {
            LoadPreferences();
            var root = rootVisualElement;
            root.Clear();
            root.AddToClassList("abi-root");
            root.AddToClassList(AbiTheme.IsDark ? "abi-dark" : "abi-light");
            var styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
            if (styleSheet != null) root.styleSheets.Add(styleSheet);
            else Debug.LogWarning("[AvaBuild Inspector] StyleSheetを読み込めません: " + StyleSheetPath);
            root.style.flexDirection = FlexDirection.Column;
            root.RegisterCallback<PointerUpEvent>(_ => _checkPaintState = null, TrickleDown.TrickleDown);

            BuildHeader();
            BuildMainTabs();
            _statusArea = new VisualElement { style = { flexShrink = 0 } };
            root.Add(_statusArea);
            _content = new VisualElement { style = { flexGrow = 1 } };
            root.Add(_content);

            UpdateAvatarPickerButton();
            var initialAvatar = FindAvatarRoot(_currentAvatar) ?? FindSelectedAvatar();
            _avatarField.SetValueWithoutNotify(initialAvatar);
            _currentAvatar = initialAvatar;
            SelectMainTab(_selectedTab);
            if (initialAvatar != null) RequestScan(initialAvatar);
            else UpdateStatusArea();
        }

        private void OnEnable()
        {
            EditorApplication.hierarchyChanged += OnEditorHierarchyChanged;
            Undo.postprocessModifications += OnPostprocessModifications;
            Selection.selectionChanged += OnEditorSelectionChanged;
            AvatarBuildHistoryStore.Changed += OnBuildHistoryChanged;
        }

        private void OnDisable()
        {
            EditorApplication.hierarchyChanged -= OnEditorHierarchyChanged;
            Undo.postprocessModifications -= OnPostprocessModifications;
            Selection.selectionChanged -= OnEditorSelectionChanged;
            AvatarBuildHistoryStore.Changed -= OnBuildHistoryChanged;
            _autoRescanItem?.Pause();
            StopAvatarScan();
        }

        #region Preferences

        private void LoadPreferences()
        {
            _selectedTab = ReadEnum("MainTab", MainTab.ScanResults);
            _selectedBreakdown = ReadEnum("BreakdownTab", BreakdownTab.TextureMemory);
            _sortColumn = ReadEnum("SortColumn", SortColumn.Default);
            _sortDescending = EditorPrefs.GetBool(PrefPrefix + "SortDescending", true);
            _groupTexturesByDirectory = EditorPrefs.GetBool(PrefPrefix + "GroupTextures", false);
            _groupSkinnedMeshesByDirectory = EditorPrefs.GetBool(PrefPrefix + "GroupSkinnedMeshes", false);
            _groupMaterialsByDirectory = EditorPrefs.GetBool(PrefPrefix + "GroupMaterials", false);
            _showTextureDirectoryPercentages = EditorPrefs.GetBool(PrefPrefix + "ShowTexturePercentages", true);
            _autoRescan = EditorPrefs.GetBool(PrefPrefix + "AutoRescan", false);
            _textureSettingsTarget = ReadEnum("TextureSettingsTarget", TextureSettingsTarget.Default);
            _historyPlatformFilter = EditorPrefs.GetString(PrefPrefix + "HistoryPlatform", HistoryPlatformAll);
            _showChangedOnly = EditorPrefs.GetBool(PrefPrefix + "HistoryChangedOnly", true);
        }

        private static T ReadEnum<T>(string key, T fallback) where T : struct, Enum
        {
            var value = EditorPrefs.GetInt(PrefPrefix + key, Convert.ToInt32(fallback));
            return Enum.IsDefined(typeof(T), value) ? (T)Enum.ToObject(typeof(T), value) : fallback;
        }

        private static void WriteEnum<T>(string key, T value) where T : struct, Enum =>
            EditorPrefs.SetInt(PrefPrefix + key, Convert.ToInt32(value));

        private static void WriteBool(string key, bool value) => EditorPrefs.SetBool(PrefPrefix + key, value);

        private static TargetPlatform GetInitialPlatform()
        {
            var saved = SessionState.GetInt(PrefPrefix + "Platform", -1);
            if (saved == (int)TargetPlatform.PC || saved == (int)TargetPlatform.Android) return (TargetPlatform)saved;
            return EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android ? TargetPlatform.Android : TargetPlatform.PC;
        }

        #endregion

        #region Change detection

        private void OnEditorHierarchyChanged()
        {
            ScheduleAvatarCountUpdate();
            if (_report == null) return;
            if (_report.AvatarRoot == null)
            {
                // Avatarが削除された、またはSceneが閉じられた。
                StopAvatarScan();
                _report = null;
                _currentAvatar = null;
                _isReportStale = false;
                _avatarField?.SetValueWithoutNotify(null);
                ClearReportCaches();
                UpdateHeaderState();
                UpdateStatusArea();
                RebuildSelectedTab();
                return;
            }

            var currentSignature = GetHierarchySignature(_report.AvatarRoot);
            if (string.Equals(currentSignature, _scannedHierarchySignature, StringComparison.Ordinal)) return;
            MarkReportStale("Avatarの構成（Hierarchy・Component）が変更されました。表示中の結果は古い可能性があります。");
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
            if (affectsAvatar && rootVisualElement != null)
                rootVisualElement.schedule.Execute(() => MarkReportStale("Avatar配下のプロパティが変更されました。表示中の結果は古い可能性があります。"));
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

        private void MarkReportStale(string reason)
        {
            if (_report == null || _report.AvatarRoot == null || IsScanning) return;
            if (!_isReportStale)
            {
                _isReportStale = true;
                _staleReason = reason;
                UpdateStatusArea();
            }
            if (_autoRescan) ScheduleAutoRescan();
        }

        private void ScheduleAutoRescan()
        {
            if (rootVisualElement == null) return;
            _autoRescanItem?.Pause();
            _autoRescanItem = rootVisualElement.schedule.Execute(() =>
            {
                if (_isReportStale && _autoRescan && !IsScanning && _currentAvatar != null) RequestScan(_currentAvatar);
            });
            _autoRescanItem.ExecuteLater(AutoRescanDelayMilliseconds);
        }

        private void SetAutoRescan(bool value)
        {
            if (_autoRescan == value) return;
            _autoRescan = value;
            WriteBool("AutoRescan", value);
            if (value && _isReportStale) ScheduleAutoRescan();
            else if (!value) _autoRescanItem?.Pause();
        }

        #endregion

        #region Header

        private void BuildHeader()
        {
            var header = new VisualElement();
            header.style.paddingLeft = 12;
            header.style.paddingRight = 12;
            header.style.paddingTop = 10;
            header.style.paddingBottom = 10;
            rootVisualElement.Add(header);

            var inputRow = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };
            header.Add(inputRow);

            _avatarField = new ObjectField("Avatar") { objectType = typeof(GameObject), allowSceneObjects = true };
            _avatarField.style.flexGrow = 1;
            _avatarField.style.flexShrink = 1;
            _avatarField.style.minWidth = 160;
            _avatarField.labelElement.style.minWidth = 50;
            _avatarField.RegisterValueChangedCallback(evt => OnAvatarChanged(evt.newValue as GameObject));
            inputRow.Add(_avatarField);

            _avatarPickerButton = new Button(ShowAvatarPicker) { text = "▾" };
            _avatarPickerButton.style.minWidth = 34;
            _avatarPickerButton.style.height = 20;
            _avatarPickerButton.style.marginLeft = 2;
            inputRow.Add(_avatarPickerButton);

            _rescanButton = new Button(() =>
            {
                if (_currentAvatar != null) RequestScan(_currentAvatar);
            }) { text = "再スキャン" };
            _rescanButton.style.width = 88;
            _rescanButton.style.height = 21;
            _rescanButton.style.marginLeft = 4;
            _rescanButton.tooltip = "現在のAvatarをもう一度Quick Scanします。";
            inputRow.Add(_rescanButton);

            _platformField = new EnumField("Platform", GetInitialPlatform());
            _platformField.style.width = 170;
            _platformField.style.marginLeft = 8;
            _platformField.labelElement.style.minWidth = 56;
            _platformField.RegisterValueChangedCallback(evt =>
            {
                SessionState.SetInt(PrefPrefix + "Platform", (int)(TargetPlatform)evt.newValue);
                _textureImportInfoCache.Clear();
                if (_currentAvatar != null) RequestScan(_currentAvatar);
            });
            inputRow.Add(_platformField);
            UpdateHeaderState();
        }

        private void BuildMainTabs()
        {
            var tabs = new VisualElement { style = { flexDirection = FlexDirection.Row, paddingLeft = 12, paddingRight = 12 } };
            tabs.style.borderBottomWidth = 1;
            tabs.style.borderBottomColor = AbiTheme.Border;
            rootVisualElement.Add(tabs);

            _scanResultsTab = CreateMainTabButton("スキャン結果表示", MainTab.ScanResults);
            tabs.Add(_scanResultsTab);
            _breakdownTab = CreateMainTabButton("内訳表示", MainTab.Breakdown);
            tabs.Add(_breakdownTab);
            _buildHistoryTab = CreateMainTabButton("ビルド履歴", MainTab.BuildHistory);
            tabs.Add(_buildHistoryTab);
        }

        private Button CreateMainTabButton(string text, MainTab tab)
        {
            var button = new Button(() => SelectMainTab(tab)) { text = text };
            button.style.flexGrow = 1;
            button.style.height = 32;
            return button;
        }

        private static void StyleTabButton(Button button, bool selected, bool subTab = false)
        {
            if (button == null) return;
            button.style.backgroundColor = selected ? (subTab ? AbiTheme.SubTabSelected : AbiTheme.TabSelected) : AbiTheme.TabIdle;
            button.style.color = selected ? new StyleColor(Color.white) : new StyleColor(StyleKeyword.Null);
            button.style.unityFontStyleAndWeight = selected ? FontStyle.Bold : FontStyle.Normal;
        }

        private void SelectMainTab(MainTab tab)
        {
            _selectedTab = tab;
            WriteEnum("MainTab", tab);
            StyleTabButton(_scanResultsTab, tab == MainTab.ScanResults);
            StyleTabButton(_breakdownTab, tab == MainTab.Breakdown);
            StyleTabButton(_buildHistoryTab, tab == MainTab.BuildHistory);
            RebuildSelectedTab();
        }

        private void SelectBreakdown(BreakdownTab tab)
        {
            _selectedBreakdown = tab;
            WriteEnum("BreakdownTab", tab);
            SelectMainTab(MainTab.Breakdown);
        }

        private void UpdateHeaderState()
        {
            _rescanButton?.SetEnabled(_currentAvatar != null && !IsScanning);
        }

        private void ShowAvatarPicker()
        {
            var avatars = FindSceneAvatars();
            var menu = new GenericMenu();
            if (avatars.Count == 0)
            {
                menu.AddDisabledItem(new GUIContent("シーン内にVRCAvatarDescriptorを持つAvatarがありません"));
            }
            else
            {
                var multipleScenes = avatars.Select(avatar => avatar.scene).Distinct().Count() > 1;
                var usedLabels = new HashSet<string>(StringComparer.Ordinal);
                foreach (var avatar in avatars)
                {
                    var label = avatar.name.Replace('/', '／');
                    if (!avatar.activeInHierarchy) label += "（非アクティブ）";
                    if (multipleScenes) label = avatar.scene.name.Replace('/', '／') + "/" + label;
                    var unique = label;
                    for (var suffix = 2; !usedLabels.Add(unique); suffix++) unique = label + " (" + suffix + ")";
                    var target = avatar;
                    menu.AddItem(new GUIContent(unique), avatar == _currentAvatar, () => _avatarField.value = target);
                }
            }
            menu.DropDown(_avatarPickerButton.worldBound);
        }

        private void ScheduleAvatarCountUpdate()
        {
            if (rootVisualElement == null) return;
            _avatarCountItem?.Pause();
            _avatarCountItem = rootVisualElement.schedule.Execute(UpdateAvatarPickerButton);
            _avatarCountItem.ExecuteLater(500);
        }

        private void UpdateAvatarPickerButton()
        {
            if (_avatarPickerButton == null) return;
            var count = FindSceneAvatars().Count;
            _avatarPickerButton.text = count > 1 ? "▾ " + count : "▾";
            _avatarPickerButton.tooltip = count == 0
                ? "シーン内にAvatarがありません"
                : "シーン内のAvatar（" + count + "体）から選択します。";
        }

        private void OnAvatarChanged(GameObject candidate)
        {
            var avatar = FindAvatarRoot(candidate);
            if (avatar != candidate) _avatarField.SetValueWithoutNotify(avatar);
            if (avatar != _currentAvatar)
            {
                _selectedTexturePaths.Clear();
                _textureCompressionSettings.ClearSettings();
                _expandedTextureUsages.Clear();
                _pendingApplyComparison = null;
                _historyAvatarExplicit = false;
            }
            RequestScan(avatar);
        }

        #endregion

        #region Scan

        private void RequestScan(GameObject avatar)
        {
            StopAvatarScan();
            _autoRescanItem?.Pause();
            var request = ++_scanRequest;
            var keepPrevious = avatar != null && _report != null && _report.AvatarRoot == avatar;
            _currentAvatar = avatar;
            _scanMessage = null;
            _isReportStale = false;
            _staleReason = null;
            if (!keepPrevious)
            {
                _report = null;
                ClearReportCaches();
            }

            if (avatar != null)
            {
                try
                {
                    _scanOperation = AvatarQuickAnalyzer.BeginAnalyze(avatar, CurrentPlatform);
                    _scanOperationRequest = request;
                    EditorApplication.update += AdvanceAvatarScan;
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                    _scanOperation = null;
                    _scanMessage = "Quick Scanを開始できませんでした。Consoleを確認してください。";
                }
            }

            UpdateHeaderState();
            UpdateStatusArea();
            if (keepPrevious && IsScanning) ApplyScanningDim();
            else RebuildSelectedTab();
        }

        private void AdvanceAvatarScan()
        {
            var operation = _scanOperation;
            if (operation == null) return;
            if (_scanOperationRequest != _scanRequest || _currentAvatar == null)
            {
                StopAvatarScan();
                UpdateStatusArea();
                RebuildSelectedTab();
                return;
            }

            try
            {
                // 1フレームの処理時間を制限しつつ、短い段階はまとめて進める。
                var startedAt = EditorApplication.timeSinceStartup;
                bool hasMoreStages;
                do hasMoreStages = operation.Advance();
                while (hasMoreStages && EditorApplication.timeSinceStartup - startedAt < ScanFrameBudgetSeconds);
                if (hasMoreStages)
                {
                    UpdateScanProgress();
                    return;
                }

                EditorApplication.update -= AdvanceAvatarScan;
                _scanOperation = null;
                _report = operation.Report;
                _isReportStale = false;
                _staleReason = null;
                _scannedHierarchySignature = GetHierarchySignature(_currentAvatar);
                ClearReportCaches();
                if (_report == null) _scanMessage = "Quick Scanの結果を取得できませんでした。Consoleを確認してください。";
                UpdateHeaderState();
                UpdateStatusArea();
                RebuildSelectedTab();
                ShowApplyComparisonResult();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                StopAvatarScan();
                _report = null;
                _scanMessage = "Quick Scanに失敗しました。Consoleを確認してください。";
                UpdateStatusArea();
                RebuildSelectedTab();
            }
        }

        private void StopAvatarScan()
        {
            if (_scanOperation == null) return;
            EditorApplication.update -= AdvanceAvatarScan;
            _scanOperation = null;
            UpdateHeaderState();
        }

        private void CancelScan()
        {
            if (!IsScanning) return;
            StopAvatarScan();
            _scanRequest++;
            if (_report != null)
            {
                _isReportStale = true;
                _staleReason = "Quick Scanをキャンセルしました。表示中の結果は " + _report.Timestamp.ToString("HH:mm:ss") +
                               "（" + _report.Platform + "）にスキャンしたものです。";
            }
            else
            {
                _scanMessage = "Quick Scanをキャンセルしました。再スキャンできます。";
            }
            UpdateHeaderState();
            UpdateStatusArea();
            RebuildSelectedTab();
        }

        private void ClearReportCaches()
        {
            _textureImportInfoCache.Clear();
            _usageCache.Clear();
            _searchTextCache.Clear();
        }

        private void ApplyScanningDim()
        {
            if (_content == null) return;
            var dim = IsScanning && _report != null && _selectedTab != MainTab.BuildHistory;
            _content.SetEnabled(!dim);
        }

        private void UpdateStatusArea()
        {
            if (_statusArea == null) return;
            _statusArea.Clear();
            _progressFill = null;
            _progressLabel = null;

            if (IsScanning)
            {
                var row = CreateStatusRow(AbiTheme.InfoPanel);
                var labels = new VisualElement { style = { flexGrow = 1, flexShrink = 1 } };
                _progressLabel = new Label { style = { fontSize = 11 } };
                labels.Add(_progressLabel);
                var track = new VisualElement { style = { height = 4, marginTop = 4, backgroundColor = AbiTheme.Track } };
                _progressFill = new VisualElement { style = { height = 4, width = Length.Percent(0), backgroundColor = AbiTheme.BarFill } };
                track.Add(_progressFill);
                labels.Add(track);
                row.Add(labels);
                var cancel = new Button(CancelScan) { text = "キャンセル" };
                cancel.style.marginLeft = 8;
                row.Add(cancel);
                _statusArea.Add(row);
                UpdateScanProgress();
            }
            else if (_isReportStale && _report != null)
            {
                var row = CreateStatusRow(AbiTheme.WarningPanel);
                var message = new Label(_staleReason ?? "表示中の結果は古い可能性があります。");
                message.style.flexGrow = 1;
                message.style.flexShrink = 1;
                message.style.whiteSpace = WhiteSpace.Normal;
                row.Add(message);
                var auto = new Toggle { text = "自動で再スキャン", value = _autoRescan };
                auto.tooltip = "Avatarの変更を検知したら、1秒後に自動で再スキャンします。";
                auto.style.marginLeft = 8;
                auto.RegisterValueChangedCallback(evt => SetAutoRescan(evt.newValue));
                row.Add(auto);
                var rescan = new Button(() =>
                {
                    if (_currentAvatar != null) RequestScan(_currentAvatar);
                }) { text = "再スキャン" };
                rescan.style.marginLeft = 6;
                rescan.style.unityFontStyleAndWeight = FontStyle.Bold;
                rescan.SetEnabled(_currentAvatar != null);
                row.Add(rescan);
                _statusArea.Add(row);
            }

            _statusArea.style.display = _statusArea.childCount > 0 ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private static VisualElement CreateStatusRow(Color background)
        {
            var row = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    alignItems = Align.Center,
                    marginLeft = 12,
                    marginRight = 12,
                    marginTop = 6,
                    paddingLeft = 9,
                    paddingRight = 6,
                    paddingTop = 5,
                    paddingBottom = 5,
                    backgroundColor = background
                }
            };
            AbiTheme.SetRadius(row.style, 3);
            return row;
        }

        private void UpdateScanProgress()
        {
            var operation = _scanOperation;
            if (operation == null || _progressLabel == null || _progressFill == null) return;
            var text = "Quick Scan中  " + Math.Min(operation.StageCount, operation.CompletedStageCount + 1).ToString("N0") + "/" +
                       operation.StageCount.ToString("N0") + " · " + operation.CurrentStage;
            if (_report != null) text += "（完了まで前回の結果を表示しています）";
            _progressLabel.text = text;
            _progressFill.style.width = Length.Percent(operation.Progress * 100f);
        }

        #endregion

        #region Tab content

        private void RebuildSelectedTab()
        {
            if (_content == null) return;
            SaveBreakdownScroll();
            _content.Clear();
            ResetBreakdownReferences();
            _content.SetEnabled(true);
            if (_selectedTab == MainTab.BuildHistory)
            {
                BuildHistoryTab();
                return;
            }
            if (_report == null)
            {
                ShowEmptyState(_currentAvatar == null
                    ? AvatarDropPrompt
                    : IsScanning ? "Quick Scan中..." : _scanMessage ?? "再スキャンしてください。");
                return;
            }
            if (_selectedTab == MainTab.ScanResults) BuildScanResults();
            else BuildBreakdown();
            ApplyScanningDim();
        }

        private void OnBuildHistoryChanged()
        {
            if (_selectedTab == MainTab.BuildHistory && rootVisualElement != null)
                rootVisualElement.schedule.Execute(RebuildSelectedTab);
        }

        private void ShowEmptyState(string message)
        {
            if (_content == null) return;
            _content.Clear();
            if (string.Equals(message, AvatarDropPrompt, StringComparison.Ordinal))
            {
                if (_avatarDropIcon == null)
                    _avatarDropIcon = AssetDatabase.LoadAssetAtPath<Texture2D>(AvatarDropIconPath);

                var emptyState = new VisualElement
                {
                    style =
                    {
                        flexGrow = 1,
                        justifyContent = Justify.Center,
                        alignItems = Align.Center,
                        paddingLeft = 16,
                        paddingRight = 16
                    }
                };
                if (_avatarDropIcon != null)
                {
                    var icon = new Image
                    {
                        image = _avatarDropIcon,
                        scaleMode = ScaleMode.ScaleToFit
                    };
                    icon.style.width = 190;
                    icon.style.height = 190;
                    icon.style.marginBottom = 12;
                    emptyState.Add(icon);
                }
                var prompt = new Label(message);
                prompt.style.unityTextAlign = TextAnchor.MiddleCenter;
                prompt.style.fontSize = 15;
                prompt.style.opacity = 0.78f;
                emptyState.Add(prompt);
                if (FindSceneAvatars().Count > 0)
                {
                    var pick = new Button(ShowAvatarPickerFromEmptyState) { text = "シーン内のAvatarから選択" };
                    pick.style.marginTop = 10;
                    emptyState.Add(pick);
                }
                _content.Add(emptyState);
                return;
            }

            var label = new Label(message);
            label.style.flexGrow = 1;
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            label.style.fontSize = 15;
            label.style.opacity = 0.7f;
            _content.Add(label);
        }

        private void ShowAvatarPickerFromEmptyState() => ShowAvatarPicker();

        #endregion

        #region Selection sync

        private void OnEditorSelectionChanged()
        {
            var selected = Selection.activeObject;
            if (selected == null) return;
            if (selected == _lastWindowSelection)
            {
                _lastWindowSelection = null;
                return;
            }
            _lastWindowSelection = null;
            if (_selectedTab == MainTab.Breakdown) RevealInBreakdown(selected);
        }

        private void SelectAndPing(UnityEngine.Object target)
        {
            if (target == null) return;
            _lastWindowSelection = target;
            Selection.activeObject = target;
            EditorGUIUtility.PingObject(target);
        }

        private void SelectAndPing(UnityEngine.Object[] targets)
        {
            if (targets == null || targets.Length == 0) return;
            _lastWindowSelection = targets[0];
            Selection.objects = targets;
            EditorGUIUtility.PingObject(targets[0]);
        }

        #endregion

        #region Helpers

        private static Texture2D GetAppIcon()
        {
            if (_appIcon == null) _appIcon = AssetDatabase.LoadAssetAtPath<Texture2D>(AppIconPath);
            return _appIcon;
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
            var avatars = FindSceneAvatars();
            return avatars.Count == 1 ? avatars[0] : null;
        }

        private static List<GameObject> FindSceneAvatars()
        {
            return FindObjectsOfType<VRCAvatarDescriptor>(true)
                .Where(descriptor => descriptor != null && descriptor.gameObject.scene.IsValid() &&
                                     (descriptor.gameObject.hideFlags & HideFlags.HideInHierarchy) == 0)
                .Select(descriptor => descriptor.gameObject)
                .Distinct()
                .OrderBy(avatar => avatar.scene.name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(avatar => avatar.transform.GetSiblingIndex())
                .ToList();
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

        private static bool IsSceneObject(UnityEngine.Object target) => target is GameObject || target is Component;

        private static string GetAssetPath(UnityEngine.Object target) =>
            target == null ? string.Empty : AssetDatabase.GetAssetPath(target) ?? string.Empty;

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

        private static Color RatingColor(PerformanceRating rating) => AbiTheme.Rating(rating);

        private static Texture LoadEditorIcon(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            Texture icon = null;
            if (AbiTheme.IsDark) icon = EditorGUIUtility.FindTexture("d_" + name);
            return icon != null ? icon : EditorGUIUtility.FindTexture(name);
        }

        private static void CopyToClipboard(EditorWindow window, string text, string what)
        {
            EditorGUIUtility.systemCopyBuffer = text ?? string.Empty;
            window.ShowNotification(new GUIContent(what + "をコピーしました。"));
        }

        #endregion
    }
}
