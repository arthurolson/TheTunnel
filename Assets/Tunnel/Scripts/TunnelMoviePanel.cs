using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Samples.SpatialKeyboard;

/// <summary>
/// A container's Antibody action menu (see <see cref="TunnelMovie"/>), shown beside the container: Start, Pause,
/// Play, Play again (every antibody lets go, and after a 5 second countdown it starts again from the starting state),
/// Show binding sites, the binding sites' diameter (2% to 30% of each capsid's), the binding time (10 s to 1 min,
/// how long each antibody stays bound) and Save starting state (which asks for a name on its keyboard, then saves).
/// Its status shows what is happening, how many viruses and antibodies are in the container, and the latest message.
/// Anyone can take it by the bar along its top and move it, for everyone; let go of within 25 cm of a flat tunnel
/// wall, it lies flat on the wall, like a sign. Hiding the controls (TS Menu > Containers) and showing them again
/// puts it back beside the container.
/// Made by Tunnel > Movie > Build 3D Movie.
/// </summary>
public class TunnelMoviePanel : MonoBehaviour
{
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

    void Awake()
    {
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
            XRMultiplayer.PlayerHudNotification.Instance?.ShowText($"{TunnelMovie.k_Title} menu on the wall: take it by its bar to move it", 2f);
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

    void OnDiameterMoved(float percent)
    {
        if (m_Movie == null)
            return;
        m_DiameterShown = percent / 100f;
        m_Movie.SetSiteDiameter(percent / 100f);
        ShowDiameter();
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
        string text = $"Binding time: {time}  <color=#AAA>each antibody stays bound</color>";
        if (m_BindingTimeText.text != text)
            m_BindingTimeText.text = text;
    }

    void ShowDiameter()
    {
        if (m_DiameterText == null || m_DiameterSlider == null)
            return;
        string text = $"Binding site diameter: {m_DiameterSlider.value:0}%  <color=#AAA>of each capsid's</color>";
        if (m_DiameterText.text != text)
            m_DiameterText.text = text;
    }

    void Update()
    {
        ShareWhileHeld();
        // The sliders follow a change made by another player, not their own.
        if (m_Movie != null && m_DiameterSlider != null && !Mathf.Approximately(m_Movie.siteDiameter, m_DiameterShown))
        {
            m_DiameterShown = m_Movie.siteDiameter;
            m_DiameterSlider.SetValueWithoutNotify(Mathf.Round(m_DiameterShown * 100f));
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
        SetRow(m_StartRow, state == TunnelMovie.State.Ended ? "Start again" : "Start", state == TunnelMovie.State.Ready || state == TunnelMovie.State.Ended);
        SetRow(m_PauseRow, "Pause", state == TunnelMovie.State.Playing);
        SetRow(m_PlayRow, "Play", state == TunnelMovie.State.Paused);
        SetRow(m_PlayAgainRow, state == TunnelMovie.State.Restarting
                ? $"Starting again in {m_Movie.countdown} s..."
                : state == TunnelMovie.State.Ready
                ? "Play again  <color=#AAA>(once it has started)</color>"
                : "Play again  <color=#AAA>all let go, then from the start</color>",
            m_Movie.canPlayAgain);
        SetRow(m_SitesRow, (m_Movie.showSites ? "■" : "□") + "  Show binding sites", true);
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
            string title = $"{TunnelMovie.k_Title}  <size=70%>{stateText}</size>";
            if (m_Title.text != title)
                m_Title.text = title;
        }
        if (m_Status != null)
        {
            int capsids = m_Movie.CountCapsids(), covered = m_Movie.CountCovered(), antibodies = m_Movie.CountAntibodies(), bound = m_Movie.CountBound();
            string text = $"<color=#AAA>In the container:</color> {capsids} {(capsids == 1 ? "virus" : "viruses")}{(covered > 0 ? $" ({covered} fully covered)" : "")}, "
                + $"{antibodies} free antibod{(antibodies == 1 ? "y" : "ies")}, {bound} bound\n"
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
            Save(TunnelMovie.NextName());
            return;
        }
        m_Keyboard.Open(TunnelMovie.NextName());
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
        Button playAgain, Slider bindingTime, TMP_Text bindingTimeText, XRGrabInteractable grab, float barSpace)
    {
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
