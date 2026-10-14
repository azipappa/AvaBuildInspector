# AvaBuild Inspector

<p align="center">
  <img src="Packages/com.azipaworks.ava-build-inspector/Editor/Resources/AvaBuildInspectorIcon.png" width="128" alt="AvaBuild Inspector icon">
</p>

<p align="center">
  <strong>VRChat Avatarのパフォーマンスと負荷要因を、アップロード前にすばやく確認</strong><br>
  Azipa Toolsのエディター拡張です。
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Unity-2022.3-222c37?logo=unity&logoColor=white" alt="Unity 2022.3">
  <img src="https://img.shields.io/badge/VRChat-Avatar-2d9bf0" alt="VRChat Avatar">
  <img src="https://img.shields.io/badge/VPM-0.1.1-6f42c1" alt="VPM 0.1.1">
  <img src="https://img.shields.io/badge/License-MIT-green" alt="MIT License">
</p>

## 概要

AvaBuild Inspectorは、Scene上のVRChat Avatarを対象に、VRChat SDKが算出するPC / Androidのパフォーマンスランクと、その原因となる内訳を確認するツールです。元のAvatarは変更しない非破壊の解析です。

## 主な機能

- VRChat SDKによるPC / Androidのパフォーマンス値とランク表示
- Avatar指定時の自動Quick Scanと手動再スキャン
- Poor / Very Poorの主要問題と全メトリクスの一覧（Exact / Estimated / Unavailable表示付き）
- テクスチャメモリ、スキンドメッシュ、マテリアル、マテリアルスロット、PhysBoneの内訳
- 複数テクスチャの圧縮設定（Default / PC / Android）をまとめて編集・一括適用
- 未使用の可能性があるPhysBoneの検出
- SDKビルド結果の履歴と推移の表示

テクスチャメモリとPhysBoneの衝突チェック数の内訳は診断用の推定値です。

## 動作環境

- Unity `2022.3`
- VRChat SDK Avatars `3.10.x`

## インストール

### VCC / ALCOM（推奨）

VCCまたはALCOMへAzipa ToolsのVPMリポジトリを追加し、対象プロジェクトのパッケージ管理画面から **AvaBuild Inspector** を追加してください。

```
https://azipappa.github.io/azipa-tools-vpm/index.json
```

| 項目 | 値 |
| --- | --- |
| パッケージID | `com.azipaworks.ava-build-inspector` |
| 表示名 | `AvaBuild Inspector` |
| 対応Unity | `2022.3` |

### GitHub Release

Releaseに添付された`com.azipaworks.ava-build-inspector-<version>.zip`は、`package.json`が直下にあるVPM互換パッケージです。

## 使い方

1. `Tools > Azipa Tools > AvaBuild Inspector`を開きます。
2. `VRCAvatarDescriptor`を持つAvatarをAvatar欄へドラッグ＆ドロップします。
3. Quick Scanが自動で実行され、結果が表示されます。

## ライセンス

[MIT License](LICENSE.txt)
