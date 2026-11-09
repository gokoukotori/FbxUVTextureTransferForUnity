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
            #include "FBXUVSourceSampling.hlsl"
            int _PointCount;
            float4 _Points[128];
            float4 _Affine0, _AffineU, _AffineV;
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
                return SampleSourceStraight(source);
            }
            ENDHLSL
        }
    }
}
