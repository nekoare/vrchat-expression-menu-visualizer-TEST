# Changelog (TEST)

> **⚠️ これはテスト版リポジトリです**
> このリポジトリは VRChat Expression Menu Visualizer のアップデート前のチェックを行うためのテスト用環境です。

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## 更新履歴

### 1.0.0
- VRChat Expression Menu Visualizer の初回リリース
- VCCリポジトリ対応
- ModularAvatar完全対応
- 日本語・英語UI対応

## [1.0.0] - 2025-06-26

### Added
- Initial release of VRChat Expression Menu Visualizer
- Menu hierarchy visualization for VRChat Expression Menus
- Real-time menu editing with drag & drop support
- Automatic submenu creation with asset generation
- Full ModularAvatar support:
  - MA Menu Installer integration
  - MA Menu Item compatibility
  - MA Object Toggle support
- Editor Only filtering for development-time components
- Bilingual support (Japanese/English)
- Grid and list view modes
- Menu statistics display
- Search functionality for menu items
- Control editing interface

### Features
- **Menu Visualization**: Clear hierarchical display of expression menus
- **Edit Mode**: Interactive editing with visual feedback
- **Submenu Generation**: One-click submenu creation with automatic asset linking
- **ModularAvatar Priority**: Proper handling of MA Menu Installer vs MA Menu Item precedence
- **Editor Only Support**: Automatic filtering of Editor Only tagged components (avatar-scoped)
- **Drag & Drop**: Intuitive menu item reordering and hierarchy changes
- **Localization**: Full Japanese and English language support
- **Statistics**: Real-time display of menu usage and control counts

### Technical
- Unity 2022.3+ compatibility
- VRChat SDK 3.5.0+ support
- ModularAvatar 1.9.0+ integration
- Reflection-based MA component detection for future compatibility
- Asset database integration for persistent menu changes
- Assembly definition files for proper dependency management

### Dependencies
- VRChat SDK Avatars 3.5.0+
- ModularAvatar 1.9.0+ (optional but recommended)

---

## Development Notes

This project was developed to address the need for a comprehensive tool to visualize and edit VRChat expression menus, with particular focus on ModularAvatar integration. The tool aims to provide an accurate representation of how menus appear in-game, especially regarding the interaction between MA Menu Installers and MA Menu Items.

### Key Design Decisions
- MA Menu Installers take precedence over individual MA Menu Items
- Editor Only filtering is limited to avatar hierarchy scope
- Submenu assets are created immediately upon generation
- Visual feedback matches actual in-game menu behavior