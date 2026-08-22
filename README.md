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

## ライセンス

このパッケージは [Mozilla Public License 2.0](LICENSE.md) で提供されます。
