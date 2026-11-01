// Writes the real room's depth into the depth buffer before anything virtual is drawn, so every virtual object behind
// a real one (a person, a chair) fails the depth test and the passthrough shows instead: occlusion for every material,
// not just the occlusion shaders. Drawn by RealDepthOcclusionFeature, one full-screen triangle per eye.
// Only real surfaces inside the tunnel's reach count: within _OcclusionMaxDistance, above the floor band and below
// the top of the walls, so the real floor never cuts holes and the arch still covers the real ceiling.
Shader "Hidden/Tunnel/RealDepthPrepass"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Name "RealDepthPrepass"
            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ XR_LINEAR_DEPTH

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "OcclusionComputation.hlsl"

            // World height of the tunnel's floor, and the band above it whose real surfaces count (meters).
            float _RealDepthFloorHeight;
            float _RealDepthMinHeight;
            float _RealDepthMaxHeight;
            // Real surfaces are pushed back this far (meters), so a virtual surface lying on a real one stays visible.
            float _RealDepthPushBack;
            float _RealDepthMaxDistance;

            void Frag(Varyings input, out float outDepth : SV_Depth)
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                if (_IsOcclusionOn == 0)
                    discard;

                // This pixel's ray from the eye.
                const float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
                const float3 farWS = ComputeWorldSpacePosition(screenUV, UNITY_RAW_FAR_CLIP_VALUE, UNITY_MATRIX_I_VP);
                const float3 eyeWS = _WorldSpaceCameraPos;
                const float3 ray = normalize(farWS - eyeWS);

                // The depth camera sits almost at the eye: find where the ray meets the real surface by scaling a
                // guess by real depth / guessed depth a few times.
                float t = 1.5f;
                [unroll]
                for (int i = 0; i < 3; i++)
                {
                    const float4 positionTS = mul(_OcclusionWorldToTrackingSpace, float4(eyeWS + ray * t, 1.0f));
                    const float4 clip = mul(_EnvironmentDepthProjectionMatrices[unity_StereoEyeIndex], positionTS);
                    const float2 uv = (clip.xy / clip.w + 1.0f) * 0.5f;
                    if (any(uv < 0.0f) || any(uv > 1.0f))
                        discard;
                    const float guessed = LinearizeDepth(clip.z / clip.w);
                    const float real = SampleEnvironmentDepthLinear(uv);
                    t *= real / max(guessed, 0.001f);
                }

                const float3 realWS = eyeWS + ray * t;
                const float height = realWS.y - _RealDepthFloorHeight;
                if (t <= 0.05f || t > _RealDepthMaxDistance || height < _RealDepthMinHeight || height > _RealDepthMaxHeight)
                    discard;

                const float4 clipCS = mul(UNITY_MATRIX_VP, float4(eyeWS + ray * (t + _RealDepthPushBack), 1.0f));
                outDepth = clipCS.z / clipCS.w;
                #if !UNITY_REVERSED_Z
                    outDepth = outDepth * 0.5f + 0.5f;
                #endif
            }
            ENDHLSL
        }
    }
}
