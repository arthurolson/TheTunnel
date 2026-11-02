using TMPro;
using UnityEngine;

/// <summary>
/// Above the Viewer's menu bar: whether anything is recording from the headset's microphone right now, so a Quest
/// recording (or screen capture) can be checked for sound while it is made. It asks Android which recordings are
/// active (AudioManager.getActiveRecordingConfigurations) without opening the microphone itself, which would take it
/// from the recorder. It can't tell how loud the sound is, only that it is being recorded and not silenced.
/// </summary>
public class ViewerMicIndicator : MonoBehaviour
{
    [SerializeField]
    TMP_Text m_Label;

    [SerializeField]
    [Tooltip("How often to ask (seconds).")]
    float m_Interval = 0.5f;

    float m_NextCheck;
    AndroidJavaObject m_AudioManager;

    void OnEnable()
    {
        // Only the headset can say; in the Editor the label stays empty.
        if (m_Label != null && Application.platform != RuntimePlatform.Android)
            m_Label.text = "";
    }

    void Update()
    {
        if (m_Label == null || Application.platform != RuntimePlatform.Android || Time.unscaledTime < m_NextCheck)
            return;
        m_NextCheck = Time.unscaledTime + m_Interval;
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

            int heard = 0, silenced = 0;
            using (var configs = m_AudioManager.Call<AndroidJavaObject>("getActiveRecordingConfigurations"))
            {
                int count = configs.Call<int>("size");
                for (int i = 0; i < count; i++)
                {
                    using var config = configs.Call<AndroidJavaObject>("get", i);
                    if (config.Call<bool>("isClientSilenced"))
                        silenced++;
                    else
                        heard++;
                }
            }

            // The Tunnel only opens the microphone for voice chat in a shared session (or a mouth-moving avatar).
            bool ours = Microphone.IsRecording(null) || VoiceChatInUse();
            // Paused, voice chat should have let go: anything still silenced means the recorder still gets nothing.
            if (XRMultiplayer.VoiceChatManager.s_PausedForRecording)
                return silenced > 0 ? "<color=#F55>Mic: recorded but silenced</color>" : "<color=#4F4>Mic: being recorded (chat paused)</color>";
            if (heard > (ours ? 1 : 0))
                return "<color=#4F4>Mic: being recorded</color>";
            if (ours && heard > 0)
                return "<color=#FB4>Mic: in use by voice chat</color>";
            if (silenced > 0)
                return "<color=#F55>Mic: recorded but silenced</color>";
            return "<color=#888>Mic: not recorded</color>";
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[Viewer] can't read the microphone's state: {e.Message}");
            enabled = false;
            return "";
        }
    }

    static bool VoiceChatInUse() => XRMultiplayer.XRINetworkGameManager.Connected.Value;

    void OnDestroy()
    {
        m_AudioManager?.Dispose();
    }
}
