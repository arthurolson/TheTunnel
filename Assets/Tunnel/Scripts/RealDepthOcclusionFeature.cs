using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Lets real people and objects hide every virtual object behind them, whatever its material: before anything virtual
/// is drawn, the headset's live depth of the room is written into the depth buffer (Hidden/Tunnel/RealDepthPrepass),
/// so virtual pixels behind a real surface fail the depth test and the passthrough shows there. Hard edges, at the
/// depth sensor's resolution; the tunnel's own occlusion shaders still fade their edges softly.
/// Real surfaces only count above the floor band and below the top of the tunnel walls (see the shader), so the real
/// floor never cuts holes and the arch still covers the real ceiling. Runs only while depth frames arrive
/// (<see cref="OcclusionSafety"/>), and only for the headset's eyes.
/// </summary>
public class RealDepthOcclusionFeature : ScriptableRendererFeature
{
    [SerializeField]
    Shader m_Shader;

    [SerializeField]
    [Tooltip("Real surfaces lower than this above the floor (meters) don't hide anything: the floor itself.")]
    float m_MinHeight = 0.15f;

    [SerializeField]
    [Tooltip("Real surfaces higher than this above the floor (meters) don't hide anything, so the tunnel's arch covers the ceiling.")]
    float m_MaxHeight = 2f;

    [SerializeField]
    [Tooltip("Real surfaces farther than this (meters) don't hide anything; the depth gets noisy beyond.")]
    float m_MaxDistance = 5f;

    [SerializeField]
    [Tooltip("Real surfaces are treated as this much farther (meters), so virtual things lying on them stay visible.")]
    float m_PushBack = 0.03f;

    [SerializeField]
    [Tooltip("Tints red, over everything, where a real surface hides what's behind it, and green where no depth nearer than 8 m was written (to check the occlusion).")]
    bool m_ShowDebug;

    static readonly int k_MinHeight = Shader.PropertyToID("_RealDepthMinHeight");
    static readonly int k_MaxHeight = Shader.PropertyToID("_RealDepthMaxHeight");
    static readonly int k_MaxDistance = Shader.PropertyToID("_RealDepthMaxDistance");
    static readonly int k_PushBack = Shader.PropertyToID("_RealDepthPushBack");

    Material m_Material;
    RealDepthPass m_Pass;
    RealDepthPass m_DebugPass;

    public override void Create()
    {
        if (m_Shader == null)
            m_Shader = Shader.Find("Hidden/Tunnel/RealDepthPrepass");
        m_Pass = new RealDepthPass { renderPassEvent = RenderPassEvent.BeforeRenderingOpaques };
        m_DebugPass = new RealDepthPass { renderPassEvent = RenderPassEvent.AfterRenderingTransparents, debug = true };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (m_Shader == null || !renderingData.cameraData.xrRendering)
            return;
        if (m_Material == null)
            m_Material = CoreUtils.CreateEngineMaterial(m_Shader);
        m_Material.SetFloat(k_MinHeight, m_MinHeight);
        m_Material.SetFloat(k_MaxHeight, m_MaxHeight);
        m_Material.SetFloat(k_MaxDistance, m_MaxDistance);
        m_Material.SetFloat(k_PushBack, m_PushBack);
        m_Pass.material = m_Material;
        renderer.EnqueuePass(m_Pass);
        if (m_ShowDebug)
        {
            m_DebugPass.material = m_Material;
            renderer.EnqueuePass(m_DebugPass);
        }
    }

    protected override void Dispose(bool disposing)
    {
        CoreUtils.Destroy(m_Material);
        m_Material = null;
    }

    class RealDepthPass : ScriptableRenderPass
    {
        public Material material;
        /// <summary>Draws the red debug tint into the colour instead of writing depth.</summary>
        public bool debug;

        class PassData
        {
            public Material material;
            public int pass;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resources = frameData.Get<UniversalResourceData>();
            using var builder = renderGraph.AddRasterRenderPass<PassData>(debug ? "Real depth occlusion debug" : "Real depth occlusion", out var passData);
            passData.material = material;
            passData.pass = debug ? 1 : 0;
            if (debug)
                builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.ReadWrite);
            builder.SetRenderAttachmentDepth(resources.activeDepthTexture, AccessFlags.ReadWrite);
            builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
            {
                // Debug: the green depth probe first, then the red tint over everything.
                if (data.pass == 1)
                    Blitter.BlitTexture(context.cmd, new Vector4(1f, 1f, 0f, 0f), data.material, 2);
                Blitter.BlitTexture(context.cmd, new Vector4(1f, 1f, 0f, 0f), data.material, data.pass);
            });
        }
    }
}
