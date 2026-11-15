using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// A container's Antibody action (the 3D movie that runs in it): some molecules inside it, the actors, act out what
/// is set for them, while the others go on moving as the container's medium and heat move them. The story is
/// antibodies binding viruses: the capsids (TBSV, poliovirus, Zika) and SARS-CoV-2's spikes carry invisible binding
/// sites (see <see cref="CapsidSites"/>), and an antibody (IGG) that touches one is drawn in and binds (see
/// <see cref="AntibodyBinder"/>). Any number bind, one per site; each virus turns greener the more of its sites are
/// taken, fully green when all are. Each antibody stays bound for the binding time (<see cref="bindingTime"/>,
/// 10 s to 1 min from when it bound), then lets go, is pushed off the site and is soon free to bind again.
/// Its controls are a small menu beside the container (shown by the TS Menu's Containers list, see
/// <see cref="TunnelMoviePanel"/>), which anyone can take by its bar and move, or let go of against a tunnel wall to
/// lie flat on it, for everyone: Start makes the binding sites live; Pause holds everything in the container still,
/// and Play lets it go on as it was; Play again lets every bound antibody go at once and, after a 5 second countdown,
/// starts again from the starting state (the container's contents and settings as they were when it started);
/// Show binding sites shows them (to check them), and the sliders set how wide they are (see
/// <see cref="siteDiameter"/>) and the binding time; Save starting state saves the container and every molecule in
/// it as they were just before it started (or as they are, before it has), under a name typed on the menu's keyboard,
/// as a file in the 3D Movies folder (see <see cref="folder"/>).
/// Its other movie is Ligand binding (<see cref="Scenario"/>, chosen on its menu before it starts): EGFR proteins and
/// many free EGFR ligands move in the container; each EGFR has an invisible capture sphere on its ligand's pocket (its
/// radius set by the Capture radius slider, 2 to 30 Å at the molecules' true scale, 10 Å at first; see
/// <see cref="captureRadius"/>), and a free ligand that touches it glides into its pose in the crystal structure
/// (9S3X), stays bound for the binding time, and is then let go out of the pocket, resting briefly before it can bind
/// again. An EGFR with its ligand bound is light blue. Play again, pausing, showing the sites (the capture spheres)
/// and saving work the same way, and its saved states are named after it.
/// In a session everyone shares its state, the movie chosen, the sliders, the sites' visibility, the menu's place and
/// its messages; the host decides bindings, releases and the restart; each player saves to their own headset.
/// </summary>
public class TunnelMovie : NetworkBehaviour
{
    public enum State : byte
    {
        /// <summary>Not started: the sites are not live.</summary>
        Ready,
        Playing,
        Paused,
        /// <summary>Stopped for good (not used by the Antibody action now; kept for saved states and older menus).</summary>
        Ended,
        /// <summary>Play again: every antibody has let go, and it starts again from the starting state when the countdown ends.</summary>
        Restarting,
    }

    /// <summary>Which story it plays: antibodies binding viruses, or ligands binding their protein's pocket.</summary>
    public enum Scenario : byte
    {
        AntibodyAction,
        LigandBinding,
    }

    /// <summary>What people see the antibody movie called, on its menu, the Containers list and its saved states.</summary>
    public const string k_Title = "Antibody action";

    /// <summary>What people see the ligand movie called.</summary>
    public const string k_LigandTitle = "Ligand binding";

    /// <summary>A movie's name as people see it.</summary>
    public static string TitleOf(Scenario scenario) => scenario == Scenario.LigandBinding ? k_LigandTitle : k_Title;

    [SerializeField]
    [Tooltip("The movie's controls, shown on the container.")]
    TunnelMoviePanel m_PanelPrefab;

    [SerializeField]
    [Tooltip("The molecules the TS Menu lists: Play again makes the starting state's molecules from their prefabs.")]
    TunnelPrefabRegistry m_Prefabs;

    [SerializeField]
    [Tooltip("Gap between the container's side and the panel (meters).")]
    float m_PanelGap = 0.04f;

    [SerializeField]
    [Tooltip("Seconds Play again waits, after letting every antibody go, before it starts again.")]
    float m_RestartDelay = 5f;

    [SerializeField]
    [Tooltip("The menu let go of with its middle this close to a flat tunnel wall (meters) lies flat on the wall.")]
    float m_PanelSnapDistance = 0.25f;

    /// <summary>The binding sites' diameters, as a fraction of each capsid's diameter: the slider's range.</summary>
    public const float k_MinSiteDiameter = 0.02f, k_MaxSiteDiameter = 0.3f, k_DefaultSiteDiameter = 0.1f;

    /// <summary>How long an antibody stays bound (seconds): the Binding time slider's range.</summary>
    public const float k_MinBindingTime = 10f, k_MaxBindingTime = 60f, k_DefaultBindingTime = 30f;

    /// <summary>The capture sphere's radius in Ligand binding (ångströms at the molecules' true scale): the slider's range.</summary>
    public const float k_MinCaptureRadius = 2f, k_MaxCaptureRadius = 30f, k_DefaultCaptureRadius = 10f;

    /// <summary>Where saved starting states go: the 3D Movies folder in the app's own storage.</summary>
    public static string folder => Path.Combine(Application.persistentDataPath, "3D Movies");

    readonly NetworkVariable<byte> m_State = new((byte)State.Ready, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<bool> m_ShowSites = new(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<bool> m_Controls = new(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<float> m_SiteDiameter = new(k_DefaultSiteDiameter, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<float> m_BindingTime = new(k_DefaultBindingTime, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<byte> m_Scenario = new((byte)Scenario.AntibodyAction, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<float> m_CaptureRadius = new(k_DefaultCaptureRadius, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<byte> m_Countdown = new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<bool> m_PanelMoved = new(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<Vector3> m_PanelPosition = new(Vector3.zero, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<Quaternion> m_PanelRotation = new(Quaternion.identity, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    float m_LocalSiteDiameter = k_DefaultSiteDiameter;
    float m_LocalBindingTime = k_DefaultBindingTime;
    Scenario m_LocalScenario;
    float m_LocalCaptureRadius = k_DefaultCaptureRadius;
    State m_LocalState;
    bool m_LocalShowSites;
    bool m_LocalControls;
    int m_LocalCountdown;
    bool m_LocalPanelMoved;
    Pose m_LocalPanelPose = Pose.identity;

    TunnelContainer m_Container;
    TunnelMoviePanel m_Panel;
    MovieSnapshot m_StartingState;
    Coroutine m_Restarting;
    string m_Message = "";
    Vector3 m_Facing;
    bool m_PanelPlacedAtShared;

    public State state => IsSpawned ? (State)m_State.Value : m_LocalState;
    public bool isPlaying => state == State.Playing;
    public bool isPaused => state == State.Paused;
    public bool isRestarting => state == State.Restarting;
    public bool showSites => IsSpawned ? m_ShowSites.Value : m_LocalShowSites;
    public bool controlsShown => IsSpawned ? m_Controls.Value : m_LocalControls;

    /// <summary>
    /// How wide the binding sites are, as a fraction of each capsid's diameter (<see cref="k_MinSiteDiameter"/> to
    /// <see cref="k_MaxSiteDiameter"/>; a tenth at first). Set from the panel's slider, for everyone.
    /// </summary>
    public float siteDiameter => IsSpawned ? m_SiteDiameter.Value : m_LocalSiteDiameter;

    /// <summary>
    /// How long each antibody stays bound, from when it binds (10 s to 1 min; half a minute at first), counted only
    /// while the movie plays. Set from the menu's Binding time slider, for everyone; a change applies to antibodies
    /// that bind from then on.
    /// </summary>
    public float bindingTime => Mathf.Clamp(IsSpawned ? m_BindingTime.Value : m_LocalBindingTime, k_MinBindingTime, k_MaxBindingTime);

    /// <summary>Which movie it plays (chosen on its menu before it starts), for everyone.</summary>
    public Scenario scenario => IsSpawned ? (Scenario)m_Scenario.Value : m_LocalScenario;

    /// <summary>What people see it called: Antibody action or Ligand binding.</summary>
    public string title => TitleOf(scenario);

    /// <summary>
    /// In Ligand binding, each EGFR's capture sphere radius in ångströms at the molecules' true scale (2 to 30 Å;
    /// 10 Å at first), so it grows with the protein's magnification. Set from the menu's slider, for everyone.
    /// </summary>
    public float captureRadius => Mathf.Clamp(IsSpawned ? m_CaptureRadius.Value : m_LocalCaptureRadius, k_MinCaptureRadius, k_MaxCaptureRadius);

    /// <summary>Seconds left before Play again starts it again (0 when it isn't counting down).</summary>
    public int countdown => IsSpawned ? m_Countdown.Value : m_LocalCountdown;

    /// <summary>Whether Play again can be pressed: it has been started (and isn't already starting again).</summary>
    public bool canPlayAgain => state == State.Playing || state == State.Paused || state == State.Ended;

    /// <summary>Whether someone has moved its menu (it then stays where they put it, for everyone, instead of beside the container).</summary>
    public bool panelMoved => IsSpawned ? m_PanelMoved.Value : m_LocalPanelMoved;

    /// <summary>Where its menu was put (meaningful once <see cref="panelMoved"/>).</summary>
    public Pose panelPose => IsSpawned ? new Pose(m_PanelPosition.Value, m_PanelRotation.Value) : m_LocalPanelPose;

    public TunnelContainer container => m_Container;

    /// <summary>Its menu on this headset, once it has been shown.</summary>
    public TunnelMoviePanel panel => m_Panel;

    /// <summary>The starting state Play again goes back to (kept when it started), or null.</summary>
    public MovieSnapshot startingState => m_StartingState;

    /// <summary>The latest message, shown on everyone's panel.</summary>
    public string message => m_Message;

    void Awake() => m_Container = GetComponent<TunnelContainer>();

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        m_State.OnValueChanged += OnStateChanged;
    }

    public override void OnNetworkDespawn()
    {
        m_State.OnValueChanged -= OnStateChanged;
        base.OnNetworkDespawn();
    }

    public override void OnDestroy()
    {
        if (m_Panel != null)
            Destroy(m_Panel.gameObject);
        base.OnDestroy();
    }

    void OnStateChanged(byte previous, byte current) => StateChanged((State)previous, (State)current);

    void StateChanged(State previous, State current)
    {
        // Just before the sites first go live, everyone keeps the starting state, to save (and the host, to play again).
        if (current == State.Playing && (previous == State.Ready || previous == State.Ended))
            m_StartingState = Capture();
        Debug.Log($"[Movie] {name}: {previous} -> {current}");
    }

    #region Controls

    public void SetControls(bool on)
    {
        if (IsSpawned)
            SetControlsRpc(on);
        else
        {
            m_LocalControls = on;
            if (!on)
                m_LocalPanelMoved = false;
        }
    }

    // Hidden, its menu goes back beside the container next time it's shown.
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void SetControlsRpc(bool on)
    {
        m_Controls.Value = on;
        if (!on)
            m_PanelMoved.Value = false;
    }

    public void SetShowSites(bool on)
    {
        if (IsSpawned)
            SetShowSitesRpc(on);
        else
            m_LocalShowSites = on;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void SetShowSitesRpc(bool on) => m_ShowSites.Value = on;

    public void SetSiteDiameter(float fraction)
    {
        fraction = Mathf.Clamp(fraction, k_MinSiteDiameter, k_MaxSiteDiameter);
        if (IsSpawned)
            SetSiteDiameterRpc(fraction);
        else
            m_LocalSiteDiameter = fraction;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void SetSiteDiameterRpc(float fraction) => m_SiteDiameter.Value = Mathf.Clamp(fraction, k_MinSiteDiameter, k_MaxSiteDiameter);

    /// <summary>Sets how long antibodies that bind from now on stay bound (10 to 60 s, whole seconds), for everyone.</summary>
    public void SetBindingTime(float seconds)
    {
        seconds = Mathf.Clamp(Mathf.Round(seconds), k_MinBindingTime, k_MaxBindingTime);
        if (IsSpawned)
            SetBindingTimeRpc(seconds);
        else
            m_LocalBindingTime = seconds;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void SetBindingTimeRpc(float seconds) => m_BindingTime.Value = Mathf.Clamp(Mathf.Round(seconds), k_MinBindingTime, k_MaxBindingTime);

    /// <summary>Chooses the movie (only before it starts, or after it has ended), for everyone.</summary>
    public void SetScenario(Scenario value)
    {
        if (state != State.Ready && state != State.Ended)
            return;
        if (IsSpawned)
            SetScenarioRpc((byte)value);
        else
            m_LocalScenario = value;
        Debug.Log($"[Movie] {name}: {TitleOf(value)} chosen");
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void SetScenarioRpc(byte value)
    {
        if (state == State.Ready || state == State.Ended)
            m_Scenario.Value = (byte)Mathf.Clamp(value, 0, 1);
    }

    /// <summary>Sets the capture spheres' radius in ångströms (2 to 30, whole ångströms), for everyone.</summary>
    public void SetCaptureRadius(float angstroms)
    {
        angstroms = Mathf.Clamp(Mathf.Round(angstroms), k_MinCaptureRadius, k_MaxCaptureRadius);
        if (IsSpawned)
            SetCaptureRadiusRpc(angstroms);
        else
            m_LocalCaptureRadius = angstroms;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void SetCaptureRadiusRpc(float angstroms) => m_CaptureRadius.Value = Mathf.Clamp(Mathf.Round(angstroms), k_MinCaptureRadius, k_MaxCaptureRadius);

    /// <summary>Makes the binding sites live, if there is anything in the container for them to do.</summary>
    public void StartMovie()
    {
        if (state != State.Ready && state != State.Ended)
            return;
        int capsids = CountCapsids();
        bool ligands = scenario == Scenario.LigandBinding;
        if (capsids == 0)
        {
            Announce(ligands
                ? "Put an EGFR and some EGFR ligands in the container first."
                : "Put a virus (TBSV, 2PLV, Zika or SARS-CoV-2) and some IGG antibodies in the container first.");
            return;
        }
        SetState(State.Playing);
        int free = CountAntibodies();
        Announce(ligands
            ? $"Playing: {capsids} EGFR, {free} free ligand{(free == 1 ? "" : "s")}; capture radius {captureRadius:0} Å, each stays bound {bindingTime:0} s."
            : $"Playing: {Viruses(capsids)}, {free} antibod{(free == 1 ? "y" : "ies")}; each stays bound {bindingTime:0} s.");
    }

    // What binds in this movie, for its messages.
    string Binders(int count) => scenario == Scenario.LigandBinding
        ? (count == 1 ? "1 ligand" : $"{count} ligands")
        : (count == 1 ? "1 antibody" : $"{count} antibodies");

    public void Pause()
    {
        if (state != State.Playing)
            return;
        SetState(State.Paused);
        Announce("Paused: everything in the container holds still.");
    }

    public void Play()
    {
        if (state != State.Paused)
            return;
        SetState(State.Playing);
        Announce("Playing again.");
    }

    /// <summary>
    /// Play again: every bound antibody lets go at once; after a 5 second countdown everything in the container is put
    /// back as it was when it started (the same molecules where they were, moving as they were, and the container's
    /// viscosity, heat and motion), and it plays again. The host does it, for everyone.
    /// </summary>
    public void PlayAgain()
    {
        if (!canPlayAgain)
            return;
        Debug.Log($"[Movie] {name}: Play again pressed ({(IsSpawned ? (IsServer ? "for everyone" : "asking the host") : "not connected, here only")})");
        if (IsSpawned)
            PlayAgainRpc();
        else
            BeginPlayAgain();
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void PlayAgainRpc() => BeginPlayAgain();

    // On the host (or here, not connected).
    void BeginPlayAgain()
    {
        if (!canPlayAgain || m_Restarting != null)
            return;
        int released = ReleaseAll("Play again");
        SetState(State.Restarting);
        Announce($"Play again: {Binders(released)} let go. Starting again in {Mathf.CeilToInt(m_RestartDelay)} s.");
        m_Restarting = StartCoroutine(Restart());
    }

    /// <summary>On the host (or here, not connected): every antibody bound to a virus (and ligand bound to a protein) in the container lets go. Returns how many.</summary>
    public int ReleaseAll(string why)
    {
        int released = 0;
        foreach (var capsid in CapsidSites.all.ToArray())
            if (capsid != null && Inside(capsid.body))
                released += capsid.ReleaseAll(why);
        return released;
    }

    IEnumerator Restart()
    {
        for (int left = Mathf.CeilToInt(m_RestartDelay); left > 0; left--)
        {
            SetCountdown(left);
            yield return new WaitForSeconds(Mathf.Min(1f, m_RestartDelay));
        }
        SetCountdown(0);
        int restored = RestoreStartingState();
        m_Restarting = null;
        SetState(State.Playing);
        Announce(restored >= 0
            ? $"Playing again from the start: {restored} molecule{(restored == 1 ? "" : "s")} as they were; each {(scenario == Scenario.LigandBinding ? "ligand" : "antibody")} stays bound {bindingTime:0} s."
            : "Playing again (no starting state was kept, so the container is as it was).");
    }

    void SetCountdown(int seconds)
    {
        if (IsSpawned)
            m_Countdown.Value = (byte)Mathf.Clamp(seconds, 0, 255);
        else
            m_LocalCountdown = seconds;
    }

    /// <summary>
    /// On the host (or here, not connected): takes the molecules now in the container away and makes those of the
    /// starting state in their places (in the container's own frame, so it works wherever the container is now),
    /// with their sizes, motion and spin, and sets the container's viscosity, heat and motion as they were. Returns
    /// how many molecules it made, or -1 if no starting state was kept.
    /// </summary>
    public int RestoreStartingState()
    {
        var snapshot = m_StartingState;
        if (snapshot == null || m_Container == null)
            return -1;
        foreach (var molecule in MoleculeGravity.all.ToArray())
            if (molecule != null && molecule.isActiveAndEnabled && Inside(molecule.body))
                TunnelContainer.RemoveForEveryone(molecule.gameObject);

        var c = snapshot.container;
        m_Container.SetViscosity(c.viscosity);
        m_Container.SetTemperature(c.temperatureSetting);
        if (Enum.TryParse(c.motion, out TunnelContainer.Motion motion))
            m_Container.SetMotion(motion);

        // Kept in the container's own frame, turned with it but not scaled (see Capture).
        var t = m_Container.transform;
        int made = 0;
        foreach (var m in snapshot.molecules)
        {
            var prefab = PrefabFor(m.molecule);
            if (prefab == null)
            {
                Debug.LogWarning($"[Movie] {name}: no {m.molecule} in the Molecules list to put back");
                continue;
            }
            Make(prefab, t.position + t.rotation * m.position, t.rotation * m.rotation, m.scale,
                t.rotation * m.velocity, t.rotation * m.spin, m.parked);
            made++;
        }
        Debug.Log($"[Movie] {name}: starting state put back, {made} of {snapshot.molecules.Count} molecules");
        return made;
    }

    // The prefab the Molecules list makes a molecule of this name from.
    GameObject PrefabFor(string molecule)
    {
        if (m_Prefabs == null)
            return null;
        foreach (var entry in m_Prefabs.molecules)
            if (entry != null && entry.prefab != null && string.Equals(entry.name, molecule, StringComparison.OrdinalIgnoreCase))
                return entry.prefab;
        foreach (var entry in m_Prefabs.molecules)
            if (entry != null && entry.prefab != null && string.Equals(CapsidSites.MoleculeName(entry.prefab), molecule, StringComparison.OrdinalIgnoreCase))
                return entry.prefab;
        return null;
    }

    // One molecule, made as the Molecules list makes one: sized and placed before it wakes (its network transform
    // takes its pose when it's created), then spawned for everyone, moving (or parked, if it was).
    GameObject Make(GameObject prefab, Vector3 position, Quaternion rotation, float scale, Vector3 velocity, Vector3 spin, bool parked)
    {
        var holder = new GameObject("Movie Molecule Holder");
        holder.SetActive(false);
        var molecule = Instantiate(prefab, holder.transform);
        if (scale > 0f)
            molecule.transform.localScale = Vector3.one * scale;
        molecule.transform.SetPositionAndRotation(position, rotation);
        if (molecule.TryGetComponent(out XRMultiplayer.NetworkPhysicsInteractable networkPhysics))
            networkPhysics.spawnLocked = parked;
        molecule.transform.SetParent(null, true);
        Destroy(holder);

        if (IsSpawned && molecule.TryGetComponent(out NetworkObject networkObject))
            networkObject.Spawn(true);
        if (!parked && molecule.TryGetComponent(out Rigidbody body))
        {
            body.constraints = RigidbodyConstraints.None;
            body.isKinematic = false;
            body.linearVelocity = velocity;
            body.angularVelocity = spin;
        }
        return molecule;
    }

    void SetState(State value)
    {
        if (IsSpawned)
        {
            if (IsServer)
                m_State.Value = (byte)value;
            else
                SetStateRpc((byte)value);
            return;
        }
        var previous = m_LocalState;
        m_LocalState = value;
        if (previous != value)
            StateChanged(previous, value);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void SetStateRpc(byte value) => m_State.Value = value;

    /// <summary>A message on everyone's panel (and in the log).</summary>
    public void Announce(string text)
    {
        if (IsSpawned)
            AnnounceRpc(text);
        else
            ShowMessage(text);
    }

    [Rpc(SendTo.Everyone, InvokePermission = RpcInvokePermission.Everyone)]
    void AnnounceRpc(string text) => ShowMessage(text);

    void ShowMessage(string text)
    {
        m_Message = text;
        Debug.Log($"[Movie] {name}: {text}");
    }

    static string Viruses(int count) => count == 1 ? "1 virus" : $"{count} viruses";

    #endregion

    #region Cast

    bool Inside(Rigidbody body) => body != null && m_Container != null && m_Container.Contains(body.worldCenterOfMass);

    // Whether a molecule with sites plays in this movie: viruses in Antibody action, proteins with a pocket in Ligand binding.
    bool InThisMovie(CapsidSites capsid) => capsid.pocket == (scenario == Scenario.LigandBinding);

    /// <summary>The molecules with sites for this movie in the container: viruses (Antibody action) or EGFRs (Ligand binding).</summary>
    public int CountCapsids()
    {
        int count = 0;
        foreach (var capsid in CapsidSites.all)
            if (capsid != null && InThisMovie(capsid) && Inside(capsid.body))
                count++;
        return count;
    }

    /// <summary>Those with every site taken: fully green viruses, or light blue EGFRs (their ligand bound).</summary>
    public int CountCovered()
    {
        int count = 0;
        foreach (var capsid in CapsidSites.all)
            if (capsid != null && InThisMovie(capsid) && capsid.fullyCovered && Inside(capsid.body))
                count++;
        return count;
    }

    /// <summary>Free binders for this movie in the container: antibodies (Antibody action) or ligands (Ligand binding).</summary>
    public int CountAntibodies()
    {
        int count = 0;
        foreach (var molecule in MoleculeGravity.all)
            if (molecule != null && molecule.isActiveAndEnabled && molecule.TryGetComponent(out AntibodyBinder binder) && binder.enabled
                && binder.ligand == (scenario == Scenario.LigandBinding) && Inside(molecule.body))
                count++;
        return count;
    }

    /// <summary>Binders bound in the container: antibodies on viruses, or ligands in EGFR pockets.</summary>
    public int CountBound()
    {
        int count = 0;
        foreach (var capsid in CapsidSites.all)
            if (capsid != null && InThisMovie(capsid) && Inside(capsid.body))
                count += capsid.bindingCount;
        return count;
    }

    #endregion

    void Update() => ShowPanel();

    #region Panel

    void ShowPanel()
    {
        if (!controlsShown)
        {
            if (m_Panel != null)
                m_Panel.gameObject.SetActive(false);
            return;
        }
        if (m_Panel == null)
        {
            if (m_PanelPrefab == null)
                return;
            m_Panel = Instantiate(m_PanelPrefab);
            m_Panel.name = $"{name.Replace("(Clone)", "")} 3D Movie Menu";
            m_Panel.Bind(this);
        }
        if (!m_Panel.gameObject.activeSelf)
        {
            m_Panel.gameObject.SetActive(true);
            m_PanelPlacedAtShared = false;
            if (!panelMoved)
                PlacePanel(true);
        }
    }

    void LateUpdate()
    {
        if (m_Panel == null || !m_Panel.gameObject.activeSelf || m_Panel.ownPose)
            return;
        if (panelMoved)
            FollowSharedPose();
        else
        {
            m_PanelPlacedAtShared = false;
            PlacePanel(false);
        }
    }

    // Moved by someone: the menu goes where they put it (gliding there while it's being carried).
    void FollowSharedPose()
    {
        var pose = panelPose;
        var t = m_Panel.transform;
        if (!m_PanelPlacedAtShared || (t.position - pose.position).sqrMagnitude > 1f)
        {
            m_PanelPlacedAtShared = true;
            t.SetPositionAndRotation(pose.position, pose.rotation);
        }
        else
        {
            float k = 1f - Mathf.Exp(-15f * Time.deltaTime);
            t.SetPositionAndRotation(Vector3.Lerp(t.position, pose.position, k), Quaternion.Slerp(t.rotation, pose.rotation, k));
        }
        m_Panel.PlaceKeyboard(0.12f);
    }

    /// <summary>
    /// Shares where its menu is now, for everyone: while it's carried (often, and it may be skipped) and where it's
    /// let go (always arrives). From then on it stays there instead of beside the container, until the controls are
    /// hidden.
    /// </summary>
    public void SetPanelPose(Pose pose, bool final)
    {
        if (!IsSpawned)
        {
            m_LocalPanelMoved = true;
            m_LocalPanelPose = pose;
            return;
        }
        if (final)
            PanelPoseRpc(pose.position, pose.rotation);
        else
            PanelPoseMovingRpc(pose.position, pose.rotation);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void PanelPoseRpc(Vector3 position, Quaternion rotation) => StorePanelPose(position, rotation);

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone, Delivery = RpcDelivery.Unreliable)]
    void PanelPoseMovingRpc(Vector3 position, Quaternion rotation) => StorePanelPose(position, rotation);

    void StorePanelPose(Vector3 position, Quaternion rotation)
    {
        m_PanelPosition.Value = position;
        m_PanelRotation.Value = rotation;
        m_PanelMoved.Value = true;
    }

    /// <summary>
    /// Where its menu, let go of here, lies: flat on a tunnel wall if its middle is within reach of one (see
    /// <see cref="TunnelWallSnap"/>), else where it is. True if it went onto a wall.
    /// </summary>
    public bool SnapPanel(out Pose pose)
    {
        var t = m_Panel.transform;
        pose = new Pose(t.position, t.rotation);
        // The canvas's middle isn't the whole menu's (the bar is above it): the whole menu, bar too, goes on the wall.
        var middle = t.TransformPoint(new Vector3(0f, m_Panel.centreOffset, 0f));
        if (!TunnelWallSnap.TryFind(middle, new Vector2(m_Panel.width * 0.5f, m_Panel.totalHeight * 0.5f), m_Panel.back, m_PanelSnapDistance, out var onWall, out var wall))
            return false;
        pose = new Pose(onWall.position - onWall.rotation * new Vector3(0f, m_Panel.centreOffset, 0f), onWall.rotation);
        Debug.Log($"[Movie] {name}: menu set flat on the wall of {wall.name} at {pose.position.y:F2} m");
        return true;
    }

    /// <summary>
    /// Beside the container, on its left as whoever looks at it sees it, half way up, facing them; it turns (and moves
    /// round) to face them only while they aren't typing. On a mixed reality box set into a wall it lies flat on the
    /// wall, left of the box's front, facing into the tunnel. (Once someone moves it, it stays where they put it.)
    /// </summary>
    public void PlacePanel(bool snap)
    {
        if (m_Panel == null)
            return;
        var t = m_Container != null ? m_Container.transform : transform;
        var half = t.lossyScale * 0.5f;
        float Extent(Vector3 direction) => m_Container != null && m_Container.shape == TunnelContainer.Shape.Sphere
            ? half.x
            : Mathf.Abs(Vector3.Dot(t.right, direction)) * half.x + Mathf.Abs(Vector3.Dot(t.up, direction)) * half.y + Mathf.Abs(Vector3.Dot(t.forward, direction)) * half.z;
        float halfWidth = m_Panel.width * 0.5f;

        if (m_Container != null && m_Container.isSetInWall)
        {
            var normal = m_Container.frontNormal; // out of the wall, into the tunnel
            var left = Vector3.Cross(Vector3.up, normal).normalized;
            var front = t.position + normal * Extent(normal);
            m_Panel.transform.SetPositionAndRotation(front + left * (Extent(left) + m_PanelGap + halfWidth) + normal * 0.01f,
                Quaternion.LookRotation(-normal, Vector3.up));
            m_Facing = normal;
            m_Panel.PlaceKeyboard(0.12f);
            return;
        }

        var head = Camera.main != null ? Camera.main.transform.position : t.position - t.forward;
        var toHead = head - t.position;
        toHead.y = 0f;
        if (toHead.sqrMagnitude > 1e-4f && (!m_Panel.typing || snap || m_Facing == Vector3.zero))
        {
            toHead.Normalize();
            m_Facing = snap || m_Facing == Vector3.zero ? toHead : Vector3.Slerp(m_Facing, toHead, 1f - Mathf.Exp(-3f * Time.deltaTime)).normalized;
        }
        var facing = m_Facing != Vector3.zero ? m_Facing : -t.forward;
        var side = Vector3.Cross(Vector3.up, facing).normalized; // their left
        m_Panel.transform.SetPositionAndRotation(t.position + side * (Extent(side) + m_PanelGap + halfWidth),
            Quaternion.LookRotation(-facing, Vector3.up));
        m_Panel.PlaceKeyboard(0.12f);
    }

    #endregion

    #region Saving

    /// <summary>The container and every molecule in it, as they are now.</summary>
    public MovieSnapshot Capture()
    {
        var snapshot = new MovieSnapshot
        {
            savedFrom = Application.version,
            scenario = title,
            bindingTime = bindingTime,
            siteDiameter = siteDiameter,
            captureRadius = captureRadius,
        };
        if (m_Container == null)
            return snapshot;
        var t = m_Container.transform;
        var inverse = Quaternion.Inverse(t.rotation);
        var c = snapshot.container;
        c.shape = m_Container.shape.ToString();
        c.mixedReality = m_Container.isMixedReality;
        c.setInWall = m_Container.isSetInWall;
        c.position = t.position;
        c.rotation = t.rotation;
        c.size = t.lossyScale;
        c.viscosity = m_Container.viscosity;
        c.temperatureSetting = m_Container.temperature;
        c.temperatureCelsius = TunnelContainer.Kelvin(m_Container.temperature) - 273.15f;
        c.motion = m_Container.motion.ToString();

        foreach (var molecule in MoleculeGravity.all)
        {
            if (molecule == null || !molecule.isActiveAndEnabled || molecule.body == null || !Inside(molecule.body))
                continue;
            if (molecule.TryGetComponent(out AntibodyBinder binder) && !binder.enabled)
                continue; // binding: about to go
            var body = molecule.body;
            var m = molecule.transform;
            snapshot.molecules.Add(new MoleculeState
            {
                molecule = CapsidSites.MoleculeName(molecule.gameObject),
                position = inverse * (m.position - t.position),
                rotation = inverse * m.rotation,
                scale = m.lossyScale.x,
                velocity = body.isKinematic ? Vector3.zero : inverse * body.linearVelocity,
                spin = body.isKinematic ? Vector3.zero : inverse * body.angularVelocity,
                parked = molecule.isParked,
                antibodiesBound = molecule.TryGetComponent(out CapsidSites capsid) ? capsid.bindingCount : 0,
            });
        }
        return snapshot;
    }

    /// <summary>The state to save: the one just before the movie started, or, before it has, as things are now.</summary>
    public MovieSnapshot StateToSave(out bool atStart)
    {
        atStart = state != State.Ready && m_StartingState != null;
        return atStart ? m_StartingState : Capture();
    }

    /// <summary>Writes a starting state to the 3D Movies folder as name.json; returns the file's path.</summary>
    public string Save(string name, MovieSnapshot snapshot)
    {
        name = CleanName(name);
        if (name.Length == 0)
            throw new ArgumentException("no name");
        snapshot.name = name;
        snapshot.saved = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name + ".json");
        File.WriteAllText(path, JsonUtility.ToJson(snapshot, true));
        Debug.Log($"[Movie] saved {snapshot.molecules.Count} molecules to {path}");
        return path;
    }

    /// <summary>A name usable as a file name.</summary>
    public static string CleanName(string name)
    {
        name = (name ?? "").Replace("\n", " ").Trim();
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Length > 60 ? name.Substring(0, 60).Trim() : name;
    }

    /// <summary>A name not yet used in the 3D Movies folder, after the movie: Antibody action 1, Ligand binding 2, ...</summary>
    public static string NextName(string title = k_Title)
    {
        for (int i = 1; ; i++)
        {
            string name = $"{title} {i}";
            if (!File.Exists(Path.Combine(folder, name + ".json")))
                return name;
        }
    }

    public static bool Exists(string name) => File.Exists(Path.Combine(folder, CleanName(name) + ".json"));

    #endregion
}

/// <summary>A container and the molecules in it, as saved to the 3D Movies folder.</summary>
[Serializable]
public class MovieSnapshot
{
    public string name;
    public string saved;
    public string savedFrom; // the app's version
    [Tooltip("Which movie it is for: \"Antibody action\" or \"Ligand binding\" (empty in states saved before it had a name).")]
    public string scenario;
    [Tooltip("The movie's binding time (seconds), binding site diameter (fraction of a capsid's) and capture radius (Å); 0 in older states.")]
    public float bindingTime;
    public float siteDiameter;
    public float captureRadius;
    public ContainerState container = new();
    [Tooltip("Positions, rotations, velocities and spins in the container's own frame (meters, not scaled with it).")]
    public List<MoleculeState> molecules = new();
}

[Serializable]
public class ContainerState
{
    public string shape;
    public bool mixedReality;
    public bool setInWall;
    public Vector3 position;
    public Quaternion rotation;
    public Vector3 size;
    public float viscosity;
    public float temperatureSetting;
    public float temperatureCelsius;
    public string motion;
}

[Serializable]
public class MoleculeState
{
    public string molecule;
    public Vector3 position;
    public Quaternion rotation;
    public float scale;
    public Vector3 velocity;
    public Vector3 spin;
    public bool parked;
    public int antibodiesBound;
}
