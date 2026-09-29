Shader "Hidden/FBXUVTextureTransfer/EyeTransfer"
{
    Properties { _MainTex ("Source", 2D) = "white" {} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            Blend One Zero
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #include "UnityCG.cginc"
            Texture2D<float4> _MainTex;
            StructuredBuffer<float4> _IrisTriangles, _PupilTriangles, _OcclusionTriangles;
            StructuredBuffer<int2> _IrisRanges, _PupilRanges, _OcclusionRanges;
            StructuredBuffer<int> _IrisIndices, _PupilIndices, _OcclusionIndices;
            StructuredBuffer<float4> _SourceContour, _TargetContour;
            StructuredBuffer<float4> _TargetTriangles, _ManualPins;
            StructuredBuffer<int2> _TargetRanges;
            StructuredBuffer<int> _TargetIndices;
            float4 _TargetBounds;
            int _ManualCount;
            float4 _IrisBounds, _PupilBounds, _OcclusionBounds, _PupilCenters, _PupilScale, _OutputSize;
            float _RestoreSRGB;
            int _SourceCount, _TargetCount, _HasPupil;
            struct Attributes { float4 position : POSITION; float2 q : TEXCOORD0; float2 physical : TEXCOORD1; };
            struct Varyings { float4 position : SV_POSITION; float2 q : TEXCOORD0; float2 physical : TEXCOORD1; };
            Varyings Vert(Attributes input)
            {
                Varyings output;
                float2 p = input.position.xy / _OutputSize.xy;
                output.position = float4(p.x * 2 - 1, 1 - p.y * 2, 0, 1);
                output.q = input.q;
                output.physical = input.physical;
                return output;
            }
            float Cross(float2 a, float2 b) { return a.x * b.y - a.y * b.x; }
            float Radius(float2 q, StructuredBuffer<float4> contour, int count)
            {
                float2 d = dot(q, q) < 1e-16 ? float2(1, 0) : normalize(q);
                float radius = 1e20;
                [loop] for (int i = 0; i < count; i++)
                {
                    float2 a = contour[i].xy, edge = contour[(i + 1) % count].xy - a;
                    float den = Cross(d, edge);
                    if (abs(den) < 1e-8) continue;
                    float t = Cross(a, edge) / den, u = Cross(a, d) / den;
                    if (t > 0 && u >= -1e-5 && u <= 1.00001) radius = min(radius, t);
                }
                return radius;
            }
            bool Locate(float2 q, StructuredBuffer<float4> triangles, StructuredBuffer<int2> ranges,
                StructuredBuffer<int> indices, float4 bounds, out float2 uv, out float depth)
            {
                uv = 0; depth = -1e20;
                float2 norm = (q - bounds.xy) * bounds.zw;
                if (any(norm < 0) || any(norm > 1)) return false;
                int2 cell = min(int2(norm * 32), 31); int2 range = ranges[cell.y * 32 + cell.x];
                bool found = false;
                [loop] for (int i = 0; i < range.y; i++)
                {
                    int index = indices[range.x + i] * 5;
                    float3 a = triangles[index].xyz, b = triangles[index + 1].xyz, c = triangles[index + 2].xyz;
                    float den = Cross(b.xy - a.xy, c.xy - a.xy);
                    float wb = Cross(q - a.xy, c.xy - a.xy) / den;
                    float wc = Cross(b.xy - a.xy, q - a.xy) / den; float wa = 1 - wb - wc;
                    if (min(wa, min(wb, wc)) < -1e-5) continue;
                    float z = wa * a.z + wb * b.z + wc * c.z;
                    if (z < depth) continue;
                    float4 ab = triangles[index + 3];
                    uv = wa * ab.xy + wb * ab.zw + wc * triangles[index + 4].xy;
                    depth = z; found = true;
                }
                return found;
            }
            float Gamma(float value) { return value <= .0031308 ? value * 12.92 : 1.055 * pow(max(value, 0), 1.0 / 2.4) - .055; }
            float4 Load(Texture2D<float4> tex, int2 pixel, int2 size, float restore)
            {
                float4 c = tex.Load(int3(clamp(pixel, 0, size - 1), 0));
                if (restore > .5) c.rgb = float3(Gamma(c.r), Gamma(c.g), Gamma(c.b));
                c.rgb *= c.a; return c;
            }
            float4 Sample(Texture2D<float4> tex, float2 uv, float restore)
            {
                uint width, height; tex.GetDimensions(width, height); int2 size = int2(width, height);
                float2 p = saturate(uv) * size - .5; int2 pixel = int2(floor(p)); float2 f = frac(p);
                // Filter premultiplied texels so transparent RGB cannot contaminate the eye.
                return lerp(lerp(Load(tex, pixel, size, restore), Load(tex, pixel + int2(1, 0), size, restore), f.x),
                    lerp(Load(tex, pixel + int2(0, 1), size, restore), Load(tex, pixel + 1, size, restore), f.x), f.y);
            }
            float4 Frag(Varyings input) : SV_Target
            {
                if (_ManualCount > 0)
                {
                    float2 p = input.physical;
                    [loop] for (int i = 0; i < _ManualCount; i++)
                    {
                        float4 pin = _ManualPins[i * 2];
                        float r = length(input.physical - pin.xy) / pin.z;
                        float a = max(0, 1 - r);
                        p += _ManualPins[i * 2 + 1].xy * a * a * a * a * (1 + 4 * r);
                    }
                    float depth;
                    if (!Locate(p, _TargetTriangles, _TargetRanges, _TargetIndices, _TargetBounds, input.q, depth)) return 0;
                }
                float2 source = input.q * Radius(input.q, _SourceContour, _SourceCount) / Radius(input.q, _TargetContour, _TargetCount);
                float2 uv; float depth;
                float4 color = 0;
                if (Locate(source, _IrisTriangles, _IrisRanges, _IrisIndices, _IrisBounds, uv, depth)) color = Sample(_MainTex, uv, _RestoreSRGB);
                float2 pupilPoint = (input.physical - _PupilCenters.zw) / _PupilScale.xy + _PupilCenters.xy;
                float2 pupilUv; float pupilDepth;
                if (_HasPupil != 0 && Locate(pupilPoint, _PupilTriangles, _PupilRanges, _PupilIndices, _PupilBounds, pupilUv, pupilDepth))
                {
                    bool hasIris = Locate(pupilPoint, _OcclusionTriangles, _OcclusionRanges, _OcclusionIndices, _OcclusionBounds, uv, depth);
                    if (!hasIris || pupilDepth >= depth - 1e-7)
                    {
                        float4 pupil = Sample(_MainTex, pupilUv, _RestoreSRGB);
                        color = pupil + color * (1 - pupil.a);
                    }
                }
                if (color.a > 0) color.rgb /= color.a;
                return color;
            }
            ENDHLSL
        }
    }
}
