using Unity.Netcode;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Interactors.Visuals;
using XRMultiplayer;

/// <summary>
/// Lets every player see the other players' lasers. While a player's laser is on, the others see a line from that
/// player's hand (or controller) to what it points at, in the player's colour, curved as it curves for them, with a dot
/// where it touches a button, a menu or a thing; when the player pinches (selects, presses, grabs) the line turns
/// brighter and thicker, and the dot grows. So everyone can tell who is pointing at what and when they press it.
/// Sits on the networked player (XRI_Network_Player_Avatar; Tunnel > Players > Add Shared Lasers puts it there): the
/// player's own headset reads its two lasers each frame and shares them, and every other headset draws them. The lines
/// aren't part of the avatar, so they still show when the avatar is hidden for a player standing in the same room.
/// Nothing to share when not connected.
/// </summary>
public class PlayerLaser : NetworkBehaviour
{
    /// <summary>One hand's laser as its player sees it: on or off, pointing at something, pressed; where it runs.</summary>
    public struct Ray : INetworkSerializable, System.IEquatable<Ray>
    {
        public const byte k_On = 1, k_Hit = 2, k_Pressed = 4;
        public byte flags;
        public Vector3 start, middle, end;

        public bool on => (flags & k_On) != 0;
        public bool hit => (flags & k_Hit) != 0;
        public bool pressed => (flags & k_Pressed) != 0;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref flags);
            if (on)
            {
                serializer.SerializeValue(ref start);
                serializer.SerializeValue(ref middle);
                serializer.SerializeValue(ref end);
            }
        }

        /// <summary>Close enough that sending it again would change nothing anyone can see (2 mm).</summary>
        public bool Equals(Ray other) =>
            flags == other.flags && (!on || ((start - other.start).sqrMagnitude < 4e-6f
                && (middle - other.middle).sqrMagnitude < 4e-6f && (end - other.end).sqrMagnitude < 4e-6f));
    }

    const float k_Width = 0.004f;
    const float k_PressedWidth = 0.008f;
    const float k_DotSize = 0.015f;
    const float k_PressedDotSize = 0.025f;
    const int k_Points = 16;
    const float k_Follow = 25f;

    readonly NetworkVariable<Ray> m_Left = new(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    readonly NetworkVariable<Ray> m_Right = new(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    // On the player's own headset: its lasers, found again now and then (controllers and hands come and go).
    NearFarInteractor[] m_Interactors = new NearFarInteractor[0];
    float m_NextFind;
    static Material s_LineMaterial;

    // On the other headsets: the lines drawn for this player.
    readonly Drawn m_DrawnLeft = new(), m_DrawnRight = new();

    class Drawn
    {
        public LineRenderer line, dot;
        public Vector3 start, middle, end;
        public bool shown;
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        if (!IsOwner)
            Debug.Log($"[Laser] player {OwnerClientId}'s lasers will show here");
    }

    public override void OnNetworkDespawn()
    {
        Remove(m_DrawnLeft);
        Remove(m_DrawnRight);
        base.OnNetworkDespawn();
    }

    public override void OnDestroy()
    {
        Remove(m_DrawnLeft);
        Remove(m_DrawnRight);
        base.OnDestroy();
    }

    void LateUpdate()
    {
        if (!IsSpawned)
            return;
        if (IsOwner)
        {
            Share(m_Left, Read(InteractorHandedness.Left));
            Share(m_Right, Read(InteractorHandedness.Right));
            return;
        }
        var color = TryGetComponent(out XRINetworkPlayer player) ? player.playerColor : Color.white;
        Draw(m_DrawnLeft, m_Left.Value, color, "left");
        Draw(m_DrawnRight, m_Right.Value, color, "right");
    }

    static void Share(NetworkVariable<Ray> shared, Ray now)
    {
        if (!shared.Value.Equals(now))
            shared.Value = now;
    }

    // This headset's laser on one side, as its player sees it: the ray's drawn line, if it shows.
    Ray Read(InteractorHandedness side)
    {
        if (Time.unscaledTime >= m_NextFind)
        {
            m_NextFind = Time.unscaledTime + 2f;
            var origin = FindAnyObjectByType<XROrigin>();
            m_Interactors = origin != null ? origin.GetComponentsInChildren<NearFarInteractor>(false) : new NearFarInteractor[0];
        }
        foreach (var interactor in m_Interactors)
        {
            if (interactor == null || !interactor.isActiveAndEnabled || interactor.handedness != side || !interactor.enableFarCasting)
                continue;
            var visual = interactor.GetComponentInChildren<CurveVisualController>();
            var line = visual != null ? visual.lineRenderer : null;
            if (line == null || !line.enabled || !line.gameObject.activeInHierarchy || line.positionCount < 2)
                continue;
            if (s_LineMaterial == null)
                s_LineMaterial = line.sharedMaterial;
            int count = line.positionCount;
            var ray = new Ray
            {
                start = PointOf(line, 0),
                middle = PointOf(line, count / 2),
                end = PointOf(line, count - 1),
                flags = Ray.k_On,
            };
            var endPoint = interactor.TryGetCurveEndPoint(out _);
            if (endPoint is EndPointType.UI or EndPointType.ValidCastHit or EndPointType.AttachPoint)
                ray.flags |= Ray.k_Hit;
            if (interactor.isSelectActive || interactor.hasSelection)
                ray.flags |= Ray.k_Pressed;
            return ray;
        }
        return default;
    }

    static Vector3 PointOf(LineRenderer line, int index)
    {
        var p = line.GetPosition(index);
        return line.useWorldSpace ? p : line.transform.TransformPoint(p);
    }

    // On another headset: draws this player's laser on one side, following the shared one smoothly.
    void Draw(Drawn drawn, Ray ray, Color color, string side)
    {
        if (!ray.on)
        {
            if (drawn.shown)
            {
                drawn.shown = false;
                if (drawn.line != null)
                    drawn.line.enabled = false;
                if (drawn.dot != null)
                    drawn.dot.enabled = false;
            }
            return;
        }
        if (drawn.line == null)
            Make(drawn, $"Player {OwnerClientId} {side} laser");
        if (!drawn.shown)
        {
            drawn.shown = true;
            drawn.start = ray.start;
            drawn.middle = ray.middle;
            drawn.end = ray.end;
        }
        else
        {
            float t = 1f - Mathf.Exp(-k_Follow * Time.deltaTime);
            drawn.start = Vector3.Lerp(drawn.start, ray.start, t);
            drawn.middle = Vector3.Lerp(drawn.middle, ray.middle, t);
            drawn.end = Vector3.Lerp(drawn.end, ray.end, t);
        }

        // A curve through the three points (start, the middle as it was drawn, end).
        var control = 2f * drawn.middle - 0.5f * (drawn.start + drawn.end);
        for (int i = 0; i < k_Points; i++)
        {
            float u = i / (k_Points - 1f);
            float a = 1f - u;
            drawn.line.SetPosition(i, a * a * drawn.start + 2f * a * u * control + u * u * drawn.end);
        }
        var bright = ray.pressed ? Color.Lerp(color, Color.white, 0.5f) : color;
        // The lasers' material blends premultiplied: colour times opacity.
        drawn.line.startColor = Premultiplied(bright, ray.pressed ? 0.9f : 0.35f);
        drawn.line.endColor = Premultiplied(bright, ray.pressed ? 1f : 0.7f);
        drawn.line.widthMultiplier = ray.pressed ? k_PressedWidth : k_Width;
        drawn.line.enabled = true;

        drawn.dot.enabled = ray.hit || ray.pressed;
        if (drawn.dot.enabled)
        {
            var back = (drawn.start - drawn.end).normalized * 0.002f;
            drawn.dot.SetPosition(0, drawn.end + back);
            drawn.dot.SetPosition(1, drawn.end - back);
            drawn.dot.startColor = drawn.dot.endColor = new Color(bright.r, bright.g, bright.b, 1f);
            drawn.dot.widthMultiplier = ray.pressed ? k_PressedDotSize : k_DotSize;
        }
    }

    static Color Premultiplied(Color color, float alpha) => new(color.r * alpha, color.g * alpha, color.b * alpha, alpha);

    static void Make(Drawn drawn, string name)
    {
        drawn.line = NewLine(name, k_Points, 0);
        drawn.dot = NewLine(name + " dot", 2, 10);
    }

    static LineRenderer NewLine(string name, int points, int caps)
    {
        // Not under the avatar, so hiding the avatar of a player in the same room doesn't hide it.
        var line = new GameObject(name).AddComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.positionCount = points;
        line.numCapVertices = caps;
        line.alignment = LineAlignment.View;
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;
        line.sharedMaterial = s_LineMaterial != null ? s_LineMaterial : LaserMaterial();
        line.enabled = false;
        return line;
    }

    // The material for others' lasers: the same one this headset's own laser uses, so it is in the build.
    static Material LaserMaterial()
    {
        foreach (var visual in FindObjectsByType<CurveVisualController>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (visual.lineRenderer != null && visual.lineRenderer.sharedMaterial != null)
                return s_LineMaterial = visual.lineRenderer.sharedMaterial;
        }
        return null;
    }

    static void Remove(Drawn drawn)
    {
        if (drawn.line != null)
            Object.Destroy(drawn.line.gameObject);
        if (drawn.dot != null)
            Object.Destroy(drawn.dot.gameObject);
        drawn.line = drawn.dot = null;
        drawn.shown = false;
    }
}
