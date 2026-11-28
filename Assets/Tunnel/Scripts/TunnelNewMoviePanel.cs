using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Samples.SpatialKeyboard;

/// <summary>
/// The New movie menu, opened by New movie... on a container's 3D movie menu (see <see cref="TunnelMoviePanel"/>) and
/// shown beside it: it makes a new 3D movie without code (see <see cref="MovieDefinition"/>).
/// Top to bottom: its name (tap, then type on the keyboard under it); the receptor, chosen with ◄ ► from the molecules
/// that carry binding sites (the viruses and EGFR), and the binder, from those that bind that receptor (only pairs with
/// binding-site data are offered: IGG for the viruses, EGFR ligand for EGFR); the magnification its molecules are made
/// at (×1 to ×20 on the Molecules list's stops, starting at the list's value); how many of each (1 to 10 receptors, 1 to
/// 100 binders, fewer when the triangle budget says so); the capture radius (a pocket, 2 to 30 Å) or the binding
/// sites' size (2 to 30% of a virus's diameter); the binding time (10 s to 1 min); how a receptor shows what is bound
/// (shifting toward a colour by the share of its sites taken, as the viruses go green, or that colour all the while
/// anything is bound, as EGFR goes light blue) and the colour; and the starting conditions, the container's viscosity,
/// heat and motion (taken from the container when the menu opens). Create stores the movie on the host for everyone,
/// adds it to every movie menu's list and chooses it here, and fills the container with those molecules at the movie's
/// own magnification, scattered and moving (what was in it goes first; see <see cref="TunnelMovie.CreateMovie"/>).
/// In a session it is one menu for everyone: New movie... on any headset opens it (or closes it) on every headset,
/// beside the movie menu as whoever opened it sees it, and everything on it is shared as it is set (the name as it is
/// typed, with the keyboard out on every headset, the receptor, every slider, the colour and the starting conditions),
/// so two players see and edit the same movie; each change sends only the setting it changed (see
/// <see cref="TunnelMovie.EditNewMovie"/>). It can be taken by the bar along its top and moved, for everyone, and let
/// go of within 25 cm of a flat tunnel wall it lies flat on the wall; until it is moved by hand it goes along when the
/// movie menu is carried. Works locally when not connected. Made by Tunnel > Movie > Build 3D Movie.
/// </summary>
public class TunnelNewMoviePanel : MonoBehaviour
{
    [SerializeField] TMP_Text m_Title;
    [SerializeField] TMP_Text m_Status;
    [SerializeField] Button m_NameRow;
    [SerializeField] XRKeyboard m_Keyboard;
    [SerializeField] Button m_ReceptorPrev;
    [SerializeField] Button m_ReceptorRow;
    [SerializeField] Button m_ReceptorNext;
    [SerializeField] Button m_BinderPrev;
    [SerializeField] Button m_BinderRow;
    [SerializeField] Button m_BinderNext;
    [SerializeField] Slider m_MagnificationSlider;
    [SerializeField] TMP_Text m_MagnificationText;
    [SerializeField] Slider m_ReceptorsSlider;
    [SerializeField] TMP_Text m_ReceptorsText;
    [SerializeField] Slider m_BindersSlider;
    [SerializeField] TMP_Text m_BindersText;
    [SerializeField] Slider m_StrengthSlider;
    [SerializeField] TMP_Text m_StrengthText;
    [SerializeField] Slider m_TimeSlider;
    [SerializeField] TMP_Text m_TimeText;
    [SerializeField] Button m_ShiftRow;
    [SerializeField] Button m_SolidRow;
    [SerializeField] Button[] m_Swatches = new Button[0];
    [SerializeField] Slider m_ViscositySlider;
    [SerializeField] TMP_Text m_ViscosityText;
    [SerializeField] Slider m_HeatSlider;
    [SerializeField] TMP_Text m_HeatText;
    [SerializeField] Button m_MotionRow;
    [SerializeField] Button m_CreateButton;
    [SerializeField] Button m_CloseButton;
    [SerializeField] XRGrabInteractable m_Grab;
    [SerializeField] float m_Width = 0.25f;
    [SerializeField] float m_Height = 0.5f;
    [SerializeField] float m_BarSpace = 0.035f;

    [SerializeField]
    [Tooltip("Gap between the movie menu and this one (meters).")]
    float m_Gap = 0.02f;

    /// <summary>The colours to choose from, left to right: green (the viruses'), light blue (EGFR's), yellow, orange, magenta, red.</summary>
    public static readonly Color[] k_Colours =
    {
        new(0.18f, 0.85f, 0.25f), new(0.55f, 0.82f, 1f), new(1f, 0.85f, 0.2f), new(1f, 0.55f, 0.15f), new(0.9f, 0.3f, 0.85f), new(0.9f, 0.2f, 0.2f),
    };

    /// <summary>A molecule that can be a receptor: its name in the Molecules list, its toy, and the binder its sites take.</summary>
    public struct Receptor
    {
        public string name;
        public GameObject prefab;
        public CapsidSites sites;
        public string binder;
        public GameObject binderPrefab;
    }

    /// <summary>
    /// The settings on it, one bit each, so a change sends only what it changed (see <see cref="TunnelMovie.EditNewMovie"/>):
    /// two players changing different settings at once both keep their change.
    /// </summary>
    [System.Flags]
    public enum DraftFields : ushort
    {
        None = 0,
        Name = 1 << 0,
        /// <summary>The receptor, with its binder and whether it binds in a pocket.</summary>
        Receptor = 1 << 1,
        Magnification = 1 << 2,
        Receptors = 1 << 3,
        Binders = 1 << 4,
        CaptureRadius = 1 << 5,
        SiteDiameter = 1 << 6,
        BindingTime = 1 << 7,
        ColourEffect = 1 << 8,
        Colour = 1 << 9,
        Viscosity = 1 << 10,
        Temperature = 1 << 11,
        Motion = 1 << 12,
    }

    TunnelMovie m_Movie;
    TunnelMoviePanel m_MoviePanel;
    readonly List<Receptor> m_Receptors = new();
    MovieDefinition m_Draft;
    MovieEntry m_DraftFor;
    bool m_Typing; // its keyboard is out here for the name
    bool m_ClosingHere; // the keyboard is being put away here because it is away for everyone
    bool m_SyncingName; // the keyboard is being given the name (typed on another headset)
    bool m_Holding;
    float m_OwnPoseUntil;
    float m_NextShare;
    bool m_PlacedAtShared;
    bool m_Refresh = true;
    float m_NextRefresh;

    public float width => m_Width;
    public float height => m_Height;
    public float totalHeight => m_Height + m_BarSpace;
    public float centreOffset => m_BarSpace * 0.5f;
    public float back => 0.012f;

    /// <summary>The movie it makes, as set on it now (the same for everyone).</summary>
    public MovieDefinition draft => CurrentDraft();

    /// <summary>The molecules it offers as receptors, each with the binder its sites take.</summary>
    public List<Receptor> receptors => m_Receptors;

    /// <summary>The movie menu it belongs to.</summary>
    public TunnelMoviePanel moviePanel => m_MoviePanel;

    /// <summary>Whether its keyboard is out for the name.</summary>
    public bool typing => m_Typing && m_Keyboard != null && m_Keyboard.isOpen;

    /// <summary>
    /// Whether this headset places it itself: while held here, and a moment after letting go (until everyone's shared
    /// place for it has caught up).
    /// </summary>
    public bool ownPose => m_Holding || Time.unscaledTime < m_OwnPoseUntil;

    void Awake()
    {
        Listen(m_NameRow, ToggleKeyboard);
        Listen(m_ReceptorPrev, () => StepReceptor(-1));
        Listen(m_ReceptorNext, () => StepReceptor(1));
        Listen(m_ReceptorRow, () => StepReceptor(1));
        // One binder per receptor today: its arrows say so.
        Listen(m_BinderPrev, () => Note("Only one binder has binding-site data for this receptor so far."));
        Listen(m_BinderNext, () => Note("Only one binder has binding-site data for this receptor so far."));
        Listen(m_BinderRow, () => Note("Only one binder has binding-site data for this receptor so far."));
        Listen(m_ShiftRow, () => SetEffect(MovieColourEffect.ShiftBySitesTaken));
        Listen(m_SolidRow, () => SetEffect(MovieColourEffect.SolidWhileBound));
        for (int i = 0; i < m_Swatches.Length; i++)
        {
            int index = i;
            Listen(m_Swatches[i], () => SetColour(index));
        }
        Listen(m_MotionRow, ToggleMotion);
        Listen(m_CreateButton, Create);
        Listen(m_CloseButton, Close);
        Slide(m_MagnificationSlider, 0, TunnelMoleculeMenu.k_Steps.Length - 1, v => Edit(DraftFields.Magnification, d => d.magnification = TunnelMoleculeMenu.MagnificationAt(v)));
        Slide(m_ReceptorsSlider, 1, TunnelMovie.k_MaxReceptors, v => Edit(DraftFields.Receptors | DraftFields.Binders, d =>
        {
            d.receptors = Mathf.RoundToInt(v);
            d.binders = Mathf.Min(d.binders, MostBinders(d));
        }));
        Slide(m_BindersSlider, 1, TunnelMovie.k_MaxBinders, v => Edit(DraftFields.Binders, d => d.binders = Mathf.Min(Mathf.RoundToInt(v), MostBinders(d))));
        Slide(m_StrengthSlider, 2, 30, v =>
        {
            if (CurrentDraft().pocket)
                Edit(DraftFields.CaptureRadius, d => d.captureRadius = Mathf.Round(v));
            else
                Edit(DraftFields.SiteDiameter, d => d.siteDiameter = Mathf.Round(v) / 100f);
        });
        Slide(m_TimeSlider, TunnelMovie.k_MinBindingTime, TunnelMovie.k_MaxBindingTime, v => Edit(DraftFields.BindingTime, d => d.bindingTime = Mathf.Round(v)));
        Slide(m_ViscositySlider, 0, 10, v => Edit(DraftFields.Viscosity, d => d.viscosity = Mathf.Round(v) / 10f));
        if (m_HeatSlider != null)
        {
            m_HeatSlider.minValue = 0f;
            m_HeatSlider.maxValue = TunnelContainer.k_MaxTemperature;
            m_HeatSlider.wholeNumbers = false;
            m_HeatSlider.onValueChanged.AddListener(v =>
            {
                Edit(DraftFields.Temperature, d => d.temperature = Mathf.Round(v * 20f) / 20f);
                m_Movie?.SetNewMovieStartNote("");
            });
        }
        if (m_Keyboard != null)
        {
            m_Keyboard.onTextUpdated.AddListener(OnNameTyped);
            m_Keyboard.onTextSubmitted.AddListener(OnNameSubmitted);
            m_Keyboard.onClosed.AddListener(OnKeyboardClosed);
        }
        if (m_Grab != null)
        {
            m_Grab.firstSelectEntered.AddListener(OnTaken);
            m_Grab.lastSelectExited.AddListener(OnLetGo);
        }
    }

    static void Listen(Button button, UnityEngine.Events.UnityAction action)
    {
        if (button != null)
            button.onClick.AddListener(action);
    }

    static void Slide(Slider slider, float min, float max, UnityEngine.Events.UnityAction<float> action)
    {
        if (slider == null)
            return;
        slider.minValue = min;
        slider.maxValue = max;
        slider.wholeNumbers = true;
        slider.onValueChanged.AddListener(action);
    }

    void OnDestroy()
    {
        if (m_Keyboard != null)
        {
            m_Keyboard.onTextUpdated.RemoveListener(OnNameTyped);
            m_Keyboard.onTextSubmitted.RemoveListener(OnNameSubmitted);
            m_Keyboard.onClosed.RemoveListener(OnKeyboardClosed);
        }
        if (m_Grab != null)
        {
            m_Grab.firstSelectEntered.RemoveListener(OnTaken);
            m_Grab.lastSelectExited.RemoveListener(OnLetGo);
        }
    }

    void OnDisable()
    {
        CloseKeyboardHere();
        m_Holding = false;
    }

    /// <summary>
    /// New movie... on the movie menu: opens it for everyone, for a container's movie, beside the movie menu as this
    /// player sees it, with a fresh movie to set (EGFR and its ligand if there is EGFR, else the first receptor) and
    /// the container's medium and heat as they are now.
    /// </summary>
    public void Open(TunnelMovie movie, TunnelMoviePanel moviePanel)
    {
        m_Movie = movie;
        m_MoviePanel = moviePanel;
        if (movie == null)
            return;
        FindReceptors();
        var start = new MovieDefinition
        {
            name = "",
            receptors = 1,
            binders = 15,
            // The Molecules list's magnification to begin with; the movie keeps its own.
            magnification = Mathf.Max(1, TunnelMoleculeMenu.currentMagnification),
            captureRadius = TunnelMovie.k_DefaultCaptureRadius,
            siteDiameter = TunnelMovie.k_DefaultSiteDiameter,
            bindingTime = TunnelMovie.k_DefaultBindingTime,
        };
        var container = movie.container;
        start.viscosity = container != null ? container.viscosity : 1f;
        start.temperature = container != null ? container.temperature : TunnelContainer.k_RoomTemperature;
        start.motion = (container != null ? container.motion : TunnelContainer.Motion.Random).ToString();
        string startNote = "";
        if (start.temperature <= 0f)
        {
            // A movie at absolute zero is a still picture.
            start.temperature = TunnelContainer.k_RoomTemperature;
            startNote = "The container has no heat; 20 °C here so the molecules move.";
        }
        int egfr = m_Receptors.FindIndex(r => r.name == "EGFR");
        if (m_Receptors.Count > 0)
            ApplyReceptor(start, egfr >= 0 ? egfr : 0, false);
        movie.OpenNewMovie(start.ToEntry(), startNote, BesidePose());
        Debug.Log($"[Movie] New movie menu opened for {movie.name}: {m_Receptors.Count} receptors to choose from " +
            $"({string.Join(", ", m_Receptors.Select(r => $"{r.name} + {r.binder}"))})");
    }

    /// <summary>Shows it on this headset, as it is for everyone (by the movie menu, when it is open for everyone).</summary>
    public void ShowHere(TunnelMovie movie, TunnelMoviePanel moviePanel)
    {
        m_Movie = movie;
        m_MoviePanel = moviePanel;
        FindReceptors();
        gameObject.SetActive(true);
        m_PlacedAtShared = false;
        if (!ownPose)
            FollowSharedPose();
        m_Refresh = true;
        ShowSettings();
    }

    /// <summary>Hides it on this headset (closed for everyone, or the movie menu put away here).</summary>
    public void HideHere()
    {
        CloseKeyboardHere();
        gameObject.SetActive(false);
    }

    /// <summary>Puts it away for everyone (Close, Create, or New movie... again on the movie menu).</summary>
    public void Close()
    {
        if (m_Movie != null)
            m_Movie.CloseNewMovie();
        if (m_Movie == null || !m_Movie.newMovieOpen)
            HideHere();
    }

    // The molecules in the Molecules list with binding sites whose binder is in the list too (the same list, in the
    // same order, on every headset).
    void FindReceptors()
    {
        m_Receptors.Clear();
        var registry = m_Movie != null ? m_Movie.prefabs : null;
        if (registry == null)
            return;
        foreach (var entry in registry.molecules)
        {
            if (entry == null || entry.prefab == null || !entry.prefab.TryGetComponent(out CapsidSites sites) || string.IsNullOrEmpty(sites.binds))
                continue;
            var binder = registry.molecules.FirstOrDefault(m => m != null && m.prefab != null && sites.Binds(m.prefab));
            if (binder == null)
                continue; // no binder with site data for it in the list
            m_Receptors.Add(new Receptor { name = entry.name, prefab = entry.prefab, sites = sites, binder = binder.name, binderPrefab = binder.prefab });
        }
    }

    /// <summary>The movie as set on it now: everyone's, with this player's own latest change shown at once.</summary>
    MovieDefinition CurrentDraft()
    {
        var entry = m_Movie != null ? m_Movie.newMovieDraft : default;
        if (m_Draft == null || !entry.Equals(m_DraftFor))
        {
            m_DraftFor = entry;
            m_Draft = entry.ToDefinition();
            m_Refresh = true;
        }
        return m_Draft;
    }

    /// <summary>Changes these settings on it, for everyone.</summary>
    void Edit(DraftFields fields, System.Action<MovieDefinition> change)
    {
        if (m_Movie == null)
            return;
        var edited = CurrentDraft().Clone();
        change(edited);
        m_Movie.EditNewMovie(edited.ToEntry(), (ushort)fields);
        m_Refresh = true;
    }

    // Where its receptor is in the list (-1 if it isn't one of them).
    int ReceptorIndex(MovieDefinition movie) => m_Receptors.FindIndex(r => r.name == movie.receptor);

    void StepReceptor(int by)
    {
        if (m_Receptors.Count == 0)
            return;
        int at = Mathf.Max(0, ReceptorIndex(CurrentDraft()));
        ChooseReceptor((at + by + m_Receptors.Count) % m_Receptors.Count);
    }

    /// <summary>Chooses the receptor (by its place in <see cref="receptors"/>), with its binder and the colour effect that suits it, for everyone.</summary>
    public void ChooseReceptor(int index)
    {
        if (m_Receptors.Count == 0 || m_Movie == null)
            return;
        index = Mathf.Clamp(index, 0, m_Receptors.Count - 1);
        bool typed = m_Movie.newMovieNameTyped;
        Edit(DraftFields.Receptor | DraftFields.ColourEffect | DraftFields.Colour | DraftFields.Binders | (typed ? DraftFields.None : DraftFields.Name),
            d => ApplyReceptor(d, index, typed));
        Note("");
    }

    // The receptor, its binder, and the colour effect that suits it; and its name too until someone has typed one.
    void ApplyReceptor(MovieDefinition movie, int index, bool nameTyped)
    {
        var receptor = m_Receptors[index];
        movie.receptor = receptor.name;
        movie.binder = receptor.binder;
        movie.pocket = receptor.sites.pocket;
        // As the built-in movies show them: a pocket solid while bound (light blue), a virus shifting by its sites (green).
        movie.colourEffect = movie.pocket ? MovieColourEffect.SolidWhileBound : MovieColourEffect.ShiftBySitesTaken;
        movie.colour = movie.pocket ? k_Colours[1] : k_Colours[0];
        if (!nameTyped)
            movie.name = MovieLibrary.UniqueName($"{receptor.binder} on {receptor.name}");
        movie.binders = Mathf.Min(movie.binders, TunnelMovie.MostBinders(receptor.prefab, movie.receptors, receptor.binderPrefab));
    }

    void SetEffect(MovieColourEffect effect) => Edit(DraftFields.ColourEffect, d => d.colourEffect = effect);

    void SetColour(int index) => Edit(DraftFields.Colour, d => d.colour = k_Colours[Mathf.Clamp(index, 0, k_Colours.Length - 1)]);

    void ToggleMotion() => Edit(DraftFields.Motion, d =>
        d.motion = d.motion == nameof(TunnelContainer.Motion.Ballistic) ? nameof(TunnelContainer.Motion.Random) : nameof(TunnelContainer.Motion.Ballistic));

    void Note(string text)
    {
        m_Movie?.SetNewMovieNote(text);
        m_Refresh = true;
    }

    // The most binders a movie allows with the receptors set (the triangle budget, at most 100).
    int MostBinders(MovieDefinition movie)
    {
        int at = ReceptorIndex(movie);
        return at < 0 ? TunnelMovie.k_MaxBinders : TunnelMovie.MostBinders(m_Receptors[at].prefab, movie.receptors, m_Receptors[at].binderPrefab);
    }

    /// <summary>The most binders it allows with the receptors set (the triangle budget, at most 100).</summary>
    public int mostBinders => MostBinders(CurrentDraft());

    /// <summary>
    /// Create: stores the movie on the host for everyone, chooses it on this container and fills the container at the
    /// movie's magnification; then this menu goes, for everyone.
    /// </summary>
    public void Create()
    {
        if (m_Movie == null || m_Receptors.Count == 0)
        {
            Note("There is no molecule with binding sites to make a movie with.");
            return;
        }
        var movie = CurrentDraft().Clone();
        movie.name = MovieLibrary.CleanName(movie.name);
        if (movie.name.Length == 0)
            movie.name = $"{movie.binder} on {movie.receptor}";
        movie.binders = Mathf.Min(movie.binders, MostBinders(movie));
        m_Movie.CreateMovie(movie);
        Debug.Log($"[Movie] New movie \"{movie.name}\" created from the menu at ×{movie.sizeToUse}");
        Close();
    }

    #region Name

    // The name row: the keyboard comes out, or goes away, on every headset.
    void ToggleKeyboard()
    {
        if (m_Keyboard == null || m_Movie == null)
            return;
        if (m_Movie.newMovieTyping)
            m_Movie.SetNewMovieTyping(false);
        else
        {
            m_Movie.SetNewMovieTyping(true);
            Note("Type its name, then Enter.");
        }
        SyncKeyboard();
    }

    /// <summary>
    /// The keyboard on this headset as it is for everyone: out or away, and holding the name as typed (so what another
    /// player types shows in it too).
    /// </summary>
    void SyncKeyboard()
    {
        if (m_Keyboard == null || m_Movie == null)
            return;
        bool want = m_Movie.newMovieTyping && isActiveAndEnabled;
        string name = CurrentDraft().name;
        if (want && !m_Keyboard.isOpen)
        {
            m_Typing = true;
            m_Keyboard.submitOnEnter = true;
            m_SyncingName = true;
            m_Keyboard.Open(name);
            m_SyncingName = false;
            m_Refresh = true;
        }
        else if (!want && m_Keyboard.isOpen)
            CloseKeyboardHere();
        else if (want && m_Keyboard.text != name)
        {
            // Typed on another headset (this player's own typing is already the name here).
            m_SyncingName = true;
            m_Keyboard.Open(name);
            m_SyncingName = false;
        }
    }

    void CloseKeyboardHere()
    {
        m_Typing = false;
        if (m_Keyboard == null || !m_Keyboard.isOpen)
            return;
        m_ClosingHere = true;
        m_Keyboard.Close(false);
        m_ClosingHere = false;
        m_Refresh = true;
    }

    void OnNameTyped(KeyboardTextEventArgs args)
    {
        if (!m_Typing || m_SyncingName)
            return;
        SetTypedName(args.keyboardText);
    }

    // The name as it is being typed: spaces kept (even at its end), at most 40 characters, nothing a file name can't hold.
    void SetTypedName(string text)
    {
        if (m_Movie == null)
            return;
        text = (text ?? "").Replace("\r", " ").Replace("\n", " ");
        foreach (char c in System.IO.Path.GetInvalidFileNameChars())
            text = text.Replace(c, '_');
        if (text.Length > MovieLibrary.k_MaxNameLength)
            text = text.Substring(0, MovieLibrary.k_MaxNameLength);
        if (text == CurrentDraft().name)
            return;
        Edit(DraftFields.Name, d => d.name = text);
        if (!m_Movie.newMovieNameTyped)
            m_Movie.SetNewMovieNameTyped(true);
    }

    /// <summary>Sets the movie's name, for everyone (as typed: at most 40 characters, nothing a file name can't hold).</summary>
    public void SetName(string text)
    {
        if (m_Movie == null)
            return;
        string name = MovieLibrary.CleanName(text);
        Edit(DraftFields.Name, d => d.name = name);
        if (!m_Movie.newMovieNameTyped)
            m_Movie.SetNewMovieNameTyped(true);
    }

    void OnNameSubmitted(KeyboardTextEventArgs args)
    {
        if (!m_Typing || m_SyncingName || m_Movie == null)
            return;
        SetName(args.keyboardText);
        m_Movie.SetNewMovieTyping(false);
        Note("");
        SyncKeyboard();
    }

    // Closed by its own key on this headset: away for everyone.
    void OnKeyboardClosed(KeyboardTextEventArgs args)
    {
        m_Typing = false;
        if (!m_ClosingHere && m_Movie != null && m_Movie.newMovieTyping)
            m_Movie.SetNewMovieTyping(false);
        m_Refresh = true;
    }

    #endregion

    #region Showing

    void Update()
    {
        if (m_Movie == null)
            return;
        SyncKeyboard();
        ShareWhileHeld();
        CurrentDraft(); // notices a change made by another player
        // A few times a second for the notes, at once for a change.
        if (m_Refresh || Time.unscaledTime >= m_NextRefresh)
            ShowSettings();
    }

    void ShowSettings()
    {
        var d = CurrentDraft();
        m_Refresh = false;
        m_NextRefresh = Time.unscaledTime + 0.25f;
        if (m_Movie == null)
            return;
        bool any = m_Receptors.Count > 0;
        int at = ReceptorIndex(d);
        if (m_Title != null)
            m_Title.text = "New movie";
        SetRow(m_NameRow, $"Name: <b>{(d.name.Length > 0 ? d.name : "(none)")}</b>{(typing ? "<color=#7F7>|</color>" : "")}  <color=#AAA>{(typing ? "typing" : "tap to type")}</color>", true);
        SetRow(m_ReceptorRow, any ? $"Receptor: <b>{d.receptor}</b>  <color=#AAA>{(d.pocket ? "a pocket" : "binding sites")}</color>" : "Receptor: <color=#AAA>none with binding sites</color>", any);
        SetRow(m_ReceptorPrev, "◄", m_Receptors.Count > 1);
        SetRow(m_ReceptorNext, "►", m_Receptors.Count > 1);
        // One binder per receptor today: no arrows to step through one.
        Show(m_BinderPrev, false);
        Show(m_BinderNext, false);
        SetRow(m_BinderRow, any ? $"Binder: <b>{d.binder}</b>  <color=#AAA>binds {d.receptor}</color>" : "Binder: <color=#AAA>none</color>", any);

        int most = MostBinders(d);
        int binders = Mathf.Min(d.binders, most);
        SetSlider(m_MagnificationSlider, TunnelMoleculeMenu.StepOf(d.sizeToUse));
        string across = at >= 0 ? $"{d.receptor} {TunnelMovie.SizeOf(m_Receptors[at].prefab, d.sizeToUse) * 100f:0.#} cm across" : "";
        SetText(m_MagnificationText, $"Magnification: ×{d.sizeToUse}  <color=#AAA>{across}</color>");
        SetSlider(m_ReceptorsSlider, d.receptors);
        if (m_BindersSlider != null)
            m_BindersSlider.maxValue = TunnelMovie.k_MaxBinders;
        SetSlider(m_BindersSlider, binders);
        SetText(m_ReceptorsText, $"Receptors: {d.receptors}  <color=#AAA>{d.receptor}</color>");
        SetText(m_BindersText, $"Binders: {binders}  <color=#AAA>{d.binder}" +
            (most < TunnelMovie.k_MaxBinders ? $", at most {most}</color>" : "</color>"));
        if (d.pocket)
        {
            SetSlider(m_StrengthSlider, d.captureRadius);
            SetText(m_StrengthText, $"Capture radius: {d.captureRadius:0} Å  <color=#AAA>round the pocket's mouth</color>");
        }
        else
        {
            SetSlider(m_StrengthSlider, d.siteDiameter * 100f);
            SetText(m_StrengthText, $"Binding site diameter: {d.siteDiameter * 100f:0}%  <color=#AAA>of each virus's</color>");
        }
        ShowTicks(m_StrengthSlider, d.pocket ? " Å" : "%");
        SetSlider(m_TimeSlider, d.bindingTime);
        SetText(m_TimeText, $"Binding time: {(d.bindingTime >= 60f ? "1 min" : $"{d.bindingTime:0} s")}  <color=#AAA>each stays bound</color>");

        bool shift = d.colourEffect == MovieColourEffect.ShiftBySitesTaken;
        SetRow(m_ShiftRow, (shift ? "■" : "□") + "  Shift toward the colour  <color=#AAA>by sites taken</color>", true);
        SetRow(m_SolidRow, (!shift ? "■" : "□") + "  Solid colour  <color=#AAA>while bound</color>", true);
        for (int i = 0; i < m_Swatches.Length && i < k_Colours.Length; i++)
        {
            var swatch = m_Swatches[i];
            if (swatch == null)
                continue;
            if (swatch.targetGraphic != null)
                swatch.targetGraphic.color = k_Colours[i];
            bool chosen = ColourNear(d.colour, k_Colours[i]);
            SetRow(swatch, chosen ? "<size=140%>■</size>" : "", true);
        }

        SetSlider(m_ViscositySlider, Mathf.Round(d.viscosity * 10f));
        SetText(m_ViscosityText, $"Viscosity: {d.viscosity:0.0}");
        SetSlider(m_HeatSlider, d.temperature);
        float kelvin = TunnelContainer.Kelvin(d.temperature);
        SetText(m_HeatText, $"Heat: {kelvin - 273.15f:0} °C  <color=#AAA>{kelvin:0} K{(d.temperature <= 0f ? ", nothing moves" : "")}; scale in °C</color>");
        bool ballistic = d.motion == nameof(TunnelContainer.Motion.Ballistic);
        SetRow(m_MotionRow, (ballistic ? "□  Random motion   ■  Ballistic" : "■  Random motion   □  Ballistic") + "  <color=#AAA>tap to switch</color>", true);

        SetRow(m_CreateButton, "Create", any && m_Movie != null);
        SetRow(m_CloseButton, "Close", true);
        if (m_Status != null)
        {
            string note = m_Movie.newMovieNote;
            string startNote = m_Movie.newMovieStartNote;
            string text = note.Length > 0 ? note
                : $"Create fills the container with these at ×{d.sizeToUse}, scattered and moving; what is in it now goes. " +
                  "The movie is kept on the host and listed on every movie menu; choosing it sets the Molecules list to its magnification.";
            if (startNote.Length > 0)
                text += "\n<color=#FD6>" + startNote + "</color>";
            if (m_Status.text != text)
                m_Status.text = text;
        }
    }

    static void Show(Button button, bool on)
    {
        if (button != null && button.gameObject.activeSelf != on)
            button.gameObject.SetActive(on);
    }

    static bool ColourNear(Color a, Color b) => Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b) < 0.02f;

    static void SetSlider(Slider slider, float value)
    {
        if (slider != null && !Mathf.Approximately(slider.value, value))
            slider.SetValueWithoutNotify(value);
    }

    static void SetText(TMP_Text text, string value)
    {
        if (text != null && text.text != value)
            text.text = value;
    }

    // The strength slider's tick labels: ångströms or percent.
    static void ShowTicks(Slider slider, string unit)
    {
        var ticks = slider != null && slider.transform.parent != null ? slider.transform.parent.Find("Ticks") : null;
        if (ticks == null)
            return;
        foreach (Transform tick in ticks)
            if (tick.TryGetComponent(out TMP_Text label))
            {
                string number = label.text.Replace("%", "").Replace(" Å", "").Trim();
                string text = number + unit;
                if (label.text != text)
                    label.text = text;
            }
    }

    static void SetRow(Button row, string text, bool interactable)
    {
        if (row == null)
            return;
        if (row.interactable != interactable)
            row.interactable = interactable;
        var label = row.GetComponentInChildren<TMP_Text>(true);
        if (label != null && label.text != text)
            label.text = text;
    }

    #endregion

    #region Placing and moving

    /// <summary>
    /// Beside the movie menu as this headset shows it, on its far side from the container, level with its top and
    /// turned as it is (so on the wall when the movie menu is on the wall).
    /// </summary>
    Pose BesidePose()
    {
        if (m_MoviePanel == null)
            return new Pose(transform.position, transform.rotation);
        var t = m_MoviePanel.transform;
        var away = m_MoviePanel.awayFromContainer;
        var position = t.position + away * (m_MoviePanel.width * 0.5f + m_Gap + m_Width * 0.5f)
            + t.up * ((m_MoviePanel.height - m_Height) * 0.5f);
        return new Pose(position, t.rotation);
    }

    /// <summary>Puts it beside the movie menu on this headset (see <see cref="BesidePose"/>), its keyboard under it.</summary>
    public void PlaceBesideMenu()
    {
        var pose = BesidePose();
        transform.SetPositionAndRotation(pose.position, pose.rotation);
        PlaceKeyboard();
    }

    /// <summary>
    /// The movie menu was carried (or let go of) by a hand on this headset: unless someone has moved this one by hand,
    /// it goes along beside the movie menu, for everyone.
    /// </summary>
    public void GoBesideMenu(bool final)
    {
        if (!gameObject.activeSelf || m_Movie == null || m_MoviePanel == null || m_Movie.newMovieMoved || m_Holding)
            return;
        PlaceBesideMenu();
        m_OwnPoseUntil = Time.unscaledTime + 1f;
        m_Movie.SetNewMoviePose(new Pose(transform.position, transform.rotation), final, false);
    }

    void PlaceKeyboard()
    {
        if (m_Keyboard == null || m_Keyboard.transform.parent == null)
            return;
        m_Keyboard.transform.parent.localPosition = new Vector3(0f, -m_Height * 0.5f - 0.2f, -0.12f);
    }

    void LateUpdate()
    {
        // Where it is for everyone, unless this headset is placing it itself (carrying it, or just let go of it).
        if (m_Movie == null || !m_Movie.newMovieOpen || ownPose)
            return;
        FollowSharedPose();
    }

    // Where it is for everyone (gliding there while someone else carries it).
    void FollowSharedPose()
    {
        if (m_Movie == null)
            return;
        var pose = m_Movie.newMoviePose;
        var t = transform;
        if (!m_PlacedAtShared || (t.position - pose.position).sqrMagnitude > 1f)
        {
            m_PlacedAtShared = true;
            t.SetPositionAndRotation(pose.position, pose.rotation);
        }
        else
        {
            float k = 1f - Mathf.Exp(-15f * Time.deltaTime);
            t.SetPositionAndRotation(Vector3.Lerp(t.position, pose.position, k), Quaternion.Slerp(t.rotation, pose.rotation, k));
        }
        PlaceKeyboard();
    }

    void OnTaken(SelectEnterEventArgs args)
    {
        m_Holding = true;
        m_NextShare = 0f;
        Debug.Log("[Movie] New movie menu taken in hand");
    }

    void OnLetGo(SelectExitEventArgs args)
    {
        m_Holding = false;
        m_OwnPoseUntil = Time.unscaledTime + 1f;
        if (isActiveAndEnabled)
            StartCoroutine(SettleAfterLetGo());
    }

    IEnumerator SettleAfterLetGo()
    {
        yield return null; // after the interaction toolkit has let go
        if (!m_Holding)
            Settle();
    }

    // While carried, everyone sees it move.
    void ShareWhileHeld()
    {
        if (!m_Holding || m_Movie == null || Time.unscaledTime < m_NextShare)
            return;
        m_NextShare = Time.unscaledTime + 1f / 15f;
        m_Movie.SetNewMoviePose(new Pose(transform.position, transform.rotation), false, true);
    }

    /// <summary>
    /// Let go of here: flat on a tunnel wall if one is within 25 cm, else where it is; shared with everyone, and it
    /// stays there. True if it went onto a wall.
    /// </summary>
    public bool Settle()
    {
        var t = transform;
        var middle = t.TransformPoint(new Vector3(0f, centreOffset, 0f));
        bool onWall = TunnelWallSnap.TryFind(middle, new Vector2(m_Width * 0.5f, totalHeight * 0.5f), back, 0.25f, out var wallPose, out var wall);
        var pose = onWall
            ? new Pose(wallPose.position - wallPose.rotation * new Vector3(0f, centreOffset, 0f), wallPose.rotation)
            : new Pose(t.position, t.rotation);
        t.SetPositionAndRotation(pose.position, pose.rotation);
        if (TryGetComponent(out Rigidbody body))
        {
            body.position = pose.position;
            body.rotation = pose.rotation;
        }
        PlaceKeyboard();
        m_OwnPoseUntil = Time.unscaledTime + 1f;
        m_Movie?.SetNewMoviePose(pose, true, true);
        if (onWall)
            Debug.Log($"[Movie] New movie menu set flat on the wall of {wall.name}");
        return onWall;
    }

    #endregion

#if UNITY_EDITOR
    /// <summary>Set by Tunnel > Movie > Build 3D Movie.</summary>
    public void Configure(TMP_Text title, TMP_Text status, Button nameRow, XRKeyboard keyboard,
        Button receptorPrev, Button receptorRow, Button receptorNext, Button binderPrev, Button binderRow, Button binderNext,
        Slider magnification, TMP_Text magnificationText, Slider receptors, TMP_Text receptorsText, Slider binders, TMP_Text bindersText, Slider strength, TMP_Text strengthText,
        Slider time, TMP_Text timeText, Button shift, Button solid, Button[] swatches, Slider viscosity, TMP_Text viscosityText,
        Slider heat, TMP_Text heatText, Button motion, Button create, Button close, XRGrabInteractable grab, float width, float height, float barSpace)
    {
        m_Title = title;
        m_Status = status;
        m_NameRow = nameRow;
        m_Keyboard = keyboard;
        m_ReceptorPrev = receptorPrev;
        m_ReceptorRow = receptorRow;
        m_ReceptorNext = receptorNext;
        m_BinderPrev = binderPrev;
        m_BinderRow = binderRow;
        m_BinderNext = binderNext;
        m_MagnificationSlider = magnification;
        m_MagnificationText = magnificationText;
        m_ReceptorsSlider = receptors;
        m_ReceptorsText = receptorsText;
        m_BindersSlider = binders;
        m_BindersText = bindersText;
        m_StrengthSlider = strength;
        m_StrengthText = strengthText;
        m_TimeSlider = time;
        m_TimeText = timeText;
        m_ShiftRow = shift;
        m_SolidRow = solid;
        m_Swatches = swatches;
        m_ViscositySlider = viscosity;
        m_ViscosityText = viscosityText;
        m_HeatSlider = heat;
        m_HeatText = heatText;
        m_MotionRow = motion;
        m_CreateButton = create;
        m_CloseButton = close;
        m_Grab = grab;
        m_Width = width;
        m_Height = height;
        m_BarSpace = barSpace;
    }
#endif
}
