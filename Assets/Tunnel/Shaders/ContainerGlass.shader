// The glass over a mixed reality box's open front (TunnelContainer): a thin, faintly tinted pane that reflects the
// surroundings more at grazing angles (Fresnel), catches the main light as a highlight and shows a few soft diagonal
// streaks, so it reads as a solid surface and nobody reaches into the wall for what's behind it.
Shader "Tunnel/Container Glass"
{
    Properties
    {
        _Tint("Tint", Color) = (0.82, 0.92, 1, 1)
        _BaseAlpha("Opacity facing it", Range(0, 1)) = 0.06
        _EdgeAlpha("Opacity at grazing angles", Range(0, 1)) = 0.55
        _FresnelPower("Fresnel power", Range(1, 8)) = 4
        _Reflection("Reflection", Range(0, 1)) = 0.45
        _Smoothness("Smoothness", Range(0, 1)) = 0.95
        _Highlight("Highlight", Range(0, 4)) = 1.5
        _Streaks("Streaks", Range(0, 1)) = 0.12
    }

    SubShader
    {
        // After the box's inside and the tunnel (both opaque, or the tunnel's transparent shell).
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent+10" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        Pass
        {
            Name "Glass"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Tint;
                half _BaseAlpha;
                half _EdgeAlpha;
                half _FresnelPower;
                half _Reflection;
                half _Smoothness;
                half _Highlight;
                half _Streaks;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 positionOS : TEXCOORD2;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.positionOS = input.positionOS.xyz;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float3 n = normalize(input.normalWS);
                float3 v = normalize(GetWorldSpaceViewDir(input.positionWS));
                half facing = saturate(dot(n, v));
                half fresnel = pow(1 - facing, _FresnelPower);

                // The surroundings, mirrored in it.
                half roughness = 1 - _Smoothness;
                half3 reflected = GlossyEnvironmentReflection(reflect(-v, n), roughness * roughness, 1);

                // The main light's highlight.
                Light light = GetMainLight();
                float3 h = normalize(light.direction + v);
                half highlight = pow(saturate(dot(n, h)), 256) * _Highlight;

                // A few soft diagonal streaks across the pane (in the box's own coordinates, so they move with it).
                float3 p = input.positionOS;
                float diagonal = p.x + p.y + p.z;
                half streak = smoothstep(0, 0.04, abs(frac(diagonal * 1.5 + 0.25) - 0.5) - 0.42) * _Streaks;

                half alpha = saturate(lerp(_BaseAlpha, _EdgeAlpha, fresnel) + streak + highlight);
                half3 color = _Tint.rgb * (0.35 + 0.65 * lerp(half3(1, 1, 1), reflected, _Reflection))
                    + light.color * highlight + streak;
                return half4(color, alpha);
            }
            ENDHLSL
        }
    }
}
