# AvaBuild Inspector 引継ぎ資料

最終更新: 2026-09-30  
パッケージバージョン: 0.1.0  
現在の検証環境: Unity 2022.3.22f1 / VRChat SDK Avatars 3.10.5

## 1. このツールの目的

AvaBuild Inspectorは、Scene上のVRChat Avatarを対象に、アップロード前のパフォーマンス情報と負荷要因を素早く確認するUnity Editor拡張である。

現在は「Avatarを投入してすぐ確認する」用途へ絞っている。過去に検討・実装していたNDMF Build、履歴、レポート保存、比較、エクスポート等は削除済みであり、意図的に依存・画面を増やしていない。

基本操作:

1. `Tools > AvaBuild Inspector > Open Inspector`を開く。
2. `VRCAvatarDescriptor`を持つAvatarまたはその子ObjectをAvatar欄へドラッグ＆ドロップする。
3. Quick Scanが自動実行される。
4. 必要に応じて`再スキャン`を押す。

## 2. 現在のUI構成

メイン画面は次の2タブだけで構成する。

### スキャン結果表示

- VRChat SDKが算出したOverall Performance
- Poor / Very Poorの主要問題
- SDKの全パフォーマンス項目
- PC / Android切り替え
- Exact / Estimated / Unavailableの精度表示

### 内訳表示

次の4種類に限定している。

- テクスチャメモリ
- スキンドメッシュ
- マテリアルスロット
- PBコンポーネント

内訳の各行をクリックすると、対応するHierarchy ObjectまたはProject Assetを選択してPingする。

## 3. 実装済み機能

### Quick Scan

- Avatar欄の変更時に自動実行する。
- Avatarの子Objectが投入された場合も、親の`VRCAvatarDescriptor`まで正規化する。
- SceneにAvatarが1体だけ存在する場合は、ウィンドウを開いた時に自動選択する。
- `再スキャン`ボタンから任意のタイミングで再実行できる。
- SDK集計には`AvatarPerformance.CalculatePerformanceStats`を使用する。
- Source Avatarを非破壊で調査し、AssetBundleやNDMF Previewは生成しない。

### テクスチャメモリ内訳

- 小さいサムネイル、ファイル名、右端の推定メモリ量を高密度な一覧で表示する。
- RendererのMaterialが参照しているTextureを重複排除して表示する。
- 個別推定値は`Profiler.GetRuntimeMemorySizeLong`を使用する。
- SDKの合計値と個別推定値は算出方法が異なるため、一致しない場合がある。

### テクスチャ圧縮の一括編集

- 一覧のチェックボックスで複数Textureを選択できる。
- Default / PC（Standalone）/ Androidの設定を編集できる。
- 対応項目:
  - Override（PC / Android）
  - Max Size
  - Resize Algorithm
  - Format
  - Compression: None / Low Quality / Normal Quality / High Quality
  - Use Crunch Compression
  - Compressor Quality
- UIで値を変えただけではImportしない。
- TextureとPlatformの組み合わせごとにメモリ上のDraftへ保留する。
- `すべてApply`を押した時だけ、保留中のTextureをまとめて再Importする。
- Apply後は自動的にQuick Scanを再実行する。
- Apply失敗はConsoleへ出力し、失敗分はPending状態を維持する。

重要: TextureImporter設定はAvatar専用設定ではない。同じTextureを別Avatarや別Materialが使用している場合、それらにも変更が影響する。

### スキンドメッシュ内訳

- SkinnedMeshRenderer単位で表示する。
- 三角形数の多い順に表示する。
- Mesh名、頂点数、Blend Shape数、Bone参照数を表示する。

### マテリアルスロット内訳

- Renderer単位でMaterial Slot数を表示する。
- 使用Material名とMissing状態を詳細へ表示する。

### PBコンポーネント内訳

- PhysBone単位で対象Transform数、Collider数、推定Collision Check数を表示する。
- `ignoreTransforms`を除外して対象Transformチェーンを収集する。
- 全SkinnedMeshRendererの`bones`と照合し、対象Transformが1本も含まれないPhysBoneを`未使用候補`として表示する。
- 未使用候補の件数、強調行、`候補のみ表示`フィルターを持つ。

未使用判定は削除可否の確定ではない。Mesh変形以外の目的でPhysBoneを意図的に利用している可能性があるため、必ずユーザーが確認する。

## 4. ソース構成

```text
Packages/com.avabuild.inspector/
├─ package.json
├─ README.md
├─ HANDOFF.md
└─ Editor/
   ├─ AvaBuildInspector.Editor.asmdef
   ├─ Core/
   │  ├─ AnalysisModels.cs
   │  ├─ AvatarQuickAnalyzer.cs
   │  └─ TextureCompressionQueue.cs
   └─ UI/
      └─ AvaBuildInspectorWindow.cs
```

### `AnalysisModels.cs`

Quick Scanの結果モデルを定義する。

- `TargetPlatform`: PC / Android
- `AnalysisAccuracy`: Exact / Estimated / Unavailable
- `Contributor`: 内訳1行分のデータ
- `MetricResult`: SDK項目と内訳の組
- `AvatarReport`: 1回分のスキャン結果

### `AvatarQuickAnalyzer.cs`

解析本体。主な責務:

- VRChat SDKによる集計とRank取得
- 各SDK項目から`MetricResult`を作成
- Texture / Mesh / Material / PhysBone等のContributor収集
- 個別Contributor解析の例外分離
- PB未使用候補の判定

Contributor収集は`TryContributors`で個別に保護している。1種類の解析が失敗しても、スキャン全体を失敗させない設計である。

### `TextureCompressionQueue.cs`

TextureImporter設定の読込、保留、Applyを担当する。

- Draftキー: `AssetPath + Platform`
- PCのUnity内部Platform名: `Standalone`
- AndroidのUnity内部Platform名: `Android`
- DefaultのUnity内部Platform名: `DefaultTexturePlatform`
- `AssetDatabase.StartAssetEditing` / `StopAssetEditing`でApply処理をまとめる。
- Platformごとに`TextureImporterPlatformSettings`を読み書きする。

### `AvaBuildInspectorWindow.cs`

UI ToolkitベースのEditorWindow。主な責務:

- Avatar投入と再スキャン
- メインタブ、内訳タブの構築
- 結果一覧と詳細行の表示
- Texture複数選択と圧縮設定UI
- PhysBone未使用候補の表示とフィルタ
- Hierarchy / Project上の対象選択

現状はUXML / USSを使用せず、すべてC#でUIを構築している。

## 5. データフロー

```text
Avatarを投入 / 再スキャン
        ↓
AvatarBodyのVRCAvatarDescriptor Rootへ正規化
        ↓
AvatarQuickAnalyzer.Analyze
        ├─ VRChat SDKの集計値・Rank
        └─ 各Component / Assetの内訳収集
        ↓
AvatarReport
        ↓
スキャン結果表示 または 内訳表示
```

Texture圧縮は別フローとなる。

```text
Textureを複数選択
        ↓
Default / PC / Android設定を変更
        ↓
TextureCompressionDraftへ保留（Importしない）
        ↓
すべてApply
        ↓
TextureImporter更新・一括再Import
        ↓
Quick Scanを再実行
```

## 6. 依存関係

`package.json`上の直接依存:

```json
"com.vrchat.avatars": "3.10.5"
```

asmdef参照:

- `VRC.SDKBase`
- `VRC.SDK3A`
- `VRC.Enums.Validation.Performance`
- `VRC.Dynamics`
- `VRC.SDK3.Dynamics.PhysBone`

NDMF、Modular Avatar、Avatar Optimizerへの直接依存はない。それらを導入していないAvatar Projectでも、対応するVRChat SDK APIが存在すれば使用できる設計である。

## 7. 別プロジェクトへの移行手順

1. 移行先をUnity 2022.3系のVRChat Avatar Projectとして用意する。
2. VRChat SDK Avatars 3.10.5が利用可能であることを確認する。
3. この`Packages/com.avabuild.inspector`フォルダを、フォルダごと移行先の`Packages`直下へコピーする。
4. `.meta`ファイルもすべてコピーする。
5. `Library`、`Temp`、`Logs`のファイルはコピーしない。
6. Unityを開き、Package ImportとScript Compileの完了を待つ。
7. `Tools > AvaBuild Inspector > Open Inspector`が表示されることを確認する。

VRCSDKのバージョンを上げる場合は、最初に次を確認する。

- `package.json`の依存バージョン
- asmdef参照名
- `AvatarPerformanceStats`と`AvatarPerformanceCategory`のAPI変更
- PhysBoneの`ignoreTransforms`、`colliders`、`rootTransform`のAPI変更
- `TextureImporterPlatformSettings`のUnityバージョン差

## 8. 移行後のスモークテスト

最低限、次の操作を確認する。

1. コンパイルエラーがない。
2. メニューからウィンドウを開ける。
3. Avatar Rootと子Objectの両方を投入できる。
4. PC / Android切り替えで再スキャンされる。
5. `再スキャン`が動作する。
6. スキャン結果のOverall、Main Issues、各Metricが表示される。
7. 4種類の内訳タブが表示される。
8. 内訳行クリックで対象を選択できる。
9. Textureを複数選択し、異なるPlatformへ設定を保留できる。
10. 設定変更だけではImportが走らない。
11. `すべてApply`で再Importされ、その後にメモリ値が更新される。
12. PB未使用候補が強調表示され、フィルタできる。

Texture圧縮テストは、元に戻せるテスト用Textureで行う。共有Textureや購入Assetを直接変更する場合は、事前にバージョン管理へ保存する。

## 9. 既知の制約・注意点

- Download SizeとUncompressed SizeはAssetBundleを生成しないためUnavailableになる。
- Source Avatarのみを解析するため、NDMFやビルド時処理後の最終Avatarとは数値が異なる場合がある。
- Texture個別メモリは推定であり、SDK合計値と完全一致しない。
- Material経由で静的に参照されていないTextureは内訳へ出ない場合がある。
- Texture圧縮Draftと選択状態はEditorWindow上のメモリだけに存在し、Domain Reloadやウィンドウ再生成で失われる。
- ApplyにはUndoを実装していない。バージョン管理またはImporter設定のバックアップを前提とする。
- Formatの組み合わせによってはPlatformで使用できず、Unity側が警告または変換を行う場合がある。
- PB未使用候補は`SkinnedMeshRenderer.bones`との参照比較であり、Vertex Weightが実際に存在するかまでは解析していない。
- PBがMesh以外のObject、Collider、Contact、ギミック等を動かす用途は未使用候補になる可能性がある。
- 自動テストは未整備。現時点ではUnityコンパイルと手動スモークテストが中心である。

## 10. 現在の設計方針

- 機能を増やす場合も、Avatar投入直後に結果が見える操作感を維持する。
- Build Pipeline依存は、明確な要件がない限り再導入しない。
- SDK集計値と独自推定値を混同せず、精度を明示する。
- Assetを変更する機能は、即時実行せずDraftとApplyを分離する。
- 自動削除や自動修正は避け、候補表示と対象選択を優先する。
- 共有Assetへ影響する操作は、UI上で影響範囲を分かるようにする。

## 11. 次の改善候補

候補は多いが、見た目の改善を先行すると、誤った値を表示したまま編集できる危険がある。まずデータの正確性と変更範囲の明示を固め、その後に一覧性とScene連携を改善する。いずれも、アバター改変中に判断しやすく、誤操作を避けられるUIにすることを重視する。

### 候補への批判的レビューと実装順

1. **最優先: 複数Texture編集の混在値対応（候補1）**
   現状は複数選択時に先頭Textureの値を表示するため、選択中の他Textureと異なる設定でも同じ値に見える。これは視認性ではなく編集結果の誤認につながるため、UI改善の前提として最初に直す。混在値を表す専用状態をデータ/UI双方で扱い、Apply対象も明示する。
2. **次点: 圧縮設定パネルの配置・初期状態（候補4）**
   変更範囲が比較的小さく、候補1の混在値表示と同時に検証しやすい。パネルを常時表示するだけでは不十分で、未選択時の説明、選択件数、対象Platform、保留中の変更を同じ場所で確認できるようにする。
3. **次点: 詳細一覧の再設計（候補2）**
   効果は大きいが、Skinned Mesh・Material Slot・PhysBoneを一度に作り替えると仕様が膨らみやすい。共通の行・折りたたみ・フィルター部品を先に定義し、タブごとに段階導入する。情報を隠しすぎて確認しづらくならないよう、件数と問題項目を常時表示する。
4. **検証後に拡張: Texture使用箇所の可視化（候補3）**
   参照関係の一覧とHierarchy/ProjectへのPingは実現性が高い。一方、Scene上の色付けはSelection、Prefab、非表示Objectなどの状態に左右されるため、必須要件にせず、安定した参照一覧を先に完成させる。Sceneハイライトは独立した追加機能として検証する。

### 既存候補の実装要件

1. Textureを複数選択したときの圧縮設定表示を改善する。
   - 選択中のTexture間で値が一致している項目は、その共通値を表示する。
   - 値が異なる項目は空欄、または「複数の設定」と分かる混在状態で表示する。
   - 混在状態の項目を変更した場合に、選択中の全Textureへ適用されることを明示する。
   - Platform（Default / PC / Android）ごとの設定対象が一目で分かるようにする。

2. Skinned Mesh、Material Slot、PhysBoneの詳細表示を再設計する。
   - 現在の縦一列の羅列を見直し、折りたたみ可能なセクション、グループ化、要約行などで情報量を整理する。
   - 項目数、問題のある項目、未使用候補などをヘッダーで把握できるようにする。
   - 必要に応じて検索・フィルター・ソートを追加し、多数の項目から目的の対象へすぐ到達できるようにする。
   - Hierarchy / Project上の対象を選択・Pingできる現在の導線は維持する。

3. Textureの使用箇所を確認できるようにする。
   - Textureを選択したとき、そのTextureを参照するMaterial、Material Slot、Renderer、Hierarchy上のObjectを一覧表示する。
   - 各行から対象ObjectやAssetを選択・Pingできるようにし、圧縮してよいか判断できる情報をまとめて確認できるようにする。
   - 可能であればScene上の対象Rendererを一時的にハイライトする。Scene演出が安定しない場合は、Hierarchy選択、Ping、参照関係の詳細表示を代替手段とする。

4. Textureの圧縮設定パネルの初期表示と配置を見直す。
   - ウィンドウを開いた時点で圧縮設定を展開して表示する。
   - 圧縮設定はTexture一覧の上ではなく、対象Texture一覧の下に配置する。
   - 複数選択時の対象範囲と、Draft / Applyの状態が常に分かるようにする。

### 追加したいUI調整案

5. **解析状態と古い結果の明示**
   - Avatar変更・Platform変更・再スキャン中は、結果を「解析中」として無効化し、古い結果を最新結果と誤認できないようにする。
   - 解析完了時刻と対象Avatar / Platformを表示し、変更後に再スキャンが必要な場合は警告バナーを出す。
   - 可能ならキャンセル可能な進捗表示を追加する。キャンセルできない処理では、少なくとも処理中であることを示す。

6. **変更保護と復旧導線**
   - Draftがある状態でAvatar変更、再スキャン、ウィンドウを閉じる操作を行った場合、破棄確認を表示する。
   - Apply前に変更件数、対象Texture、対象Platformを確認できる要約を表示する。
   - Apply後にUnityのUndo、またはImporter設定のバックアップ・復元導線を用意する。現状はUndo未実装のため、候補1〜4より先に無理に自動変更範囲を広げない。

7. **一覧の検索・絞り込み・並び替え**
   - 4つの内訳タブすべてで、名前、Hierarchyパス、問題状態、サイズなどを対象に検索・フィルターできるようにする。
   - 現在の並び順と、選択中のフィルター条件をヘッダーに表示する。
   - 0件時は「該当なし」と「フィルター解除」を明示し、空のデータと絞り込み結果を混同しないようにする。

8. **詳細表示と一覧表示の切り替え**
   - 項目数が多い場合は、名前・主要値だけのコンパクト表示と、詳細情報を含む表示を切り替えられるようにする。
   - 折りたたみ状態、選択状態、フィルター状態を再スキャンやタブ切り替えで意図せず失わないようにする。

9. **対象への移動と文脈表示の統一**
   - すべてのContributor行で、対象の種類（Scene Object / Project Asset）、Hierarchyパス、Assetパスを同じ位置関係で表示する。
   - クリックで選択、ダブルクリックまたは専用ボタンでPingなど、操作方法を統一する。
   - 対象が削除済み・Missingになった場合は、無効なボタンと理由を表示する。

10. **狭いウィンドウでも崩れないレイアウト**
    - 横幅が狭い場合に値やパスが見切れないよう、重要列を優先し、補足情報は折りたたみまたはツールチップへ送る。
    - 最小サイズ、長いAvatar名、長いHierarchyパス、多数のTextureを使った状態で手動スモークテストする。

11. **レポートの確認・共有**
   - 現在のスキャン対象、Platform、Overall、主要な問題、各内訳の件数をクリップボードへコピーできるようにする。
   - エラーやUnavailableの理由は、Consoleだけでなく該当箇所のUIから確認できるようにする。

### 2026-09-30 実装更新

- **候補1: 実装** — 複数選択TextureのDefault / PC / Android各項目にUI Toolkitの混在値表示を追加した。変更したフィールドだけを選択中全TextureのDraftへ書き込み、Apply前にTexture数・設定数・対象Platformを確認する。混在値を先頭Textureの値と誤認しないための初期表示である。検索/フィルター中の「表示中を選択」は現在表示される行だけを対象とし、選択済みの非表示Texture数もパネルに表示する。
- **候補4: 実装** — 圧縮設定パネルをTexture一覧の後ろへ移動し、ウィンドウ初期状態から表示する。選択なし説明、選択枚数、対象Platform、保留件数、破棄/Applyを同じパネルに配置した。
- **候補2: 実装** — 全内訳に名前/詳細/Hierarchyパス検索、値の大きい順ソート、コンパクト表示切り替えを追加した。詳細タブはContributor.Groupごとに折りたたみ、グループ件数、MaterialのMissing数、PhysBone未使用候補数を表示し、開閉状態を再描画後も保持する。PhysBone未使用候補フィルターと併用できる。
- **候補3: 実装（安定経路）** — 選択中TextureごとにAvatar配下RendererのMaterial、Material Slot、Shader texture propertyを列挙し、RendererのHierarchy選択/PingとMaterialのProject選択/Pingを提供した。Scene色付けはSelection等の状態に左右されるため実装していない。Renderer.sharedMaterialsから現在確認できる参照が対象で、独自Shaderの非Texture参照やAvatar外のMaterial利用状況は示さない。
- **候補5: 実装（キャンセルは段階境界）** — スキャン中は結果をクリアして進捗バーを出し、完了結果にAvatar名・Platform・時刻を表示する。解析はSDK集計とContributor種別ごとの段階に分け、各段階の間でキャンセルできる。SDKの集計1回と各Contributor種別の処理中は中断できない。解析サブ処理の警告は結果画面に出す。Avatar配下のTransform/Component構成署名の変化と、Avatar配下Componentに対するUndo対象変更後に古い可能性のある結果として警告し、再スキャンを促す。Hierarchy署名は親子・名前・activeSelf・Component型を比較する。Asset単体変更の検知は未実装。
- **候補6: 実装（復元は仮実装）** — Avatar変更、Platform変更、明示的な再スキャン、Draft破棄、Apply、ウィンドウ終了で確認を行う。終了時は`Library/AvaBuildInspector/pending-texture-drafts.json`への保持または破棄を選択し、再オープン時に保持したDraftを読み戻す。Unity再コンパイル中はDraftをLibraryへ自動退避する。Apply直前のImporter設定（変更対象Platform）を`Library/AvaBuildInspector/texture-settings-backup.json`へ保存し、Textureパネルから直前Apply前へ復元できるようにした。バックアップは次のApplyで置き換わり、正常復元後に削除される。Unity Undoそのものは未実装。
- **候補7: 実装** — 全内訳で検索と値順ソート、検索ゼロ件の解除ボタンを追加した。Textureメモリの1/4 MB以上、Skinned Meshの三角形数/Blend Shape、Material Slotのスロット数/Missing、PhysBone未使用候補で絞り込める。検索/フィルター/ソート条件は各コントロールに表示され、絞り込み件数も表示する。
- **候補8: 部分実装** — コンパクト表示切り替えを追加した。グループ折りたたみ状態、検索/フィルター/ソート/表示モードはウィンドウ内で保持する。
- **候補9: 実装** — 全詳細Contributor行の名前直下にScene Object / Project Assetの種類とパスを表示し、ツールチップで全文を確認できる。Skinned MeshやMaterial Slotでは関連Mesh/Materialの専用ボタンを追加した。Texture行は同じ位置にProject Assetパスを表示し、使用箇所の参照行にRendererとMaterialの選択/Pingを用意した。Missing対象は行を無効化して理由を表示する。
- **候補10: 実装（手動検証待ち）** — 既存の最小ウィンドウ幅620pxを維持し、検索ツール列は狭幅時に折り返す。名前/パス/詳細は省略表示とツールチップ、値列は固定幅で優先する。最小幅・長名・大量項目のGUI手動スモークテストは未実施。
- **候補11: 実装** — Avatar、Platform、解析時刻、Overall、主要問題、各Metricの値/Rank/件数/精度、警告をクリップボードへコピーするボタンを追加した。Unavailableの説明はMetric名のツールチップから確認できる。

SceneハイライトはSelection等の状態に左右されるため未実装。Importer復元と終了時Draft保持はLibraryファイルを使う仮実装で、まだUnity GUIによる保存/復元動作確認が必要である。Applyバックアップは次のApplyで上書きされる。Unity GUIで混在値表示、Apply/復元後のImporter実値と再インポート、再オープン時のDraft保持、参照行の移動、狭い幅の手動確認を行う。


## 12. 現在の検証状況

- Unity 2022.3.22f1向けコンパイラでコンパイル成功を確認済み。
- asmdefはEditor専用になっている。
- パッケージ外のコードへ直接依存していない。
- 実Avatarごとの数値妥当性、Platform別Format、圧縮後の見た目は移行先でも再確認すること。
- 2026-09-30: 最終ソースをUnity同梱Roslyn (`DotNetSdkRoslyn/csc.dll`) とUnity Bee生成のEditor用response fileでコンパイル成功。キャンセル可能な段階式スキャンを含む最新変更後も再実行し、Tempへ検証DLL (89,088 bytes) を出力した。プロジェクト成果物は上書きしていない。
- Unity Editor batchmodeでの実行は、Hubライセンス修復後もEditorのLicensingClient IPCが60秒でタイムアウトし終了コード199となった。試行時にreadonly databaseメッセージも出た。したがってEditorドメインロード/ILPostProcessとGUI手動スモークテストは未確認。Hub/Editorを正常起動できる状態でImporter Apply/復元、Draft再オープン、Hierarchy移動を確認すること。
- 最終差分の静的確認ではUI Toolkit `BaseField.showMixedValue`、ShaderUtilによるTexture property参照列挙、Contributor.Group/Mesh参照、Draft/BackupのJSON構造と呼び出し整合性を確認した。`git diff --check`に空白エラーなし。Unity上の表示・入力挙動とImporter Apply/復元の実データ結果は未確認。

### Unity GUIでの手動検証手順（未実施）

1. バージョン管理済みのテスト用Textureを2枚選び、ImporterのMax Size/Compressionを別々にする。Texture内訳で両方を選び、混在表示を確認して1項目だけ変更する。選択両方にPending表示が出ること、他項目の値は保持されることを確認する。
2. Applyダイアログに対象Asset/Platformが出ることを確認し、Apply後にImporter Inspectorで値を確認する。`直前のApplyを復元`を実行し、対象Platformの元値へ戻ることを確認する。
3. Draftを作ってウィンドウを閉じ、「保持して閉じる」を選ぶ。再度開いてDraftが復元されることを確認する。同じ手順で破棄を選んだ場合は再表示されないことを確認する。
4. スキャン後にAvatarのHierarchyまたはUndo対象プロパティを変更し、古い結果の警告が出ること、再スキャンで警告が消えることを確認する。
5. Texture使用箇所のRendererとMaterialボタンがHierarchy/Projectの対象を選択・Pingすることを確認する。
6. Scan中の進捗バーでキャンセルし、結果が古い情報として再利用されず再スキャンできることを確認する。各段階の処理中はキャンセルが反映されないことも確認する。
7. ウィンドウ幅620px、長いObject/Asset名、大量Contributorを用意し、フィルター・グループ折りたたみ・コンパクト表示・コピー結果を確認する。

## 13. 2026-09-30 UI改善

- Texture圧縮設定パネルをTexture一覧のスクロール領域から分離し、内訳画面の下端に固定した。Texture未選択時はパネルを隠し、行のチェック時に下部領域が開いて一覧が縮む。UI Toolkitの高さ遷移による時間ベースのスライドアニメーションは未実装で、現状はレイアウトによる表示切り替え。
- Texture行の各Texture名の下に使用箇所・Material参照Foldoutを設置した。展開時にだけAvatar配下のRenderer/Materialを検索し、RendererのHierarchy選択・Ping、MaterialのProject選択・Ping、Material Slot、Texture property名を表示する。従来の圧縮パネル内にあった一括参照リストは削除。
- Texture / Skinned Mesh / Material Slot / PhysBone各行の名前直下にあったHierarchy/Assetパス表示を削除。行の名前ツールチップと検索対象にはパスを残した。Contributor.Detailがある詳細行では、Missing等の詳細情報を引き続き表示する。
- Textureメモリの内訳見出しを明示的に左寄せに指定。
- Textureメモリ一覧では、サムネイル右側のTexture名ラベルも明示的に左寄せに指定し、ラベル領域を左揃えにした。
- Material Slot内訳ではContributorの代表Material、Skinned Mesh内訳ではMeshをUnity `AssetPreview.GetAssetPreview`で画像表示する。非同期プレビューの生成中は既存のMiniThumbnailを出し、生成完了後に差し替える。ほかの内訳のサムネイル表示は変更しない。

## 14. SDKビルド履歴

- メインタブ「ビルド履歴」を追加。VRChat SDK Avatar Builderの公開SDKイベントへ接続し、SDKビルド開始時にAvatarのSDK性能Metricを取得、成功/失敗時にレポート履歴を保存する。
- 成功時はSDKから通知されるBundle pathを使ってBundleのファイルサイズを取得し、Download Size MetricをExact値へ更新する。Uncompressed SizeはQuick Scan/API経由で取れない場合Unavailableのまま表示する。
- 履歴には日時、Avatar名、Platform、成否、Overall Rank、Metric名/値/Rank/Accuracy/説明、Quick Scan警告を保存する。TextureメモリはSDK合計Metricのみで、Textureごとの内訳/contributorは保存しない。
- 履歴UIをカードと詳細レポートに再構成した。保存件数/成功/失敗の概要カード、成否色付きAvatar履歴カード、選択中レポートのStatus/Overall Rank/Bundle Size/Metric数、Section別Foldoutメトリクス表を表示する。Metric説明はツールチップに表示する。
- `Library/AvaBuildInspector/build-history.json`に直近100件を保存する。履歴記録実装後のビルドのみが対象で、既存の過去ビルド履歴は遡及復元しない。ビルド失敗イベントがSDKから発行されなかった場合、その失敗は記録されない可能性がある。
- 性能Metricはビルド開始時点のSource Avatar解析値であり、ビルド後の変換済みObject完全スナップショットではない。SDKのvalidationメッセージ全文も公開イベントからは取得していない。成功Bundleの圧縮サイズは正確値、その他のMetricはSDKが返した精度区分を維持する。
- Unity GUIのSDK Build Success/Errorイベント実動作、Bundle file size反映、履歴JSONの再起動後読込は未確認。Unity同梱Roslynで新規履歴ファイルを含めたEditorアセンブリ直接コンパイルは成功。GUI確認時はPC/Androidの成功ビルドと失敗ビルドを1件ずつ行い、履歴・Bundleサイズ・Unavailable精度・プロジェクト再起動後の保持を確認する。
- Texture行の容量表示左にあるMaterial移動ボタンを`Location`と表記し、参照中MaterialをProjectで複数選択してPingする。その隣の開閉ボタンは「参照元オブジェクト」/「参照元オブジェクト・閉じる」と表記する。展開領域では各参照をボタンではないTexture一覧風の行で表示し、行クリックでHierarchy内のRendererのGameObjectを選択・Pingする。名前下のMaterial/Slot/property/Asset pathの小さい説明ラベルは表示しない。親フォルダーグループ化がONの場合、参照元ObjectもHierarchy上の直近親GameObjectパスでグループ化する。参照検索結果はLocationボタンと参照パネルで共有する。
- Skinned Mesh / Material SlotのグループFoldoutは初期状態を閉じる。ユーザーが開いた状態は再描画時に保持する。
- Textureメモリ一覧に「親フォルダーでグループ化」オプションを追加。初期状態はOFF。ONにすると3階層（Assets直下のフォルダー名。Assets自体は表示しない / 現在の親ディレクトリ / Texture行）で表示する。Assets直下のTextureは「(ルート)」へ、PackagesなどAssets外は「Assets以外」へまとめ、Assets内グループから間隔を空ける。グループ状態は再描画前にVisualElementから読み取り、チェック変更やオプション切り替え後も保持する。グループ表示時の初回状態は展開。パスが取得できないAssetは専用グループにまとめる。
- Skinned Mesh / Material Slotにも独立した「親フォルダーでグループ化」オプションを追加。OFF時は従来のAvatar内グループ表示、ON時はTextureと同じ3階層表示。Skinned Meshは関連Mesh Asset、Material Slotは代表Material Assetの親フォルダーを基準にする。表示対象のAssetがない場合は「(パスなし)」にまとめる。
- 今回はUnity Editor GUIでの見た目と操作検証は未実施。特に下端パネルの高さが小さいウィンドウで圧迫しないか、選択時の表示が期待どおりか、使用箇所のRenderer/Material移動を手動確認する。滑らかなスライドアニメーションが必要ならUnity 2022.3で利用可能なUI Toolkit APIを実機確認して追加する。
- 今回のソース変更後、Unity同梱RoslynのEditor用response fileを使った直接コンパイルに成功。検証DLLは`Temp/AvaBuildInspector.Editor.verify.dll`へ出力し、Bee生成済みアセンブリは上書きしていない。バッチ起動も再試行したがLicenseClientのIPC接続拒否で終了コード199となり、GUI検証には進めなかった。


### ビルド履歴UI調整（2026-09-30）
- 「保存済み・成功・失敗」の概要カードを削除し、選択中のビルド結果を中心に確認できる構成に変更。
- 履歴件数に応じて一覧領域の高さを調整（最小54px、最大230px）。一覧が少ない場合に詳細エリアが離れすぎないよう、見出しと詳細カードの余白も縮小。
- 検証: Unity同梱RoslynによるEditorアセンブリ直接コンパイル成功、`git diff --check` 成功。Unity Editor上での描画確認は未実施。

### マテリアルタブ分割と一覧追加（2026-09-30）
- 内訳一覧の「マテリアルスロット」を「マテリアル」に変更し、配下のサブタブで「マテリアル」と従来の「マテリアルスロット」を切り替えるUIを追加。
- 新しいマテリアル一覧はAvatar配下のRenderer.sharedMaterialsからMaterial Assetを参照単位で重複排除して表示する。行にはMaterialプレビュー、使用スロット数、参照Renderer数を表示し、行クリックでProject上のMaterialを選択・Ping、右側のObject操作で代表RendererのGameObjectをHierarchy上で選択する。
- Materialごとのスロット数は同一MaterialがRenderer間/複数slotで何回使われるかの合計。Missing slotはMaterial AssetがないためMaterial一覧には出ず、従来のスロット内訳で確認する。
- 親フォルダーグループ化はMaterial Asset path基準。未保存/Asset pathなしMaterialは専用グループにまとめる。
- 検証: Unity同梱Roslyn Editorアセンブリ直接コンパイル、`git diff --check` を実施。Unity Editor上でのUI操作・表示確認は未実施。

### Materialグループパス修正（2026-09-30）
- Material一覧のAssetフォルダーグループ化は、代表Renderer（SecondaryTarget）ではなくMaterial Asset（Target）のAssetDatabaseパスを参照するよう修正。Materialの通常表示は無用なフォルダー単位の折りたたみを作らず、グループ化を有効にした場合だけAsset階層で分類する。
- Material Slotはディレクトリグループ化オプションを外し、既存のRenderer GameObject階層（Contributor.Group）で常に分類する。
- 検証: Unity同梱Roslyn Editorアセンブリ直接コンパイル、`git diff --check` 成功。AssetDatabaseの実アセットパスとUI表示はUnity Editor上で未確認。

### Textureディレクトリ容量の可視化（2026-09-30）
- 親フォルダーグループ化時、各Asset階層と親ディレクトリの見出しにTexture推定メモリ合計（MB）と全Texture合計に対する割合を表示し、比率バーを追加。
- 集計基準は検索/フィルター前の全Texture contributorで固定。絞り込み中もディレクトリの総容量と割合は変化せず、一覧の表示件数だけが絞り込み結果に応じて変わる。割合の母数はTexture別Profiler推定値の合計であり、SDK報告の合計とは一致しない場合がある。
- 検証: Unity同梱Roslyn Editorアセンブリ直接コンパイル、`git diff --check` を実施。

### Texture圧縮対象をチェック選択へ一本化（2026-09-30）
- オレンジ色のPending/Draft点、保留件数、変更破棄ボタン、Draftの終了時確認、LibraryへのDraft保存・再読込を削除。
- チェック中のTextureをすべて現在の対象PlatformへのApply対象にする。未変更の設定もApply対象となり、対象はボタン押下時のチェック状態で決まる。設定UIの混在値と編集値はウィンドウ中だけ保持し、適用後はImporterから再読込する。
- Avatarを切り替えるとTexture選択と一時設定を消去する。Platform別のImporterバックアップ/復元は維持。
- 旧実装の`Library/AvaBuildInspector/pending-texture-drafts.json`（3件）は今回削除。今後は生成・読込しない。
- 検証: Unity同梱Roslyn Editorアセンブリ直接コンパイル、`git diff --check` を実施。Unity Editor GUIでのチェック別Apply動作は未実施。

### Texture圧縮パネルのボタン表記整理（2026-09-30）
- 圧縮設定パネルの操作語を日本語に統一。「表示中をチェック」「チェックを解除」「選択中に適用」「直前の設定に戻す」とし、適用対象件数もチェック状態と一致する表記にした。
- 確認ダイアログの操作ボタンも「適用」に統一。
- 検証: Unity同梱Roslyn Editorアセンブリ直接コンパイル、`git diff --check` 成功。

### Texture容量割合の表示切り替え（2026-09-30）
- Textureメモリで「親フォルダーでグループ化」をONにしたときだけ「容量の割合を表示」オプションを表示。初期値はON。
- OFF時はAsset階層/親ディレクトリ見出しの割合表示と比率バーを隠す。推定容量（MB）は引き続き表示する。合計・割合の全Texture基準と検索/フィルター非依存の集計は維持。
- 検証: Unity同梱Roslyn Editorアセンブリ直接コンパイル、`git diff --check` を実施。

### Avatar入力と再スキャン操作の配置（2026-09-30）
- ヘッダーのAvatar ObjectField直後に「再スキャン」ボタンを移動し、Platform選択より前に配置。Avatarのドロップ/選択操作と再スキャンを隣接させた。
- 検証: Unity同梱Roslyn Editorアセンブリ直接コンパイル、`git diff --check` 成功。

### Texture割合表示のフラット一覧対応と折りたたみ時の維持（2026-09-30）
- 「容量の割合を表示」は親フォルダーでグループ化していない一覧でも使えるよう変更。OFF時はフラット一覧の個別Texture割合も非表示。
- フラット一覧では各Textureの全Texture推定合計に対する割合とバーを表示する。
- グループ表示の割合バーはFoldout内部ではなく見出しの外側に置き、親グループ/子グループを閉じても該当階層の割合が見えるようにした。検索・フィルターによらず全Texture基準の集計は維持。
- 検証: Unity同梱Roslyn Editorアセンブリ直接コンパイル、`git diff --check` を実施。
