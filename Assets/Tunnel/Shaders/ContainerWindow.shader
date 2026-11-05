// The front of a Container's mixed reality box (TunnelContainer): draws nothing, but marks where it is on screen
// (stencil 16) before the tunnel is drawn, so the tunnel shell (OcclusionLit with _WindowStencilComp = 6) leaves a
// hole there and the box's inside shows through the wall.
Shader "Tunnel/Container Window"
{
    Properties
    {
        _StencilRef("Stencil Ref", Float) = 16
    }

    SubShader
    {
        // After the table's passthrough volume (Geometry-5), before the tunnel shell (Geometry and Transparent).
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry-2" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "Window"
            Cull Off
            ZWrite Off
            ZTest Always
            ColorMask 0

            Stencil
            {
                Ref [_StencilRef]
                Comp Always
                Pass Replace
            }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

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

            half4 frag(Varyings input) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }
    }
}
