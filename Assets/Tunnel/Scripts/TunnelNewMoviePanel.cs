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
/// shown beside it, for whoever pressed it: it makes a new 3D movie without code (see <see cref="MovieDefinition"/>).
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
/// It can be taken by the bar along its top and moved, and let go of within 25 cm of a flat tunnel wall it lies flat on
/// the wall; until it is moved it stays beside the movie menu. What is set on it is this player's own until Create.
/// Made by Tunnel > Movie > Build 3D Movie.
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

    TunnelMovie m_Movie;
    TunnelMoviePanel m_MoviePanel;
    readonly List<Receptor> m_Receptors = new();
    MovieDefinition m_Draft;
    int m_Receptor;
    bool m_NameTyped;
    bool m_Typing;
    bool m_Moved;
    bool m_Holding;
    string m_Note = "";
    string m_StartNote = "";
    bool m_Refresh = true;

    public float width => m_Width;
    public float height => m_Height;
    public float totalHeight => m_Height + m_BarSpace;
    public float centreOffset => m_BarSpace * 0.5f;
    public float back => 0.012f;

    /// <summary>The movie it makes, as set on it now.</summary>
    public MovieDefinition draft => m_Draft;

    /// <summary>The molecules it offers as receptors, each with the binder its sites take.</summary>
    public List<Receptor> receptors => m_Receptors;

    /// <summary>The movie menu it belongs to.</summary>
    public TunnelMoviePanel moviePanel => m_MoviePanel;

    /// <summary>Whether its keyboard is out for the name.</summary>
    public bool typing => m_Typing && m_Keyboard != null && m_Keyboard.isOpen;

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
        Slide(m_MagnificationSlider, 0, TunnelMoleculeMenu.k_Steps.Length - 1, v => { m_Draft.magnification = TunnelMoleculeMenu.MagnificationAt(v); m_Refresh = true; });
        Slide(m_ReceptorsSlider, 1, TunnelMovie.k_MaxReceptors, v => { m_Draft.receptors = Mathf.RoundToInt(v); m_Refresh = true; });
        Slide(m_BindersSlider, 1, TunnelMovie.k_MaxBinders, v => { m_Draft.binders = Mathf.RoundToInt(v); m_Refresh = true; });
        Slide(m_StrengthSlider, 2, 30, v =>
        {
            if (m_Draft.pocket)
                m_Draft.captureRadius = Mathf.Round(v);
            else
                m_Draft.siteDiameter = Mathf.Round(v) / 100f;
            m_Refresh = true;
        });
        Slide(m_TimeSlider, TunnelMovie.k_MinBindingTime, TunnelMovie.k_MaxBindingTime, v => { m_Draft.bindingTime = Mathf.Round(v); m_Refresh = true; });
        Slide(m_ViscositySlider, 0, 10, v => { m_Draft.viscosity = Mathf.Round(v) / 10f; m_Refresh = true; });
        if (m_HeatSlider != null)
        {
            m_HeatSlider.minValue = 0f;
            m_HeatSlider.maxValue = TunnelContainer.k_MaxTemperature;
            m_HeatSlider.wholeNumbers = false;
            m_HeatSlider.onValueChanged.AddListener(v => { m_Draft.temperature = Mathf.Round(v * 20f) / 20f; m_StartNote = ""; m_Refresh = true; });
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
        if (m_Keyboard != null && m_Keyboard.isOpen)
            m_Keyboard.Close(false);
        m_Typing = false;
        m_Holding = false;
    }

    /// <summary>
    /// Opens it for a container's movie, beside its movie menu: a fresh movie to set (EGFR and its ligand if there is
    /// EGFR, else the first receptor), with the container's medium and heat as they are now.
    /// </summary>
    public void Open(TunnelMovie movie, TunnelMoviePanel moviePanel)
    {
        m_Movie = movie;
        m_MoviePanel = moviePanel;
        m_Moved = false;
        gameObject.SetActive(true);
        FindReceptors();
        m_Draft = new MovieDefinition
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
        m_NameTyped = false;
        m_Note = "";
        var container = movie != null ? movie.container : null;
        m_Draft.viscosity = container != null ? container.viscosity : 1f;
        m_Draft.temperature = container != null ? container.temperature : TunnelContainer.k_RoomTemperature;
        m_Draft.motion = (container != null ? container.motion : TunnelContainer.Motion.Random).ToString();
        m_StartNote = "";
        if (m_Draft.temperature <= 0f)
        {
            // A movie at absolute zero is a still picture.
            m_Draft.temperature = TunnelContainer.k_RoomTemperature;
            m_StartNote = "The container has no heat; 20 °C here so the molecules move.";
        }
        int egfr = m_Receptors.FindIndex(r => r.name == "EGFR");
        ChooseReceptor(egfr >= 0 ? egfr : 0);
        ShowSettings();
        PlaceBesideMenu();
        Debug.Log($"[Movie] New movie menu opened for {(movie != null ? movie.name : "no container")}: {m_Receptors.Count} receptors to choose from " +
            $"({string.Join(", ", m_Receptors.Select(r => $"{r.name} + {r.binder}"))})");
    }

    /// <summary>Puts it away (Close, or New movie... again on the movie menu).</summary>
    public void Close()
    {
        if (m_Keyboard != null && m_Keyboard.isOpen)
            m_Keyboard.Close(false);
        gameObject.SetActive(false);
    }

    // The molecules in the Molecules list with binding sites whose binder is in the list too.
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

    void StepReceptor(int by)
    {
        if (m_Receptors.Count == 0)
            return;
        ChooseReceptor((m_Receptor + by + m_Receptors.Count) % m_Receptors.Count);
    }

    /// <summary>Chooses the receptor (by its place in <see cref="receptors"/>), with its binder and the colour effect that suits it.</summary>
    public void ChooseReceptor(int index)
    {
        if (m_Receptors.Count == 0 || m_Draft == null)
            return;
        m_Receptor = Mathf.Clamp(index, 0, m_Receptors.Count - 1);
        var receptor = m_Receptors[m_Receptor];
        m_Draft.receptor = receptor.name;
        m_Draft.binder = receptor.binder;
        m_Draft.pocket = receptor.sites.pocket;
        // As the built-in movies show them: a pocket solid while bound (light blue), a virus shifting by its sites (green).
        m_Draft.colourEffect = m_Draft.pocket ? MovieColourEffect.SolidWhileBound : MovieColourEffect.ShiftBySitesTaken;
        m_Draft.colour = m_Draft.pocket ? k_Colours[1] : k_Colours[0];
        if (!m_NameTyped)
            m_Draft.name = MovieLibrary.UniqueName($"{receptor.binder} on {receptor.name}");
        m_Note = "";
        ShowSettings();
    }

    void SetEffect(MovieColourEffect effect)
    {
        m_Draft.colourEffect = effect;
        m_Refresh = true;
    }

    void SetColour(int index)
    {
        m_Draft.colour = k_Colours[Mathf.Clamp(index, 0, k_Colours.Length - 1)];
        m_Refresh = true;
    }

    void ToggleMotion()
    {
        m_Draft.motion = m_Draft.motion == nameof(TunnelContainer.Motion.Ballistic) ? nameof(TunnelContainer.Motion.Random) : nameof(TunnelContainer.Motion.Ballistic);
        m_Refresh = true;
    }

    void Note(string text)
    {
        m_Note = text;
        m_Refresh = true;
    }

    /// <summary>The most binders it allows with the receptors set (the triangle budget, at most 100).</summary>
    public int mostBinders => m_Receptors.Count == 0 ? TunnelMovie.k_MaxBinders
        : TunnelMovie.MostBinders(m_Receptors[m_Receptor].prefab, m_Draft.receptors, m_Receptors[m_Receptor].binderPrefab);

    /// <summary>
    /// Create: stores the movie on the host for everyone, chooses it on this container and fills the container at the
    /// Molecules list's magnification; then this menu goes.
    /// </summary>
    public void Create()
    {
        if (m_Movie == null || m_Receptors.Count == 0)
        {
            Note("There is no molecule with binding sites to make a movie with.");
            return;
        }
        var movie = m_Draft.Clone();
        movie.name = MovieLibrary.CleanName(movie.name);
        if (movie.name.Length == 0)
            movie.name = $"{movie.binder} on {movie.receptor}";
        movie.binders = Mathf.Min(movie.binders, mostBinders);
        m_Movie.CreateMovie(movie);
        Debug.Log($"[Movie] New movie \"{movie.name}\" created from the menu at ×{movie.sizeToUse}");
        Close();
    }

    #region Name

    void ToggleKeyboard()
    {
        if (m_Keyboard == null)
            return;
        if (m_Keyboard.isOpen)
        {
            m_Keyboard.Close(true);
            return;
        }
        m_Typing = true;
        m_Keyboard.submitOnEnter = true;
        m_Keyboard.Open(m_Draft.name);
        Note("Type its name, then Enter.");
    }

    void OnNameTyped(KeyboardTextEventArgs args)
    {
        if (!m_Typing)
            return;
        SetName(args.keyboardText);
    }

    /// <summary>Sets the movie's name (as typed: at most 40 characters, nothing a file name can't hold).</summary>
    public void SetName(string text)
    {
        m_Draft.name = MovieLibrary.CleanName(text);
        m_NameTyped = true;
        m_Refresh = true;
    }

    void OnNameSubmitted(KeyboardTextEventArgs args)
    {
        if (!m_Typing)
            return;
        SetName(args.keyboardText);
        m_Typing = false;
        if (m_Keyboard.isOpen)
            m_Keyboard.Close(true);
        Note("");
    }

    void OnKeyboardClosed(KeyboardTextEventArgs args)
    {
        m_Typing = false;
        m_Refresh = true;
    }

    #endregion

    #region Showing

    void Update()
    {
        if (m_Draft == null)
            return;
        if (m_Refresh)
            ShowSettings();
    }

    void ShowSettings()
    {
        m_Refresh = false;
        if (m_Draft == null)
            return;
        bool any = m_Receptors.Count > 0;
        if (m_Title != null)
            m_Title.text = "New movie";
        SetRow(m_NameRow, $"Name: <b>{(m_Draft.name.Length > 0 ? m_Draft.name : "(none)")}</b>{(typing ? "<color=#7F7>|</color>" : "")}  <color=#AAA>{(typing ? "typing" : "tap to type")}</color>", true);
        SetRow(m_ReceptorRow, any ? $"Receptor: <b>{m_Draft.receptor}</b>  <color=#AAA>{(m_Draft.pocket ? "a pocket" : "binding sites")}</color>" : "Receptor: <color=#AAA>none with binding sites</color>", any);
        SetRow(m_ReceptorPrev, "◄", m_Receptors.Count > 1);
        SetRow(m_ReceptorNext, "►", m_Receptors.Count > 1);
        // One binder per receptor today: no arrows to step through one.
        Show(m_BinderPrev, false);
        Show(m_BinderNext, false);
        SetRow(m_BinderRow, any ? $"Binder: <b>{m_Draft.binder}</b>  <color=#AAA>binds {m_Draft.receptor}</color>" : "Binder: <color=#AAA>none</color>", any);

        int most = mostBinders;
        if (m_Draft.binders > most)
            m_Draft.binders = most;
        SetSlider(m_MagnificationSlider, TunnelMoleculeMenu.StepOf(m_Draft.sizeToUse));
        string across = m_Receptors.Count > 0 ? $"{m_Draft.receptor} {TunnelMovie.SizeOf(m_Receptors[m_Receptor].prefab, m_Draft.sizeToUse) * 100f:0.#} cm across" : "";
        SetText(m_MagnificationText, $"Magnification: ×{m_Draft.sizeToUse}  <color=#AAA>{across}</color>");
        SetSlider(m_ReceptorsSlider, m_Draft.receptors);
        if (m_BindersSlider != null)
            m_BindersSlider.maxValue = TunnelMovie.k_MaxBinders;
        SetSlider(m_BindersSlider, m_Draft.binders);
        SetText(m_ReceptorsText, $"Receptors: {m_Draft.receptors}  <color=#AAA>{m_Draft.receptor}</color>");
        SetText(m_BindersText, $"Binders: {m_Draft.binders}  <color=#AAA>{m_Draft.binder}" +
            (most < TunnelMovie.k_MaxBinders ? $", at most {most}</color>" : "</color>"));
        if (m_Draft.pocket)
        {
            SetSlider(m_StrengthSlider, m_Draft.captureRadius);
            SetText(m_StrengthText, $"Capture radius: {m_Draft.captureRadius:0} Å  <color=#AAA>round the pocket's mouth</color>");
        }
        else
        {
            SetSlider(m_StrengthSlider, m_Draft.siteDiameter * 100f);
            SetText(m_StrengthText, $"Binding site diameter: {m_Draft.siteDiameter * 100f:0}%  <color=#AAA>of each virus's</color>");
        }
        ShowTicks(m_StrengthSlider, m_Draft.pocket ? " Å" : "%");
        SetSlider(m_TimeSlider, m_Draft.bindingTime);
        SetText(m_TimeText, $"Binding time: {(m_Draft.bindingTime >= 60f ? "1 min" : $"{m_Draft.bindingTime:0} s")}  <color=#AAA>each stays bound</color>");

        bool shift = m_Draft.colourEffect == MovieColourEffect.ShiftBySitesTaken;
        SetRow(m_ShiftRow, (shift ? "■" : "□") + "  Shift toward the colour  <color=#AAA>by sites taken</color>", true);
        SetRow(m_SolidRow, (!shift ? "■" : "□") + "  Solid colour  <color=#AAA>while bound</color>", true);
        for (int i = 0; i < m_Swatches.Length && i < k_Colours.Length; i++)
        {
            var swatch = m_Swatches[i];
            if (swatch == null)
                continue;
            if (swatch.targetGraphic != null)
                swatch.targetGraphic.color = k_Colours[i];
            bool chosen = ColourNear(m_Draft.colour, k_Colours[i]);
            SetRow(swatch, chosen ? "<size=140%>■</size>" : "", true);
        }

        SetSlider(m_ViscositySlider, Mathf.Round(m_Draft.viscosity * 10f));
        SetText(m_ViscosityText, $"Viscosity: {m_Draft.viscosity:0.0}");
        SetSlider(m_HeatSlider, m_Draft.temperature);
        float kelvin = TunnelContainer.Kelvin(m_Draft.temperature);
        SetText(m_HeatText, $"Heat: {kelvin - 273.15f:0} °C  <color=#AAA>{kelvin:0} K{(m_Draft.temperature <= 0f ? ", nothing moves" : "")}; scale in °C</color>");
        bool ballistic = m_Draft.motion == nameof(TunnelContainer.Motion.Ballistic);
        SetRow(m_MotionRow, (ballistic ? "□  Random motion   ■  Ballistic" : "■  Random motion   □  Ballistic") + "  <color=#AAA>tap to switch</color>", true);

        SetRow(m_CreateButton, "Create", any && m_Movie != null);
        SetRow(m_CloseButton, "Close", true);
        if (m_Status != null)
        {
            string text = m_Note.Length > 0 ? m_Note
                : $"Create fills the container with these at ×{m_Draft.sizeToUse}, scattered and moving; what is in it now goes. " +
                  "The movie is kept on the host and listed on every movie menu; choosing it sets the Molecules list to its magnification.";
            if (m_StartNote.Length > 0)
                text += "\n<color=#FD6>" + m_StartNote + "</color>";
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
    /// Beside the movie menu, on its far side from the container, level with its top and turned as it is (so on the
    /// wall when the movie menu is on the wall); its keyboard under it.
    /// </summary>
    public void PlaceBesideMenu()
    {
        if (m_MoviePanel == null)
            return;
        var t = m_MoviePanel.transform;
        var away = m_MoviePanel.awayFromContainer;
        var position = t.position + away * (m_MoviePanel.width * 0.5f + m_Gap + m_Width * 0.5f)
            + t.up * ((m_MoviePanel.height - m_Height) * 0.5f);
        transform.SetPositionAndRotation(position, t.rotation);
        PlaceKeyboard();
    }

    void PlaceKeyboard()
    {
        if (m_Keyboard == null || m_Keyboard.transform.parent == null)
            return;
        m_Keyboard.transform.parent.localPosition = new Vector3(0f, -m_Height * 0.5f - 0.2f, -0.12f);
    }

    void LateUpdate()
    {
        // It keeps beside the movie menu (which turns toward whoever looks) until someone moves it.
        if (!m_Moved && !m_Holding && !typing && m_MoviePanel != null && m_MoviePanel.isActiveAndEnabled)
            PlaceBesideMenu();
    }

    void OnTaken(SelectEnterEventArgs args)
    {
        m_Holding = true;
        m_Moved = true;
        Debug.Log("[Movie] New movie menu taken in hand");
    }

    void OnLetGo(SelectExitEventArgs args)
    {
        m_Holding = false;
        if (isActiveAndEnabled)
            StartCoroutine(SettleAfterLetGo());
    }

    IEnumerator SettleAfterLetGo()
    {
        yield return null; // after the interaction toolkit has let go
        if (!m_Holding)
            Settle();
    }

    /// <summary>Let go of here: flat on a tunnel wall if one is within 25 cm, else where it is. True if it went onto a wall.</summary>
    public bool Settle()
    {
        m_Moved = true;
        var t = transform;
        var middle = t.TransformPoint(new Vector3(0f, centreOffset, 0f));
        if (!TunnelWallSnap.TryFind(middle, new Vector2(m_Width * 0.5f, totalHeight * 0.5f), back, 0.25f, out var onWall, out var wall))
            return false;
        var pose = new Pose(onWall.position - onWall.rotation * new Vector3(0f, centreOffset, 0f), onWall.rotation);
        t.SetPositionAndRotation(pose.position, pose.rotation);
        if (TryGetComponent(out Rigidbody body))
        {
            body.position = pose.position;
            body.rotation = pose.rotation;
        }
        PlaceKeyboard();
        Debug.Log($"[Movie] New movie menu set flat on the wall of {wall.name}");
        return true;
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
