using System;
using System.Globalization;
using System.Text;
using TLab.WebView;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Keeps the videos on a shared browser page playing together on every headset. Each headset runs its own copy of
/// the page (see <see cref="BrowserSync"/>); when the player using the panel plays, pauses, seeks or changes the speed
/// of a video, everyone else's copy of that video does the same, at the same moment of the video, within a fraction of
/// a second (small differences are evened out by playing a little faster or slower, larger ones by jumping). Players
/// who join later, or whose page loads late, catch up to the same moment. The Loop button makes the page's videos start
/// over when they end, for everyone.
/// It works with videos in the page itself (most sites, YouTube's own pages included), through a small script added to
/// every page. It can't reach a video inside another site's embedded player (a YouTube video shown on a news page),
/// and sites that need a sign-in or protect their videos (DRM) may refuse to play them at all; such videos still
/// respond to taps, which are repeated on every copy, but aren't kept at the same moment.
/// Works locally when not connected (Loop only: there is nobody to keep in step with).
/// </summary>
public class BrowserVideoSync : NetworkBehaviour
{
    // Installed once per page (the page keeps it while it stays loaded, through a site's own page changes too);
    // tick() is then called on every check. It reports the page's main video back to Unity, applies Loop, and on a
    // follower's copy moves the video to where the shared one is.
    const string k_Script = @"(function () {
  var T = window.__tunnelVideo;
  if (T && T.version === 1) return;
  T = window.__tunnelVideo = { version: 1, go: '', method: '', active: null, lastTouch: 0, lastSeek: 0, lastKick: 0, nudge: null, followed: -1, timer: 0 };
  T.send = function (o) { try { window.tlab.unitySendMessage(T.go, T.method, JSON.stringify(o)); } catch (e) {} };
  T.page = function () {
    var s = location.href.split('#')[0].replace(/\/+$/, ''), h = 2166136261;
    for (var i = 0; i < s.length; i++) { h ^= s.charCodeAt(i); h = Math.imul(h, 16777619); }
    return h >>> 0;
  };
  T.videos = function () { return Array.prototype.slice.call(document.getElementsByTagName('video')); };
  T.pick = function (vs) {
    if (T.active && T.active.isConnected) { var a = vs.indexOf(T.active); if (a >= 0) return a; }
    var best = -1, bestScore = -1;
    for (var i = 0; i < vs.length; i++) {
      var r = vs[i].getBoundingClientRect();
      var w = Math.max(0, Math.min(r.right, innerWidth) - Math.max(r.left, 0));
      var h = Math.max(0, Math.min(r.bottom, innerHeight) - Math.max(r.top, 0));
      var score = w * h + (vs[i].currentSrc ? 1 : 0);
      if (score > bestScore) { bestScore = score; best = i; }
    }
    return best;
  };
  T.rate = function (v) { return T.nudge && T.nudge.v === v ? T.nudge.base : v.playbackRate; };
  T.report = function (reason) {
    var vs = T.videos();
    var i = T.followed >= 0 && T.followed < vs.length ? T.followed : T.pick(vs);
    var v = i >= 0 ? vs[i] : null;
    var o = { reason: reason, page: T.page(), url: location.href.substring(0, 300), count: vs.length, index: i, has: !!v };
    if (v) {
      o.paused = v.paused; o.time = v.currentTime; o.rate = T.rate(v);
      o.duration = isFinite(v.duration) ? v.duration : -1; o.ready = v.readyState; o.loop = v.loop;
    }
    T.send(o);
  };
  T.soon = function (reason) { clearTimeout(T.timer); T.timer = setTimeout(function () { T.report(reason); }, 60); };
  ['pointerdown', 'touchstart'].forEach(function (type) {
    document.addEventListener(type, function () { T.lastTouch = Date.now(); }, true);
  });
  ['play', 'pause', 'seeked', 'ratechange', 'ended'].forEach(function (type) {
    document.addEventListener(type, function (e) {
      var v = e.target;
      if (!v || v.tagName !== 'VIDEO') return;
      if (type === 'ratechange' && T.nudge && T.nudge.v === v) return;
      if (type !== 'ratechange' && Date.now() - T.lastTouch < 3000) T.active = v;
      T.soon(type);
    }, true);
  });
  T.endNudge = function () {
    if (!T.nudge) return;
    var n = T.nudge; T.nudge = null;
    if (n.v.playbackRate !== n.base) n.v.playbackRate = n.base;
  };
  T.kick = function (v) {
    var box = (v.closest && v.closest('[class*=player],[id*=player]')) || document;
    var b = box.querySelector("".ytp-large-play-button,.vjs-big-play-button,.plyr__control--overlaid,button[aria-label='Play'],button[aria-label='Play video']"");
    T.send({ reason: 'kick', error: b ? 'pressed the player\'s play button' : 'no play button found' });
    if (b) b.click();
  };
  T.follow = function (v, f) {
    var now = Date.now();
    if (T.nudge && T.nudge.v !== v) T.endNudge();
    if (f.paused) {
      T.endNudge();
      if (!v.paused) v.pause();
      if (v.playbackRate !== f.rate) v.playbackRate = f.rate;
      if (v.readyState >= 1 && isFinite(v.duration) && !v.seeking && now - T.lastSeek > 1000) {
        var at = Math.min(f.time, v.duration);
        if (Math.abs(v.currentTime - at) > f.tol) { T.lastSeek = now; v.currentTime = at; }
      }
      return;
    }
    if (v.paused) {
      if (v.readyState === 0 && !v.currentSrc && now - T.lastKick > 3000) { T.lastKick = now; T.kick(v); }
      var p = v.play();
      if (p && p.catch) p.catch(function (e) { T.send({ reason: 'error', error: 'play refused: ' + (e && e.name ? e.name : e) }); });
    }
    if (v.readyState < 1 || !isFinite(v.duration) || v.duration <= 0) {
      T.endNudge();
      if (v.playbackRate !== f.rate) v.playbackRate = f.rate;
      return;
    }
    var d = v.duration, target, diff;
    if (v.loop) {
      target = ((f.time % d) + d) % d;
      diff = v.currentTime - target;
      diff = (((diff + d / 2) % d) + d) % d - d / 2;
    } else {
      target = Math.min(f.time, d);
      diff = v.currentTime - target;
    }
    if (Math.abs(diff) > f.tol) {
      T.endNudge();
      if (v.playbackRate !== f.rate) v.playbackRate = f.rate;
      if (!v.seeking && now - T.lastSeek > 2000) { T.lastSeek = now; v.currentTime = Math.min(target + f.lead, d); }
    } else if (Math.abs(diff) > f.fine) {
      if (!T.nudge) T.nudge = { v: v, base: f.rate };
      T.nudge.base = f.rate;
      var want = f.rate * (diff > 0 ? 1 - f.nudge : 1 + f.nudge);
      if (Math.abs(v.playbackRate - want) > 0.001) v.playbackRate = want;
    } else {
      T.endNudge();
      if (v.playbackRate !== f.rate) v.playbackRate = f.rate;
    }
  };
  T.tick = function (a) {
    T.go = a.go; T.method = a.method;
    var vs = T.videos();
    for (var i = 0; i < vs.length; i++) {
      var v = vs[i];
      if (a.loop) { if (!v.loop) { v.loop = true; v.__tunnelLoop = true; } }
      else if (v.__tunnelLoop) { v.loop = false; v.__tunnelLoop = false; }
    }
    T.followed = -1;
    var f = a.follow;
    if (f && f.page === T.page() && vs.length > 0) {
      var idx = f.count === vs.length && f.index >= 0 && f.index < vs.length ? f.index : T.pick(vs);
      if (idx >= 0) { T.followed = idx; T.follow(vs[idx], f); }
    } else {
      T.endNudge();
    }
    T.report('tick');
  };
})();";

    const string k_LoopOnText = "Loop: On";
    const string k_LoopOffText = "Loop: Off";

    /// <summary>The shared video, as the player using the panel last saw it.</summary>
    struct VideoState : INetworkSerializable
    {
        public bool has;
        public uint page;      // which page it's on (a hash of its address)
        public int index;      // which of the page's videos, and how many there were
        public int count;
        public bool paused;
        public double time;    // the video's time (seconds) at the shared clock's 'stamp'
        public float rate;
        public double duration;
        public double stamp;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref has);
            serializer.SerializeValue(ref page);
            serializer.SerializeValue(ref index);
            serializer.SerializeValue(ref count);
            serializer.SerializeValue(ref paused);
            serializer.SerializeValue(ref time);
            serializer.SerializeValue(ref rate);
            serializer.SerializeValue(ref duration);
            serializer.SerializeValue(ref stamp);
        }
    }

    /// <summary>What the page's script reports (JSON).</summary>
    [Serializable]
    class PageReport
    {
        public string reason;
        public long page;
        public string url;
        public int count;
        public int index;
        public bool has;
        public bool paused;
        public double time;
        public float rate = 1f;
        public double duration = -1;
        public int ready;
        public bool loop;
        public string error;
    }

    [SerializeField]
    BrowserContainer m_Container;

    [SerializeField]
    [Tooltip("Who is using the panel: their copy's video is the one everyone follows.")]
    BrowserSync m_Sync;

    [SerializeField]
    [Tooltip("The bar's Loop button (optional), and its label.")]
    Button m_LoopButton;

    [SerializeField]
    TMP_Text m_LoopLabel;

    [SerializeField]
    [Tooltip("How often each copy checks its video (seconds).")]
    float m_CheckInterval = 0.5f;

    [SerializeField]
    [Tooltip("A copy further than this from the shared video jumps to it (seconds).")]
    float m_SeekTolerance = 0.5f;

    [SerializeField]
    [Tooltip("A copy further than this, but within the jump distance, plays slightly faster or slower to catch up " +
        "(seconds). Keeps headsets in the same room from sounding like an echo.")]
    float m_FineTolerance = 0.08f;

    [SerializeField]
    [Tooltip("How much faster or slower a copy plays while catching up (0.05 = 5%).")]
    float m_NudgeAmount = 0.05f;

    [SerializeField]
    [Tooltip("A jump lands this far ahead, to allow for the time the video takes to resume (seconds).")]
    float m_SeekLead = 0.1f;

    [SerializeField]
    [Tooltip("The player using the panel shares their video again when it has drifted this far from what was last " +
        "shared (a seek, or the video stalling to load) (seconds).")]
    float m_PublishDrift = 0.4f;

    readonly NetworkVariable<VideoState> m_State = new(new VideoState { rate = 1f }, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<bool> m_Loop = new(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    bool m_LocalLoop;
    float m_NextCheck;
    VideoState m_LastPublished;
    bool m_HasPublished;
    readonly StringBuilder m_Args = new();

    Browser browser => m_Container != null ? m_Container.browser : null;

    /// <summary>The page's videos start over when they end (for everyone).</summary>
    public bool isLooping => IsSpawned ? m_Loop.Value : m_LocalLoop;

    /// <summary>
    /// This headset's video is the one the others follow: it's the copy of the player using the panel (or of one who
    /// has just touched it, whose claim may still be on its way to the host).
    /// </summary>
    bool isLeading
    {
        get
        {
            if (!IsSpawned)
                return true;
            if (m_Sync == null)
                return IsServer;
            return m_Sync.isDriver || m_Sync.UsedRecently(2f);
        }
    }

    double clock => IsSpawned ? NetworkManager.ServerTime.Time : Time.unscaledTimeAsDouble;

    void Awake()
    {
        if (m_LoopButton != null)
            m_LoopButton.onClick.AddListener(() => SetLoop(!isLooping));
        UpdateLabel();
    }

    ///<inheritdoc/>
    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        m_State.OnValueChanged += OnStateChanged;
        m_Loop.OnValueChanged += OnLoopChanged;
        UpdateLabel();
    }

    ///<inheritdoc/>
    public override void OnNetworkDespawn()
    {
        base.OnNetworkDespawn();
        m_State.OnValueChanged -= OnStateChanged;
        m_Loop.OnValueChanged -= OnLoopChanged;
    }

    /// <summary>Turns Loop on or off for everyone.</summary>
    public void SetLoop(bool on)
    {
        Debug.Log($"[BrowserVideo] {name}: loop {(on ? "on" : "off")}");
        if (IsSpawned)
        {
            SetLoopRpc(on);
            return;
        }

        m_LocalLoop = on;
        UpdateLabel();
        m_NextCheck = 0f;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void SetLoopRpc(bool on) => m_Loop.Value = on;

    void OnLoopChanged(bool previous, bool current)
    {
        UpdateLabel();
        m_NextCheck = 0f;
    }

    // Someone's video changed: apply it now rather than at the next check.
    void OnStateChanged(VideoState previous, VideoState current) => m_NextCheck = 0f;

    void UpdateLabel()
    {
        if (m_LoopLabel != null)
            m_LoopLabel.text = isLooping ? k_LoopOnText : k_LoopOffText;
    }

    void Update()
    {
        if (browser == null || Time.unscaledTime < m_NextCheck)
            return;
        m_NextCheck = Time.unscaledTime + m_CheckInterval;
        browser.EvaluateJS(k_Script + "window.__tunnelVideo.tick(" + BuildArgs() + ");");
    }

    /// <summary>What the page's script needs this time: where to report, Loop, and (on a follower) the shared video.</summary>
    string BuildArgs()
    {
        var sb = m_Args.Clear();
        sb.Append("{\"go\":\"").Append(gameObject.name).Append("\",\"method\":\"").Append(nameof(OnVideoMessage))
            .Append("\",\"loop\":").Append(isLooping ? "true" : "false").Append(",\"follow\":");

        var s = m_State.Value;
        if (!IsSpawned || isLeading || !s.has)
        {
            sb.Append("null}");
            return sb.ToString();
        }

        double time = s.paused ? s.time : s.time + (clock - s.stamp) * s.rate;
        sb.Append("{\"page\":").Append(s.page.ToString(CultureInfo.InvariantCulture))
            .Append(",\"index\":").Append(s.index.ToString(CultureInfo.InvariantCulture))
            .Append(",\"count\":").Append(s.count.ToString(CultureInfo.InvariantCulture))
            .Append(",\"paused\":").Append(s.paused ? "true" : "false")
            .Append(",\"time\":").Append(Number(time))
            .Append(",\"rate\":").Append(Number(s.rate))
            .Append(",\"tol\":").Append(Number(m_SeekTolerance))
            .Append(",\"fine\":").Append(Number(m_FineTolerance))
            .Append(",\"nudge\":").Append(Number(m_NudgeAmount))
            .Append(",\"lead\":").Append(Number(m_SeekLead))
            .Append("}}");
        return sb.ToString();
    }

    static string Number(double value) =>
        double.IsNaN(value) || double.IsInfinity(value) ? "0" : value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Called by the page's script (by this object's name) with its main video's state.</summary>
    public void OnVideoMessage(string json)
    {
        PageReport report;
        try
        {
            report = JsonUtility.FromJson<PageReport>(json);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[BrowserVideo] {name}: unreadable report ({e.Message}): {json}");
            return;
        }

        if (report == null)
            return;

        if (report.reason is "error" or "kick")
        {
            Debug.Log($"[BrowserVideo] {name}: {report.reason}: {report.error}");
            return;
        }

        if (!IsSpawned)
            return;

        if (!isLeading)
        {
            if (report.reason is "play" or "pause" or "ended")
                Debug.Log($"[BrowserVideo] {name}: following, this copy's video {report.reason} at {report.time:0.0} s");
            return;
        }

        var now = new VideoState
        {
            has = report.has,
            page = (uint)report.page,
            index = report.index,
            count = report.count,
            paused = report.paused,
            time = report.time,
            rate = report.rate > 0f ? report.rate : 1f,
            duration = report.duration,
            stamp = clock,
        };

        var last = Latest();
        if (!Differs(last, now))
            return;

        if (now.has)
            Debug.Log($"[BrowserVideo] {name}: sharing video {now.index + 1} of {now.count}, " +
                $"{(now.paused ? "paused" : "playing")} at {now.time:0.0} s" +
                (now.duration >= 0 ? $" of {now.duration:0.0}" : " (live)") +
                (Mathf.Approximately(now.rate, 1f) ? "" : $" ×{now.rate:0.##}") +
                $" ({report.reason}) on {report.url}");
        else
            Debug.Log($"[BrowserVideo] {name}: no video to share on {report.url}");

        m_LastPublished = now;
        m_HasPublished = true;
        PublishRpc(now);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void PublishRpc(VideoState state, RpcParams rpcParams = default)
    {
        // Only the player using the panel shares their video; a report from the previous user's copy, still on its
        // way, is dropped.
        if (m_Sync == null || m_Sync.IsDriverId(rpcParams.Receive.SenderClientId))
            m_State.Value = state;
    }

    /// <summary>The newest of what's shared and what this headset last shared (which may not have come back yet).</summary>
    VideoState Latest()
    {
        var shared = m_State.Value;
        return m_HasPublished && m_LastPublished.stamp > shared.stamp ? m_LastPublished : shared;
    }

    bool Differs(VideoState last, VideoState now)
    {
        if (last.has != now.has)
            return true;
        if (!now.has)
            return false;
        if (last.page != now.page || last.index != now.index || last.count != now.count || last.paused != now.paused)
            return true;
        if (Mathf.Abs(last.rate - now.rate) > 0.01f)
            return true;

        // Where the shared video should be by now, against where this one is (around the loop, if looping).
        double expected = last.paused ? last.time : last.time + (now.stamp - last.stamp) * last.rate;
        double diff = now.time - expected;
        if (isLooping && now.duration > 0)
        {
            double d = now.duration;
            diff = ((diff + d / 2) % d + d) % d - d / 2;
        }

        return Math.Abs(diff) > m_PublishDrift;
    }
}
