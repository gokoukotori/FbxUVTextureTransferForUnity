# Changelog

このプロジェクトの主な変更はこのファイルに記録します。

## [0.1.2] - 2026-08-22

### Fixed

- NDMF Apply on Play がコンポーネントの `OnEnable` より先に実行された場合に、FBX UV Texture Transfer Layer の転写結果が反映されない問題を修正。
- FBX UV Texture Transfer Layer がVRC SDKの不正コンポーネントとして検出され、SDK Control Panelでエラーになる問題を修正。

## [0.1.1] - 2026-08-22

### Added

- Source Texture のalphaを常に保持し、透明pixelと転写coverageの穴を区別して補完する透明テクスチャ対応を追加。

### Fixed

- Target の Mesh / Submesh 候補が1組だけの場合の自動選択を復元し、InspectorやProjectの更新で未保存のMesh選択が失われる問題を修正。

## [0.1.0] - 2026-08-18

### Added

- 初回リリース

### Known limitations

- TexTransTool の Experimental API に依存。
- 未確認の将来バージョンに対する互換性は保証しない。
- TexTransTool の Unity backend のみ対応。
- Windows / Direct3D 11 のみ受入対象。
- 1 個の MLIC が扱える転写先テクスチャは 1 個。
