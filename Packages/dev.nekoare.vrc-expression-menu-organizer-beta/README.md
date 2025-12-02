# 新メニュー整理ツール-beta

![Unity](https://img.shields.io/badge/Unity-2021.3+-blue.svg)
![VRChat SDK](https://img.shields.io/badge/VRChat%20SDK-3.7.0+-green.svg)
![ModularAvatar](https://img.shields.io/badge/ModularAvatar-1.9.0+-orange.svg)

VRChat Expression Menu Organizer は、VRChatのエクスプレッションメニューを視覚化・編集するための高機能Unity Editorツールです。Modular Avatarとの完全な互換性を提供し、マーカーベースの管理システムにより、メニュー構造の一元管理を実現します。

> ⚠️ **ベータ版について**: このツールは開発中のベータ版です。安定版が必要な場合は「メニュー整理ツール/安定版」をご利用ください。

## 特徴

### 🎯 主要機能
- **メニュー構造の可視化**: VRChat標準メニューとMA メニューを統合したツリー/グリッド表示
- **リアルタイム編集**: メニュー項目の追加、削除、編集をリアルタイムで実行
- **サブメニュー生成**: 新しいサブメニューを簡単に作成し、自動的にアセットファイルを生成
- **ドラッグ&ドロップ**: メニュー項目の並び替えや移動を直感的に操作
- **検索機能**: コントロール名、タイプ、パラメータ名によるフィルタリング
- **統計情報表示**: メニュー使用量やコントロール数の表示

### 🔧 Modular Avatar 完全対応
- **MA Menu Installer 完全サポート**: ModularAvatarのMenu Installerを適切に処理・表示
- **MA Menu Item 統合**: Menu Itemコンポーネントとの適切な優先度管理
- **除外/非除外ワークフロー**: MA Menu Item/Installerを除外/非除外として管理
- **マーカーベース管理**: 3種類のマーカーコンポーネントで項目の状態を永続化
- **自動MA変換**: Expression MenuをMA構造へ自動変換し、「メニュー項目」ルート配下にミラーリング

### 📱 ユーザビリティ
- **日本語・英語対応**: UI言語の切り替えが可能
- **グリッド・リスト表示**: メニュー表示形式を選択可能
- **統計情報表示**: メニュー使用量やコントロール数の表示
- **検索機能**: メニュー項目の高速検索

## マーカーコンポーネント

ツールは以下の3種類のマーカーコンポーネントを使用して項目の状態を管理します:

### 1. ExprMenuVisualizerExcluded
- **用途**: 除外項目（変換対象外の項目）をマーク
- **効果**: 元のGameObjectに付与され、MA Menu Installerは削除されない

### 2. ExprMenuVisualizerIncluded
- **用途**: 非除外項目（変換対象の項目）をマーク
- **効果**: 元のGameObjectに付与され、「メニュー項目」内に新規GameObjectが生成される

### 3. ExprMenuVisualizerGenerated
- **用途**: ツールによって「メニュー項目」内に生成されたGameObjectをマーク
- **効果**: ツールウィンドウ上で表示・編集可能

## インストール方法

### VCC (ALCOM) を使用する場合

1. VCC (ALCOM) を開く
2. 「Settings」→「Packages」→「Add Repository」をクリック
3. 以下のURLを入力:
   ```
   https://nekoare.github.io/vrchat-expression-menu-visualizer-TEST/index.json
   ```
4. 「I Understand, Add Repository」をクリック
5. プロジェクトの「Packages」タブから「新メニュー整理ツール-beta」を追加

## 使い方

### 基本的な使用方法

1. Unity Editorで「メニュー整理ツール」→「最新版-beta」を選択
2. VRCAvatarDescriptorを持つアバターを選択
3. 除外選択ダイアログでMA Menu Item/Installerの除外/非除外を選択
4. メニュー構造が自動的に表示されます

### 編集モード

1. 「編集モード」をオンにする
2. メニュー項目をクリックして選択
3. ドラッグ&ドロップで項目を移動・並び替え
4. 「💾 保存」ボタンで変更を保存（自動的にMA変換が実行されます）

## 必要環境

- Unity 2021.3以降
- VRChat SDK Avatars 3.7.0以降
- ModularAvatar 1.9.0以降（必須）

## 安定版との違い

| 機能 | 安定版 | 最新版-beta |
|------|--------|-------------|
| 基本的な視覚化 | ✅ | ✅ |
| ドラッグ&ドロップ編集 | ✅ | ✅ |
| MA Menu Installer対応 | 基本対応 | 完全対応 |
| マーカーベース管理 | ❌ | ✅ |
| 除外/非除外ワークフロー | ❌ | ✅ |
| 自動MA変換 | ❌ | ✅ |

## トラブルシューティング

| 現象 | 対処 |
|------|------|
| 「選択待ち」メッセージが出る | 除外選択ダイアログで選択を完了してください |
| MA変換が行われない | Modular Avatarパッケージが導入済みか確認してください |
| 除外した項目が非除外として扱われる | マーカーコンポーネントが正しく付与されているか確認してください |

## ライセンス

MIT License - 詳細は [LICENSE](https://github.com/nekoare/vrchat-expression-menu-visualizer-TEST/blob/main/LICENSE) ファイルを参照してください。

## サポート

- バグ報告・機能要望: [GitHub Issues](https://github.com/nekoare/vrchat-expression-menu-visualizer-TEST/issues)
- 最新情報: [GitHub Releases](https://github.com/nekoare/vrchat-expression-menu-visualizer-TEST/releases)

---
最終更新: 2025-12-02
