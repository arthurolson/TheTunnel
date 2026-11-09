using System;
using System.Collections.Generic;
using System.IO;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// A container's 3D movie: some molecules inside it, the actors, act out what is set for them, while the others go
/// on moving as the container's medium and heat move them. For now the story is antibodies neutralising viruses: the
/// capsids (TBSV, poliovirus, Zika) carry invisible binding sites (see <see cref="CapsidSites"/>), and an antibody
/// (IGG) that touches one is drawn in and binds (see <see cref="AntibodyBinder"/>); a capsid that takes more than
/// three disappears, and when no capsid is left in the container the movie ends.
/// Its controls are a small panel on the container (shown by the TS Menu's Containers list, see
/// <see cref="TunnelMoviePanel"/>): Start movie makes the binding sites live; Pause holds everything in the container
/// still, and Play lets it go on as it was; Show binding sites shows them (to check them); Save starting state
/// saves the container and every molecule in it as they were just before the movie started (or as they are, before it
/// has), under a name typed on the panel's keyboard, as a file in the 3D Movies folder (see <see cref="folder"/>).
/// In a session everyone shares the movie's state, the sites' visibility and the panel's messages; each player saves
/// to their own headset.
/// </summary>
public class TunnelMovie : NetworkBehaviour
{
    public enum State : byte
    {
        /// <summary>Not started: the sites are not live.</summary>
        Ready,
        Playing,
        Paused,
        /// <summary>Every capsid in the container is gone.</summary>
        Ended,
    }

    [SerializeField]
    [Tooltip("The movie's controls, shown on the container.")]
    TunnelMoviePanel m_PanelPrefab;

    [SerializeField]
    [Tooltip("Gap between the container's top and the panel (meters).")]
    float m_PanelGap = 0.03f;

    /// <summary>Where saved starting states go: the 3D Movies folder in the app's own storage.</summary>
    public static string folder => Path.Combine(Application.persistentDataPath, "3D Movies");

    readonly NetworkVariable<byte> m_State = new((byte)State.Ready, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<bool> m_ShowSites = new(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<bool> m_Controls = new(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    State m_LocalState;
    bool m_LocalShowSites;
    bool m_LocalControls;

    TunnelContainer m_Container;
    TunnelMoviePanel m_Panel;
    MovieSnapshot m_StartingState;
    float m_NextCheck;
    string m_Message = "";

    public State state => IsSpawned ? (State)m_State.Value : m_LocalState;
    public bool isPlaying => state == State.Playing;
    public bool isPaused => state == State.Paused;
    public bool showSites => IsSpawned ? m_ShowSites.Value : m_LocalShowSites;
    public bool controlsShown => IsSpawned ? m_Controls.Value : m_LocalControls;
    public TunnelContainer container => m_Container;

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
        // Just before the sites go live, everyone keeps the starting state, to save.
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
            m_LocalControls = on;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void SetControlsRpc(bool on) => m_Controls.Value = on;

    public void SetShowSites(bool on)
    {
        if (IsSpawned)
            SetShowSitesRpc(on);
        else
            m_LocalShowSites = on;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void SetShowSitesRpc(bool on) => m_ShowSites.Value = on;

    /// <summary>Makes the binding sites live, if there is anything in the container for them to do.</summary>
    public void StartMovie()
    {
        if (state == State.Playing || state == State.Paused)
            return;
        int capsids = CountCapsids();
        if (capsids == 0)
        {
            Announce("Put a capsid (TBSV, 2PLV or Zika) and some IGG antibodies in the container first.");
            return;
        }
        SetState(State.Playing);
        Announce($"Playing: {capsids} capsid{(capsids == 1 ? "" : "s")}, {CountAntibodies()} antibod{(CountAntibodies() == 1 ? "y" : "ies")}.");
    }

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

    void SetState(State value)
    {
        if (IsSpawned)
        {
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

    #endregion

    #region Cast

    bool Inside(Rigidbody body) => body != null && m_Container != null && m_Container.Contains(body.worldCenterOfMass);

    /// <summary>The capsids still in the movie: in the container and not on their way out.</summary>
    public int CountCapsids()
    {
        int count = 0;
        foreach (var capsid in CapsidSites.all)
            if (capsid != null && !capsid.vanishing && Inside(capsid.body))
                count++;
        return count;
    }

    /// <summary>Free antibodies in the container.</summary>
    public int CountAntibodies()
    {
        int count = 0;
        foreach (var molecule in MoleculeGravity.all)
            if (molecule != null && molecule.isActiveAndEnabled && molecule.TryGetComponent(out AntibodyBinder binder) && binder.enabled && Inside(molecule.body))
                count++;
        return count;
    }

    /// <summary>Antibodies bound to the capsids in the container.</summary>
    public int CountBound()
    {
        int count = 0;
        foreach (var capsid in CapsidSites.all)
            if (capsid != null && !capsid.vanishing && Inside(capsid.body))
                count += capsid.bindingCount;
        return count;
    }

    #endregion

    void Update()
    {
        // The host (or this headset, alone) ends the movie when no capsid is left in the container.
        bool decides = !IsSpawned || IsServer;
        if (decides && (state == State.Playing || state == State.Paused) && Time.time >= m_NextCheck)
        {
            m_NextCheck = Time.time + 0.5f;
            if (CountCapsids() == 0)
            {
                SetState(State.Ended);
                Announce("The end: every capsid is gone.");
            }
        }

        ShowPanel();
    }

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
            m_Panel.name = $"{name.Replace("(Clone)", "")} Movie Panel";
            m_Panel.Bind(this);
        }
        if (!m_Panel.gameObject.activeSelf)
            m_Panel.gameObject.SetActive(true);
    }

    void LateUpdate()
    {
        if (m_Panel != null && m_Panel.gameObject.activeSelf)
            PlacePanel(false);
    }

    /// <summary>
    /// Over the container's top, facing whoever looks at it (it turns to face them only while they aren't typing). On
    /// a mixed reality box set into a wall, over its front, on the tunnel's side.
    /// </summary>
    public void PlacePanel(bool snap)
    {
        var t = m_Container != null ? m_Container.transform : transform;
        var half = t.lossyScale * 0.5f;
        var head = Camera.main != null ? Camera.main.transform.position : t.position - t.forward;
        var toHead = head - t.position;
        toHead.y = 0f;
        var facing = toHead.sqrMagnitude > 1e-4f ? toHead.normalized : -t.forward;

        float Extent(Vector3 direction) => m_Container != null && m_Container.shape == TunnelContainer.Shape.Sphere
            ? half.x
            : Mathf.Abs(Vector3.Dot(t.right, direction)) * half.x + Mathf.Abs(Vector3.Dot(t.up, direction)) * half.y + Mathf.Abs(Vector3.Dot(t.forward, direction)) * half.z;

        var position = t.position + Vector3.up * (Extent(Vector3.up) + m_PanelGap + m_Panel.height * 0.5f);
        bool inWall = m_Container != null && m_Container.isSetInWall;
        if (inWall)
            position += facing * (Extent(facing) + 0.05f);
        m_Panel.transform.position = position;

        if (m_Panel.typing && !snap)
            return;
        // Its keyboard: just in front of the container's near top edge, below the panel.
        m_Panel.PlaceKeyboard(inWall ? 0.1f : Extent(facing) + 0.1f);
        var forward = position - head;
        forward.y = 0f;
        if (forward.sqrMagnitude < 1e-4f)
            return;
        var rotation = Quaternion.LookRotation(forward.normalized, Vector3.up);
        m_Panel.transform.rotation = snap ? rotation : Quaternion.Slerp(m_Panel.transform.rotation, rotation, 1f - Mathf.Exp(-4f * Time.deltaTime));
    }

    #endregion

    #region Saving

    /// <summary>The container and every molecule in it, as they are now.</summary>
    public MovieSnapshot Capture()
    {
        var snapshot = new MovieSnapshot { savedFrom = Application.version };
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

    /// <summary>A name not yet used in the 3D Movies folder: Movie 1, Movie 2, ...</summary>
    public static string NextName()
    {
        for (int i = 1; ; i++)
        {
            string name = $"Movie {i}";
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
