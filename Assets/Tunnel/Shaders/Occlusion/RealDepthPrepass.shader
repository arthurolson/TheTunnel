// Writes the real room's depth into the depth buffer before anything virtual is drawn, so every virtual object behind
// a real one (a person, a chair) fails the depth test and the passthrough shows instead: occlusion for every material,
// not just the occlusion shaders. Drawn by RealDepthOcclusionFeature, one full-screen triangle per eye.
// Only real surfaces inside the tunnel's reach count: within _RealDepthMaxDistance, above the floor band and below
// the top of the walls, so the real floor never cuts holes and the arch still covers the real ceiling.
// Pass 1 (debug) tints red, over everything, where a real surface counts: brighter the closer it is. Pass 2 (debug)
// draws green at 8 m, depth tested: it shows wherever no depth nearer than that was written.
Shader "Hidden/Tunnel/RealDepthPrepass"
{
    HLSLINCLUDE
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
    // How far the nearer surface wins at an outline (0 = plain smoothing, 1 = nearest of the neighbours).
    float _RealDepthWiden;

    struct RealVaryings
    {
        float4 positionCS : SV_POSITION;
        // The pixel's clip-space position as the GPU draws it: unprojecting it with the camera's own (GPU) matrix
        // gives the right ray whether or not this target is drawn flipped. (Screen UVs came out upside down on the
        // Quest's eye textures and right way up in the Editor.)
        float2 clipXY : TEXCOORD0;
        UNITY_VERTEX_OUTPUT_STEREO
    };

    RealVaryings RealVert(Attributes input)
    {
        RealVaryings output;
        UNITY_SETUP_INSTANCE_ID(input);
        UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
        output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
        output.clipXY = output.positionCS.xy;
        return output;
    }

    float LinearDepthAt(const float2 uv)
    {
        const float raw = SAMPLE_TEXTURE2D_ARRAY_LOD(_EnvironmentDepthTexture, sampler_LinearClamp, uv, unity_StereoEyeIndex, 0).r;
        #ifdef XR_LINEAR_DEPTH
            return raw;
        #else
            return LinearizeDepth(ConvertDepthToSymmetricRange(raw));
        #endif
    }

    // The sensor's depth is low resolution and noisy, so outlines flicker texel by texel. Five filtered samples
    // a texel apart (about 3 x 3 texels): their average smooths the outline, their nearest widens what's in
    // front a little, so single noisy texels no longer flip it.
    float SmoothedLinearDepthAt(const float2 uv)
    {
        uint width, height, layers;
        _EnvironmentDepthTexture.GetDimensions(width, height, layers);
        const float2 offset = 1.0f / float2(max(width, 1u), max(height, 1u));
        const float centre = LinearDepthAt(uv);
        const float a = LinearDepthAt(uv + float2(offset.x, offset.y));
        const float b = LinearDepthAt(uv + float2(-offset.x, offset.y));
        const float c = LinearDepthAt(uv + float2(offset.x, -offset.y));
        const float d = LinearDepthAt(uv + float2(-offset.x, -offset.y));
        const float average = (2.0f * centre + a + b + c + d) / 6.0f;
        const float nearest = min(centre, min(min(a, b), min(c, d)));
        return lerp(average, nearest, _RealDepthWiden);
    }

    // Where this pixel's ray meets a real surface that counts: its distance from the eye, else false.
    bool FindRealSurface(RealVaryings input, out float3 eyeWS, out float3 ray, out float t)
    {
        eyeWS = _WorldSpaceCameraPos;
        const float3 farWS = ComputeWorldSpacePosition(float4(input.clipXY, UNITY_RAW_FAR_CLIP_VALUE, 1.0f), UNITY_MATRIX_I_VP);
        ray = normalize(farWS - eyeWS);
        t = 1.5f;
        if (_IsOcclusionOn == 0)
            return false;

        // The depth camera sits almost at the eye: find where the ray meets the real surface by scaling a guess by
        // real depth / guessed depth a few times (single samples to get close, the smoothed depth for the last).
        [unroll]
        for (int i = 0; i < 3; i++)
        {
            const float4 positionTS = mul(_OcclusionWorldToTrackingSpace, float4(eyeWS + ray * t, 1.0f));
            const float4 clip = mul(_EnvironmentDepthProjectionMatrices[unity_StereoEyeIndex], positionTS);
            const float2 uv = (clip.xy / clip.w + 1.0f) * 0.5f;
            if (any(uv < 0.0f) || any(uv > 1.0f))
                return false;
            const float guessed = LinearizeDepth(clip.z / clip.w);
            const float real = i < 2 ? LinearDepthAt(uv) : SmoothedLinearDepthAt(uv);
            t *= real / max(guessed, 0.001f);
        }

        const float height = (eyeWS + ray * t).y - _RealDepthFloorHeight;
        return t > 0.05f && t <= _RealDepthMaxDistance && height >= _RealDepthMinHeight && height <= _RealDepthMaxHeight;
    }

    void FragDepth(RealVaryings input, out float outDepth : SV_Depth)
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
        float3 eyeWS, ray;
        float t;
        if (!FindRealSurface(input, eyeWS, ray, t))
            discard;

        const float4 clipCS = mul(UNITY_MATRIX_VP, float4(eyeWS + ray * (t + _RealDepthPushBack), 1.0f));
        outDepth = clipCS.z / clipCS.w;
        #if !UNITY_REVERSED_Z
            outDepth = outDepth * 0.5f + 0.5f;
        #endif
    }

    float4 FragDebug(RealVaryings input) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
        float3 eyeWS, ray;
        float t;
        if (!FindRealSurface(input, eyeWS, ray, t))
            discard;
        return float4(1.0f, 0.0f, 0.0f, lerp(0.6f, 0.15f, saturate(t / _RealDepthMaxDistance)));
    }

    // Debug probe: green at 8 m, depth tested, so it only shows where the depth buffer holds nothing nearer. On a
    // real surface that hides things it should stay out (red only); green there means the depth never got written.
    float4 FragProbe(RealVaryings input, out float outDepth : SV_Depth) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
        const float3 eyeWS = _WorldSpaceCameraPos;
        const float3 farWS = ComputeWorldSpacePosition(float4(input.clipXY, UNITY_RAW_FAR_CLIP_VALUE, 1.0f), UNITY_MATRIX_I_VP);
        const float4 clipCS = mul(UNITY_MATRIX_VP, float4(eyeWS + normalize(farWS - eyeWS) * 8.0f, 1.0f));
        outDepth = clipCS.z / clipCS.w;
        #if !UNITY_REVERSED_Z
            outDepth = outDepth * 0.5f + 0.5f;
        #endif
        return float4(0.0f, 1.0f, 0.0f, 0.35f);
    }
    ENDHLSL

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
            #pragma vertex RealVert
            #pragma fragment FragDepth
            ENDHLSL
        }

        Pass
        {
            Name "RealDepthDebug"
            ZWrite Off
            ZTest Always
            Cull Off
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex RealVert
            #pragma fragment FragDebug
            ENDHLSL
        }

        Pass
        {
            Name "RealDepthProbe"
            ZWrite Off
            ZTest LEqual
            Cull Off
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex RealVert
            #pragma fragment FragProbe
            ENDHLSL
        }
    }
}
