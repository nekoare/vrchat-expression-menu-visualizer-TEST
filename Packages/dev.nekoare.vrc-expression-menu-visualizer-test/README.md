# VRChat Expression Menu Visualizer

![Unity](https://img.shields.io/badge/Unity-2022.3+-blue.svg)
![VRChat SDK](https://img.shields.io/badge/VRChat%20SDK-3.5.0+-green.svg)
![ModularAvatar](https://img.shields.io/badge/ModularAvatar-1.9.0+-orange.svg)

VRChat Expression Menu Visualizer は、VRChatのエクスプレッションメニューを視覚化・編集するためのUnity Editorツールです。ModularAvatarとの完全な互換性を提供し、直感的なインターフェースでメニュー階層を表示・編集できます。

## 特徴

### 🎯 主要機能
- **メニュー階層の視覚化**: VRChatエクスプレッションメニューの構造を直感的に表示
- **リアルタイム編集**: メニュー項目の追加、削除、編集をリアルタイムで実行
- **サブメニュー生成**: 新しいサブメニューを簡単に作成し、自動的にアセットファイルを生成
- **ドラッグ&ドロップ**: メニュー項目の並び替えや移動を直感的に操作

### 🔧 ModularAvatar 対応
- **MA Menu Installer 完全サポート**: ModularAvatarのMenu Installerを適切に処理・表示
- **MA Menu Item 統合**: Menu Itemコンポーネントとの適切な優先度管理
- **Editor Only フィルタリング**: Editor Onlyタグが設定されたコンポーネントを自動除外
- **階層優先度**: MA Menu InstallerがMA Menu Itemより優先される正確な動作

### 📱 ユーザビリティ
- **日本語・英語対応**: UI言語の切り替えが可能
- **グリッド・リスト表示**: メニュー表示形式を選択可能
- **統計情報表示**: メニュー使用量やコントロール数の表示
- **検索機能**: メニュー項目の高速検索

## インストール方法

### VCC (ALCOM) を使用する場合

1. VCC (ALCOM) を開く
2. 「Settings」→「Packages」→「Add Repository」をクリック
3. 以下のURLを入力:
   ```
   https://nekoare.github.io/vrchat-expression-menu-visualizer/index.json
   ```
4. 「I Understand, Add Repository」をクリック
5. プロジェクトの「Packages」タブから「VRChat Expression Menu Visualizer」を追加

### 手動インストール

1. [Releases](https://github.com/nekoare/vrchat-expression-menu-visualizer/releases) から最新版をダウンロード
2. UnityPackageファイルをプロジェクトにインポート

## 使い方

### 基本的な使用方法

1. Unity Editorで「Tools」→「VRChat Expression Menu Visualizer」を選択
2. VRCAvatarDescriptorを持つアバターを選択
3. メニュー構造が自動的に表示されます

### 編集モード

1. 「編集モード」をオンにする
2. メニュー項目をクリックして選択
3. ドラッグ&ドロップで項目を移動・並び替え
4. 「💾 保存」ボタンで変更を保存

## 必要環境

- Unity 2022.3以降
- VRChat SDK Avatars 3.7.0以降
- ModularAvatar 1.9.0以降（推奨）

## ライセンス

MIT License - 詳細は [LICENSE](./LICENSE) ファイルを参照してください。

## サポート

- バグ報告・機能要望: [GitHub Issues](https://github.com/nekoare/vrchat-expression-menu-visualizer/issues)
- 最新情報: [GitHub Releases](https://github.com/nekoare/vrchat-expression-menu-visualizer/releases)
