Shader "Hidden/FBXUVTextureTransfer/TransferTriangles"
{
    Properties
    {
        _MainTex ("Source", 2D) = "white" {}
    }

    SubShader
    {
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            Name "Transfer"
            Blend One Zero

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment FragTransfer
            #include "UnityCG.cginc"

            Texture2D _MainTex;
            SamplerState sampler_LinearClamp;
            float4 _OutputSize;
            float _RestoreSRGB;

            struct Attributes
            {
                float4 position : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 position : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            float GammaFromLinear(float value)
            {
                return value <= 0.0031308 ? value * 12.92 : 1.055 * pow(max(value, 0.0), 1.0 / 2.4) - 0.055;
            }

            Varyings Vert(Attributes input)
            {
                Varyings output;
                // CPU座標はpixel-top-left。UVからは y=(1-v)*height で作り、
                // D3D clip spaceへ戻すことでsource samplingのUV自体は(u,v)を維持する。
                float2 normalized = input.position.xy / _OutputSize.xy;
                output.position = float4(normalized.x * 2.0 - 1.0, 1.0 - normalized.y * 2.0, 0.0, 1.0);
                output.uv = input.uv;
                return output;
            }

            float4 FragTransfer(Varyings input) : SV_Target
            {
                float4 color = _MainTex.Sample(sampler_LinearClamp, saturate(input.uv));
                if (_RestoreSRGB > 0.5)
                {
                    color.rgb = float3(
                        GammaFromLinear(color.r),
                        GammaFromLinear(color.g),
                        GammaFromLinear(color.b));
                }
                // TTTのAlphaBlendingはstraight alphaを受け取るためpremultiplyしない。
                return color;
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
            #pragma vertex VertMask
            #pragma fragment FragMask

            float4 _OutputSize;

            struct Attributes
            {
                float4 position : POSITION;
            };

            struct Varyings
            {
                float4 position : SV_POSITION;
            };

            Varyings VertMask(Attributes input)
            {
                Varyings output;
                float2 normalized = input.position.xy / _OutputSize.xy;
                output.position = float4(normalized.x * 2.0 - 1.0, 1.0 - normalized.y * 2.0, 0.0, 1.0);
                return output;
            }

            float4 FragMask(Varyings input) : SV_Target
            {
                return float4(1.0, 0.0, 0.0, 0.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
