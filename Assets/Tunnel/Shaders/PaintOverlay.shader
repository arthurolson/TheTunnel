// The marks painted on a tunnel segment (TunnelSegmentPaint): its canvas, and over it any pencil line still being
// drawn, laid on a surface just in front of the inside of the shell, so they cover the wall's colour and texture.
// Both textures hold premultiplied colour. Unlit: the colours as chosen on the palette. Not drawn where a Container's
// mixed reality box opens the wall (stencil 16, as the shell), and depth tested, so the real room's occlusion and
// anything in front hide it as they hide the wall. It only ever adds to the frame's alpha, so passthrough never
// shows through paint.
Shader "Tunnel/Paint Overlay"
{
    Properties
    {
        _Canvas("Canvas", 2D) = "black" {}
        _Preview("Pencil preview", 2D) = "black" {}
    }

    SubShader
    {
        // After the shell, opaque or see-through.
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent+5" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        Pass
        {
            Name "Paint"
            Tags { "LightMode" = "UniversalForward" }
            Blend One OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Back

            Stencil
            {
                Ref 16
                ReadMask 255
                Comp NotEqual
            }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_Canvas);
            SAMPLER(sampler_Canvas);
            TEXTURE2D(_Preview);
            SAMPLER(sampler_Preview);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                half4 canvas = SAMPLE_TEXTURE2D(_Canvas, sampler_Canvas, input.uv);
                half4 preview = SAMPLE_TEXTURE2D(_Preview, sampler_Preview, input.uv);
                half4 color = preview + canvas * (1 - preview.a);
                clip(color.a - 0.002);
                return color;
            }
            ENDHLSL
        }
    }
}
