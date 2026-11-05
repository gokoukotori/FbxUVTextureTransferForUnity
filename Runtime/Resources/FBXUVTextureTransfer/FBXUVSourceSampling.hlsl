#ifndef FBXUV_SOURCE_SAMPLING_INCLUDED
#define FBXUV_SOURCE_SAMPLING_INCLUDED

Texture2D<float4> _MainTex;
StructuredBuffer<float4> _SourceTriangles;
StructuredBuffer<int2> _SourceCellRanges;
StructuredBuffer<int> _SourceCellIndices;
int _SourceGridSize;
float4 _SourceBounds, _SourceSize;
float _RestoreSRGB;

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

#endif
