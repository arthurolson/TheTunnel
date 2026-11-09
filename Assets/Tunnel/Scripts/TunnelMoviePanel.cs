using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Samples.SpatialKeyboard;

/// <summary>
/// A container's 3D movie controls (see <see cref="TunnelMovie"/>), shown over the container: Start movie, Pause,
/// Play, Show binding sites and Save starting state (which asks for a name on its keyboard, then saves). Its status
/// shows what the movie is doing, how many capsids and antibodies are in it, and its latest message.
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
    TMP_Text m_Title;

    [SerializeField]
    TMP_Text m_Status;

    [SerializeField]
    XRKeyboard m_Keyboard;

    [SerializeField]
    [Tooltip("The panel's height (meters).")]
    float m_Height = 0.2f;

    TunnelMovie m_Movie;
    MovieSnapshot m_Saving;
    bool m_SavingAtStart;
    string m_Note = "";
    float m_NextRefresh;

    public float height => m_Height;

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
    }

    void Update()
    {
        if (m_Movie == null || Time.unscaledTime < m_NextRefresh)
            return;
        m_NextRefresh = Time.unscaledTime + 0.2f;
        var state = m_Movie.state;
        SetRow(m_StartRow, state == TunnelMovie.State.Ended ? "Start again" : "Start movie", state == TunnelMovie.State.Ready || state == TunnelMovie.State.Ended);
        SetRow(m_PauseRow, "Pause", state == TunnelMovie.State.Playing);
        SetRow(m_PlayRow, "Play", state == TunnelMovie.State.Paused);
        SetRow(m_SitesRow, (m_Movie.showSites ? "■" : "□") + "  Show binding sites", true);
        SetRow(m_SaveRow, state == TunnelMovie.State.Ready ? "Save starting state..." : "Save starting state...  <color=#AAA>as it was at the start</color>", m_Saving == null);

        string stateText = state switch
        {
            TunnelMovie.State.Ready => "Ready",
            TunnelMovie.State.Playing => "<color=#7F7>Playing</color>",
            TunnelMovie.State.Paused => "<color=#FD6>Paused</color>",
            _ => "<color=#AAA>The end</color>",
        };
        if (m_Title != null)
        {
            string title = $"3D Movie  <size=70%>{stateText}</size>";
            if (m_Title.text != title)
                m_Title.text = title;
        }
        if (m_Status != null)
        {
            int capsids = m_Movie.CountCapsids(), antibodies = m_Movie.CountAntibodies(), bound = m_Movie.CountBound();
            string text = $"<color=#AAA>In the container:</color> {capsids} capsid{(capsids == 1 ? "" : "s")}, {antibodies} free antibod{(antibodies == 1 ? "y" : "ies")}, {bound} bound\n"
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
    public void Configure(Button start, Button pause, Button play, Button sites, Button save, TMP_Text title, TMP_Text status, XRKeyboard keyboard, float height)
    {
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
