// Brush stamps drawn into a tunnel segment's paint canvas (TunnelSegmentPaint), one quad each in canvas coordinates.
// The canvas holds premultiplied colour. Pass 0, solid paint and pencil: a hard round stamp; the stencil lets each
// pixel take paint once per stroke, so overlapping stamps of a see-through colour don't darken it. Pass 1, airbrush:
// soft and grainy, building up stamp over stamp. Pass 2, eraser: takes paint off under a hard round stamp.
Shader "Tunnel/Paint Brush"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Cull Off
        ZWrite Off
        ZTest Always

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        struct Attributes
        {
            float4 positionOS : POSITION;
            float2 corner : TEXCOORD0; // -1..1 across the stamp
            float2 center : TEXCOORD1; // the stamp's centre on the canvas
            half4 color : COLOR;
        };

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float2 corner : TEXCOORD0;
            float2 center : TEXCOORD1;
            half4 color : COLOR;
        };

        Varyings vert(Attributes input)
        {
            Varyings output;
            output.positionCS = TransformWorldToHClip(input.positionOS.xyz);
            output.corner = input.corner;
            output.center = input.center;
            output.color = input.color;
            return output;
        }

        float Hash(float2 p)
        {
            return frac(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453);
        }
        ENDHLSL

        Pass
        {
            Name "Hard"
            Blend One OneMinusSrcAlpha
            Stencil
            {
                Ref 1
                Comp NotEqual
                Pass Replace
            }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            half4 frag(Varyings input) : SV_Target
            {
                clip(1 - length(input.corner));
                half a = input.color.a;
                return half4(input.color.rgb * a, a);
            }
            ENDHLSL
        }

        Pass
        {
            Name "Air"
            Blend One OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            half4 frag(Varyings input) : SV_Target
            {
                float r = length(input.corner);
                clip(1 - r);
                half falloff = saturate(1 - r * r);
                falloff *= falloff;
                // Grain: specks that differ from stamp to stamp, so the spray builds up unevenly.
                half speck = step(0.6, Hash(floor(input.positionCS.xy) + input.center * 1031.0));
                half a = saturate(input.color.a * 0.1 * falloff * (0.3 + 1.6 * speck));
                return half4(input.color.rgb * a, a);
            }
            ENDHLSL
        }

        Pass
        {
            Name "Erase"
            Blend Zero OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            half4 frag(Varyings input) : SV_Target
            {
                clip(1 - length(input.corner));
                return half4(0, 0, 0, 1);
            }
            ENDHLSL
        }
    }
}
