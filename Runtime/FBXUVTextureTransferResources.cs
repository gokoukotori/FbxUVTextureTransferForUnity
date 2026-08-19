using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer
{
    public static class FBXUVTextureTransferResources
    {
        public const string TriangleShaderResourcePath = "FBXUVTextureTransfer/TransferTriangles";
        public const string PixelProcessShaderResourcePath = "FBXUVTextureTransfer/PixelProcess";

        public static Shader LoadTriangleShader()
        {
            return Resources.Load<Shader>(TriangleShaderResourcePath);
        }

        public static ComputeShader LoadPixelProcessShader()
        {
            return Resources.Load<ComputeShader>(PixelProcessShaderResourcePath);
        }

        public static bool TryLoad(out Shader triangleShader, out ComputeShader pixelProcessShader)
        {
            triangleShader = LoadTriangleShader();
            pixelProcessShader = LoadPixelProcessShader();
            return triangleShader != null && pixelProcessShader != null;
        }
    }
}
