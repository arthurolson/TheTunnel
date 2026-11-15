using UnityEngine;

/// <summary>
/// An antibody (IGG) in a container's 3D movie (see <see cref="TunnelMovie"/>). When it touches a capsid's live
/// binding site (see <see cref="CapsidSites"/>), the nearer tip of its two Fab arms is drawn to the site's base on
/// the capsid's surface, while the container's heat goes on jostling it; when it reaches the capsid it binds there,
/// its Fab arm pointing straight out along the site's axis, for the movie's binding time (see
/// <see cref="TunnelMovie.bindingTime"/>); then the capsid lets it go again, pushed off the site, and it rests a few
/// seconds (see <see cref="Rest"/>) before it can bind again. It lets go of a site that is taken meanwhile, or that it
/// hasn't reached in a while, and isn't drawn while held in a hand or while the movie is paused (it waits).
/// A spike's site (SARS-CoV-2) draws it up to the middle of the spike's outer end, and it binds there, pointing
/// out along the spike. Only the headset that moves it (its owner) draws it; the host decides the binding.
/// The Fab tips are found from its chains by Tunnel > Movie > Build 3D Movie.
/// A ligand (EGFR's, in the Ligand binding movie) carries one too, with no Fab arms: when it touches a free capture
/// sphere of a protein it binds to (see <see cref="CapsidSites.pocket"/>), it glides straight into its pose in the
/// crystal structure, shown at the protein's size, for the movie's binding time; then it is let go out of the pocket
/// and rests a few seconds, like an antibody. Each binds only to its own kind of site: an antibody to a virus's, a
/// ligand to its protein's pocket.
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
    float m_RestUntil = -1f;

    /// <summary>Whether it is a ligand (no Fab arms): it binds in a protein's pocket rather than on a virus.</summary>
    public bool ligand => m_Tips.Length == 0;

    /// <summary>The capsid it is drawn to, if any.</summary>
    public CapsidSites target => m_Target;

    /// <summary>Whether it is resting after being let go by a capsid: no site draws it in yet.</summary>
    public bool resting => Time.time < m_RestUntil;

    /// <summary>
    /// Keeps it from being drawn to any site for this many seconds: just let go by a capsid, it drifts away from the
    /// site first, then binds again like any other free antibody.
    /// </summary>
    public void Rest(float seconds)
    {
        m_RestUntil = Time.time + Mathf.Max(0f, seconds);
        m_Target = null;
        m_AskedAt = -1f;
    }

    void Awake() => m_Physics = GetComponent<MoleculeGravity>();

    void OnDisable() => m_Target = null;

    void OnTriggerStay(Collider other)
    {
        if (m_Target != null || m_AskedAt >= 0f || resting || !other.TryGetComponent(out CapsidSite site))
            return;
        var capsid = site.capsid;
        if (capsid == null || !capsid.sitesActive || !capsid.SiteOpen(site.index) || !m_Physics.simulatedHere || m_Physics.isHeld)
            return;
        if (!capsid.Binds(gameObject))
            return; // not its kind of site
        if (capsid.pocket)
        {
            BindInPocket(capsid, site.index);
            return;
        }
        if (m_Tips.Length == 0)
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
        if (toTarget.magnitude < m_Target.SiteLength(m_Site) * 0.1f)
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
        if (m_Target != null && m_AskedAt < 0f && collision.rigidbody != null && collision.rigidbody == m_Target.body
            && m_Target.Reached(m_Site, transform.TransformPoint(m_Tips[m_Tip])))
            Reach();
    }

    void LetGo(string why)
    {
        Debug.Log($"[Movie] {name} let go of {(m_Target != null ? m_Target.name : "a capsid")}'s site {m_Site + 1}: {why}");
        m_Target = null;
    }

    // A ligand in a capture sphere: it glides from where it is into its pose in the pocket, at the protein's size.
    void BindInPocket(CapsidSites protein, int site)
    {
        var frame = protein.transform;
        var inverse = Quaternion.Inverse(frame.rotation);
        float startScale = transform.lossyScale.x / frame.lossyScale.x;
        // Shown at the protein's own magnification: the pose is in toy units, which both toys share (one
        // ångström-to-meter scale), so it is 1 in the protein's frame.
        const float scale = 1f;
        var binding = new CapsidSites.Binding
        {
            site = (byte)site,
            startPosition = frame.InverseTransformPoint(transform.position),
            startRotation = inverse * transform.rotation,
            position = protein.boundPosition,
            rotation = protein.boundRotation,
            scale = scale,
            startScale = startScale,
            volume = m_Physics.volume * scale * scale * scale,
        };
        Debug.Log($"[Movie] {name} touched {protein.name}'s capture sphere: gliding into its pocket");
        m_AskedAt = Time.time;
        protein.RequestBind(gameObject, binding);
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
            startScale = scale,
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
