using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer
{
    internal sealed class FBXUVPixelProcessor
    {
        private const int ThreadGroupSize = 8;
        private readonly ComputeShader computeShader;
        private readonly ComputeKernels kernels;

        internal FBXUVPixelProcessor(ComputeShader computeShader)
        {
            this.computeShader = computeShader;
            kernels = new ComputeKernels(computeShader);
        }

        // The caller owns the textures. Return the final ping-pong buffer so both
        // production rendering and the test entry point use the same post-process.
        internal RenderTexture FillAndBleed(
            RenderTexture source, RenderTexture mask, RenderTexture work, RenderTexture other,
            RenderTexture seedA, RenderTexture seedB, int bleedPixels)
        {
            var current = FillTransparentTargetPixels(source, mask, work, seedA, seedB);
            for (var pass = 0; pass < bleedPixels; pass++)
            {
                Dilate(current, mask, other);
                var swap = current;
                current = other;
                other = swap;
            }
            return current;
        }

        private RenderTexture FillTransparentTargetPixels(
            RenderTexture source,
            RenderTexture mask,
            RenderTexture output,
            RenderTexture seedA,
            RenderTexture seedB)
        {
            var initialize = kernels.InitializeSeeds;
            SetTextureSize(computeShader, source.width, source.height);
            computeShader.SetTexture(initialize, "_Source", source);
            computeShader.SetTexture(initialize, "_Mask", mask);
            computeShader.SetTexture(initialize, "_SeedWrite", seedA);
            Dispatch(computeShader, initialize, source.width, source.height);

            var propagate = kernels.PropagateSeeds;
            var read = seedA;
            var write = seedB;
            for (var jump = HighestJump(source.width, source.height); jump >= 1; jump >>= 1)
            {
                computeShader.SetInt("_Jump", jump);
                computeShader.SetTexture(propagate, "_SeedRead", read);
                computeShader.SetTexture(propagate, "_SeedWrite", write);
                Dispatch(computeShader, propagate, source.width, source.height);
                var swap = read;
                read = write;
                write = swap;
            }

            var resolve = kernels.ResolveFill;
            computeShader.SetTexture(resolve, "_Source", source);
            computeShader.SetTexture(resolve, "_Mask", mask);
            computeShader.SetTexture(resolve, "_SeedRead", read);
            computeShader.SetTexture(resolve, "_Output", output);
            Dispatch(computeShader, resolve, source.width, source.height);
            return output;
        }

        private void Dilate(
            RenderTexture source,
            RenderTexture mask,
            RenderTexture output)
        {
            var kernel = kernels.Dilate8Connected;
            SetTextureSize(computeShader, source.width, source.height);
            computeShader.SetTexture(kernel, "_Source", source);
            computeShader.SetTexture(kernel, "_Mask", mask);
            computeShader.SetTexture(kernel, "_Output", output);
            Dispatch(computeShader, kernel, source.width, source.height);
        }

        internal void CompositeStraightAlpha(
            RenderTexture source,
            RenderTexture destination,
            int offsetX,
            int offsetY)
        {
            var kernel = kernels.CompositeStraightAlpha;
            SetTextureSize(computeShader, source.width, source.height);
            computeShader.SetInts("_DestinationOffset", offsetX, offsetY);
            computeShader.SetInts("_DestinationSize", destination.width, destination.height);
            computeShader.SetTexture(kernel, "_Source", source);
            computeShader.SetTexture(kernel, "_Destination", destination);
            Dispatch(computeShader, kernel, source.width, source.height);
        }

        private static void SetTextureSize(ComputeShader shader, int width, int height)
        {
            shader.SetInts("_TextureSize", width, height);
        }

        private static void Dispatch(ComputeShader shader, int kernel, int width, int height)
        {
            shader.Dispatch(kernel, (width + ThreadGroupSize - 1) / ThreadGroupSize, (height + ThreadGroupSize - 1) / ThreadGroupSize, 1);
        }

        private static int HighestJump(int width, int height)
        {
            var maximum = Mathf.Max(width, height);
            var jump = 1;
            while (jump < maximum && jump <= (int.MaxValue >> 1)) jump <<= 1;
            return Mathf.Max(1, jump >> 1);
        }

        private sealed class ComputeKernels
        {
            private readonly ComputeShader shader;
            private int initializeSeeds;
            private int propagateSeeds;
            private int resolveFill;
            private int dilate8Connected;
            private int compositeStraightAlpha;
            private bool hasInitializeSeeds;
            private bool hasPropagateSeeds;
            private bool hasResolveFill;
            private bool hasDilate8Connected;
            private bool hasCompositeStraightAlpha;

            public ComputeKernels(ComputeShader shader)
            {
                this.shader = shader;
            }

            public int InitializeSeeds
            {
                get { return Resolve("InitializeSeeds", ref initializeSeeds, ref hasInitializeSeeds); }
            }

            public int PropagateSeeds
            {
                get { return Resolve("PropagateSeeds", ref propagateSeeds, ref hasPropagateSeeds); }
            }

            public int ResolveFill
            {
                get { return Resolve("ResolveFill", ref resolveFill, ref hasResolveFill); }
            }

            public int Dilate8Connected
            {
                get { return Resolve("Dilate8Connected", ref dilate8Connected, ref hasDilate8Connected); }
            }

            public int CompositeStraightAlpha
            {
                get { return Resolve("CompositeStraightAlpha", ref compositeStraightAlpha, ref hasCompositeStraightAlpha); }
            }

            private int Resolve(string name, ref int kernel, ref bool resolved)
            {
                if (!resolved)
                {
                    kernel = shader.FindKernel(name);
                    resolved = true;
                }
                return kernel;
            }
        }

    }
}
