#ifndef FBXUV_COLOR_INCLUDED
#define FBXUV_COLOR_INCLUDED

// sRGB encoding of one linear channel. Used to restore the stored bytes of an sRGB texture that the GPU decoded.
float GammaFromLinear(float value)
{
    return value <= 0.0031308 ? value * 12.92 : 1.055 * pow(max(value, 0.0), 1.0 / 2.4) - 0.055;
}

#endif
