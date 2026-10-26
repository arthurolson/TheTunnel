using TLab.WebView;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Turns XR pointer input on the browser view into web touches that survive hand/laser jitter and slow pinches.
/// Nothing is sent when a press starts. If it moves further than <see cref="m_TapSlopPixels"/> it becomes a drag:
/// the touch-down is sent at the start point, then the movement. If it is released without moving that far, a
/// quick touch-down + touch-up is sent at the start point, however long it was held, so the page always sees a
/// clean tap (buttons click, text fields focus) rather than a long-press. Scroll input (controller thumbstick)
/// scrolls the page.
/// </summary>
[RequireComponent(typeof(RectTransform))]
public class StableBrowserInputListener : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler, IPointerExitHandler, IScrollHandler
{
    const int k_ActionDown = 0;
    const int k_ActionUp = 1;
    const int k_ActionDrag = 2;

    [SerializeField]
    BrowserContainer m_Container;

    [SerializeField]
    [Tooltip("Movement (in page pixels) a press may wobble before it counts as a drag instead of a tap.")]
    float m_TapSlopPixels = 24f;

    [SerializeField]
    [Tooltip("Page pixels scrolled per unit of scroll input (controller thumbstick).")]
    float m_ScrollPixelsPerUnit = 60f;

    [SerializeField]
    [Tooltip("A new press this soon after a release is ignored (seconds). Hand-tracking pinches can chatter, which a " +
        "page would otherwise see as a double tap (e.g. a video's play/pause toggling straight back).")]
    float m_RepressIgnoreTime = 0.15f;

    [SerializeField]
    [Tooltip("Write each touch sent to the page to the log, for diagnosing.")]
    bool m_LogTouches = true;

    RectTransform m_Rect;
    int? m_PointerId;
    long m_DownTime;
    Vector2 m_DownPixels;
    Vector2 m_LastPixels;
    bool m_Dragging;
    float m_LastReleaseTime = float.NegativeInfinity;

    Browser browser => m_Container != null ? m_Container.browser : null;

    void Awake() => m_Rect = (RectTransform)transform;

    void OnDisable() => m_PointerId = null;

    public void OnPointerDown(PointerEventData eventData)
    {
        if (m_PointerId != null || browser == null || !TryGetPixels(eventData, false, out var pixels))
            return;

        if (Time.unscaledTime - m_LastReleaseTime < m_RepressIgnoreTime)
        {
            Log($"ignored re-press {(Time.unscaledTime - m_LastReleaseTime) * 1000f:0} ms after release (pointer {eventData.pointerId})");
            return;
        }

        // Touch-down is deferred: a press held still on a button would otherwise become a long-press.
        m_PointerId = eventData.pointerId;
        m_DownPixels = m_LastPixels = pixels;
        m_Dragging = false;
        Log($"press at {pixels} (pointer {eventData.pointerId})");
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (m_PointerId != eventData.pointerId || !TryGetPixels(eventData, true, out var pixels))
            return;

        m_LastPixels = pixels;
        if (!m_Dragging)
        {
            if ((pixels - m_DownPixels).magnitude < m_TapSlopPixels)
                return; // still a tap: swallow the wobble

            // Became a drag: put the finger down where the press started, then follow the pointer.
            m_Dragging = true;
            m_DownTime = browser.TouchEvent((int)m_DownPixels.x, (int)m_DownPixels.y, k_ActionDown, m_DownTime);
            Log($"drag starts at {m_DownPixels}");
        }

        browser.TouchEvent((int)pixels.x, (int)pixels.y, k_ActionDrag, m_DownTime);
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        if (m_PointerId != eventData.pointerId)
            return;
        if (TryGetPixels(eventData, true, out var pixels))
            m_LastPixels = pixels;
        Release();
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        if (m_PointerId == eventData.pointerId)
            Release();
    }

    public void OnScroll(PointerEventData eventData)
    {
        if (browser == null)
            return;
        Vector2 delta = eventData.scrollDelta * m_ScrollPixelsPerUnit;
        // Pushing the stick up/forward means "scroll up" (content moves down), i.e. a negative page scroll.
        browser.ScrollBy((int)delta.x, (int)-delta.y);
    }

    void Release()
    {
        Vector2 at = m_Dragging ? m_LastPixels : m_DownPixels;
        if (browser != null)
        {
            // A press that never moved is sent now as a quick tap: down and up at the same spot.
            if (!m_Dragging)
                m_DownTime = browser.TouchEvent((int)at.x, (int)at.y, k_ActionDown, m_DownTime);
            browser.TouchEvent((int)at.x, (int)at.y, k_ActionUp, m_DownTime);
        }
        Log($"{(m_Dragging ? "drag end" : "tap")} at {at}");
        m_PointerId = null;
        m_Dragging = false;
        m_LastReleaseTime = Time.unscaledTime;
    }

    void Log(string message)
    {
        if (m_LogTouches)
            Debug.Log("[BrowserTouch] " + message);
    }

    /// <summary>Pointer position in page pixels (origin top-left). Outside the view it is either rejected or clamped.</summary>
    bool TryGetPixels(PointerEventData eventData, bool clampOutside, out Vector2 pixels)
    {
        pixels = default;
        var camera = eventData.pressEventCamera != null ? eventData.pressEventCamera : eventData.enterEventCamera;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(m_Rect, eventData.position, camera, out var local))
            return false;

        Rect rect = m_Rect.rect;
        float u = (local.x - rect.xMin) / rect.width;
        float v = 1f - (local.y - rect.yMin) / rect.height;
        bool inside = u >= 0f && u <= 1f && v >= 0f && v <= 1f;
        if (!inside && !clampOutside)
            return false;

        Vector2Int view = browser.viewSize;
        pixels = new Vector2(Mathf.Clamp01(u) * view.x, Mathf.Clamp01(v) * view.y);
        return true;
    }
}
