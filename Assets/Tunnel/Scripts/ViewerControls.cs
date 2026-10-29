using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The Viewer's video controls, under the screen while a video or spatial video is shown: Start (back to the
/// beginning), back 10 s, Play / Pause, Stop (pause at the beginning), forward 10 s, Fast (double speed), Loop, and a
/// slider to jump anywhere. Every button acts for everyone watching (see <see cref="ViewerPanel"/>).
/// </summary>
public class ViewerControls : MonoBehaviour
{
    [SerializeField]
    ViewerPanel m_Viewer;

    [SerializeField]
    Button m_StartButton;

    [SerializeField]
    Button m_BackButton;

    [SerializeField]
    Button m_PlayButton;

    [SerializeField]
    TMP_Text m_PlayLabel;

    [SerializeField]
    Button m_StopButton;

    [SerializeField]
    Button m_ForwardButton;

    [SerializeField]
    Button m_FastButton;

    [SerializeField]
    TMP_Text m_FastLabel;

    [SerializeField]
    Button m_LoopButton;

    [SerializeField]
    TMP_Text m_LoopLabel;

    [SerializeField]
    Slider m_Seek;

    [SerializeField]
    TMP_Text m_TimeText;

    [SerializeField]
    [Tooltip("How far the back and forward buttons jump (seconds).")]
    float m_SkipSeconds = 10f;

    [SerializeField]
    float m_FastRate = 2f;

    [SerializeField]
    [Tooltip("While the slider is dragged, the new time is shared this often (seconds).")]
    float m_SeekSendInterval = 0.15f;

    float m_LastSeekSent = -10f;
    float m_LastSliderMove = -10f;
    float m_PendingSeek = -1f;

    void Awake()
    {
        Listen(m_StartButton, () => m_Viewer.Seek(0));
        Listen(m_BackButton, () => m_Viewer.Skip(-m_SkipSeconds));
        Listen(m_PlayButton, () => m_Viewer.SetPlaying(!m_Viewer.isPlaying));
        Listen(m_StopButton, () =>
        {
            m_Viewer.SetPlaying(false);
            m_Viewer.Seek(0);
        });
        Listen(m_ForwardButton, () => m_Viewer.Skip(m_SkipSeconds));
        Listen(m_FastButton, () => m_Viewer.SetRate(m_Viewer.rate > 1.01f ? 1f : m_FastRate));
        Listen(m_LoopButton, () => m_Viewer.SetLoop(!m_Viewer.isLooping));

        if (m_Seek != null)
        {
            m_Seek.minValue = 0f;
            m_Seek.maxValue = 1f;
            m_Seek.wholeNumbers = false;
            m_Seek.onValueChanged.AddListener(OnSliderMoved);
        }
    }

    void Listen(Button button, Action action)
    {
        if (button != null)
            button.onClick.AddListener(() =>
            {
                if (m_Viewer != null)
                    action();
            });
    }

    void OnSliderMoved(float value)
    {
        m_LastSliderMove = Time.unscaledTime;
        m_PendingSeek = value;
    }

    void Update()
    {
        if (m_Viewer == null)
            return;

        double duration = m_Viewer.duration;
        if (m_PendingSeek >= 0f && duration > 0 && Time.unscaledTime - m_LastSeekSent >= m_SeekSendInterval)
        {
            m_Viewer.Seek(m_PendingSeek * duration);
            m_LastSeekSent = Time.unscaledTime;
            m_PendingSeek = -1f;
        }

        double time = m_Viewer.currentTime;
        // Leave the slider alone just after someone here moved it, until the shared time catches up.
        if (m_Seek != null && duration > 0 && Time.unscaledTime - m_LastSliderMove > 0.5f)
            m_Seek.SetValueWithoutNotify((float)(time / duration));

        if (m_TimeText != null)
            m_TimeText.text = duration > 0 ? $"{Format(time)} / {Format(duration)}" : Format(time);
        if (m_PlayLabel != null)
            m_PlayLabel.text = m_Viewer.isPlaying ? "Pause" : "Play";
        if (m_FastLabel != null)
            m_FastLabel.text = m_Viewer.rate > 1.01f ? "Normal" : $"Fast ×{m_FastRate:0.#}";
        if (m_LoopLabel != null)
            m_LoopLabel.text = m_Viewer.isLooping ? "Loop: On" : "Loop: Off";
    }

    static string Format(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");
    }
}
