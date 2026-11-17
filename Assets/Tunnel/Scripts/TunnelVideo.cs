using UnityEngine;
using UnityEngine.Video;

/// <summary>
/// A video asset playing on its quad or 360 sphere (see <see cref="TunnelAssets.Load"/>), its sound coming from where
/// it is. It plays from the copy on the headset when the file is small, otherwise it streams from its URL. It loops.
/// A splat-box keeps everyone's copy at the same time and paused or playing together (see <see cref="SplatBox"/>).
/// </summary>
[RequireComponent(typeof(VideoPlayer))]
public class TunnelVideo : MonoBehaviour
{
    VideoPlayer m_Player;
    float m_LastSeek = -10f;

    /// <summary>Ready to play: its first frame and length are known.</summary>
    public bool isPrepared => m_Player != null && m_Player.isPrepared;

    /// <summary>Why it can't play, or "".</summary>
    public string error { get; private set; } = "";

    /// <summary>Its length (seconds), or 0 until it is known.</summary>
    public double length => m_Player != null && m_Player.length > 0 ? m_Player.length : 0;

    /// <summary>Where it is (seconds).</summary>
    public double time => m_Player != null ? m_Player.time : 0;

    public bool isPlaying => m_Player != null && m_Player.isPlaying;

    internal void Begin(string url, RenderTexture target)
    {
        m_Player = GetComponent<VideoPlayer>();
        m_Player.playOnAwake = false;
        m_Player.isLooping = true;
        m_Player.skipOnDrop = true;
        m_Player.waitForFirstFrame = true;
        m_Player.source = VideoSource.Url;
        m_Player.url = url;
        m_Player.renderMode = VideoRenderMode.RenderTexture;
        m_Player.targetTexture = target;

        var audio = gameObject.AddComponent<AudioSource>();
        audio.playOnAwake = false;
        audio.spatialBlend = 1f; // from where the video is
        audio.minDistance = 1.5f;
        audio.maxDistance = 25f;
        audio.rolloffMode = AudioRolloffMode.Linear;
        m_Player.audioOutputMode = VideoAudioOutputMode.AudioSource;
        m_Player.controlledAudioTrackCount = 1;
        m_Player.EnableAudioTrack(0, true);
        m_Player.SetTargetAudioSource(0, audio);

        m_Player.errorReceived += (_, message) =>
        {
            error = message;
            Debug.LogWarning($"[Assets] video {name}: {message}");
        };
        m_Player.Prepare();
    }

    /// <summary>Plays or pauses it, at this time (seconds); a small difference is left alone, to avoid stutter.</summary>
    public void Follow(bool playing, double at)
    {
        if (!isPrepared)
            return;
        if (length > 0)
            at = ((at % length) + length) % length;
        double off = System.Math.Abs(m_Player.time - at);
        if (length > 0)
            off = System.Math.Min(off, length - off); // across the loop
        float tolerance = playing ? 0.35f : 0.05f;
        // A seek in a streamed video takes a moment: not more than one every two seconds.
        if (off > tolerance && Time.unscaledTime - m_LastSeek > 2f)
        {
            m_Player.time = at;
            m_LastSeek = Time.unscaledTime;
        }
        if (playing && !m_Player.isPlaying)
            m_Player.Play();
        else if (!playing && m_Player.isPlaying)
            m_Player.Pause();
    }
}
