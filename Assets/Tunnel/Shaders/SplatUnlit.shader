// A splat-box's portrait (SplatBox): a Gaussian splat baked to a triangle mesh whose colour is all in its vertex
// colours (COLOR_0, linear, as glTF stores them), or a captured mesh whose colour is in its texture, or both (the
// texture times the vertex colours). Drawn unlit, exactly as captured, times a tint (white by default). Meshes loaded
// from the asset repository at runtime (TunnelAssets) use it too. Molecules and viruses (_Lit 1) get soft shading from
// their normals: a light from the viewer plus one from above, so their shape reads without the scene's lights. Opaque,
// with no keywords, so every variant the headset needs is built.
Shader "Tunnel/Splat Unlit"
{
    Properties
    {
        _BaseColor("Tint", Color) = (1, 1, 1, 1)
        _MainTex("Texture", 2D) = "white" {}
        _VertexColor("Vertex colours", Range(0, 1)) = 1
        _Lit("Shading from normals", Range(0, 1)) = 0
        _Exposure("Exposure", Range(0, 4)) = 1
        [Enum(UnityEngine.Rendering.CullMode)] _Cull("Cull", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        Pass
        {
            Name "Unlit"
            Tags { "LightMode" = "UniversalForward" }
            Cull [_Cull]
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                float4 _MainTex_ST;
                half _VertexColor;
                half _Lit;
                half _Exposure;
                half _Cull;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                half4 color : COLOR;
                float2 uv : TEXCOORD0;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4 color : COLOR;
                float2 uv : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 viewWS : TEXCOORD2;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                // Without vertex colours (_VertexColor 0) the mesh is as bright as its texture alone.
                output.color = lerp(half4(1, 1, 1, 1), input.color, _VertexColor);
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.viewWS = GetWorldSpaceViewDir(TransformObjectToWorld(input.positionOS.xyz));
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                half3 texel = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv).rgb;
                half3 color = texel * input.color.rgb * _BaseColor.rgb * _Exposure;
                if (_Lit > 0 && dot(input.normalWS, input.normalWS) > 1e-6) // a mesh without normals stays unlit
                {
                    // Both sides lit (surfaces' windings vary between exporters).
                    float3 n = normalize(input.normalWS);
                    half toViewer = abs(dot(n, normalize(input.viewWS)));
                    half fromAbove = abs(dot(n, normalize(float3(0.3, 0.85, 0.45))));
                    half shade = 0.3 + 0.5 * toViewer + 0.3 * fromAbove;
                    color *= lerp(1, shade, _Lit);
                }
                // Opaque: the frame's alpha stays 1, so the passthrough never shows through the portrait.
                return half4(color, 1);
            }
            ENDHLSL
        }

        // Depth only, for when the renderer draws a depth prepass.
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            Cull [_Cull]
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                float4 _MainTex_ST;
                half _VertexColor;
                half _Lit;
                half _Exposure;
                half _Cull;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                return input.positionCS.z;
            }
            ENDHLSL
        }
    }
}
