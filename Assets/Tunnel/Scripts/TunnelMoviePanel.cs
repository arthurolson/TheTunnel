using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Samples.SpatialKeyboard;

/// <summary>
/// A container's 3D movie menu (see <see cref="TunnelMovie"/>), shown beside the container. Its top row chooses the
/// movie from every one there is (the two built in, Antibody action and Ligand binding, then those people made), with ◄
/// and ► (or a tap on the name for the next); choosing another while one plays stops it. Under it, New movie... opens
/// the New movie menu beside this one (see <see cref="TunnelNewMoviePanel"/>), and Delete movie deletes the movie
/// chosen, for everyone, if someone made it (a second tap confirms; the built-in two can't be deleted). The picker shows
/// each movie's magnification (Ligand binding ×20); choosing a movie sets the Molecules list's Magnification to it, and
/// when molecules in the container are at another size the status says so ("EGFR here is ×10; this movie is ×20") and
/// Match size, between New movie... and Delete, makes them the movie's size for everyone (before Start). Then Start,
/// Pause, Play, Play again
/// (everything bound lets go, and after a 5 second countdown it starts again from the starting state), Show binding
/// sites (the capture spheres, in Ligand binding), a slider for the binding sites' diameter (2% to 30% of each
/// capsid's) in Antibody action or the capture radius (2 to 30 Å) in Ligand binding, the binding time (10 s to 1 min,
/// how long each antibody or ligand stays bound) and Save starting state (which asks for a name on its keyboard,
/// then saves; the name offered is the movie's). Its status shows what is happening, what is in the container (viruses
/// and antibodies, or EGFRs and ligands), and the latest message.
/// Anyone can take it by the bar along its top and move it, for everyone; let go of within 25 cm of a flat tunnel
/// wall, it lies flat on the wall, like a sign. Hiding the controls (TS Menu > Containers) and showing them again
/// puts it back beside the container.
/// Made by Tunnel > Movie > Build 3D Movie.
/// </summary>
public class TunnelMoviePanel : MonoBehaviour
{
    [SerializeField]
    [Tooltip("Shows the movie chosen; a tap chooses the next.")]
    Button m_ScenarioRow;

    [SerializeField]
    [Tooltip("Chooses the movie before this one in the list.")]
    Button m_PrevMovieButton;

    [SerializeField]
    [Tooltip("Chooses the movie after this one in the list.")]
    Button m_NextMovieButton;

    [SerializeField]
    [Tooltip("Opens the New movie menu beside this one.")]
    Button m_NewMovieRow;

    [SerializeField]
    [Tooltip("Deletes the movie chosen, if someone made it (a second tap confirms).")]
    Button m_DeleteMovieRow;

    [SerializeField]
    [Tooltip("Match size: makes the molecules in the container the movie's magnification, for everyone.")]
    Button m_MatchSizeRow;

    [SerializeField]
    [Tooltip("The New movie menu (made by Tunnel > Movie > Build 3D Movie).")]
    TunnelNewMoviePanel m_NewMoviePrefab;

    [SerializeField]
    Button m_StartRow;

    [SerializeField]
    Button m_PauseRow;

    [SerializeField]
    Button m_PlayRow;

    [SerializeField]
    Button m_SitesRow;

    [SerializeField]
    Button m_SaveRow;

    [SerializeField]
    Button m_PlayAgainRow;

    [SerializeField]
    Slider m_BindingTimeSlider;

    [SerializeField]
    TMP_Text m_BindingTimeText;

    [SerializeField]
    [Tooltip("What hands take it by: the bar along its top.")]
    XRGrabInteractable m_Grab;

    [SerializeField]
    [Tooltip("Room the bar takes above the canvas (meters).")]
    float m_BarSpace = 0.035f;

    [SerializeField]
    Slider m_DiameterSlider;

    [SerializeField]
    TMP_Text m_DiameterText;

    [SerializeField]
    TMP_Text m_Title;

    [SerializeField]
    TMP_Text m_Status;

    [SerializeField]
    XRKeyboard m_Keyboard;

    [SerializeField]
    [Tooltip("The panel's height (meters).")]
    float m_Height = 0.2f;

    [SerializeField]
    [Tooltip("The panel's width (meters).")]
    float m_Width = 0.25f;

    TunnelMovie m_Movie;
    MovieSnapshot m_Saving;
    bool m_SavingAtStart;
    string m_Note = "";
    float m_NextRefresh;
    float m_DiameterShown = -1f;
    float m_BindingTimeShown = -1f;
    string m_MovieShown;
    bool m_PocketShown;
    TunnelNewMoviePanel m_NewMovie;
    string m_DeleteArmedFor;
    float m_DeleteArmedUntil;
    bool m_Holding;
    float m_OwnPoseUntil;
    float m_NextShare;

    public float height => m_Height;

    public float width => m_Width;

    /// <summary>Its whole height, the bar included (meters).</summary>
    public float totalHeight => m_Height + m_BarSpace;

    /// <summary>How far the middle of the whole menu (bar included) is above the canvas's middle (meters).</summary>
    public float centreOffset => m_BarSpace * 0.5f;

    /// <summary>How far its back is behind its face (meters): on a wall, the back is just off the wall.</summary>
    public float back => 0.012f;

    /// <summary>Whether a hand on this headset holds it.</summary>
    public bool held => m_Holding;

    /// <summary>
    /// Whether this headset places it itself: while held here, and a moment after letting go (until everyone's
    /// shared place for it has caught up).
    /// </summary>
    public bool ownPose => m_Holding || Time.unscaledTime < m_OwnPoseUntil;

    /// <summary>Whether its keyboard is out for a name (the panel then stays put).</summary>
    public bool typing => m_Saving != null && m_Keyboard != null && m_Keyboard.isOpen;

    public void Bind(TunnelMovie movie)
    {
        m_Movie = movie;
        m_Movie.PlacePanel(true);
    }

    /// <summary>Its New movie menu, once it has been opened on this headset.</summary>
    public TunnelNewMoviePanel newMovieMenu => m_NewMovie;

    /// <summary>Along the menu, the way away from its container (where the New movie menu goes).</summary>
    public Vector3 awayFromContainer
    {
        get
        {
            var right = transform.right;
            var container = m_Movie != null ? m_Movie.container : null;
            if (container == null)
                return -right;
            return Vector3.Dot(transform.position - container.transform.position, right) >= 0f ? right : -right;
        }
    }

    void Awake()
    {
        m_ScenarioRow?.onClick.AddListener(() => StepMovie(1));
        m_PrevMovieButton?.onClick.AddListener(() => StepMovie(-1));
        m_NextMovieButton?.onClick.AddListener(() => StepMovie(1));
        m_NewMovieRow?.onClick.AddListener(ToggleNewMovie);
        m_DeleteMovieRow?.onClick.AddListener(DeleteMovie);
        m_MatchSizeRow?.onClick.AddListener(() => m_Movie?.MatchSize());
        m_StartRow?.onClick.AddListener(() => m_Movie?.StartMovie());
        m_PauseRow?.onClick.AddListener(() => m_Movie?.Pause());
        m_PlayRow?.onClick.AddListener(() => m_Movie?.Play());
        m_SitesRow?.onClick.AddListener(() => m_Movie?.SetShowSites(!m_Movie.showSites));
        m_SaveRow?.onClick.AddListener(StartSaving);
        m_PlayAgainRow?.onClick.AddListener(() => m_Movie?.PlayAgain());
        if (m_BindingTimeSlider != null)
        {
            // Whole seconds.
            m_BindingTimeSlider.minValue = TunnelMovie.k_MinBindingTime;
            m_BindingTimeSlider.maxValue = TunnelMovie.k_MaxBindingTime;
            m_BindingTimeSlider.wholeNumbers = true;
            m_BindingTimeSlider.SetValueWithoutNotify(TunnelMovie.k_DefaultBindingTime);
            m_BindingTimeSlider.onValueChanged.AddListener(OnBindingTimeMoved);
        }
        if (m_Grab != null)
        {
            m_Grab.firstSelectEntered.AddListener(OnTaken);
            m_Grab.lastSelectExited.AddListener(OnLetGo);
        }
        if (m_DiameterSlider != null)
        {
            // Whole percent of the capsid's diameter.
            m_DiameterSlider.minValue = TunnelMovie.k_MinSiteDiameter * 100f;
            m_DiameterSlider.maxValue = TunnelMovie.k_MaxSiteDiameter * 100f;
            m_DiameterSlider.wholeNumbers = true;
            m_DiameterSlider.SetValueWithoutNotify(TunnelMovie.k_DefaultSiteDiameter * 100f);
            m_DiameterSlider.onValueChanged.AddListener(OnDiameterMoved);
        }
        if (m_Keyboard != null)
        {
            m_Keyboard.onTextSubmitted.AddListener(OnNameSubmitted);
            m_Keyboard.onTextUpdated.AddListener(OnNameTyped);
            m_Keyboard.onClosed.AddListener(OnKeyboardClosed);
        }
    }

    /// <summary>
    /// Puts its keyboard (tilted toward whoever types) this far toward them from the panel, and a little below its
    /// bottom edge (clear of the panel's status, which shows the name typed): just in front of the container.
    /// </summary>
    public void PlaceKeyboard(float forward)
    {
        if (m_Keyboard == null || m_Keyboard.transform.parent == null)
            return;
        m_Keyboard.transform.parent.localPosition = new Vector3(0f, -m_Height * 0.5f - 0.2f, -forward);
    }

    void OnDestroy()
    {
        if (m_NewMovie != null)
            Destroy(m_NewMovie.gameObject);
        if (m_Grab != null)
        {
            m_Grab.firstSelectEntered.RemoveListener(OnTaken);
            m_Grab.lastSelectExited.RemoveListener(OnLetGo);
        }
        if (m_Keyboard != null)
        {
            m_Keyboard.onTextSubmitted.RemoveListener(OnNameSubmitted);
            m_Keyboard.onTextUpdated.RemoveListener(OnNameTyped);
            m_Keyboard.onClosed.RemoveListener(OnKeyboardClosed);
        }
    }

    void OnDisable()
    {
        if (m_NewMovie != null)
            m_NewMovie.Close();
        if (m_Keyboard != null && m_Keyboard.isOpen)
            m_Keyboard.Close(false);
        m_Saving = null;
        m_Holding = false;
    }

    #region Moving

    void OnTaken(SelectEnterEventArgs args)
    {
        m_Holding = true;
        m_NextShare = 0f;
        Debug.Log($"[Movie] {name} taken in hand");
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
        if (m_Movie == null || m_Holding)
            yield break;
        Settle();
    }

    /// <summary>
    /// Let go of here: flat on a tunnel wall if one is within 25 cm, else where it is; shared with everyone. True if
    /// it went onto a wall.
    /// </summary>
    public bool Settle()
    {
        if (m_Movie == null)
            return false;
        bool onWall = m_Movie.SnapPanel(out var pose);
        transform.SetPositionAndRotation(pose.position, pose.rotation);
        if (TryGetComponent(out Rigidbody body))
        {
            body.position = pose.position;
            body.rotation = pose.rotation;
        }
        PlaceKeyboard(0.12f);
        m_Movie.SetPanelPose(pose, true);
        m_OwnPoseUntil = Time.unscaledTime + 1f;
        if (onWall)
            XRMultiplayer.PlayerHudNotification.Instance?.ShowText($"{m_Movie.title} menu on the wall: take it by its bar to move it", 2f);
        return onWall;
    }

    // While carried, everyone sees it move.
    void ShareWhileHeld()
    {
        if (!m_Holding || m_Movie == null || Time.unscaledTime < m_NextShare)
            return;
        m_NextShare = Time.unscaledTime + 1f / 15f;
        m_Movie.SetPanelPose(new Pose(transform.position, transform.rotation), false);
    }

    #endregion

    #region Movies

    /// <summary>
    /// Chooses the movie this many places along the list (+1 the next, -1 the one before, round the ends), for
    /// everyone. One playing stops.
    /// </summary>
    public void StepMovie(int by)
    {
        if (m_Movie == null)
            return;
        var list = m_Movie.movies;
        if (list.Count == 0)
            return;
        int at = list.FindIndex(m => m.id == m_Movie.definition.id);
        if (at < 0)
            at = 0;
        var next = list[((at + by) % list.Count + list.Count) % list.Count];
        m_DeleteArmedFor = null;
        m_Movie.SetMovie(next);
        m_NextRefresh = 0f;
    }

    /// <summary>Opens the New movie menu beside this one (or puts it away if it is open).</summary>
    public void ToggleNewMovie()
    {
        if (m_Movie == null)
            return;
        if (m_NewMovie != null && m_NewMovie.gameObject.activeSelf)
        {
            m_NewMovie.Close();
            m_NextRefresh = 0f;
            return;
        }
        if (m_NewMovie == null)
        {
            if (m_NewMoviePrefab == null)
            {
                m_Note = "No New movie menu in this build.";
                return;
            }
            m_NewMovie = Instantiate(m_NewMoviePrefab);
            m_NewMovie.name = $"{name} New Movie Menu";
        }
        m_NewMovie.Open(m_Movie, this);
        m_NextRefresh = 0f;
    }

    // A made movie: a first tap asks, a second within a few seconds deletes it for everyone.
    void DeleteMovie()
    {
        if (m_Movie == null)
            return;
        var movie = m_Movie.definition;
        if (movie.builtIn)
        {
            m_Note = $"{movie.name} is built in: it can't be deleted.";
            m_NextRefresh = 0f;
            return;
        }
        if (m_DeleteArmedFor != movie.id || Time.unscaledTime > m_DeleteArmedUntil)
        {
            m_DeleteArmedFor = movie.id;
            m_DeleteArmedUntil = Time.unscaledTime + 4f;
            m_Note = $"Tap Delete again to delete \"{movie.name}\" for everyone.";
            m_NextRefresh = 0f;
            return;
        }
        m_DeleteArmedFor = null;
        m_Movie.DeleteMovie(movie.id);
        m_NextRefresh = 0f;
    }

    #endregion

    bool ligands => m_Movie != null && m_Movie.definition.pocket;

    // The slider sets the sites' diameter (whole percent) or, in Ligand binding, the capture radius (whole ångströms).
    float SliderSetting => m_Movie == null ? 0f : ligands ? m_Movie.captureRadius : m_Movie.siteDiameter * 100f;

    void OnDiameterMoved(float value)
    {
        if (m_Movie == null)
            return;
        m_DiameterShown = value;
        if (ligands)
            m_Movie.SetCaptureRadius(value);
        else
            m_Movie.SetSiteDiameter(value / 100f);
        ShowDiameter();
    }

    // The slider's range and its tick labels for the movie chosen (both run 2 to 30: percent, or ångströms).
    void ShowScenario()
    {
        if (m_DiameterSlider == null)
            return;
        bool ligand = ligands;
        m_DiameterSlider.minValue = ligand ? TunnelMovie.k_MinCaptureRadius : TunnelMovie.k_MinSiteDiameter * 100f;
        m_DiameterSlider.maxValue = ligand ? TunnelMovie.k_MaxCaptureRadius : TunnelMovie.k_MaxSiteDiameter * 100f;
        var ticks = m_DiameterSlider.transform.parent != null ? m_DiameterSlider.transform.parent.Find("Ticks") : null;
        if (ticks != null)
            foreach (Transform tick in ticks)
                if (tick.TryGetComponent(out TMP_Text label))
                {
                    string number = label.text.Replace("%", "").Replace(" Å", "").Trim();
                    label.text = ligand ? $"{number} Å" : $"{number}%";
                }
        m_DiameterShown = -1f; // shows the new setting
    }

    void OnBindingTimeMoved(float seconds)
    {
        if (m_Movie == null)
            return;
        m_BindingTimeShown = seconds;
        m_Movie.SetBindingTime(seconds);
        ShowBindingTime();
    }

    void ShowBindingTime()
    {
        if (m_BindingTimeText == null || m_BindingTimeSlider == null)
            return;
        float seconds = m_BindingTimeSlider.value;
        string time = seconds >= 60f ? "1 min" : $"{seconds:0} s";
        string text = $"Binding time: {time}  <color=#AAA>each {(m_Movie != null ? m_Movie.definition.BinderWord(1) : "binder")} stays bound</color>";
        if (m_BindingTimeText.text != text)
            m_BindingTimeText.text = text;
    }

    void ShowDiameter()
    {
        if (m_DiameterText == null || m_DiameterSlider == null)
            return;
        string text = ligands
            ? $"Capture radius: {m_DiameterSlider.value:0} Å  <color=#AAA>round each pocket's mouth</color>"
            : $"Binding site diameter: {m_DiameterSlider.value:0}%  <color=#AAA>of each capsid's</color>";
        if (m_DiameterText.text != text)
            m_DiameterText.text = text;
    }

    void Update()
    {
        ShareWhileHeld();
        if (m_Movie != null && (m_Movie.definition.id != m_MovieShown || m_Movie.definition.pocket != m_PocketShown))
        {
            m_MovieShown = m_Movie.definition.id;
            m_PocketShown = m_Movie.definition.pocket;
            ShowScenario();
            m_BindingTimeShown = -1f;
            m_NextRefresh = 0f;
        }
        // The sliders follow a change made by another player, not their own.
        if (m_Movie != null && m_DiameterSlider != null && !Mathf.Approximately(SliderSetting, m_DiameterShown))
        {
            m_DiameterShown = SliderSetting;
            m_DiameterSlider.SetValueWithoutNotify(Mathf.Round(m_DiameterShown));
            ShowDiameter();
        }
        if (m_Movie != null && m_BindingTimeSlider != null && !Mathf.Approximately(m_Movie.bindingTime, m_BindingTimeShown))
        {
            m_BindingTimeShown = m_Movie.bindingTime;
            m_BindingTimeSlider.SetValueWithoutNotify(Mathf.Round(m_BindingTimeShown));
            ShowBindingTime();
        }
        if (m_Movie == null || Time.unscaledTime < m_NextRefresh)
            return;
        m_NextRefresh = Time.unscaledTime + 0.2f;
        var state = m_Movie.state;
        var movie = m_Movie.definition;
        var list = m_Movie.movies;
        int at = list.FindIndex(m => m.id == movie.id);
        bool running = state == TunnelMovie.State.Playing || state == TunnelMovie.State.Paused || state == TunnelMovie.State.Restarting;
        string where = at >= 0 ? $"{at + 1} of {list.Count}" : "deleted";
        string size = movie.magnification > 0 ? $" {movie.sizeLabel}" : "";
        SetRow(m_ScenarioRow, $"<b>{movie.name}{size}</b>  <color=#AAA>· {where}{(running ? ", ◄ ► stops it" : "")}</color>", list.Count > 1);
        SetRow(m_PrevMovieButton, "◄", list.Count > 1);
        SetRow(m_NextMovieButton, "►", list.Count > 1);
        bool menuOpen = m_NewMovie != null && m_NewMovie.gameObject.activeSelf;
        SetRow(m_NewMovieRow, menuOpen ? "<color=#7F7>New movie...</color>" : "New movie...", true);
        bool canMatch = m_Movie.canMatchSize;
        SetRow(m_MatchSizeRow, canMatch ? $"Match {movie.sizeLabel}" : "Match size", canMatch);
        bool armed = m_DeleteArmedFor == movie.id && Time.unscaledTime <= m_DeleteArmedUntil;
        SetRow(m_DeleteMovieRow, armed ? "<color=#F55>Tap again</color>" : "Delete", !movie.builtIn);
        SetRow(m_StartRow, $"{(state == TunnelMovie.State.Ended ? "Start again" : "Start")}  <color=#AAA>{movie.name}</color>", state == TunnelMovie.State.Ready || state == TunnelMovie.State.Ended);
        SetRow(m_PauseRow, "Pause", state == TunnelMovie.State.Playing);
        SetRow(m_PlayRow, "Play", state == TunnelMovie.State.Paused);
        SetRow(m_PlayAgainRow, state == TunnelMovie.State.Restarting
                ? $"Starting again in {m_Movie.countdown} s..."
                : state == TunnelMovie.State.Ready
                ? "Play again  <color=#AAA>(once it has started)</color>"
                : "Play again  <color=#AAA>all let go, then from the start</color>",
            m_Movie.canPlayAgain);
        SetRow(m_SitesRow, (m_Movie.showSites ? "■" : "□") + (ligands ? "  Show capture spheres" : "  Show binding sites"), true);
        SetRow(m_SaveRow, state == TunnelMovie.State.Ready ? "Save starting state..." : "Save starting state...  <color=#AAA>as it was at the start</color>", m_Saving == null);

        string stateText = state switch
        {
            TunnelMovie.State.Ready => "Ready",
            TunnelMovie.State.Playing => "<color=#7F7>Playing</color>",
            TunnelMovie.State.Paused => "<color=#FD6>Paused</color>",
            TunnelMovie.State.Restarting => $"<color=#FD6>Starting again in {m_Movie.countdown} s</color>",
            _ => "<color=#AAA>The end</color>",
        };
        if (m_Title != null)
        {
            string title = $"{m_Movie.title}  <size=70%>{stateText}</size>";
            if (m_Title.text != title)
                m_Title.text = title;
        }
        if (m_Status != null)
        {
            int capsids = m_Movie.CountCapsids(), covered = m_Movie.CountCovered(), antibodies = m_Movie.CountAntibodies(), bound = m_Movie.CountBound();
            string coveredText = covered == 0 ? "" : ligands ? $" ({covered} with {movie.BinderWord(1)} bound)" : $" ({covered} fully covered)";
            string sizeNote = m_Movie.sizeNote;
            string text = $"<color=#AAA>In the container:</color> {capsids} {movie.ReceptorWord(capsids)}{coveredText}, "
                + $"{antibodies} free {movie.BinderWord(antibodies)}, {bound} bound\n"
                + (sizeNote.Length > 0 ? $"<color=#FD6>{sizeNote}{(state == TunnelMovie.State.Ready || state == TunnelMovie.State.Ended ? " Match size resizes them." : "")}</color>\n" : "")
                + (m_Note.Length > 0 ? m_Note : m_Movie.message);
            if (m_Status.text != text)
                m_Status.text = text;
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

    #region Saving

    void StartSaving()
    {
        if (m_Movie == null)
            return;
        // Kept now: what is saved is the state when Save was pressed (or at the movie's start), whatever the name.
        m_Saving = m_Movie.StateToSave(out m_SavingAtStart);
        if (m_Keyboard == null)
        {
            Save(TunnelMovie.NextName(m_Movie.title));
            return;
        }
        m_Keyboard.Open(TunnelMovie.NextName(m_Movie.title));
        ShowTyped(m_Keyboard.text);
    }

    void OnNameTyped(KeyboardTextEventArgs args)
    {
        if (m_Saving != null)
            ShowTyped(args.keyboardText);
    }

    // The name as it's typed (the keyboard has no box of its own that shows it).
    void ShowTyped(string text)
    {
        string name = TunnelMovie.CleanName(text);
        string exists = name.Length > 0 && TunnelMovie.Exists(name) ? "  <color=#FD6>(replaces the one of that name)</color>" : "";
        m_Note = $"Name: <b>{name}</b><color=#7F7>|</color>{exists}\n<color=#AAA>Type a name for this starting state ({Molecules(m_Saving.molecules.Count)}"
            + $"{(m_SavingAtStart ? ", as at the start" : "")}), then press Enter.</color>";
        m_NextRefresh = 0f;
    }

    static string Molecules(int count) => count == 1 ? "1 molecule" : $"{count} molecules";

    void OnNameSubmitted(KeyboardTextEventArgs args)
    {
        if (m_Saving == null)
            return;
        string name = TunnelMovie.CleanName(args.keyboardText);
        if (name.Length == 0)
        {
            m_Note = "Type a name first, then press Enter.";
            m_NextRefresh = 0f;
            return;
        }
        var snapshot = m_Saving;
        m_Saving = null; // so closing the keyboard doesn't count as giving up
        if (m_Keyboard != null && m_Keyboard.isOpen)
            m_Keyboard.Close(true);
        m_Saving = snapshot;
        Save(name);
    }

    void OnKeyboardClosed(KeyboardTextEventArgs args)
    {
        if (m_Saving == null)
            return;
        m_Saving = null;
        m_Note = "Not saved.";
        m_NextRefresh = 0f;
    }

    void Save(string name)
    {
        var snapshot = m_Saving;
        m_Saving = null;
        if (snapshot == null)
            return;
        try
        {
            bool replaced = TunnelMovie.Exists(name);
            m_Movie.Save(name, snapshot);
            m_Note = $"Saved \"{TunnelMovie.CleanName(name)}\" to 3D Movies{(replaced ? " (replacing the one of that name)" : "")}: the container and {Molecules(snapshot.molecules.Count)}.";
        }
        catch (System.Exception e)
        {
            m_Note = $"Couldn't save: {e.Message}";
            Debug.LogException(e);
        }
        m_NextRefresh = 0f;
    }

    #endregion

    void LateUpdate()
    {
        // A note of the panel's own (about saving) gives way to the movie's next message.
        if (m_Note.Length > 0 && m_Movie != null && m_Movie.message != m_LastMessage)
        {
            m_LastMessage = m_Movie.message;
            if (m_Saving == null)
                m_Note = "";
        }
    }

    string m_LastMessage;

#if UNITY_EDITOR
    /// <summary>Set by Tunnel > Movie > Build 3D Movie.</summary>
    public void Configure(Button start, Button pause, Button play, Button sites, Button save, Slider diameter, TMP_Text diameterText,
        TMP_Text title, TMP_Text status, XRKeyboard keyboard, float width, float height,
        Button playAgain, Slider bindingTime, TMP_Text bindingTimeText, XRGrabInteractable grab, float barSpace, Button scenario,
        Button previousMovie, Button nextMovie, Button newMovie, Button deleteMovie, TunnelNewMoviePanel newMoviePrefab, Button matchSize)
    {
        m_MatchSizeRow = matchSize;
        m_ScenarioRow = scenario;
        m_PrevMovieButton = previousMovie;
        m_NextMovieButton = nextMovie;
        m_NewMovieRow = newMovie;
        m_DeleteMovieRow = deleteMovie;
        m_NewMoviePrefab = newMoviePrefab;
        m_PlayAgainRow = playAgain;
        m_BindingTimeSlider = bindingTime;
        m_BindingTimeText = bindingTimeText;
        m_Grab = grab;
        m_BarSpace = barSpace;
        m_DiameterSlider = diameter;
        m_DiameterText = diameterText;
        m_Width = width;
        m_StartRow = start;
        m_PauseRow = pause;
        m_PlayRow = play;
        m_SitesRow = sites;
        m_SaveRow = save;
        m_Title = title;
        m_Status = status;
        m_Keyboard = keyboard;
        m_Height = height;
    }
#endif
}
