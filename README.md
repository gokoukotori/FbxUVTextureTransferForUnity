# FBX UV Texture Transfer

FBX UV Texture Transfer は、VRChat アバター上の各 Region Binding が転写元・転写先の UV 領域を一組として保持し、TexTransTool の `MultiLayerImageCanvas`（MLIC）上でテクスチャを非破壊転写する Unity エディタ拡張です。VRChat 以外の GameObject は対象にしません。

## 対応環境

- Unity `2022.3.22f1` 以上
- VRCSDK Avatars `3.10.4` 以上
- TexTransTool `1.1.0-beta.9` 以上
- NDMF `1.14.4` 以上
- Windows / Direct3D 11
- TexTransTool の Unity backend

依存パッケージには上限を設定せず、記載した最小バージョン以降を許可します。ただし、個別に受入確認した環境は Unity `2022.3.22f1`、VRCSDK Avatars `3.10.4`、TexTransTool `1.1.0-beta.9`、NDMF `1.14.4` です。将来バージョンの互換性を個別に保証するものではありません。

## 重要な制約

本パッケージは TexTransTool の Experimental API である `ExternalToolAsLayer` と `IExternalToolCanBehaveAsImageLayerV1` を使用します。この API には `TexTransToolStablePublicAPI` が付与されておらず、将来の TexTransTool 更新で破壊的に変更される可能性があります。依存範囲に上限は設けませんが、未確認の将来バージョンでエラーが発生した場合は互換性を保証しません。

Source と Target の UV アイランド選択は各 Region Binding に保存されます。表示候補が1つだけのMesh/submeshはEditorが初期表示に選ぶ場合がありますが、UVアイランドは自動推測・保存しません。ユーザーが各Regionで明示的に選択してください。

1 個の FBX UV Texture Transfer Layer が使用するsource Textureは 1 個です。Layer内のすべての Region Binding が同じsource Textureを共有します。複数のsource Textureから転写する場合は、source TextureごとにLayerを分けてください。

Source Texture の alpha は常に保持されます。完全透明または半透明のpixelも有効なSourceの値として扱い、転写coverageの欠落とは区別します。Target UV island内で転写三角形のrasterization coverageが欠けたpixelは、coverage済みpixelから補完します。この補完によってSource alpha 0のpixelを不透明化することはありません。Target UV islandの外側には従来どおり4pxのbleedを生成します。

SourceまたはTargetのModel / Prefabを変更しても、保存済みRegionを別Meshへ暗黙に置換しません。選択済みMeshが新しいModel / Prefab配下にない場合は警告し、UVアイランドを明示的に選び直すまでValidationがビルドを停止します。Targetはさらに、親MLICのTargetTextureをMaterialが参照しているMesh / Submeshだけを候補とし、最終Avatar Root配下でも使用されていることを検証します。

Region Binding の向き補正は `そのまま`、`左右反転`、`上下反転`、`180度回転` から手動指定します。既定の `そのまま` はSourceの画像上の上下左右をTarget regionでも維持します。Islandの向きが実際に異なるRegionだけ補正してください。

1 個の MLIC が扱える転写先テクスチャは 1 個です。複数の転写先テクスチャへ出力する場合は、転写先ごとに MLIC と本コンポーネントを作成してください。転写先の Renderer、Material、Texture の選択は MLIC が所有し、本コンポーネントは MLIC から渡された出力 RenderTexture 全体を書き込みます。

TexTransTool の backend は Unity に設定してください。WGPU backend では `ExternalToolAsLayer` が空レイヤーとなり、転写処理は呼び出されません。

## 操作手順

1. `VRCAvatarDescriptor` のある GameObject の配下に空の GameObject を作成し、`TTT MultiLayerImageCanvas` を追加します。
2. MLIC の `Target Texture` に転写先テクスチャを 1 個指定します。
3. MLIC の直下にレイヤー用 GameObject を作成します。
4. 同じレイヤー用 GameObject に FBX UV Texture Transfer コンポーネントを追加し、Project内の通常Prefab、Model Prefab、またはPrefab Variantのmain rootを `Source Model / Prefab` に指定します。`Target Model / Prefab` は親AvatarのPrefabから自動設定されます。Prefab接続がなく自動決定できない場合だけ、Sourceと同じ制約のアセットを手動指定します。
5. `source Texture` にLayer内で共有する転写元テクスチャを指定します。`TTT ExternalToolAsLayer` は自動追加されます。削除されている場合はValidationがビルドを停止します。
6. コンポーネントのRegion Binding一覧でRegionを追加し、名前、有効状態、向き補正、合成順を設定します。新規Regionには `Region`、`Region 2` のような一意名が自動設定されます。名前は前後の空白を除去し、大文字・小文字を区別しない比較で空名と重複名を拒否します。
7. UV Region Editorを開き、コンポーネントで作成したRegionを選びます。コンポーネントで指定したModel / Prefabから転写元・転写先のMesh、Submesh、UVチャンネルを選択し、対応するUVアイランドを選択します。
8. Region Binding の `向き補正` はコンポーネントでまず `そのまま` に設定して結果を確認し、実際にIslandの向きが異なるRegionだけ手動補正します。UV Region EditorはUVアイランドのマッピングだけを変更します。
9. Validation 表示にエラーがないことを確認します。
10. NDMF Preview で結果を確認してからアバターをビルドします。

MLIC の Opacity、Blend、Layer Mask、Clipping は、Source Texture のalphaを保持した転写画像の生成後に TexTransTool が適用します。通常は本コンポーネント側で同じ設定を重複して行う必要はありません。

## Region選択とFBXハッシュ

各RegionのUVアイランド選択は、選択時点の FBX 内容を識別するハッシュとともにコンポーネントへ保存されます。FBX の再インポート、差し替え、Mesh や UV の変更によりハッシュが変わった場合、古い UV アイランド選択は自動追従しません。Validation エラーを解消するため、転写元・転写先の Mesh と UV アイランドを再選択してください。

ハッシュ不一致を無視して以前の選択を適用すると、別の三角形や UV 領域へ転写される可能性があります。

## メイク転写レイヤー（試作）

`FBX UV Makeup Transfer Layer` は、メイクだけを含む透過 PNG を、顔の対応点に基づいて別アバターの顔 UV に変形する新しい MLIC レイヤーです。目・口の位置を個別に合わせるため、既存のアイランド輪郭に基づく転写とは別のコンポーネントとして使用します。PSD や肌込み画像からのメイク抽出は扱いません。

1. 転写先アバター配下の MLIC に、転写先の顔テクスチャを指定します。メイクだけの新規Canvasを重ねる場合は、MLIC自体の Blend を通常合成に設定します（既定の置換では、下地の肌も透過部分で置換されます）。既存Canvasを使う場合は、その下地レイヤーと合成構成に合わせてください。
2. MLIC 直下、またはその Layer Folder 配下に空の GameObject を作り、`Add Component > FBX UV Texture Transfer > FBX UV Makeup Transfer Layer` を追加します。`ExternalToolAsLayer` は自動追加されます。既存の転写コンポーネントとは別の GameObject に配置してください。
3. Source / Target Model / Prefab にアセットのルート、Makeup Texture に透過 PNG を指定します。Source Reference Texture は対応点を確認するための任意の顔画像です。
4. メイク対応点エディターを開き、左右の Mesh / Submesh / UV チャンネルと顔の UV アイランドを選択します。
5. 目・口の境界候補を確認し、必要なら左右それぞれの境界番号を選び直して対応点を生成します。余分な穴がある顔でも、独立した目・口の閉境界から生成できます。境界が不足する場合は手動テンプレートを使い、目頭・目尻・唇などが対応するよう画像上または数値で点を調整します。生成・テンプレートは既存点を置換する操作です。顔領域を選び直した場合は対応点も確認し直してください。
6. Validation のエラーを解消し、NDMF Preview で結果を確認します。Opacity、Blend、Layer Mask、Clipping は通常の MLIC レイヤー設定を使います。異なる PNG はレイヤーを分けて重ねます。

対応点から逆方向の Thin Plate Spline を計算し、元 PNG の透明度を保って転写先の顔領域に描画します。実行時に Python や外部サービスは必要ありません。境界候補の提案はUV上の左右・上下配置に基づく補助機能です。曖昧な候補は個別に選択し、境界のない顔は手動で3〜128組の対応点を設定します。境界解析で退化三角形を除外しても元Meshは変更しません。

Inspectorの「基本対応を検査」で現在の基本写像を確認できます。安定化係数によらず三角形補間を使い、局所反転と重なりを検査します。解消できない配置や顔領域を囲まない点配置は描画せず理由を表示します。安定化係数は補間する対応位置の正則化の強さです。0は正則化なしで、正の値では対応点からのずれを許容します。口境界の自動補正は係数によらず適用を試みます。保存済みの対応点は変更しません。

目口の初期配置で作った口の目印がある場合、係数0では、選択済みの顔UVから口の閉境界全体を取得して自動で合わせます。口周辺では保存点より実際のUV境界を優先し、点の間のずれによる切り欠きを抑えます。外側は従来の写像につなぎ、対応点や設定値は書き換えません。追加操作は不要です。境界が取得できない場合や反転・重なりを避けられない場合は、従来の対応点補間を維持します。「基本対応を検査」に自動補正の適用状況を表示します。唇の厚みや最大伸縮の改善は保証しないため、実モデルでの仕上がりを確認してください。

### 鼻先の対応点（試作）

Inspectorまたは転送エディターの「鼻先の候補を1点追加」で、選択した両方の顔メッシュから鼻先候補を追加できます。既存対応点・安定化係数は保持します。追加した点は転送エディターで移動・数値編集・削除でき、Undoにも対応します。目口の初期配置や手動テンプレートで既存点を置換すると、追加した鼻点も置換されます。

候補はモデルの正面+Z・上+Yを前提に、目と口の境界を基準として顔中央の最も前に出た頂点を探します。自動候補が得られない顔や向きが異なるモデルでは、通常の「対応点を追加」で手動指定してください。鼻先とメイクのハイライト中心は必ずしも一致しません。鼻点は基本変形全体へ影響するため、追加後は眼・口・頬と表情変化を確認し、必要に応じて対応点を調整してください。保存済みレイヤーには自動追加しません。

転写元は選択した顔の三角形内だけを参照し、穴や同じ矩形内の別領域の色を除外します。出力も転写先アイランド内に限定し、外側への bleed は生成しません。標本検査で未検出でも自然な仕上がりは保証できないため、表情や斜めからの見え方を含めた最終調整が必要です。

## ライセンス

このパッケージは [Mozilla Public License 2.0](LICENSE.md) で提供されます。
