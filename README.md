# VRChat Expression Menu Visualizer (TEST)

![Unity](https://img.shields.io/badge/Unity-2022.3+-blue.svg)
![VRChat SDK](https://img.shields.io/badge/VRChat%20SDK-3.5.0+-green.svg)
![ModularAvatar](https://img.shields.io/badge/ModularAvatar-1.9.0+-orange.svg)

> **⚠️ これはテスト版リポジトリです**
> このリポジトリは VRChat Expression Menu Visualizer のアップデート前のチェックを行うためのテスト用環境です。
> 本番環境では [オリジナルのリポジトリ](https://github.com/nekoare/vrchat-expression-menu-visualizer) をご使用ください。

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
3. 以下のURLを入力 **(テスト版)**:
   ```
   https://nekoare.github.io/vrchat-expression-menu-visualizer-TEST/index.json
   ```
4. 「I Understand, Add Repository」をクリック
5. プロジェクトの「Packages」タブから「VRChat Expression Menu Visualizer (TEST)」を追加

### 手動インストール

1. [Releases](https://github.com/nekoare/vrchat-expression-menu-visualizer-TEST/releases) から最新版をダウンロード
2. UnityPackageファイルをプロジェクトにインポート

## 使い方

### 基本的な使用方法

1. Unity Editor で `Tools` → `VRChat Expression Menu Visualizer` を選択
2. アバターを選択すると、自動的にエクスプレッションメニューが表示されます
3. メニュー項目をクリックして詳細を表示・編集

### サブメニューの作成

1. 編集モードを有効にする
2. 「Create Submenu」ボタンをクリック
3. サブメニュー名を入力
4. 自動的にVRCExpressionsMenuアセットが作成され、親メニューにリンクされます

### ドラッグ&ドロップ編集

1. 編集モードを有効にする
2. メニュー項目をドラッグして他の位置にドロップ
3. 階層間の移動も可能

## 必要な環境

- **Unity**: 2022.3 以上
- **VRChat SDK**: 3.5.0 以上
- **ModularAvatar**: 1.9.0 以上 (オプション)

## トラブルシューティング

### よくある問題

**Q: MA Menu Installerの内容が表示されない**
A: MA Menu Installerは「ギミック２」のようなエントリのみが表示され、詳細な項目内容は表示されません。これは実際のゲーム内動作と一致する仕様です。

**Q: Editor Onlyのギミックが表示される**
A: アバター直下のGameObjectに「EditorOnly」タグが設定されている場合、そのコンポーネントは表示されません。タグ設定を確認してください。

**Q: サブメニューを作成しても保存されない**
A: 新しい実装では、サブメニュー作成時に自動的にアセットファイルが生成されるため、保存ボタンを押す必要はありません。

## 貢献

プルリクエストやIssueの報告を歓迎します。

## ライセンス

MIT License - 詳細は [LICENSE](LICENSE) ファイルを参照してください。

## 更新履歴

詳細な更新履歴は [CHANGELOG.md](CHANGELOG.md) を参照してください。

## 作者

- **nekoare** - [GitHub](https://github.com/nekoare)

## 謝辞

- VRChat コミュニティ
- ModularAvatar 開発チーム