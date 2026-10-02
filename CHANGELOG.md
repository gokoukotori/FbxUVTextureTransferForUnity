# Changelog

このプロジェクトの主な変更はこのファイルに記録します。

## [Unreleased]

## [0.2.0-beta.5] - 2026-10-02

### Changed

- 通常転送とアイ転送のRenderer列挙・メッシュ取得・転送先候補の抽出を共通化。通常転送のMesh単位の集約と、アイ転送のRendererごとの位置計測を維持。

### Fixed

- アイ転送エディターで、パーティクルなどの対象外RendererやMeshFilterのないオブジェクトを含むModel / Prefabを指定すると、例外で描画が中断され、転送先欄などが表示されなくなる問題を修正。

## [0.2.0-beta.4] - 2026-09-29

### Added

- アイテクスチャを片目ごとに転送する `FBX UV Eye Texture Transfer Layer`（β版）と専用エディターを追加。TexTransToolのレイヤーとして転送し、NDMFで設定を検証。
- アイ転送に `従来型`・`トポロジー型` の補正方式と、瞳孔の `別パーツあり`・`別パーツなし（画像一体型）` の構成選択を追加。
- 別パーツの瞳孔を標準Meshから自動計測し、転送元の縦横比と虹彩に対する投影面積比を保って配置。位置・大きさ・縦横比の手動調整に対応。
- トポロジー型で虹彩の内周を別パーツの瞳孔外周へ追従させ、画像一体型では両側のMeshで一意の辺の収束点を検出できる場合に瞳孔中心を対応付け。
- アイ転送に補正ピン・固定ピンによる手補正を追加。目ごとに最大32個のピン、影響範囲の調整、補正前との比較、Undo / Redoに対応。

### Changed

- 通常転送・メイク転送エディターのUVプレビューに、スクロールによる拡大・縮小、中ボタンによる移動、全体表示・候補アイランドの拡大を追加。
- READMEにアイ転送の設定・手補正の操作手順と対応条件・制限を追記。

## [0.2.0-beta.3] - 2026-09-26

### Changed

- 通常転送の境界探索で空間分割によって候補三角形を絞り込み、Region Binding増加時のプレビュー生成負荷を軽減。
- Regionごとの変形結果を直近2種類の出力サイズ・向きで再利用し、三角形・Boundsの変更やUndo時は内容比較で更新。
- 通常転送・メイク転送コンポーネントの追加メニューを `Gokoukotori > FBX UV Texture Transfer` 配下へ、UV Region Editorを `Tools > Gokoukotori > FBX UV Texture Transfer` 配下へ移動。

## [0.2.0-beta.2] - 2026-09-23

### Changed

- メイク転送の繰り返し検証でMeshの内容比較を維持しながら、ハッシュ計算と三角形解析を再利用するよう改善。
- メイク転送の再描画でMaterial・Mesh・GPUバッファを再利用し、無効化・削除・Play Mode遷移・スクリプト再読み込み時に解放するよう改善。

## [0.2.0-beta.1] - 2026-09-08

### Added

- メイク転送コンポーネント追加

## [0.1.3] - 2026-08-23

### Fixed

- Prefab VariantをSceneへ配置した際、Model / Prefabの自己参照がScene Objectとして誤判定され、UV Region Editorで新規Region BindingのSource / Target Mesh候補を選択できない問題を修正。

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
