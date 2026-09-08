Shader "Hidden/FBXUVTextureTransfer/MakeupTransfer"
{
    Properties { _MainTex ("Makeup", 2D) = "black" {} }
    SubShader
    {
        Cull Off
        ZWrite Off
        ZTest Always
        Pass
        {
            Blend One Zero
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #include "UnityCG.cginc"
            Texture2D<float4> _MainTex;
            StructuredBuffer<float4> _SourceTriangles;
            StructuredBuffer<int2> _SourceCellRanges;
            StructuredBuffer<int> _SourceCellIndices;
            int _SourceGridSize;
            int _PointCount;
            float4 _Points[128];
            float4 _Affine0, _AffineU, _AffineV;
            float4 _SourceBounds, _SourceSize;
            float _RestoreSRGB;
            int _UseExactMapping;
            StructuredBuffer<float4> _ExactTriangles;
            StructuredBuffer<int2> _ExactRanges;
            StructuredBuffer<int> _ExactIndices;
            int _UseMouthMapping;
            StructuredBuffer<float4> _MouthTriangles;
            StructuredBuffer<int2> _MouthRanges;
            StructuredBuffer<int> _MouthIndices;
            struct Attributes { float4 position : POSITION; };
            struct Varyings { float4 position : SV_POSITION; float2 uv : TEXCOORD0; };
            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.uv = input.position.xy;
                output.position = float4(output.uv * 2.0 - 1.0, 0.0, 1.0);
                // TTT compute blending consumes the stored pixel rows directly.
                // On D3D, align raster row zero with the source Texture2D's UV v=0.
                #if UNITY_UV_STARTS_AT_TOP
                    output.position.y = -output.position.y;
                #endif
                return output;
            }
            float GammaFromLinear(float value)
            {
                return value <= 0.0031308 ? value * 12.92 : 1.055 * pow(max(value, 0.0), 1.0 / 2.4) - 0.055;
            }
            bool SourceContains(float2 uv)
            {
                if (any(uv < 0.0) || any(uv > 1.0)) return false;
                int2 cell = min(int2(uv * _SourceGridSize), _SourceGridSize - 1);
                int2 range = _SourceCellRanges[cell.y * _SourceGridSize + cell.x];
                [loop] for (int i = 0; i < range.y; i++)
                {
                    int index = _SourceCellIndices[range.x + i] * 2;
                    float4 ab = _SourceTriangles[index];
                    float2 c = _SourceTriangles[index + 1].xy;
                    float2 ac = ab.xy - c;
                    float2 bc = ab.zw - c;
                    float2 pc = uv - c;
                    float denominator = ac.x * bc.y - ac.y * bc.x;
                    float edgeSquared = max(dot(ac, ac), max(dot(bc, bc), dot(ab.xy - ab.zw, ab.xy - ab.zw)));
                    if (abs(denominator) <= edgeSquared * 1e-12) continue;
                    float alpha = (pc.x * bc.y - pc.y * bc.x) / denominator;
                    float beta = (ac.x * pc.y - ac.y * pc.x) / denominator;
                    // A small barycentric tolerance keeps shared triangle edges closed;
                    // it matches FBXUVIslandExtractor.ContainsPoint rather than expanding a UV island by a texel.
                    if (alpha >= -1e-6 && beta >= -1e-6 && 1.0 - alpha - beta >= -1e-6) return true;
                }
                return false;
            }
            float4 LoadPremultiplied(int2 pixel)
            {
                if (any(pixel < 0) || any(pixel >= int2(_SourceSize.xy))) return 0;
                float2 uv = (float2(pixel) + 0.5) / _SourceSize.xy;
                if (any(uv < _SourceBounds.xy) || any(uv > _SourceBounds.zw)) return 0;
                if (!SourceContains(uv)) return 0;
                float4 color = _MainTex.Load(int3(pixel, 0));
                // Restore each texel before filtering, so hidden RGB in transparent pixels cannot leak.
                if (_RestoreSRGB > 0.5)
                    color.rgb = float3(GammaFromLinear(color.r), GammaFromLinear(color.g), GammaFromLinear(color.b));
                color.rgb *= color.a;
                return color;
            }
            bool TryMouth(float2 target, out float2 source)
            {
                source = 0;
                if (_UseMouthMapping == 0) return false;
                int2 cell = min(int2(target * 32), 31);
                int2 range = _MouthRanges[cell.y * 32 + cell.x];
                [loop] for (int j = 0; j < range.y; j++)
                {
                    int index = _MouthIndices[range.x + j] * 3;
                    float4 ab = _MouthTriangles[index];
                    float4 cs = _MouthTriangles[index + 1];
                    float4 uv = _MouthTriangles[index + 2];
                    float2 ac = ab.xy - cs.xy, bc = ab.zw - cs.xy, pc = target - cs.xy;
                    float den = ac.x * bc.y - ac.y * bc.x;
                    float x = (pc.x * bc.y - pc.y * bc.x) / den;
                    float y = (ac.x * pc.y - ac.y * pc.x) / den;
                    float z = 1.0 - x - y;
                    float tolerance = 1e-7 / abs(den);
                    if (x < -tolerance * length(bc) || y < -tolerance * length(ac)
                        || z < -tolerance * length(ab.xy - ab.zw)) continue;
                    source = cs.zw * x + uv.xy * y + uv.zw * z;
                    return true;
                }
                return false;
            }
            float4 Frag(Varyings input) : SV_Target
            {
                float2 source = 0;
                // All callers evaluate the same per-pixel base map. Mesh UV carries only a correction.
                {
                    float2 target = input.uv;
                    if (_UseExactMapping != 0)
                    {
                        if (any(target < 0.0) || any(target > 1.0)) return 0;
                        int2 cell = min(int2(target * 32), 31);
                        int2 range = _ExactRanges[cell.y * 32 + cell.x];
                        bool found = TryMouth(target, source);
                        [loop] for (int j = 0; j < range.y && !found; j++)
                        {
                            int index = _ExactIndices[range.x + j] * 3;
                            float4 ab = _ExactTriangles[index];
                            float4 cs = _ExactTriangles[index + 1];
                            float4 uv = _ExactTriangles[index + 2];
                            float2 ac = ab.xy - cs.xy, bc = ab.zw - cs.xy, pc = target - cs.xy;
                            float den = ac.x * bc.y - ac.y * bc.x;
                            float x = (pc.x * bc.y - pc.y * bc.x) / den;
                            float y = (ac.x * pc.y - ac.y * pc.x) / den;
                            float z = 1.0 - x - y;
                            if (min(x, min(y, z)) < -1e-6) continue;
                            source = cs.zw * x + uv.xy * y + uv.zw * z;
                            found = true; break;
                        }
                        if (!found) return 0;
                    }
                    else
                    {
                        source = _Affine0.xy + target.x * _AffineU.xy + target.y * _AffineV.xy;
                        [loop] for (int i = 0; i < _PointCount; i++)
                        {
                            float2 delta = target - _Points[i].xy;
                            float r2 = dot(delta, delta);
                            float basis = r2 > 0.0 ? r2 * 0.5 * log(max(r2, 1e-30)) : 0.0;
                            source += basis * _Points[i].zw;
                        }
                    }
                }
                if (any(source < 0.0) || any(source > 1.0)
                    || any(source < _SourceBounds.xy) || any(source > _SourceBounds.zw)) return 0;
                if (!SourceContains(source)) return 0;
                float2 pixel = source * _SourceSize.xy - 0.5;
                int2 origin = int2(floor(pixel));
                float2 weight = frac(pixel);
                float4 lower = lerp(LoadPremultiplied(origin), LoadPremultiplied(origin + int2(1, 0)), weight.x);
                float4 upper = lerp(LoadPremultiplied(origin + int2(0, 1)), LoadPremultiplied(origin + int2(1, 1)), weight.x);
                float4 color = lerp(lower, upper, weight.y);
                // ExternalToolAsLayer consumes straight RGBA.
                return color.a > 0.0 ? float4(color.rgb / color.a, color.a) : 0;
            }
            ENDHLSL
        }
    }
}
