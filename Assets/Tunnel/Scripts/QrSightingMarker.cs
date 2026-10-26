using Unity.XR.CoreUtils;
using UnityEngine;

/// <summary>
/// Shows where the headset sees the floor QR code while aligning: a green outline the size of the printed code, with
/// an arrow to its top edge, drawn on the floor at each sighting with a short beep. If the outline sits on the paper,
/// the alignment will be good. The final measurement flashes brighter, then fades. The outline is kept in the
/// headset's tracking space (under the XR Origin), so it stays on the paper when aligning moves the virtual world.
/// </summary>
public class QrSightingMarker : MonoBehaviour
{
    [SerializeField]
    QrFloorScanner m_Scanner;

    [SerializeField]
    Material m_Material;

    [SerializeField]
    [Tooltip("Side of the printed code, white border included (meters).")]
    float m_Size = 0.19f;

    [SerializeField]
    Color m_SightingColor = new(0.3f, 1f, 0.4f, 1f);

    [SerializeField]
    Color m_FinalColor = new(0.1f, 1f, 1f, 1f);

    [SerializeField]
    [Tooltip("Seconds the final outline stays before fading out.")]
    float m_FinalHold = 4f;

    LineRenderer m_Outline;
    LineRenderer m_Arrow;
    AudioSource m_Audio;
    AudioClip m_Tick;
    AudioClip m_Done;
    float m_HideAt = float.PositiveInfinity;
    Color m_Color;

    void Awake()
    {
        m_Outline = CreateLine("QR Outline", 5, true);
        m_Arrow = CreateLine("QR Arrow", 5, false);
        m_Audio = gameObject.AddComponent<AudioSource>();
        m_Audio.playOnAwake = false;
        m_Audio.spatialBlend = 0f;
        m_Tick = Tone("QR Tick", 1320f, 0.06f);
        m_Done = Tone("QR Done", 880f, 0.25f);
        SetVisible(false);
    }

    void OnEnable()
    {
        if (m_Scanner != null)
        {
            m_Scanner.sighted += OnSighted;
            m_Scanner.finished += OnFinished;
        }
    }

    void OnDisable()
    {
        if (m_Scanner != null)
        {
            m_Scanner.sighted -= OnSighted;
            m_Scanner.finished -= OnFinished;
        }
    }

    void OnSighted(Vector3 position, float yaw)
    {
        Place(position, yaw, m_SightingColor, 0.006f);
        m_HideAt = Time.unscaledTime + 3f; // goes away if the code stops being seen
        m_Audio.PlayOneShot(m_Tick, 0.5f);
    }

    void OnFinished(QrFloorScanner.Measurement? result)
    {
        if (result == null)
        {
            SetVisible(false);
            return;
        }

        Place(result.Value.position, result.Value.yaw, m_FinalColor, 0.012f);
        m_HideAt = Time.unscaledTime + m_FinalHold;
        m_Audio.PlayOneShot(m_Done, 0.7f);
    }

    void Update()
    {
        if (!m_Outline.enabled)
            return;
        float left = m_HideAt - Time.unscaledTime;
        if (left <= 0f)
        {
            SetVisible(false);
            return;
        }

        // Fade over the last second.
        var color = m_Color;
        color.a *= Mathf.Clamp01(left);
        SetColor(color);
    }

    void Place(Vector3 position, float yaw, Color color, float width)
    {
        var origin = FindAnyObjectByType<XROrigin>();
        var space = origin != null ? origin.transform : null;
        foreach (var line in new[] { m_Outline, m_Arrow })
            line.transform.SetParent(space, false);

        var rotation = Quaternion.Euler(0f, yaw, 0f);
        Vector3 Point(float x, float z)
        {
            var world = position + rotation * new Vector3(x, 0.005f, z); // just above the floor
            return space != null ? space.InverseTransformPoint(world) : world;
        }
        float h = m_Size * 0.5f;
        m_Outline.SetPositions(new[] { Point(-h, -h), Point(-h, h), Point(h, h), Point(h, -h), Point(-h, -h) });
        // Arrow from the centre to the code's top edge.
        m_Arrow.SetPositions(new[] { Point(0f, -h * 0.4f), Point(0f, h * 0.8f), Point(-h * 0.25f, h * 0.5f), Point(0f, h * 0.8f), Point(h * 0.25f, h * 0.5f) });
        m_Outline.widthMultiplier = m_Arrow.widthMultiplier = width;
        m_Color = color;
        SetColor(color);
        SetVisible(true);
    }

    LineRenderer CreateLine(string objectName, int points, bool loop)
    {
        var line = new GameObject(objectName).AddComponent<LineRenderer>();
        line.transform.SetParent(transform, false);
        line.useWorldSpace = false; // positions are in the parent's (tracking) space
        line.positionCount = points;
        line.loop = false;
        line.sharedMaterial = m_Material;
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;
        line.numCornerVertices = loop ? 2 : 0;
        line.alignment = LineAlignment.View;
        return line;
    }

    void SetColor(Color color)
    {
        m_Outline.startColor = m_Outline.endColor = color;
        m_Arrow.startColor = m_Arrow.endColor = color;
    }

    void SetVisible(bool visible)
    {
        m_Outline.enabled = m_Arrow.enabled = visible;
        if (!visible)
            m_HideAt = float.PositiveInfinity;
    }

    static AudioClip Tone(string clipName, float frequency, float seconds)
    {
        const int rate = 44100;
        int count = Mathf.CeilToInt(rate * seconds);
        var data = new float[count];
        for (int i = 0; i < count; i++)
        {
            float t = i / (float)rate;
            float envelope = Mathf.Min(1f, t * 200f) * Mathf.Clamp01((seconds - t) * 30f);
            data[i] = Mathf.Sin(2f * Mathf.PI * frequency * t) * envelope * 0.6f;
        }

        var clip = AudioClip.Create(clipName, count, 1, rate, false);
        clip.SetData(data, 0);
        return clip;
    }
}
