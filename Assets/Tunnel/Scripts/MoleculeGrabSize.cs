using UnityEngine;

/// <summary>
/// Keeps a very small molecule easy to take hold of: while it is smaller than <see cref="minimumSize"/> across (in the
/// headset, at its current magnification or two-hand scale), its collider is stretched on those axes to that size, so
/// a hand or a ray can still find it. Only the collider grows: the molecule is drawn at its true size, on the same
/// ångström-to-meter scale as every other molecule. Once it is magnified or scaled past that size the collider fits
/// its surface again. While it is in a container whose 3D movie plays, the collider fits its surface too, so it moves,
/// collides and reaches a binding pocket as the molecule it is (Ligand binding). Each headset works it out for
/// itself, so it needs nothing over the network.
/// Added by Tunnel > Molecules > Update Molecule Library to atoms-and-bonds toys (ligands, chemicals) under that size
/// at ×1, such as the EGFR ligand.
/// </summary>
public class MoleculeGrabSize : MonoBehaviour
{
    [SerializeField]
    [Tooltip("The molecule's own collider holder (its fitted box or ellipsoid), stretched as needed.")]
    Transform m_Collider;

    [SerializeField]
    [Tooltip("The fitted collider's size along its own axes, in the toy's units (before any stretching).")]
    Vector3 m_FittedSize;

    [SerializeField]
    [Tooltip("The smallest the collider is across on any axis, in meters in the headset.")]
    float m_MinimumSize = 0.02f;

    Vector3 m_LastScale;
    bool m_LastInMovie;

    /// <summary>The smallest the collider is across on any axis, in meters in the headset.</summary>
    public float minimumSize => m_MinimumSize;

    void OnEnable()
    {
        m_LastScale = Vector3.zero;
        Fit();
    }

    // Magnification and two-hand scaling change its size, and a movie may start or stop round it; re-fit only then.
    void LateUpdate()
    {
        if (m_Collider != null && m_Collider.parent != null && (m_Collider.parent.lossyScale != m_LastScale || InMovie() != m_LastInMovie))
            Fit();
    }

    // Whether it is in a container whose 3D movie is playing.
    bool InMovie()
    {
        var container = TunnelContainer.At(transform.position);
        return container != null && container.movie != null && container.movie.isPlaying;
    }

    /// <summary>
    /// Stretches the collider so no axis is under <see cref="minimumSize"/> in the headset (or fits it to the molecule
    /// while a movie plays round it).
    /// </summary>
    public void Fit()
    {
        if (m_Collider == null || m_Collider.parent == null)
            return;
        var parentScale = m_Collider.parent.lossyScale;
        m_LastScale = parentScale;
        m_LastInMovie = Application.isPlaying && InMovie();
        if (m_LastInMovie)
        {
            m_Collider.localScale = Vector3.one;
            return;
        }
        // The toy is scaled uniformly (magnification, two hands), so one factor turns toy units into meters.
        float toMeters = Mathf.Abs(parentScale.x);
        if (toMeters <= 0f)
            return;
        float minimum = m_MinimumSize / toMeters;
        var stretch = new Vector3(
            m_FittedSize.x > 0f ? Mathf.Max(1f, minimum / m_FittedSize.x) : 1f,
            m_FittedSize.y > 0f ? Mathf.Max(1f, minimum / m_FittedSize.y) : 1f,
            m_FittedSize.z > 0f ? Mathf.Max(1f, minimum / m_FittedSize.z) : 1f);
        if (m_Collider.localScale != stretch)
            m_Collider.localScale = stretch;
    }

    /// <summary>
    /// Set when a toy is made: by Tunnel > Molecules > Update Molecule Library, or in the headset for a molecule from
    /// the asset repository (see <see cref="MoleculeToyGeometry"/>).
    /// </summary>
    public void Configure(Transform collider, Vector3 fittedSize, float minimumSize)
    {
        m_Collider = collider;
        m_FittedSize = fittedSize;
        m_MinimumSize = minimumSize;
    }
}
