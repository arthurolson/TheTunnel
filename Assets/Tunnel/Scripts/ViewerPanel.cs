using System;
using System.Collections;
using System.IO;
using TMPro;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Video;
using UnityEngine.XR.Templates.MRTTabletopAssets;

/// <summary>
/// The Viewer: a panel that shows an image, a video, a spatial (stereo) image or a spatial video, reshaped to fit
/// what it shows. It moves, snaps to walls, resizes and closes like the browser (<see cref="BrowserPanel"/>).
/// Everyone sees the same thing: whoever picks something (from the menu's lists or by typing its name or address)
/// picks it for all, and video play, pause, seeking, speed and looping are shared too, with each headset playing its
/// own copy in step with a shared clock. Media comes from <see cref="ViewerLibrary"/> or straight from a URL.
/// Works locally when not connected.
/// </summary>
public class ViewerPanel : NetworkBehaviour
{
    public const sbyte NoList = -1;

    struct MediaState : INetworkSerializable
    {
        public int id;
        public ViewerMediaKind kind;
        public bool isUrl;
        public FixedString4096Bytes source; // an entry's name, or a URL
        public FixedString128Bytes title;
        public ulong requester;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref id);
            serializer.SerializeValue(ref kind);
            serializer.SerializeValue(ref isUrl);
            serializer.SerializeValue(ref source);
            serializer.SerializeValue(ref title);
            serializer.SerializeValue(ref requester);
        }
    }

    /// <summary>Video time = anchorMedia + (now - anchorClock) * rate while playing; anchorMedia while paused.</summary>
    struct PlayState : INetworkSerializable
    {
        public double anchorClock;
        public double anchorMedia;
        public float rate;
        public bool playing;
        public bool loop;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref anchorClock);
            serializer.SerializeValue(ref anchorMedia);
            serializer.SerializeValue(ref rate);
            serializer.SerializeValue(ref playing);
            serializer.SerializeValue(ref loop);
        }
    }

    [SerializeField]
    BrowserPanel m_Panel;

    [SerializeField]
    [Tooltip("The quad showing the media (uses the Tunnel/ViewerScreen shader).")]
    Renderer m_Screen;

    [SerializeField]
    VideoPlayer m_Video;

    [SerializeField]
    AudioSource m_Audio;

    [SerializeField]
    [Tooltip("Text on the empty screen: what to do, what's loading, or what went wrong.")]
    TMP_Text m_Hint;

    [SerializeField]
    [Tooltip("The video controls under the screen; shown only for videos.")]
    GameObject m_Controls;

    [SerializeField]
    [Tooltip("A copy whose video is further than this from the shared time jumps to it (seconds).")]
    float m_VideoTolerance = 0.35f;

    [SerializeField]
    [Tooltip("Longest side a picture is shown at; bigger ones are scaled down (pixels).")]
    int m_MaxImageSize = 4096;

    const string k_EmptyHint = "Choose an image or video from the menu above,\nor type its name or address on the keyboard.";
    static readonly int k_MainTex = Shader.PropertyToID("_MainTex");
    static readonly int k_Layout = Shader.PropertyToID("_Layout");
    static readonly int k_SwapEyes = Shader.PropertyToID("_SwapEyes");

    readonly NetworkVariable<MediaState> m_Media = new(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<PlayState> m_Play = new(new PlayState { rate = 1f }, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<double> m_Length = new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<sbyte> m_OpenList = new(NoList, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    MediaState m_LocalMedia;
    PlayState m_LocalPlay = new() { rate = 1f };
    double m_LocalLength;
    sbyte m_LocalOpenList = NoList;

    Material m_Material;
    Texture2D m_Image;
    RenderTexture m_VideoTexture;
    int m_LoadedId;
    bool m_VideoReady;
    float m_NextVideoCheck;
    float m_SeekGraceUntil;
    string m_Title = string.Empty;
    string m_Status = string.Empty;

    MediaState media => IsSpawned ? m_Media.Value : m_LocalMedia;
    PlayState play => IsSpawned ? m_Play.Value : m_LocalPlay;
    double length => IsSpawned ? m_Length.Value : m_LocalLength;
    double clock => IsSpawned ? NetworkManager.ServerTime.Time : Time.unscaledTimeAsDouble;
    bool isMine => !IsSpawned || media.requester == NetworkManager.LocalClientId;

    /// <summary>Something changed that the menu or controls show (content, status, list, playback).</summary>
    public event Action changed;

    /// <summary>A message for the player who asked for something (e.g. what was found on a pasted page).</summary>
    public event Action<string> notice;

    Coroutine m_LoadRoutine;
    Coroutine m_PageRoutine;

    public bool hasContent => media.id != 0;
    public ViewerMediaKind kind => media.kind;
    public bool isVideo => hasContent && ViewerLibrary.IsVideoKind(media.kind);
    public string title => m_Title;
    public string status => m_Status;
    public bool isPlaying => play.playing;
    public bool isLooping => play.loop;
    public float rate => play.rate;
    public double duration => length;
    public sbyte openList => IsSpawned ? m_OpenList.Value : m_LocalOpenList;

    /// <summary>Where the video should be now, by the shared clock.</summary>
    public double currentTime => TimeAt(play, clock);

    double TimeAt(PlayState state, double now)
    {
        double t = state.anchorMedia + (state.playing ? (now - state.anchorClock) * state.rate : 0);
        double len = length;
        if (len > 0)
            t = state.loop ? t % len : Math.Min(t, len);
        return Math.Max(0, t);
    }

    void Awake()
    {
        if (m_Screen != null)
        {
            m_Material = m_Screen.material;
            m_Screen.enabled = false;
        }

        if (m_Video != null)
        {
            m_Video.playOnAwake = false;
            m_Video.renderMode = VideoRenderMode.RenderTexture;
            m_Video.source = VideoSource.Url;
            m_Video.skipOnDrop = true;
            m_Video.waitForFirstFrame = true;
            m_Video.prepareCompleted += OnVideoPrepared;
            m_Video.errorReceived += OnVideoError;
            m_Video.loopPointReached += OnVideoEnded;
            if (m_Audio != null)
            {
                m_Video.audioOutputMode = VideoAudioOutputMode.AudioSource;
                m_Video.SetTargetAudioSource(0, m_Audio);
            }
        }

        if (m_Controls != null)
            m_Controls.SetActive(false);
        SetStatus(k_EmptyHint);
    }

    void Start() => StartCoroutine(ViewerLibrary.Refresh());

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        m_Media.OnValueChanged += OnMediaChanged;
        m_Play.OnValueChanged += OnPlayChanged;
        m_OpenList.OnValueChanged += OnOpenListChanged;
        if (m_Media.Value.id != 0)
            StartLoad(m_Media.Value);
        changed?.Invoke();
    }

    public override void OnNetworkDespawn()
    {
        base.OnNetworkDespawn();
        m_Media.OnValueChanged -= OnMediaChanged;
        m_Play.OnValueChanged -= OnPlayChanged;
        m_OpenList.OnValueChanged -= OnOpenListChanged;
    }

    public override void OnDestroy()
    {
        base.OnDestroy();
        if (m_Video != null)
        {
            m_Video.prepareCompleted -= OnVideoPrepared;
            m_Video.errorReceived -= OnVideoError;
            m_Video.loopPointReached -= OnVideoEnded;
        }

        ReleaseMedia();
        if (m_Material != null)
            Destroy(m_Material);
    }

    void OnMediaChanged(MediaState previous, MediaState current) => StartLoad(current);

    void OnPlayChanged(PlayState previous, PlayState current)
    {
        m_NextVideoCheck = 0f;
        changed?.Invoke();
    }

    void OnOpenListChanged(sbyte previous, sbyte current) => changed?.Invoke();

    #region Choosing what to show

    /// <summary>Shows a library entry for everyone (a link entry is sent as its address).</summary>
    public void Open(ViewerLibrary.Entry entry)
    {
        if (entry == null)
            return;
        if (entry.isLink)
            Request(entry.kind, entry.location, true, entry.name);
        else
            Request(entry.kind, entry.name, false, entry.name);
    }

    /// <summary>
    /// Shows what typed text names: an address, or an entry's name (whole or the only one it's part of). Returns what
    /// was opened, or why nothing was.
    /// </summary>
    public string OpenTyped(string text, ViewerMediaKind? preferred)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
            return "Type a name from a list, or an address starting with https://";

        if (ViewerLibrary.IsUrl(text) || text.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            string url = text.Contains("://") ? text : "https://" + text;
            if (ViewerPageMedia.HasMediaExtension(url, out _))
            {
                RequestUrl(url, ViewerLibrary.KindForUrl(url, preferred), null);
                return $"Opening {url}";
            }

            // A web page (or an address that doesn't say what it is): find out first, then show it for everyone.
            if (m_PageRoutine != null)
                StopCoroutine(m_PageRoutine);
            m_PageRoutine = StartCoroutine(OpenAddress(url, preferred));
            return $"Opening {url}: looking for its picture or video...";
        }

        var entry = ViewerLibrary.Match(text, preferred);
        if (entry == null)
            return $"Nothing called \"{text}\" (or more than one). Pick it from a list, or type more of its name.";
        Open(entry);
        return $"Opening {entry.name}";
    }

    IEnumerator OpenAddress(string url, ViewerMediaKind? preferred)
    {
        ViewerPageMedia.Result found = null;
        yield return ViewerPageMedia.Resolve(url, result => found = result);
        m_PageRoutine = null;
        if (found == null || found.error != null)
        {
            notice?.Invoke(found?.error ?? $"Couldn't open {url}");
            yield break;
        }

        bool spatial = (preferred.HasValue && ViewerLibrary.IsSpatialKind(preferred.Value))
            || ViewerLibrary.StereoLayoutFor(found.url, 0, 0) != ViewerLibrary.StereoLayout.Mono;
        var kind = found.isVideo
            ? spatial ? ViewerMediaKind.SpatialVideo : ViewerMediaKind.Video
            : spatial ? ViewerMediaKind.SpatialImage : ViewerMediaKind.Image;
        Debug.Log($"[Viewer] {url} -> {kind} {found.url}");
        RequestUrl(found.url, kind, found.title);
        notice?.Invoke(found.videoOnlyInPage
            ? "Showing the page's picture: its video only plays in its own player (use the browser for that)."
            : $"Showing {(found.isVideo ? "the video" : "the picture")} from that page.");
    }

    void RequestUrl(string url, ViewerMediaKind urlKind, string pageTitle)
    {
        string name = pageTitle;
        if (string.IsNullOrWhiteSpace(name))
            name = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? Uri.UnescapeDataString(Path.GetFileName(uri.LocalPath)) : url;
        Request(urlKind, url, true, string.IsNullOrEmpty(name) ? url : name);
    }

    void Request(ViewerMediaKind newKind, string source, bool isUrl, string newTitle)
    {
        var state = new MediaState
        {
            kind = newKind,
            isUrl = isUrl,
            source = Truncate4096(source),
            title = Truncate128(newTitle),
        };

        if (IsSpawned)
        {
            OpenRpc(state);
            return;
        }

        state.id = m_LocalMedia.id + 1;
        m_LocalMedia = state;
        m_LocalPlay = new PlayState { anchorClock = clock, rate = 1f, playing = true, loop = m_LocalPlay.loop };
        m_LocalLength = 0;
        StartLoad(state);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void OpenRpc(MediaState state, RpcParams rpcParams = default)
    {
        state.id = m_Media.Value.id + 1;
        state.requester = rpcParams.Receive.SenderClientId;
        m_Length.Value = 0;
        // Videos start playing; looping stays as it was.
        m_Play.Value = new PlayState { anchorClock = NetworkManager.ServerTime.Time, rate = 1f, playing = true, loop = m_Play.Value.loop };
        m_Media.Value = state;
    }

    static FixedString4096Bytes Truncate4096(string s)
    {
        var fixedString = new FixedString4096Bytes();
        fixedString.CopyFromTruncated(s ?? string.Empty);
        return fixedString;
    }

    static FixedString128Bytes Truncate128(string s)
    {
        var fixedString = new FixedString128Bytes();
        fixedString.CopyFromTruncated(s ?? string.Empty);
        return fixedString;
    }

    #endregion

    #region Shared menu state

    /// <summary>Opens one kind's list for everyone, or closes the lists (<see cref="NoList"/>).</summary>
    public void SetOpenList(sbyte list)
    {
        if (IsSpawned)
        {
            SetOpenListRpc(list);
        }
        else
        {
            m_LocalOpenList = list;
            changed?.Invoke();
        }
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void SetOpenListRpc(sbyte list) => m_OpenList.Value = list;

    #endregion

    #region Playback

    enum PlayAction : byte
    {
        Play,
        Pause,
        Seek,
        Rate,
        Loop,
    }

    public void SetPlaying(bool playing) => ChangePlay(playing ? PlayAction.Play : PlayAction.Pause, 0);

    public void Seek(double seconds) => ChangePlay(PlayAction.Seek, seconds);

    public void Skip(double seconds) => Seek(currentTime + seconds);

    public void SetRate(float newRate) => ChangePlay(PlayAction.Rate, newRate);

    public void SetLoop(bool loop) => ChangePlay(PlayAction.Loop, loop ? 1 : 0);

    void ChangePlay(PlayAction action, double value)
    {
        if (IsSpawned)
        {
            ChangePlayRpc(action, value);
            return;
        }

        m_LocalPlay = Changed(m_LocalPlay, clock, action, value);
        m_NextVideoCheck = 0f;
        changed?.Invoke();
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void ChangePlayRpc(PlayAction action, double value) => m_Play.Value = Changed(m_Play.Value, NetworkManager.ServerTime.Time, action, value);

    /// <summary>The playback state after an action taken now; the time so far is kept by moving the anchor to now.</summary>
    PlayState Changed(PlayState state, double now, PlayAction action, double value)
    {
        state.anchorMedia = TimeAt(state, now);
        state.anchorClock = now;
        double len = length;
        switch (action)
        {
            case PlayAction.Play:
                // Play at the end of a video starts it again.
                if (!state.loop && len > 0 && state.anchorMedia >= len - 0.05)
                    state.anchorMedia = 0;
                state.playing = true;
                break;
            case PlayAction.Pause:
                state.playing = false;
                break;
            case PlayAction.Seek:
                state.anchorMedia = Math.Max(0, len > 0 ? Math.Min(value, len - 0.05) : value);
                break;
            case PlayAction.Rate:
                state.rate = Mathf.Clamp((float)value, 0.25f, 4f);
                break;
            case PlayAction.Loop:
                state.loop = value > 0.5;
                break;
        }

        return state;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void ReportLengthRpc(int mediaId, double seconds)
    {
        if (mediaId == m_Media.Value.id && seconds > 0)
            m_Length.Value = seconds;
    }

    void Update()
    {
        // The server stops a finished (not looping) video for everyone.
        if (IsSpawned && IsServer && m_Play.Value.playing && !m_Play.Value.loop && m_Length.Value > 0
            && TimeAt(m_Play.Value, clock) >= m_Length.Value - 0.01)
        {
            var state = m_Play.Value;
            state.playing = false;
            state.anchorMedia = m_Length.Value;
            state.anchorClock = clock;
            m_Play.Value = state;
        }

        if (!IsSpawned && m_LocalPlay.playing && !m_LocalPlay.loop && m_LocalLength > 0 && TimeAt(m_LocalPlay, clock) >= m_LocalLength - 0.01)
        {
            m_LocalPlay.playing = false;
            m_LocalPlay.anchorMedia = m_LocalLength;
            changed?.Invoke();
        }

        if (m_VideoReady && Time.unscaledTime >= m_NextVideoCheck)
        {
            m_NextVideoCheck = Time.unscaledTime + 0.25f;
            FollowSharedClock();
        }
    }

    /// <summary>Keeps this headset's copy of the video playing, paused, sped up and at the time everyone shares.</summary>
    void FollowSharedClock()
    {
        var state = play;
        m_Video.isLooping = state.loop;
        if (m_Video.canSetPlaybackSpeed && Math.Abs(m_Video.playbackSpeed - state.rate) > 0.01f)
            m_Video.playbackSpeed = state.rate;

        double target = currentTime;
        double len = length > 0 ? length : m_Video.length;
        bool atEnd = !state.loop && len > 0 && target >= len - 0.05;
        bool shouldPlay = state.playing && !atEnd;

        if (Time.unscaledTime >= m_SeekGraceUntil)
        {
            double difference = Math.Abs(m_Video.time - target);
            if (state.loop && len > 0)
                difference = Math.Min(difference, len - difference);
            if (difference > m_VideoTolerance * Mathf.Max(1f, state.rate))
            {
                m_Video.time = target;
                m_SeekGraceUntil = Time.unscaledTime + 1f;
            }
        }

        if (shouldPlay && !m_Video.isPlaying)
            m_Video.Play();
        else if (!shouldPlay && m_Video.isPlaying)
            m_Video.Pause();
    }

    void OnVideoEnded(VideoPlayer source)
    {
        if (!play.loop)
            changed?.Invoke();
    }

    #endregion

    #region Loading

    void StartLoad(MediaState state)
    {
        if (state.id == 0 || state.id == m_LoadedId)
            return;
        m_LoadedId = state.id;
        if (m_LoadRoutine != null)
            StopCoroutine(m_LoadRoutine);
        m_LoadRoutine = StartCoroutine(Load(state));
    }

    IEnumerator Load(MediaState state)
    {
        ReleaseMedia();
        m_Title = state.title.ToString();
        string source = state.source.ToString();
        bool video = ViewerLibrary.IsVideoKind(state.kind);
        if (m_Controls != null && m_Controls.activeSelf != video)
        {
            m_Controls.SetActive(video);
            if (m_Panel != null)
                m_Panel.RefreshLayout();
        }

        SetStatus($"Loading {m_Title}...");

        string address = source;
        if (!state.isUrl)
        {
            yield return ViewerLibrary.Refresh();
            var entry = ViewerLibrary.Find(state.kind, source);
            if (entry == null)
            {
                SetStatus($"\"{source}\" isn't on this headset.\nAdd it to the {ViewerLibrary.KindLabels[(int)state.kind]} folder, or share a link to it.");
                yield break;
            }

            address = ViewerLibrary.AddressOf(entry);
        }

        Debug.Log($"[Viewer] {ViewerLibrary.KindLabels[(int)state.kind]}: {m_Title} from {address}");
        if (video)
            LoadVideo(address);
        else
            yield return LoadImage(state, address);
    }

    IEnumerator LoadImage(MediaState state, string address)
    {
        using var request = UnityWebRequest.Get(address);
        yield return request.SendWebRequest();
        if (state.id != m_LoadedId)
            yield break;
        if (request.result != UnityWebRequest.Result.Success)
        {
            SetStatus($"Couldn't open {m_Title}:\n{request.error}");
            yield break;
        }

        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true);
        if (!texture.LoadImage(request.downloadHandler.data, false))
        {
            Destroy(texture);
            SetStatus($"{m_Title} isn't a PNG or JPEG picture.");
            yield break;
        }

        int longest = Mathf.Max(texture.width, texture.height);
        if (longest > m_MaxImageSize)
        {
            float scale = (float)m_MaxImageSize / longest;
            texture = Rescaled(texture, Mathf.RoundToInt(texture.width * scale), Mathf.RoundToInt(texture.height * scale));
        }

        texture.wrapMode = TextureWrapMode.Clamp;
        texture.filterMode = FilterMode.Trilinear;
        texture.anisoLevel = 4;
        texture.Apply(true, true);
        m_Image = texture;
        Show(state, texture, texture.width, texture.height);
    }

    static Texture2D Rescaled(Texture2D source, int width, int height)
    {
        var target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        Graphics.Blit(source, target);
        var previous = RenderTexture.active;
        RenderTexture.active = target;
        var result = new Texture2D(width, height, TextureFormat.RGBA32, true);
        result.ReadPixels(new Rect(0, 0, width, height), 0, 0);
        RenderTexture.active = previous;
        RenderTexture.ReleaseTemporary(target);
        Destroy(source);
        return result;
    }

    void LoadVideo(string address)
    {
        if (m_Video == null)
            return;
        m_VideoReady = false;
        m_Video.url = address;
        m_Video.Prepare();
    }

    void OnVideoPrepared(VideoPlayer source)
    {
        var state = media;
        if (state.id != m_LoadedId)
            return;

        int width = (int)source.width, height = (int)source.height;
        if (width <= 0 || height <= 0)
        {
            SetStatus($"Couldn't play {m_Title}: no picture in it.");
            return;
        }

        m_VideoTexture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { name = "Viewer Video" };
        m_VideoTexture.Create();
        source.targetTexture = m_VideoTexture;
        for (ushort track = 0; track < source.audioTrackCount; track++)
            source.EnableAudioTrack(track, track == 0);

        m_VideoReady = true;
        m_NextVideoCheck = 0f;
        m_SeekGraceUntil = 0f;
        Show(state, m_VideoTexture, width, height);

        if (isMine && source.length > 0)
        {
            if (IsSpawned)
                ReportLengthRpc(state.id, source.length);
            else
                m_LocalLength = source.length;
        }

        FollowSharedClock();
    }

    void OnVideoError(VideoPlayer source, string message)
    {
        if (media.id == m_LoadedId)
            SetStatus($"Couldn't play {m_Title}:\n{message}");
    }

    /// <summary>Puts the picture on the screen (one half per eye for spatial media) and reshapes the panel to it.</summary>
    void Show(MediaState state, Texture texture, int width, int height)
    {
        var layout = ViewerLibrary.StereoLayout.Mono;
        string name = state.isUrl ? state.source.ToString() : state.title.ToString();
        if (ViewerLibrary.IsSpatialKind(state.kind))
            layout = ViewerLibrary.StereoLayoutFor(name, width, height);

        float eyeWidth = layout == ViewerLibrary.StereoLayout.SideBySide ? width * 0.5f : width;
        float eyeHeight = layout == ViewerLibrary.StereoLayout.TopBottom ? height * 0.5f : height;
        if (ViewerLibrary.IsHalfStereo(name))
        {
            if (layout == ViewerLibrary.StereoLayout.SideBySide)
                eyeWidth *= 2f;
            else if (layout == ViewerLibrary.StereoLayout.TopBottom)
                eyeHeight *= 2f;
        }

        if (m_Material != null)
        {
            m_Material.SetTexture(k_MainTex, texture);
            m_Material.SetFloat(k_Layout, (float)layout);
            m_Material.SetFloat(k_SwapEyes, ViewerLibrary.SwapsEyes(name) ? 1f : 0f);
        }

        if (m_Screen != null)
            m_Screen.enabled = true;
        SetStatus(string.Empty);

        // The player who chose it reshapes the panel for everyone.
        if (isMine && m_Panel != null)
            m_Panel.SetContentAspect(eyeWidth / eyeHeight);
    }

    void ReleaseMedia()
    {
        m_VideoReady = false;
        if (m_Screen != null)
            m_Screen.enabled = false;
        if (m_Material != null)
            m_Material.SetTexture(k_MainTex, null);

        if (m_Video != null)
        {
            m_Video.Stop();
            m_Video.targetTexture = null;
        }

        if (m_VideoTexture != null)
        {
            m_VideoTexture.Release();
            Destroy(m_VideoTexture);
            m_VideoTexture = null;
        }

        if (m_Image != null)
        {
            Destroy(m_Image);
            m_Image = null;
        }
    }

    #endregion

    void SetStatus(string text)
    {
        m_Status = text;
        if (m_Hint != null)
            m_Hint.text = text;
        changed?.Invoke();
    }
}
