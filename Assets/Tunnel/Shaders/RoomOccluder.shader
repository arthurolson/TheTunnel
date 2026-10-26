// Invisible stand-in for the real room (the Quest's Space Setup room mesh: walls, ceiling, furniture). It draws
// nothing but depth, before other geometry, so virtual things behind a real wall or ceiling are hidden, just as the
// real surface would hide them. It's pushed a few centimeters away from the viewer so virtual surfaces lying on or
// right next to a real one (the tunnel floor on the real floor) stay visible.
Shader "Tunnel/RoomOccluder"
{
    Properties
    {
        _PushBack("Push Back (m)", Float) = 0.06
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry-10" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "RoomOccluder"
            Tags { "LightMode" = "UniversalForward" }
            ColorMask 0
            ZWrite On
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _PushBack;
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

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float3 positionVS = TransformWorldToView(TransformObjectToWorld(input.positionOS.xyz));
                float distance = max(length(positionVS), 1e-4);
                positionVS *= (distance + _PushBack) / distance; // farther along the line of sight
                output.positionCS = TransformWViewToHClip(positionVS);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }
    }
}
