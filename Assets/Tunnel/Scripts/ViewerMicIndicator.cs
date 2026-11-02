using TMPro;
using UnityEngine;
using UnityEngine.UI;
using XRMultiplayer;

/// <summary>
/// Above the Viewer's menu bar: whether anything is recording from the headset's microphone right now, so a Quest
/// recording (or screen capture) can be checked for sound while it is made. It asks Android which recordings are
/// active (AudioManager.getActiveRecordingConfigurations) without opening the microphone itself, which would take it
/// from the recorder. It can't tell how loud the sound is, only that it is being recorded and not silenced.
/// Its button gives the microphone to recordings, or back to voice chat (<see cref="VoiceChatManager.GiveMicToRecording"/>).
/// </summary>
public class ViewerMicIndicator : MonoBehaviour
{
    [SerializeField]
    TMP_Text m_Label;

    [SerializeField]
    [Tooltip("Gives the microphone to recordings, or back to voice chat.")]
    Button m_Button;

    [SerializeField]
    [Tooltip("How often to ask (seconds).")]
    float m_Interval = 0.5f;

    float m_NextCheck;
    AndroidJavaObject m_AudioManager;

    // Android's audio sources: the Tunnel's own microphone, voice chat, an app's sound (no microphone), and ones that
    // aren't a recording.
    const int k_VoiceRecognition = 6, k_VoiceCommunication = 7, k_RemoteSubmix = 8;
    const int k_EchoReference = 1997, k_RadioTuner = 1998, k_Hotword = 1999;

    void Awake()
    {
        if (m_Button != null)
            m_Button.onClick.AddListener(() => VoiceChatManager.GiveMicToRecording(!VoiceChatManager.s_MicForRecording));
    }

    void OnEnable()
    {
        // Only the headset can say; in the Editor the label stays empty.
        if (m_Label != null && Application.platform != RuntimePlatform.Android)
            m_Label.text = "";
    }

    void Update()
    {
        if (Time.unscaledTime < m_NextCheck)
            return;
        m_NextCheck = Time.unscaledTime + m_Interval;
        if (m_Button != null)
            m_Button.GetComponentInChildren<TMP_Text>(true).text = VoiceChatManager.s_MicForRecording ? "Mic to chat" : "Mic to recording";
        if (m_Label != null && Application.platform == RuntimePlatform.Android)
            m_Label.text = Describe();
    }

    string Describe()
    {
        try
        {
            if (m_AudioManager == null)
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                m_AudioManager = activity.Call<AndroidJavaObject>("getSystemService", "audio");
            }

            // Microphone recordings by other apps (the Quest's recorder), and whether one takes only the app's sound.
            int heard = 0, silenced = 0;
            bool soundOnly = false;
            using (var configs = m_AudioManager.Call<AndroidJavaObject>("getActiveRecordingConfigurations"))
            {
                int count = configs.Call<int>("size");
                for (int i = 0; i < count; i++)
                {
                    using var config = configs.Call<AndroidJavaObject>("get", i);
                    int source = config.Call<int>("getClientAudioSource");
                    if (source == k_RemoteSubmix)
                        soundOnly = true;
                    else if (source is k_VoiceRecognition or k_VoiceCommunication or k_EchoReference or k_RadioTuner or k_Hotword)
                        continue;
                    else if (config.Call<bool>("isClientSilenced"))
                        silenced++;
                    else
                        heard++;
                }
            }

            // The Tunnel opens the microphone for voice chat in a shared session (unless given to recordings), and for a
            // mouth-moving avatar.
            bool ours = Microphone.IsRecording(null) || (XRINetworkGameManager.Connected.Value && !VoiceChatManager.s_MicReleased);
            if (heard > 0)
                return "<color=#4F4>Mic: being recorded</color>";
            if (silenced > 0)
                return ours ? "<color=#F55>Mic silenced: press Mic to recording</color>" : "<color=#F55>Mic: recorded but silenced</color>";
            if (soundOnly)
                return ours ? "<color=#F55>No mic in recording: press Mic to recording</color>" : "<color=#F55>No mic in recording: turn on Include Mic Audio</color>";
            if (ours)
                return "<color=#FB4>Mic: in voice chat</color>";
            return VoiceChatManager.s_MicForRecording ? "<color=#4F4>Mic: free for recording</color>" : "<color=#888>Mic: free</color>";
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[Viewer] can't read the microphone's state: {e.Message}");
            enabled = false;
            return "";
        }
    }

    void OnDestroy()
    {
        m_AudioManager?.Dispose();
    }
}
