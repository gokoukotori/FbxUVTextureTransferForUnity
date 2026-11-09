Shader "Hidden/FBXUVTextureTransfer/BodyOverlayTransfer"
{
    Properties { _MainTex ("Overlay", 2D) = "black" {} }
    SubShader
    {
        Cull Off
        ZWrite Off
        ZTest Always

        HLSLINCLUDE
        #include "UnityCG.cginc"
        #include "FBXUVSourceSampling.hlsl"
        // Source UV per target texel, (-1, -1) where invalid; target island per texel, -1 where not covered.
        Texture2D<float2> _CorrespondenceMap;
        Texture2D<float> _IslandMap;
        int _MapResolution;
        float4 _OutputSize;
        // FBXUVBodyOverlayTransferRenderer.SameSourceIslandPixels: neighbours on one source island are closer
        // than this many overlay pixels.
        float _SameSourceIslandPixels;

        struct Attributes { float4 position : POSITION; };
        struct Varyings { float4 position : SV_POSITION; };

        Varyings Vert(Attributes input)
        {
            Varyings output;
            output.position = float4(input.position.xy * 2.0 - 1.0, 0.0, 1.0);
            // TTT compute blending consumes the stored pixel rows directly.
            // On D3D, align raster row zero with UV v=0, the row order of the correspondence map.
            #if UNITY_UV_STARTS_AT_TOP
                output.position.y = -output.position.y;
            #endif
            return output;
        }

        // Integer form of floor((pixel + 0.5) * R / size): the map is R x R in UV for every canvas shape.
        int2 NearestTexel(int2 pixel)
        {
            uint2 size = uint2(_OutputSize.xy);
            uint resolution = uint(_MapResolution);
            return int2(min((uint2(pixel) * 2u + 1u) * resolution / (size * 2u), resolution - 1u));
        }

        bool LoadTexel(int2 texel, out float2 uv, out float island)
        {
            uv = -1.0;
            island = -1.0;
            if (any(texel < 0) || any(texel >= _MapResolution)) return false;
            island = _IslandMap.Load(int3(texel, 0));
            uv = _CorrespondenceMap.Load(int3(texel, 0));
            return island >= 0.0 && uv.x > -0.5;
        }
        ENDHLSL

        Pass
        {
            Name "Transfer"
            Blend One Zero
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            // A neighbour qualifies when it has a bilinear weight, is valid, lies on the nearest texel's target
            // island and on the same source island. Returns its weight (0 if it does not qualify).
            float Qualify(int2 texel, float weight, float island, float2 nearest, float limit, inout float2 sum)
            {
                if (weight <= 0.0) return 0.0;
                float2 uv;
                float candidate;
                if (!LoadTexel(texel, uv, candidate) || candidate != island) return 0.0;
                float2 difference = uv - nearest;
                if (dot(difference, difference) >= limit * limit) return 0.0;
                sum += weight * uv;
                return weight;
            }

            // Round to the nearest k/255 ourselves: k/255 is stored as k by every conforming UNORM8 conversion,
            // whereas D3D11 lets the hardware round an arbitrary float within 0.6 ULP (GPUs differ).
            float4 Quantize(float4 color)
            {
                return round(saturate(color) * 255.0) / 255.0;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                int2 pixel = int2(input.position.xy);
                int2 nearest = NearestTexel(pixel);
                float2 uv;
                float island;
                // Invalid or uncovered texels are transparent; FillAndBleed fills the uncovered ones.
                if (!LoadTexel(nearest, uv, island)) return 0;
                int resolution = _MapResolution;
                if (_OutputSize.x > resolution || _OutputSize.y > resolution)
                {
                    float2 t = (float2(pixel) + 0.5) * (float(resolution) / _OutputSize.xy) - 0.5;
                    float2 base = floor(t);
                    float2 f = t - base;
                    int2 origin = int2(base);
                    if (_OutputSize.x <= resolution) { origin.x = nearest.x; f.x = 0.0; }
                    if (_OutputSize.y <= resolution) { origin.y = nearest.y; f.y = 0.0; }
                    // Interpolate only among the qualifying neighbours, with their bilinear weights renormalised;
                    // the nearest texel always qualifies, so the result never mixes in another island.
                    float limit = _SameSourceIslandPixels / _SourceSize.x;
                    float2 sum = 0.0;
                    float total = Qualify(origin, (1.0 - f.x) * (1.0 - f.y), island, uv, limit, sum);
                    total += Qualify(origin + int2(1, 0), f.x * (1.0 - f.y), island, uv, limit, sum);
                    total += Qualify(origin + int2(0, 1), (1.0 - f.x) * f.y, island, uv, limit, sum);
                    total += Qualify(origin + int2(1, 1), f.x * f.y, island, uv, limit, sum);
                    if (total > 0.0) uv = sum / total;
                }
                return Quantize(SampleSourceStraight(uv));
            }
            ENDHLSL
        }

        Pass
        {
            Name "Mask"
            Blend One Zero
            ColorMask R
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            float4 Frag(Varyings input) : SV_Target
            {
                return float4(1.0, 0.0, 0.0, 0.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "Coverage"
            Blend One Zero
            ColorMask G
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            // Covered texels keep the transfer result even when invalid; FillAndBleed fills only uncovered mask pixels.
            float4 Frag(Varyings input) : SV_Target
            {
                int2 texel = NearestTexel(int2(input.position.xy));
                return _IslandMap.Load(int3(texel, 0)) >= 0.0 ? float4(0.0, 1.0, 0.0, 0.0) : 0;
            }
            ENDHLSL
        }
    }
    Fallback Off
}
