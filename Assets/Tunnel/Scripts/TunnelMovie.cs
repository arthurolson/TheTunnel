using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// A container's 3D movie: some molecules inside it, the actors, act out what is set for them, while the others go on
/// moving as the container's medium and heat move them. Which movie it plays is data (see <see cref="MovieDefinition"/>):
/// a receptor that carries binding sites and the binder that binds to them, how strongly, and how the receptor shows
/// what is bound to it. Two are built in, and people make more with the New movie menu (see
/// <see cref="TunnelNewMoviePanel"/>), stored on the host (see <see cref="MovieLibrary"/>) and listed for everyone on
/// every container's movie menu (see <see cref="movies"/>).
/// Antibody action, the first built-in movie, is antibodies binding viruses: the capsids (TBSV, poliovirus, Zika) and
/// SARS-CoV-2's spikes carry invisible binding sites (see <see cref="CapsidSites"/>), and an antibody (IGG) that touches
/// one is drawn in and binds (see <see cref="AntibodyBinder"/>). Any number bind, one per site; each virus turns greener
/// the more of its sites are taken, fully green when all are. Each antibody stays bound for the binding time
/// (<see cref="bindingTime"/>, 10 s to 1 min from when it bound), then lets go, is pushed off the site and is soon free
/// to bind again.
/// Ligand binding, the second, is EGFR proteins and many free EGFR ligands: each EGFR has an invisible capture sphere on
/// the mouth of its ligand's pocket (its radius set by the Capture radius slider, 2 to 30 Å at the molecules' true
/// scale, 10 Å at first; see <see cref="captureRadius"/>), and a free ligand that touches it glides into its pose in the
/// crystal structure (9S3X), stays bound for the binding time, and is then let go out of the pocket, resting briefly
/// before it can bind again. An EGFR with its ligand bound is light blue.
/// A movie someone made fills the container with its receptors and binders when it is created, scattered and moving,
/// and sets the container's medium and heat whenever it is chosen.
/// Its controls are a small menu beside the container (shown by the TS Menu's Containers list, see
/// <see cref="TunnelMoviePanel"/>), which anyone can take by its bar and move, or let go of against a tunnel wall to
/// lie flat on it, for everyone: the movie picker (choosing another movie stops the one playing and lets everything
/// bound go); Start makes the binding sites live (bringing a container at absolute zero to room temperature first, as
/// nothing would move); Pause holds everything in the container still, and Play lets it go on as it was; Play again
/// lets every bound binder go at once and, after a 5 second countdown, starts again from the starting state (the
/// container's contents and settings as they were when it started); Show binding sites shows them (to check them), and
/// the sliders set how wide they are (see <see cref="siteDiameter"/>) or the capture radius, and the binding time; Save
/// starting state saves the container and every molecule in it as they were just before it started (or as they are,
/// before it has), under a name typed on the menu's keyboard, as a file in the 3D Movies folder (see
/// <see cref="folder"/>).
/// In a session everyone shares its state, the movie chosen, the sliders, the sites' visibility, the menu's place and
/// its messages; the host decides bindings, releases and the restart, and keeps the movies people make; each player
/// saves starting states to their own headset.
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

    /// <summary>
    /// Which kind of story it plays: binders on a virus's surface sites (Antibody action and movies like it), or
    /// binders in a protein's pocket (Ligand binding and movies like it). The movie itself is <see cref="definition"/>.
    /// </summary>
    public enum Scenario : byte
    {
        AntibodyAction,
        LigandBinding,
    }

    /// <summary>What people see the antibody movie called, on its menu, the Containers list and its saved states.</summary>
    public const string k_Title = "Antibody action";

    /// <summary>What people see the ligand movie called.</summary>
    public const string k_LigandTitle = "Ligand binding";

    /// <summary>A built-in movie's name as people see it.</summary>
    public static string TitleOf(Scenario scenario) => scenario == Scenario.LigandBinding ? k_LigandTitle : k_Title;

    /// <summary>The most molecules Create puts in a container (the spawn nozzle's limit per spray).</summary>
    public const int k_MaxReceptors = 10, k_MaxBinders = 100;

    /// <summary>The most triangles Create draws in all (the spawn nozzle's budget for one spray).</summary>
    public const int k_TriangleBudget = 3000000;

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
    readonly NetworkVariable<MovieEntry> m_Movie = new(MovieDefinition.AntibodyAction.ToEntry(), NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    // The movies made with the New movie menu, as the host keeps them (see MovieLibrary).
    readonly NetworkList<MovieEntry> m_Library = new();
    readonly NetworkVariable<float> m_CaptureRadius = new(k_DefaultCaptureRadius, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<byte> m_Countdown = new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<bool> m_PanelMoved = new(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<Vector3> m_PanelPosition = new(Vector3.zero, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<Quaternion> m_PanelRotation = new(Quaternion.identity, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    float m_LocalSiteDiameter = k_DefaultSiteDiameter;
    float m_LocalBindingTime = k_DefaultBindingTime;
    MovieDefinition m_LocalMovie = MovieDefinition.AntibodyAction;
    MovieEntry m_DefinitionFor;
    MovieDefinition m_Definition;
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
    // On the host (or here, not connected): bindings since it started, when the last was, and when it last said how it goes.
    int m_BindingsSinceStart;
    float m_PlayedFor;
    float m_LastBindingAt = -1f;
    float m_NextReport;
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

    /// <summary>The movie it plays (chosen on its menu), the same for everyone.</summary>
    public MovieDefinition definition
    {
        get
        {
            if (!IsSpawned)
                return m_LocalMovie;
            var entry = m_Movie.Value;
            if (m_Definition == null || !entry.Equals(m_DefinitionFor))
            {
                m_DefinitionFor = entry;
                m_Definition = entry.ToDefinition();
            }
            return m_Definition;
        }
    }

    /// <summary>Which kind of movie it plays: binders on a virus's sites, or in a protein's pocket.</summary>
    public Scenario scenario => definition.pocket ? Scenario.LigandBinding : Scenario.AntibodyAction;

    /// <summary>What people see it called: Antibody action, Ligand binding, or the name someone gave it.</summary>
    public string title => definition.name;

    /// <summary>
    /// Every movie it can play, as its menu lists them: the two built in, then those people made (the host's, in a
    /// session), A to Z.
    /// </summary>
    public List<MovieDefinition> movies
    {
        get
        {
            var list = MovieDefinition.BuiltIn;
            if (IsSpawned)
                foreach (var entry in m_Library)
                    list.Add(entry.ToDefinition());
            else
                list.AddRange(MovieLibrary.All());
            return list;
        }
    }

    /// <summary>The molecules the TS Menu lists (the New movie menu picks receptors and binders from them).</summary>
    public TunnelPrefabRegistry prefabs => m_Prefabs;

    /// <summary>Whether a molecule with binding sites is a receptor in the movie it plays.</summary>
    public bool Includes(CapsidSites capsid) => definition.Includes(capsid);

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
        if (IsServer)
        {
            // What it plays carries over from before it was shared (a container made while not connected).
            m_Movie.Value = m_LocalMovie.ToEntry();
            MovieLibrary.changed += ShareLibrary;
            ShareLibrary();
        }
    }

    public override void OnNetworkDespawn()
    {
        m_State.OnValueChanged -= OnStateChanged;
        MovieLibrary.changed -= ShareLibrary;
        base.OnNetworkDespawn();
    }

    public override void OnDestroy()
    {
        MovieLibrary.changed -= ShareLibrary;
        if (m_Panel != null)
            Destroy(m_Panel.gameObject);
        base.OnDestroy();
    }

    // On the host: everyone's list of the movies people made is the host's.
    void ShareLibrary()
    {
        if (!IsSpawned || !IsServer)
            return;
        var stored = MovieLibrary.All();
        bool same = stored.Count == m_Library.Count;
        for (int i = 0; same && i < stored.Count; i++)
            same = stored[i].ToEntry().Equals(m_Library[i]);
        if (same)
            return;
        m_Library.Clear();
        foreach (var movie in stored)
            m_Library.Add(movie.ToEntry());
    }

    void OnStateChanged(byte previous, byte current) => StateChanged((State)previous, (State)current);

    void StateChanged(State previous, State current)
    {
        // Just before the sites first go live, everyone keeps the starting state, to save (and the host, to play again).
        if (current == State.Playing && (previous == State.Ready || previous == State.Ended))
        {
            m_StartingState = Capture();
            m_BindingsSinceStart = 0;
            m_PlayedFor = 0f;
            m_LastBindingAt = -1f;
            m_NextReport = 15f;
        }
        if (current == State.Ready)
            m_StartingState = null;
        Debug.Log($"[Movie] {name}: {title} {previous} -> {current}");
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

    /// <summary>Chooses a built-in movie (see <see cref="SetMovie"/>).</summary>
    public void SetScenario(Scenario value) =>
        SetMovie(value == Scenario.LigandBinding ? MovieDefinition.LigandBinding : MovieDefinition.AntibodyAction);

    /// <summary>
    /// Chooses the movie it plays, for everyone. If one is playing (or paused, or starting again) it stops first: every
    /// bound binder lets go and it is ready to start, with what is in the container left as it is. Its sliders take the
    /// movie's settings, and a movie someone made sets the container's medium and heat as they made it.
    /// </summary>
    public void SetMovie(MovieDefinition movie)
    {
        if (movie == null)
            return;
        Debug.Log($"[Movie] {name}: {movie.name} chosen ({(IsSpawned ? (IsServer ? "for everyone" : "asking the host") : "not connected, here only")})");
        if (IsSpawned)
            SetMovieRpc(movie.ToEntry());
        else
            ChooseHere(movie);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void SetMovieRpc(MovieEntry movie) => ChooseHere(movie.ToDefinition());

    // On the host (or here, not connected). A movie with a magnification of its own sets the Molecules list's
    // Magnification to it (for everyone), so molecules added by hand from then on come out at the movie's size.
    void ChooseHere(MovieDefinition movie, bool setSize = true)
    {
        if (state != State.Ready && state != State.Ended)
        {
            if (m_Restarting != null)
            {
                StopCoroutine(m_Restarting);
                m_Restarting = null;
                SetCountdown(0);
            }
            int released = ReleaseAll($"{movie.name} chosen");
            SetState(State.Ready);
            Debug.Log($"[Movie] {name}: {title} stopped for {movie.name}; {released} let go");
        }
        if (IsSpawned)
            m_Movie.Value = movie.ToEntry();
        else
            m_LocalMovie = movie.Clone();
        SetCaptureRadius(movie.captureRadius > 0f ? movie.captureRadius : k_DefaultCaptureRadius);
        SetSiteDiameter(movie.siteDiameter > 0f ? movie.siteDiameter : k_DefaultSiteDiameter);
        SetBindingTime(movie.bindingTime > 0f ? movie.bindingTime : k_DefaultBindingTime);
        if (movie.hasStart)
            ApplyStart(movie);
        if (setSize && movie.magnification > 0)
            TunnelMoleculeMenu.SetMagnification(movie.sizeToUse);
        string size = movie.magnification > 0 ? $" at {movie.sizeLabel}" : "";
        Announce(movie.builtIn ? $"{movie.name}{size} chosen: the Molecules list now makes molecules{size}. Press Start."
            : $"{movie.name}{size} chosen: {movie.receptors} {movie.ReceptorWord(movie.receptors)} and {movie.binders} {movie.BinderWord(movie.binders)}. Press Start.");
    }

    // A movie someone made sets the container's medium and heat.
    void ApplyStart(MovieDefinition movie)
    {
        if (m_Container == null)
            return;
        m_Container.SetViscosity(movie.viscosity);
        m_Container.SetTemperature(movie.temperature);
        if (Enum.TryParse(movie.motion, out TunnelContainer.Motion motion))
            m_Container.SetMotion(motion);
    }

    /// <summary>
    /// Makes a new movie (from the New movie menu) for everyone: the host stores it (see <see cref="MovieLibrary"/>),
    /// lists it on every container's movie menu, chooses it here and fills this container with its receptors and
    /// binders at the movie's own magnification, scattered and moving (what was in the container goes first).
    /// </summary>
    public void CreateMovie(MovieDefinition movie)
    {
        if (movie == null)
            return;
        movie = movie.Clone();
        movie.magnification = movie.sizeToUse; // a movie always has one from now on
        int magnification = movie.magnification;
        Debug.Log($"[Movie] {name}: New movie \"{movie.name}\": {movie.receptors} {movie.receptor} and {movie.binders} {movie.binder} at ×{magnification:0.#} " +
            $"({(IsSpawned ? (IsServer ? "for everyone" : "asking the host") : "not connected, here only")})");
        if (IsSpawned)
            CreateMovieRpc(movie.ToEntry(), magnification);
        else
            CreateHere(movie, magnification);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void CreateMovieRpc(MovieEntry movie, int magnification) => CreateHere(movie.ToDefinition(), magnification);

    // On the host (or here, not connected).
    void CreateHere(MovieDefinition movie, int magnification)
    {
        var stored = MovieLibrary.Add(movie);
        if (stored == null)
        {
            Announce($"Couldn't store the movie \"{movie.name}\".");
            return;
        }
        ShareLibrary();
        ChooseHere(stored);
        string filled = Fill(stored, magnification);
        Announce($"New movie \"{stored.name}\" made and chosen: {filled} Press Start.");
    }

    #region Size

    /// <summary>
    /// The magnification a molecule is at: its size over its toy's in the Molecules list (0 if it isn't one of them).
    /// </summary>
    public float MagnificationOf(GameObject molecule)
    {
        var prefab = molecule != null ? PrefabFor(CapsidSites.MoleculeName(molecule)) : null;
        return prefab != null && prefab.transform.localScale.x > 0f ? molecule.transform.lossyScale.x / prefab.transform.localScale.x : 0f;
    }

    /// <summary>
    /// The molecules in the container whose size isn't the movie's magnification, by name, with the magnification they
    /// are at (rounded); empty when they all match, or the movie has none of its own.
    /// </summary>
    public List<(string name, int magnification)> SizesOff()
    {
        var off = new List<(string name, int magnification)>();
        var movie = definition;
        if (movie.magnification <= 0 || m_Container == null)
            return off;
        int wanted = movie.sizeToUse;
        foreach (var molecule in m_Container.Contents(true))
        {
            float at = MagnificationOf(molecule);
            if (at <= 0f || Mathf.Abs(at - wanted) < 0.05f * wanted)
                continue;
            string molecularName = CapsidSites.MoleculeName(molecule);
            int rounded = Mathf.Max(1, Mathf.RoundToInt(at));
            if (!off.Contains((molecularName, rounded)))
                off.Add((molecularName, rounded));
        }
        return off;
    }

    /// <summary>"EGFR here is ×10; this movie is ×20", or "" when the container's molecules are at the movie's size.</summary>
    public string sizeNote
    {
        get
        {
            var off = SizesOff();
            if (off.Count == 0)
                return "";
            string here = off.Count == 1
                ? $"{off[0].name} here is ×{off[0].magnification}"
                : string.Join(", ", off.Take(3).Select(o => $"{o.name} ×{o.magnification}")) + (off.Count > 3 ? ", ..." : "") + " here";
            return $"{here}; this movie is {definition.sizeLabel}.";
        }
    }

    /// <summary>Whether Match size can be pressed: some molecules here aren't at the movie's size, and it isn't running.</summary>
    public bool canMatchSize => (state == State.Ready || state == State.Ended) && SizesOff().Count > 0;

    /// <summary>
    /// Match size: every molecule in the container is made the movie's magnification, for everyone, staying where it is
    /// in the container. If they would crowd it (as Create would judge), the container grows first, and their places
    /// with it. Each headset resizes what it moves (its owner's), so everyone sees the same; only before Start.
    /// </summary>
    public void MatchSize()
    {
        if (!canMatchSize)
            return;
        int magnification = definition.sizeToUse;
        float grow = GrowthFor(m_Container.Contents(true), magnification);
        Debug.Log($"[Movie] {name}: Match size to ×{magnification} ({sizeNote}); container ×{grow:0.00} " +
            $"({(IsSpawned ? "for everyone" : "not connected, here only")})");
        if (IsSpawned)
            MatchSizeRpc(magnification, grow);
        else
            MatchHere(magnification, grow);
        Announce($"Match size: everything in the container is ×{magnification} now{(grow > 1.01f ? $"; the container grew to {m_Container.transform.lossyScale.x * (IsSpawned ? grow : 1f) * 100f:0} cm to hold them" : "")}.");
    }

    [Rpc(SendTo.Everyone, InvokePermission = RpcInvokePermission.Everyone)]
    void MatchSizeRpc(int magnification, float grow) => MatchHere(magnification, grow);

    // On each headset: resizes the molecules (and the container) it moves.
    void MatchHere(int magnification, float grow)
    {
        if (m_Container == null)
            return;
        var t = m_Container.transform;
        var centre = t.position;
        var inside = m_Container.Contents(true);
        if (grow > 1.001f && (!IsSpawned || IsOwner))
            t.localScale *= grow;
        int resized = 0;
        foreach (var molecule in inside)
        {
            var prefab = PrefabFor(CapsidSites.MoleculeName(molecule));
            if (prefab == null || !molecule.TryGetComponent(out MoleculeGravity physics) || !physics.simulatedHere)
                continue;
            var position = centre + (molecule.transform.position - centre) * Mathf.Max(1f, grow);
            molecule.transform.localScale = Vector3.one * prefab.transform.localScale.x * magnification;
            molecule.transform.position = position;
            if (physics.body != null)
                physics.body.position = position;
            resized++;
        }
        Physics.SyncTransforms();
        Debug.Log($"[Movie] {name}: {resized} molecule(s) made ×{magnification} here");
    }

    // How much larger the container must be for these molecules at this magnification (1 if it is large enough): room
    // for the largest side by side with space to move, and the molecules taking up at most a tenth of it.
    float GrowthFor(List<GameObject> molecules, float magnification)
    {
        if (m_Container == null || molecules.Count == 0)
            return 1f;
        float largest = 0f, volume = 0f;
        foreach (var molecule in molecules)
        {
            var prefab = PrefabFor(CapsidSites.MoleculeName(molecule));
            if (prefab == null)
                continue;
            float r = RadiusOf(prefab, magnification);
            largest = Mathf.Max(largest, r);
            volume += 4.19f * r * r * r;
        }
        var size = m_Container.transform.lossyScale;
        float smallest = m_Container.isBox ? Mathf.Min(size.x, Mathf.Min(size.y, size.z)) : size.x;
        float containerVolume = m_Container.isBox ? size.x * size.y * size.z : 0.5236f * size.x * size.x * size.x;
        float grow = Mathf.Max(2f * largest * 1.6f / Mathf.Max(1e-4f, smallest), Mathf.Pow(volume * 10f / Mathf.Max(1e-6f, containerVolume), 1f / 3f));
        return Mathf.Max(1f, grow);
    }

    #endregion

    /// <summary>Deletes a movie someone made, for everyone (never a built-in one). Containers that have it chosen and aren't playing it go back to Antibody action.</summary>
    public void DeleteMovie(string id)
    {
        if (string.IsNullOrEmpty(id) || id == MovieDefinition.k_AntibodyActionId || id == MovieDefinition.k_LigandBindingId)
            return;
        Debug.Log($"[Movie] {name}: delete the movie {id} ({(IsSpawned ? (IsServer ? "for everyone" : "asking the host") : "not connected, here only")})");
        if (IsSpawned)
            DeleteMovieRpc(id);
        else
            DeleteHere(id);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void DeleteMovieRpc(string id) => DeleteHere(id);

    // On the host (or here, not connected).
    void DeleteHere(string id)
    {
        var movie = MovieLibrary.Find(id);
        if (movie == null || !MovieLibrary.Delete(id))
        {
            Announce("That movie was already deleted.");
            return;
        }
        ShareLibrary();
        foreach (var container in FindObjectsByType<TunnelMovie>(FindObjectsInactive.Exclude))
            if (container != null && container.definition.id == id && (container.state == State.Ready || container.state == State.Ended))
                container.ChooseHere(MovieDefinition.AntibodyAction, false);
        Announce($"Deleted the movie \"{movie.name}\".");
    }

    /// <summary>
    /// On the host (or here, not connected): empties the container of molecules and puts in the movie's receptors and
    /// binders, as the Molecules list makes them at this magnification, scattered where they don't overlap (receptors
    /// first), each moving off in a random direction. A container too small for them all is made larger first. Returns
    /// what it did, for the movie's message.
    /// </summary>
    public string Fill(MovieDefinition movie, float magnification)
    {
        var receptor = PrefabFor(string.IsNullOrEmpty(movie.receptor) ? "2PLV" : movie.receptor);
        var binder = PrefabFor(movie.binder);
        if (m_Container == null || receptor == null || binder == null)
            return "nothing put in (no such molecules in the Molecules list).";
        magnification = Mathf.Max(0.1f, magnification);
        int receptors = Mathf.Clamp(movie.receptors, 0, k_MaxReceptors);
        int binders = Mathf.Clamp(movie.binders, 0, MostBinders(receptor, receptors, binder));

        foreach (var molecule in m_Container.Contents(true))
            TunnelContainer.RemoveForEveryone(molecule);

        float receptorRadius = RadiusOf(receptor, magnification);
        float binderRadius = RadiusOf(binder, magnification);

        // Big enough for every receptor side by side with room to move, and with the molecules taking up at most a
        // tenth of it.
        var t = m_Container.transform;
        var size = t.lossyScale;
        float smallest = m_Container.isBox ? Mathf.Min(size.x, Mathf.Min(size.y, size.z)) : size.x;
        float needSide = 2f * receptorRadius * 1.6f + 4f * binderRadius;
        float volume = receptors * Mathf.Pow(receptorRadius, 3f) * 4.19f + binders * Mathf.Pow(binderRadius, 3f) * 4.19f;
        float containerVolume = m_Container.isBox ? size.x * size.y * size.z : 0.5236f * size.x * size.x * size.x;
        float grow = Mathf.Max(needSide / Mathf.Max(1e-4f, smallest), Mathf.Pow(volume * 10f / Mathf.Max(1e-6f, containerVolume), 1f / 3f));
        string grew = "";
        if (grow > 1.01f)
        {
            t.localScale *= grow;
            Physics.SyncTransforms();
            grew = $" The container grew to {t.lossyScale.x * 100f:0} cm to hold them.";
            Debug.Log($"[Movie] {name}: container made {grow:0.00}× larger for {receptors} {movie.receptor} and {binders} {movie.binder} at ×{magnification:0.#}");
        }

        int madeReceptors = Scatter(receptor, receptors, magnification, receptorRadius, 0.05f);
        int madeBinders = Scatter(binder, binders, magnification, binderRadius, 0.15f);
        string what = $"{madeReceptors} {movie.ReceptorWord(madeReceptors)} and {madeBinders} {movie.BinderWord(madeBinders)} at ×{magnification:0.#}";
        Debug.Log($"[Movie] {name}: filled with {what} (asked for {movie.receptors} and {movie.binders})");
        string missing = madeReceptors < receptors || madeBinders < binders ? " (no room for the rest)" : "";
        string capped = binders < movie.binders ? $" (at most {binders} {movie.BinderWord(binders)} with {receptors} {movie.ReceptorWord(receptors)}: the triangle budget)" : "";
        return $"{what}{missing}{capped}.{grew}";
    }

    /// <summary>
    /// The most binders Create puts in with this many receptors: no more than 100, and within the triangle budget of a
    /// spray of the spawn nozzle (3 million triangles in all).
    /// </summary>
    public static int MostBinders(GameObject receptor, int receptors, GameObject binder)
    {
        int receptorTriangles = receptor != null ? SpawnNozzle.Triangles(receptor) : 0;
        int binderTriangles = binder != null ? Mathf.Max(1, SpawnNozzle.Triangles(binder)) : 1;
        int left = k_TriangleBudget - receptors * receptorTriangles;
        return Mathf.Clamp(left / binderTriangles, 1, k_MaxBinders);
    }

    // Puts this many of a molecule in the container where nothing else is, moving off at this speed in random
    // directions; returns how many it found room for.
    int Scatter(GameObject prefab, int count, float magnification, float radius, float speed)
    {
        var t = m_Container.transform;
        var half = t.lossyScale * 0.5f;
        int made = 0;
        for (int i = 0; i < count; i++)
        {
            for (int attempt = 0; attempt < 40; attempt++)
            {
                Vector3 local;
                if (m_Container.isBox)
                {
                    // Inside, clear of the walls by its radius.
                    var room = new Vector3(Mathf.Max(0f, half.x - radius * 1.1f), Mathf.Max(0f, half.y - radius * 1.1f), Mathf.Max(0f, half.z - radius * 1.1f));
                    local = new Vector3(UnityEngine.Random.Range(-room.x, room.x), UnityEngine.Random.Range(-room.y, room.y), UnityEngine.Random.Range(-room.z, room.z));
                }
                else
                {
                    local = UnityEngine.Random.insideUnitSphere * Mathf.Max(0f, half.x - radius * 1.1f);
                }
                var position = t.position + t.rotation * local;
                if (Occupied(position, radius))
                    continue;
                var molecule = Make(prefab, position, UnityEngine.Random.rotation, prefab.transform.localScale.x * magnification,
                    UnityEngine.Random.onUnitSphere * speed, Vector3.zero, false);
                if (molecule != null)
                    made++;
                Physics.SyncTransforms();
                break;
            }
        }
        return made;
    }

    /// <summary>
    /// How far a molecule made from this prefab at this magnification reaches from its middle (meters): from its drawn
    /// meshes, so a small ligand's grab collider (stretched to 2 cm at ×1) doesn't count.
    /// </summary>
    public static float RadiusOf(GameObject prefab, float magnification)
    {
        if (prefab == null)
            return 0f;
        var root = prefab.transform;
        var toRoot = root.worldToLocalMatrix;
        float reach = 0f;
        foreach (var filter in prefab.GetComponentsInChildren<MeshFilter>(true))
        {
            if (filter.sharedMesh == null || Hidden(filter.transform, root))
                continue;
            var m = toRoot * filter.transform.localToWorldMatrix;
            var b = filter.sharedMesh.bounds;
            for (int corner = 0; corner < 8; corner++)
            {
                var p = b.center + Vector3.Scale(b.extents, new Vector3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1));
                reach = Mathf.Max(reach, m.MultiplyPoint3x4(p).magnitude);
            }
        }
        static bool Hidden(Transform part, Transform root)
        {
            for (var t = part; t != null && t != root; t = t.parent)
                if (!t.gameObject.activeSelf)
                    return true; // shown only now and then (EGFR's Cys775)
            return false;
        }
        // Box corners overstate a round molecule's reach a little; that only leaves a little more room.
        return reach * Mathf.Abs(root.localScale.x) * magnification * 0.85f;
    }

    /// <summary>
    /// How large a molecule made from this prefab at this magnification is (meters): the longest side of the box round
    /// its drawn meshes, as people would measure it (EGFR is 1.2 cm at ×1).
    /// </summary>
    public static float SizeOf(GameObject prefab, float magnification)
    {
        if (prefab == null)
            return 0f;
        var root = prefab.transform;
        var toRoot = root.worldToLocalMatrix;
        var bounds = new Bounds();
        bool any = false;
        foreach (var filter in prefab.GetComponentsInChildren<MeshFilter>(true))
        {
            if (filter.sharedMesh == null || !filter.gameObject.activeInHierarchy && prefab.scene.IsValid())
                continue;
            var m = toRoot * filter.transform.localToWorldMatrix;
            var b = filter.sharedMesh.bounds;
            for (int corner = 0; corner < 8; corner++)
            {
                var p = m.MultiplyPoint3x4(b.center + Vector3.Scale(b.extents, new Vector3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1)));
                if (!any)
                    bounds = new Bounds(p, Vector3.zero);
                else
                    bounds.Encapsulate(p);
                any = true;
            }
        }
        var size = bounds.size;
        return Mathf.Max(size.x, Mathf.Max(size.y, size.z)) * Mathf.Abs(root.localScale.x) * magnification;
    }

    static readonly Collider[] s_Overlaps = new Collider[64];

    // Whether another molecule already takes up this spot.
    static bool Occupied(Vector3 position, float radius)
    {
        int count = Physics.OverlapSphereNonAlloc(position, radius * 1.05f, s_Overlaps, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
        {
            var body = s_Overlaps[i].attachedRigidbody;
            if (body != null && body.TryGetComponent(out MoleculeGravity _))
                return true;
        }
        return false;
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

    /// <summary>
    /// Makes the binding sites live, if there is anything in the container for them to do. A container at absolute zero
    /// (no heat, as a new one is) is brought to room temperature first: with no heat nothing in it moves, and nothing
    /// would ever bind.
    /// </summary>
    public void StartMovie()
    {
        if (state != State.Ready && state != State.Ended)
            return;
        var movie = definition;
        int capsids = CountCapsids();
        if (capsids == 0)
        {
            // Another movie's molecules are in the container (EGFR and its ligands while Antibody action is chosen):
            // that is the movie they mean, so it is chosen for them, ready to start.
            var fits = movies.FirstOrDefault(m => m.id != movie.id && CountReceptors(m, true) > 0 && CountBinders(m, true) > 0);
            if (fits != null)
            {
                Debug.Log($"[Movie] {name}: Start with {movie.name} chosen, but the container holds {fits.name}'s molecules: choosing {fits.name}");
                SetMovie(fits);
                Announce($"{movie.name} was chosen, but the container holds {CountReceptors(fits, true)} {fits.ReceptorWord(CountReceptors(fits, true))} and " +
                    $"{CountBinders(fits, true)} {fits.BinderWord(CountBinders(fits, true))}: {fits.name} is chosen now. Press Start.");
                return;
            }
            Announce($"{movie.name} is chosen: it needs {Needs(movie)} inside the container.{Outside(movie)} " +
                "To play another movie, choose it with ◄ ► at the top of this menu.");
            return;
        }
        string warmed = "";
        if (m_Container != null && m_Container.temperature <= 0f)
        {
            m_Container.SetTemperature(TunnelContainer.k_RoomTemperature);
            warmed = " The container had no heat, so it is now 20 °C (nothing moves without heat).";
            Debug.Log($"[Movie] {name}: the container was at absolute zero; warmed to 20 °C so the molecules move");
        }
        SetState(State.Playing);
        int free = CountAntibodies();
        Announce(movie.pocket
            ? $"Playing: {capsids} {movie.ReceptorWord(capsids)}, {free} free {movie.BinderWord(free)}; capture radius {captureRadius:0} Å, each stays bound {bindingTime:0} s.{warmed}"
            : $"Playing: {capsids} {movie.ReceptorWord(capsids)}, {free} {movie.BinderWord(free)}; each stays bound {bindingTime:0} s.{warmed}");
    }

    // What binds in this movie, for its messages.
    string Binders(int count) => $"{count} {definition.BinderWord(count)}";

    // What a movie needs in the container, for the message when it can't start.
    static string Needs(MovieDefinition movie) => movie.id == MovieDefinition.k_AntibodyActionId || string.IsNullOrEmpty(movie.receptor)
        ? "a virus (TBSV, 2PLV, Zika or SARS-CoV-2) and some IGG antibodies"
        : $"{(movie.receptor == "EGFR" ? "an" : "a")} {movie.receptor} and some {movie.binder}s (Molecules list or spawn nozzle)";

    // The movie's molecules that are in the tunnel but not in this container.
    string Outside(MovieDefinition movie)
    {
        int receptors = CountReceptors(movie, false) - CountReceptors(movie, true);
        int binders = CountBinders(movie, false) - CountBinders(movie, true);
        if (receptors <= 0 && binders <= 0)
            return "";
        var parts = new List<string>();
        if (receptors > 0)
            parts.Add($"{receptors} {movie.ReceptorWord(receptors)}");
        if (binders > 0)
            parts.Add($"{binders} {movie.BinderWord(binders)}");
        return $" ({string.Join(" and ", parts)} {(receptors + binders == 1 ? "is" : "are")} outside it: carry {(receptors + binders == 1 ? "it" : "them")} in.)";
    }

    /// <summary>A movie's receptors in this container (or anywhere, with inside false).</summary>
    public int CountReceptors(MovieDefinition movie, bool inside)
    {
        int count = 0;
        foreach (var capsid in CapsidSites.all)
            if (capsid != null && capsid.isActiveAndEnabled && movie.Includes(capsid) && (!inside || Inside(capsid.body)))
                count++;
        return count;
    }

    /// <summary>A movie's free binders in this container (or anywhere, with inside false).</summary>
    public int CountBinders(MovieDefinition movie, bool inside)
    {
        int count = 0;
        foreach (var molecule in MoleculeGravity.all)
            if (molecule != null && molecule.isActiveAndEnabled && molecule.TryGetComponent(out AntibodyBinder binder) && binder.enabled
                && movie.IsBinder(molecule.gameObject) && (!inside || Inside(molecule.body)))
                count++;
        return count;
    }

    /// <summary>
    /// On the host (or here, not connected): a receptor in this container has just taken a binder. Counted for the
    /// log's reports of how the movie goes.
    /// </summary>
    public void NoteBinding(CapsidSites receptor)
    {
        m_BindingsSinceStart++;
        m_LastBindingAt = m_PlayedFor;
        Debug.Log($"[Movie] {name}: {title}: binding {m_BindingsSinceStart} since it started, {m_PlayedFor:0} s in ({CountBound()} bound now, {CountAntibodies()} free)");
    }

    // On the host (or here, not connected), while it plays: every half minute the log says how it goes, so a headset's
    // log shows whether binders reach their sites at all, and why not if they don't.
    void Report()
    {
        if (!isPlaying || (IsSpawned && !IsServer))
            return;
        m_PlayedFor += Time.deltaTime;
        if (m_PlayedFor < m_NextReport)
            return;
        m_NextReport = m_PlayedFor + 30f;
        var movie = definition;
        int receptors = CountCapsids(), free = CountAntibodies(), bound = CountBound();
        string heat = m_Container != null ? $"{TunnelContainer.Kelvin(m_Container.temperature) - 273.15f:0} °C, viscosity {m_Container.viscosity:0.0}, {m_Container.motion}" : "";
        string last = m_LastBindingAt < 0f ? "none yet" : $"the last {m_PlayedFor - m_LastBindingAt:0} s ago";
        Debug.Log($"[Movie] {name}: {title} playing {m_PlayedFor:0} s: {receptors} {movie.ReceptorWord(receptors)}, {free} free {movie.BinderWord(free)}, {bound} bound; " +
            $"{m_BindingsSinceStart} binding{(m_BindingsSinceStart == 1 ? "" : "s")} so far ({last}); {heat}" +
            (movie.pocket ? $"; capture radius {captureRadius:0} Å" : $"; sites {siteDiameter * 100f:0}% wide"));
        if (m_BindingsSinceStart == 0 && m_PlayedFor > 50f)
            Debug.LogWarning($"[Movie] {name}: nothing has bound in {m_PlayedFor:0} s: " +
                (receptors == 0 ? $"no {movie.ReceptorWord(1)} in the container" : free == 0 ? $"no free {movie.BinderWord(2)} in the container"
                    : m_Container != null && m_Container.temperature <= 0f ? "the container has no heat, so nothing moves" : $"try a larger {(movie.pocket ? "capture radius" : "binding site")} or more {movie.BinderWord(2)}"));
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
            ? $"Playing again from the start: {restored} molecule{(restored == 1 ? "" : "s")} as they were; each {definition.BinderWord(1)} stays bound {bindingTime:0} s."
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

    // Whether a molecule with sites plays in this movie as its receptor.
    bool InThisMovie(CapsidSites capsid) => definition.Includes(capsid);

    /// <summary>The movie's receptors in the container: viruses (Antibody action), EGFRs (Ligand binding), or a made movie's.</summary>
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

    /// <summary>The movie's free binders in the container: antibodies (Antibody action), ligands (Ligand binding), or a made movie's.</summary>
    public int CountAntibodies()
    {
        int count = 0;
        foreach (var molecule in MoleculeGravity.all)
            if (molecule != null && molecule.isActiveAndEnabled && molecule.TryGetComponent(out AntibodyBinder binder) && binder.enabled
                && definition.IsBinder(molecule.gameObject) && Inside(molecule.body))
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

    void Update()
    {
        ShowPanel();
        Report();
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
        RidePanelWithWall();
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

    UnityEngine.XR.Templates.MRTTabletopAssets.TunnelSegmentGeometry m_PanelWall;

    /// <summary>
    /// A menu someone laid flat on a tunnel wall goes with the tunnel when someone moves it: the host (or this headset,
    /// when not connected) moves where it was put by the same amount (see <see cref="TunnelWallRide"/>).
    /// </summary>
    void RidePanelWithWall()
    {
        if (m_Panel == null || !panelMoved || m_Panel.ownPose || (IsSpawned && !IsServer))
        {
            m_PanelWall = null;
            return;
        }
        if (m_PanelWall != null && TunnelWallRide.Moved(m_PanelWall, out var move))
        {
            var pose = panelPose;
            var position = move.MultiplyPoint3x4(pose.position);
            var rotation = move.rotation * pose.rotation;
            if (IsSpawned)
                StorePanelPose(position, rotation);
            else
                m_LocalPanelPose = new Pose(position, rotation);
            return;
        }
        m_PanelWall = PanelWallAt(panelPose);
    }

    // The wall a menu at this pose lies flat on (as SnapPanel lays it), or null.
    UnityEngine.XR.Templates.MRTTabletopAssets.TunnelSegmentGeometry PanelWallAt(Pose pose)
    {
        var middle = pose.position + pose.rotation * new Vector3(0f, m_Panel.centreOffset, 0f);
        if (!TunnelWallSnap.TryFind(middle, new Vector2(m_Panel.width * 0.5f, m_Panel.totalHeight * 0.5f), m_Panel.back, 0.05f, out var onWall, out var wall))
            return null;
        var snapped = onWall.position - onWall.rotation * new Vector3(0f, m_Panel.centreOffset, 0f);
        return (snapped - pose.position).sqrMagnitude < 1e-4f && Quaternion.Angle(onWall.rotation, pose.rotation) < 2f ? wall : null;
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
            movie = definition.id,
            magnification = definition.magnification,
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
        c.frontFace = m_Container.frontFace;

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
                assetId = molecule.TryGetComponent(out RepositoryMolecule repository) ? repository.assetId : "",
                frozen = molecule.isFrozen,
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
    [Tooltip("Which movie it is for, by name: \"Antibody action\", \"Ligand binding\" or one someone made (empty in states saved before it had a name).")]
    public string scenario;
    [Tooltip("The movie's id (see MovieDefinition.id; empty in older states).")]
    public string movie;
    [Tooltip("The movie's own magnification (0 in older states, or for a movie without one); each molecule keeps its own size in scale.")]
    public int magnification;
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
    [Tooltip("Which face of a mixed reality box is its open front (-1 in older states: not kept).")]
    public int frontFace = -1;
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
    [Tooltip("For a molecule from the asset repository, its asset id (empty for one built into the app).")]
    public string assetId;
    [Tooltip("Frozen where it was put (the Molecules list's Freeze); false in older states.")]
    public bool frozen;
}
