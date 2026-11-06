using nadena.dev.ndmf;
using net.rs64.TexTransTool.MultiLayerImage;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer
{
    // Shared shell of the transfer layers: the Model / Prefab references, the TexTransTool image layer entry point and
    // the component setup. LoadImage clears the destination; a layer enabled in the hierarchy then prepares and renders
    // its transfer (RenderPrepared), and any failure leaves a transparent layer. The members a layer supplies are
    // private protected, so only the layers of this assembly derive from it.
    public abstract class FBXUVTransferLayerBase : MonoBehaviour, IExternalToolCanBehaveAsImageLayerV1, INDMFEditorOnly
    {
        public GameObject sourceModelOrPrefab;
        public GameObject targetModelOrPrefab;

        public GameObject SourceModelOrPrefab { get { return sourceModelOrPrefab; } set { sourceModelOrPrefab = value; } }
        public GameObject TargetModelOrPrefab { get { return targetModelOrPrefab; } set { targetModelOrPrefab = value; } }

        // Apply on Play can evaluate layers from an earlier Awake before this component's OnEnable runs.
        // Keep explicit component and hierarchy disabling semantics without depending on lifecycle callbacks.
        public bool IsEnabledInHierarchy => enabled && gameObject.activeInHierarchy;

        // Logged with the exception when rendering fails, and when even the transparent fallback fails.
        private protected abstract string LoadImageFailureMessage { get; }
        private protected abstract string LoadImageFallbackFailureMessage { get; }

        public bool HasExclusiveExternalToolImplementation() => FBXUVLayerUtility.HasExclusiveImplementation(this);

        public void LoadImage(RenderTexture writeDistentionTexture)
        {
            FBXUVLayerUtility.LoadImage(writeDistentionTexture, this, RenderIfReady,
                LoadImageFailureMessage, LoadImageFallbackFailureMessage);
        }

        // Prepares the transfer and draws it into the cleared destination. Returns without drawing when the settings
        // cannot render. Called only while the layer is enabled in the hierarchy.
        private protected abstract void RenderPrepared(RenderTexture destination);

        // Fills serialized lists and settings left null by older data or by a script edit (OnValidate only).
        private protected virtual void NormalizeSerializedFields()
        {
        }

        // Assigns the package's shaders to the shader fields that are still empty.
        private protected virtual void AssignMissingResources()
        {
        }

        protected void Reset()
        {
            EnsureSetup();
        }

        protected void OnValidate()
        {
            NormalizeSerializedFields();
            EnsureSetup();
        }

        private void RenderIfReady(RenderTexture destination)
        {
            if (!IsEnabledInHierarchy) return;
            RenderPrepared(destination);
        }

        private void EnsureSetup()
        {
            FBXUVLayerUtility.EnsureExternalToolAsLayer(gameObject);
            AssignMissingResources();
        }
    }
}
