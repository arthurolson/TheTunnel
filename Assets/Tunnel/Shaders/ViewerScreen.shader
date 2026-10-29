// The Viewer's screen: shows an image or video frame, unlit. For spatial (stereo) media each eye samples its own half
// of the frame: _Layout 1 = side by side (left eye on the left), 2 = top and bottom (left eye on top); _SwapEyes
// swaps them for right-eye-first files; 3 = a stereo pair in two textures (_MainTex left, _RightTex right). With one
// eye (Editor, no headset) the left eye's view is shown.
Shader "Tunnel/ViewerScreen"
{
    Properties
    {
        _MainTex ("Media", 2D) = "black" {}
        _RightTex ("Right Eye (pairs)", 2D) = "black" {}
        [Enum(Mono,0,SideBySide,1,TopBottom,2,Pair,3)] _Layout ("Layout", Float) = 0
        [Toggle] _SwapEyes ("Swap Eyes", Float) = 0
        _Brightness ("Brightness", Range(0, 2)) = 1
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "Unlit"
            Cull Back
            ZWrite On

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            TEXTURE2D(_RightTex);
            SAMPLER(sampler_RightTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float _Layout;
                float _SwapEyes;
                float _Brightness;
            CBUFFER_END

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

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 uv = input.uv;
                float eye = (float)unity_StereoEyeIndex;
                if (_SwapEyes > 0.5)
                    eye = 1.0 - eye;

                if (_Layout > 0.5 && _Layout < 1.5)
                    uv.x = uv.x * 0.5 + eye * 0.5;          // left eye: left half
                else if (_Layout > 1.5 && _Layout < 2.5)
                    uv.y = uv.y * 0.5 + (1.0 - eye) * 0.5;  // left eye: top half

                half3 color = (_Layout > 2.5 && eye > 0.5)
                    ? SAMPLE_TEXTURE2D(_RightTex, sampler_RightTex, uv).rgb
                    : SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv).rgb;
                color *= _Brightness;
                return half4(color, 1.0);
            }
            ENDHLSL
        }
    }
}
