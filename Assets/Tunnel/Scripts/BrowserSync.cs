using TLab.WebView;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Shares a browser panel between players. Every headset runs its own copy of the page; whatever one player does
/// (taps, drags, scrolling, typing, going back, opening a page) is applied to their copy at once and repeated on
/// everyone else's copy, so all players see the same thing.
/// The player who last used the panel is its "driver": their headset reports its page address and scroll position,
/// and the other copies catch up if they drift (a slow page load, a late joiner). The Sync button makes everyone
/// reload the driver's page straight away. Works locally when not connected.
/// </summary>
public class BrowserSync : NetworkBehaviour
{
    const int k_ActionDown = 0;
    const int k_ActionUp = 1;
    const int k_ActionDrag = 2;
    const ulong k_NoDriver = ulong.MaxValue;

    [SerializeField]
    BrowserContainer m_Container;

    [SerializeField]
    [Tooltip("How often the driver reports its page, and the others check theirs (seconds).")]
    float m_CheckInterval = 0.5f;

    [SerializeField]
    [Tooltip("How long a copy may show a different page before it loads the driver's (seconds). Repeated clicks " +
        "usually navigate every copy by themselves, so this only catches the ones that didn't.")]
    float m_UrlCatchUpDelay = 2f;

    [SerializeField]
    [Tooltip("Scroll difference (page pixels) that a copy corrects to the driver's.")]
    int m_ScrollTolerance = 40;

    [SerializeField]
    [Tooltip("How long a copy may be scrolled differently before it is corrected (seconds).")]
    float m_ScrollCatchUpDelay = 1f;

    readonly NetworkVariable<ulong> m_Driver = new(k_NoDriver, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<FixedString4096Bytes> m_Url = new(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<Vector2Int> m_Scroll = new(Vector2Int.zero, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    long m_LocalDownTime;
    long m_RemoteDownTime;
    float m_NextCheck;
    string m_ReportedUrl;
    Vector2Int m_ReportedScroll;
    float m_UrlMismatchSince = -1f;
    float m_ScrollMismatchSince = -1f;

    Browser browser => m_Container != null ? m_Container.browser : null;

    /// <summary>Until someone uses the panel, the host's page is the one to follow.</summary>
    bool isDriver => !IsSpawned || IsDriverId(NetworkManager.LocalClientId);

    bool IsDriverId(ulong clientId) =>
        m_Driver.Value == clientId || (m_Driver.Value == k_NoDriver && clientId == NetworkManager.ServerClientId);

    #region Local actions (applied here and repeated on the other copies)

    /// <summary>A touch at a page position given as fractions of the view (0-1, origin top-left).</summary>
    public void Touch(Vector2 normalized, int action)
    {
        MarkInteraction();
        ApplyTouch(normalized, action, ref m_LocalDownTime);
        if (IsSpawned)
            TouchRpc(normalized, action);
    }

    public void ScrollBy(int x, int y)
    {
        MarkInteraction();
        browser?.ScrollBy(x, y);
        if (IsSpawned)
            ScrollByRpc(x, y);
    }

    public void TypeCharacter(char character)
    {
        MarkInteraction();
        browser?.KeyEvent(character);
        if (IsSpawned)
            TypeCharacterRpc(character);
    }

    /// <summary>An Android key code, e.g. 67 for backspace.</summary>
    public void PressKey(int keyCode)
    {
        MarkInteraction();
        browser?.KeyEvent(keyCode);
        if (IsSpawned)
            PressKeyRpc(keyCode);
    }

    public void LoadUrl(string url)
    {
        if (string.IsNullOrEmpty(url))
            return;
        MarkInteraction();
        browser?.LoadUrl(url);
        if (IsSpawned)
            LoadUrlRpc(url);
    }

    public void GoBack()
    {
        MarkInteraction();
        browser?.GoBack();
        if (IsSpawned)
            GoBackRpc();
    }

    /// <summary>Everyone reloads the driver's page now (for when a page's state has drifted apart).</summary>
    public void SyncNow()
    {
        if (IsSpawned)
            SyncNowRpc();
    }

    /// <summary>
    /// This player is using the panel, so their page is the one the others follow. Called by every local action,
    /// and by things that change the page without going through here (typing an address, tapping the address bar).
    /// </summary>
    public void MarkInteraction()
    {
        if (IsSpawned && m_Driver.Value != NetworkManager.LocalClientId)
            ClaimDriverRpc();
    }

    #endregion

    #region Repeating actions on the other copies

    [Rpc(SendTo.NotMe, InvokePermission = RpcInvokePermission.Everyone)]
    void TouchRpc(Vector2 normalized, int action) => ApplyTouch(normalized, action, ref m_RemoteDownTime);

    [Rpc(SendTo.NotMe, InvokePermission = RpcInvokePermission.Everyone)]
    void ScrollByRpc(int x, int y) => browser?.ScrollBy(x, y);

    [Rpc(SendTo.NotMe, InvokePermission = RpcInvokePermission.Everyone)]
    void TypeCharacterRpc(char character) => browser?.KeyEvent(character);

    [Rpc(SendTo.NotMe, InvokePermission = RpcInvokePermission.Everyone)]
    void PressKeyRpc(int keyCode) => browser?.KeyEvent(keyCode);

    [Rpc(SendTo.NotMe, InvokePermission = RpcInvokePermission.Everyone)]
    void LoadUrlRpc(string url) => browser?.LoadUrl(url);

    [Rpc(SendTo.NotMe, InvokePermission = RpcInvokePermission.Everyone)]
    void GoBackRpc() => browser?.GoBack();

    [Rpc(SendTo.Everyone, InvokePermission = RpcInvokePermission.Everyone)]
    void SyncNowRpc()
    {
        if (isDriver || browser == null)
            return;
        string url = m_Url.Value.ToString();
        if (!string.IsNullOrEmpty(url))
            browser.LoadUrl(url);
        m_UrlMismatchSince = m_ScrollMismatchSince = -1f;
    }

    void ApplyTouch(Vector2 normalized, int action, ref long downTime)
    {
        if (browser == null)
            return;
        Vector2Int view = browser.viewSize;
        int x = Mathf.RoundToInt(Mathf.Clamp01(normalized.x) * view.x);
        int y = Mathf.RoundToInt(Mathf.Clamp01(normalized.y) * view.y);
        long result = browser.TouchEvent(x, y, action, downTime);
        if (action == k_ActionDown)
            downTime = result;
    }

    #endregion

    #region Keeping the copies on the same page

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void ClaimDriverRpc(RpcParams rpcParams = default) => m_Driver.Value = rpcParams.Receive.SenderClientId;

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void ReportUrlRpc(string url, RpcParams rpcParams = default)
    {
        if (IsDriverId(rpcParams.Receive.SenderClientId))
            m_Url.Value = new FixedString4096Bytes(url);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void ReportScrollRpc(Vector2Int scroll, RpcParams rpcParams = default)
    {
        if (IsDriverId(rpcParams.Receive.SenderClientId))
            m_Scroll.Value = scroll;
    }

    void Update()
    {
        if (!IsSpawned || browser == null || Time.unscaledTime < m_NextCheck)
            return;
        m_NextCheck = Time.unscaledTime + m_CheckInterval;

        string url = browser.GetUrl();
        if (string.IsNullOrEmpty(url))
            return; // still starting up or loading

        var scroll = new Vector2Int(browser.GetScrollX(), browser.GetScrollY());
        if (isDriver)
            Report(url, scroll);
        else
            CatchUp(url, scroll);
    }

    void Report(string url, Vector2Int scroll)
    {
        if (url != m_ReportedUrl && System.Text.Encoding.UTF8.GetByteCount(url) <= FixedString4096Bytes.UTF8MaxLengthInBytes)
        {
            m_ReportedUrl = url;
            ReportUrlRpc(url);
        }

        if ((scroll - m_ReportedScroll).sqrMagnitude > 16)
        {
            m_ReportedScroll = scroll;
            ReportScrollRpc(scroll);
        }
    }

    void CatchUp(string url, Vector2Int scroll)
    {
        m_ReportedUrl = null; // report afresh if this player becomes the driver
        string target = m_Url.Value.ToString();
        if (string.IsNullOrEmpty(target))
            return;

        if (!SameUrl(url, target))
        {
            m_ScrollMismatchSince = -1f;
            if (m_UrlMismatchSince < 0f)
                m_UrlMismatchSince = Time.unscaledTime;
            else if (Time.unscaledTime - m_UrlMismatchSince >= m_UrlCatchUpDelay)
            {
                Debug.Log($"[BrowserSync] {name}: catching up to {target}");
                browser.LoadUrl(target);
                m_UrlMismatchSince = -1f;
            }

            return;
        }

        m_UrlMismatchSince = -1f;
        Vector2Int targetScroll = m_Scroll.Value;
        if (Mathf.Abs(scroll.x - targetScroll.x) <= m_ScrollTolerance && Mathf.Abs(scroll.y - targetScroll.y) <= m_ScrollTolerance)
        {
            m_ScrollMismatchSince = -1f;
            return;
        }

        if (m_ScrollMismatchSince < 0f)
            m_ScrollMismatchSince = Time.unscaledTime;
        else if (Time.unscaledTime - m_ScrollMismatchSince >= m_ScrollCatchUpDelay)
        {
            browser.ScrollTo(targetScroll.x, targetScroll.y);
            m_ScrollMismatchSince = -1f;
        }
    }

    static bool SameUrl(string a, string b) => a.TrimEnd('/') == b.TrimEnd('/');

    #endregion
}
