using UnityEngine;

/// <summary>
/// An antibody (IGG) in a container's 3D movie (see <see cref="TunnelMovie"/>). When it touches a capsid's live
/// binding site (see <see cref="CapsidSites"/>), the nearer tip of its two Fab arms is drawn to the site's base on
/// the capsid's surface, while the container's heat goes on jostling it; when it reaches the capsid it binds there,
/// its Fab arm pointing straight out along the site's axis. It lets go of a site that is taken meanwhile, or that it
/// hasn't reached in a while, and isn't drawn while held in a hand or while the movie is paused (it waits).
/// Only the headset that moves it (its owner) draws it; the host decides the binding.
/// The Fab tips are found from its chains by Tunnel > Movie > Build 3D Movie.
/// </summary>
[RequireComponent(typeof(MoleculeGravity))]
public class AntibodyBinder : MonoBehaviour
{
    [SerializeField]
    [Tooltip("The tips of its two Fab arms, where it binds (its own coordinates).")]
    Vector3[] m_Tips = new Vector3[0];

    [SerializeField]
    [Tooltip("The directions of its two Fab arms, out from its centre (its own coordinates).")]
    Vector3[] m_Arms = new Vector3[0];

    [SerializeField]
    [Tooltip("How fast its tip closes in on the site's base, per meter away (per second).")]
    float m_Pull = 4f;

    [SerializeField]
    [Tooltip("The fastest its tip closes in (m/s).")]
    float m_MaxApproachSpeed = 0.2f;

    [SerializeField]
    [Tooltip("How quickly its tip takes up that speed (per second).")]
    float m_Response = 8f;

    [SerializeField]
    [Tooltip("How quickly its spin dies down while it is drawn in (per second), so it doesn't tumble.")]
    float m_SpinDamping = 3f;

    [SerializeField]
    [Tooltip("Seconds it is drawn to a site before giving up.")]
    float m_GiveUpAfter = 12f;

    MoleculeGravity m_Physics;
    CapsidSites m_Target;
    int m_Site = -1;
    int m_Tip;
    float m_Drawn;       // seconds drawn so far (not counting pauses)
    float m_AskedAt = -1f;

    /// <summary>The capsid it is drawn to, if any.</summary>
    public CapsidSites target => m_Target;

    void Awake() => m_Physics = GetComponent<MoleculeGravity>();

    void OnDisable() => m_Target = null;

    void OnTriggerStay(Collider other)
    {
        if (m_Target != null || m_AskedAt >= 0f || m_Tips.Length == 0 || !other.TryGetComponent(out CapsidSite site))
            return;
        var capsid = site.capsid;
        if (capsid == null || !capsid.sitesActive || !capsid.SiteOpen(site.index) || !m_Physics.simulatedHere || m_Physics.isHeld)
            return;

        m_Target = capsid;
        m_Site = site.index;
        m_Drawn = 0f;
        var target = capsid.SiteBase(m_Site);
        m_Tip = 0;
        float nearest = float.MaxValue;
        for (int i = 0; i < m_Tips.Length; i++)
        {
            float d = (transform.TransformPoint(m_Tips[i]) - target).sqrMagnitude;
            if (d < nearest)
            {
                nearest = d;
                m_Tip = i;
            }
        }
        Debug.Log($"[Movie] {name} touched {capsid.name}'s {capsid.symmetry} site {m_Site + 1}: drawn in by Fab tip {m_Tip + 1}");
    }

    void FixedUpdate()
    {
        // Asked the host to bind it: it goes when the host has; if it's still here a while later, the site was taken.
        if (m_AskedAt >= 0f)
        {
            if (Time.time - m_AskedAt > 1.5f)
                m_AskedAt = -1f;
            return;
        }
        if (m_Target == null)
            return;

        var body = m_Physics.body;
        var movie = m_Target.movie;
        if (movie != null && movie.isPaused && m_Target.SiteOpen(m_Site))
            return; // waits, still drawn
        if (!m_Target.sitesActive || !m_Target.SiteOpen(m_Site) || m_Physics.isHeld || !m_Physics.simulatedHere || body.isKinematic)
        {
            LetGo("the site went or it was taken in hand");
            return;
        }
        m_Drawn += Time.fixedDeltaTime;
        if (m_Drawn > m_GiveUpAfter)
        {
            LetGo($"it didn't reach it in {m_GiveUpAfter:0} s");
            return;
        }

        var tip = transform.TransformPoint(m_Tips[m_Tip]);
        var target = m_Target.SiteBase(m_Site);
        var toTarget = target - tip;
        if (toTarget.magnitude < m_Target.siteLength * 0.1f)
        {
            Reach();
            return;
        }

        // Its tip heads for the base, keeping up with the capsid; pulled by the tip, it turns to lead with it.
        var capsidBody = m_Target.body;
        var baseVelocity = capsidBody != null && !capsidBody.isKinematic ? capsidBody.GetPointVelocity(target) : Vector3.zero;
        var wanted = baseVelocity + Vector3.ClampMagnitude(toTarget * m_Pull, m_MaxApproachSpeed);
        var change = (wanted - body.GetPointVelocity(tip)) * m_Response;
        body.AddForceAtPosition(change * body.mass, tip, ForceMode.Force);
        body.angularVelocity *= Mathf.Exp(-m_SpinDamping * Time.fixedDeltaTime);
    }

    void OnCollisionStay(Collision collision)
    {
        if (m_Target != null && m_AskedAt < 0f && collision.rigidbody != null && collision.rigidbody == m_Target.body)
            Reach();
    }

    void LetGo(string why)
    {
        Debug.Log($"[Movie] {name} let go of {(m_Target != null ? m_Target.name : "a capsid")}'s site {m_Site + 1}: {why}");
        m_Target = null;
    }

    // It has reached the capsid: bound with its Fab tip on the site's base, the arm pointing out along the axis.
    void Reach()
    {
        var capsid = m_Target;
        int site = m_Site;
        m_Target = null;
        if (capsid == null || !capsid.SiteOpen(site))
            return;

        var axis = capsid.SiteAxis(site);
        var arm = transform.TransformDirection(m_Arms.Length > m_Tip ? m_Arms[m_Tip] : m_Tips[m_Tip]).normalized;
        var rotation = Quaternion.FromToRotation(arm, -axis) * transform.rotation;
        var position = capsid.SiteBase(site) - rotation * Vector3.Scale(transform.lossyScale, m_Tips[m_Tip]);
        var frame = capsid.transform;
        var inverse = Quaternion.Inverse(frame.rotation);
        float scale = transform.lossyScale.x / frame.lossyScale.x;
        var binding = new CapsidSites.Binding
        {
            site = (byte)site,
            startPosition = frame.InverseTransformPoint(transform.position),
            startRotation = inverse * transform.rotation,
            position = frame.InverseTransformPoint(position),
            rotation = inverse * rotation,
            scale = scale,
            volume = m_Physics.volume * scale * scale * scale,
        };
        m_AskedAt = Time.time;
        capsid.RequestBind(gameObject, binding);
    }

#if UNITY_EDITOR
    /// <summary>Set by Tunnel > Movie > Build 3D Movie.</summary>
    public void Configure(Vector3[] tips, Vector3[] arms)
    {
        m_Tips = tips;
        m_Arms = arms;
    }
#endif
}
