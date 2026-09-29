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

優先度の高い候補:

1. Textureの複数選択時、圧縮設定の各項目で選択しているテクスチャの設定が違う場合空欄とする・もしくは別の表示方法とするようにしてほしい。
2. スキンドメッシュ・マテリアルスロット・PBコンポーネントの視認性が悪いので視認性を上げる。
   具体的には、これらの項目はかなり項目が多くなることが予想されるが、すべて一個一個を縦一列に並べているので視認性が悪い。この視認性の悪さを解消してほしい。アバター改変をするときに使いやすいUIに作り替えてください。
3. テクスチャメモリも、現在は使っている容量とそのテクスチャメモリの場所がわかるだけだが、圧縮してもよいかの判断がつきやすいように見たいテクスチャをクリックするとそのテクスチャが使用されているマテリアルを参照しているレンダーオブジェクトが表示されるようにしてほしい。できればScene上で色がついて表示されるなどの演出がほしいが、無理そうならほかの代替案で実装。
4. テクスチャの圧縮設定は最初から表示し、配置はテクスチャの上（現在実装）ではなく、テクスチャの下に配置してほしい。
5. 


## 12. 現在の検証状況

- Unity 2022.3.22f1向けコンパイラでコンパイル成功を確認済み。
- asmdefはEditor専用になっている。
- パッケージ外のコードへ直接依存していない。
- 実Avatarごとの数値妥当性、Platform別Format、圧縮後の見た目は移行先でも再確認すること。

