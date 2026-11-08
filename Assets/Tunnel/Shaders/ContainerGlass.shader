// The glass over a mixed reality box's open front (TunnelContainer): clear, with no tint, showing only what a thin
// pane of glass reflects. The surroundings mirrored in it, faint facing it and stronger at grazing angles (Fresnel,
// Schlick), and the main light's highlight, so it reads as a solid surface and nobody reaches into the wall for what's
// behind it. The scene has no reflection map, so it also mirrors a soft made-up room (a bright ceiling and a few
// window-like bands), which moves across it as the viewer moves, as a reflection does. Premultiplied: where it reflects
// nothing it leaves what's behind exactly as it is. It never changes the frame's alpha (the box's inside behind it is
// opaque), so on the headset the passthrough never shows through the box.
Shader "Tunnel/Container Glass"
{
    Properties
    {
        _Reflectance("Reflectance facing it", Range(0, 1)) = 0.1
        _Reflection("Reflection strength", Range(0, 4)) = 2.5
        _FresnelPower("Fresnel power", Range(1, 8)) = 5
        _Smoothness("Smoothness", Range(0, 1)) = 0.95
        _Highlight("Highlight", Range(0, 4)) = 1.5
        _Room("Made-up room's brightness", Range(0, 2)) = 1
    }

    SubShader
    {
        // After the box's inside and the tunnel (both opaque, or the tunnel's transparent shell).
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent+10" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        Pass
        {
            Name "Glass"
            Tags { "LightMode" = "UniversalForward" }
            Blend One OneMinusSrcAlpha, Zero One
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
                half _Reflectance;
                half _Reflection;
                half _FresnelPower;
                half _Smoothness;
                half _Highlight;
                half _Room;
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
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float3 n = normalize(input.normalWS);
                float3 v = normalize(GetWorldSpaceViewDir(input.positionWS));
                half facing = saturate(dot(n, v));
                // How much it reflects: a little facing it, most at grazing angles.
                half reflectance = saturate((_Reflectance + (1 - _Reflectance) * pow(1 - facing, _FresnelPower)) * _Reflection);

                // The surroundings, mirrored in it.
                half roughness = 1 - _Smoothness;
                float3 r = reflect(-v, n);
                half3 reflected = GlossyEnvironmentReflection(r, roughness * roughness, 1);

                // The made-up room: brighter overhead than below, and tall bright windows all around (one every 36°,
                // each a few degrees wide), so a window or two always slides across the pane as the viewer moves.
                half ceiling = smoothstep(-0.2, 0.8, r.y);
                float around = atan2(r.z, r.x);
                half windows = smoothstep(0.82, 0.97, sin(around * 10)) * smoothstep(-0.35, -0.1, r.y) * (1 - smoothstep(0.45, 0.7, r.y));
                reflected = max(reflected, (0.05 + 0.3 * ceiling + 0.9 * windows) * _Room);

                // The main light's highlight.
                Light light = GetMainLight();
                float3 h = normalize(light.direction + v);
                half highlight = pow(saturate(dot(n, h)), 256) * _Highlight;

                // Premultiplied: the reflection added, and what's behind dimmed only as much as the pane reflects.
                half3 color = reflected * reflectance + light.color * highlight;
                half alpha = saturate(reflectance * Luminance(reflected) + highlight);
                return half4(color, alpha);
            }
            ENDHLSL
        }
    }
}
