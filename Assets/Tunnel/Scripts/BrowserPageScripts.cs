using TLab.WebView;
using UnityEngine;

/// <summary>
/// Scripts injected into every page the browser panel loads.
/// Links and pop-ups that ask for a new window open in the panel instead (the WebView would otherwise drop them).
/// Full screen is disabled: Android's WebView moves a full-screen video into a separate view that the panel can't
/// show or send touches to, which leaves the player stuck. With full screen reported as unavailable, players (e.g.
/// YouTube) hide their full-screen button and keep the video in the page; resize the panel to make it bigger.
/// </summary>
public class BrowserPageScripts : MonoBehaviour
{
    const string k_NoFullscreen = @"(function () {
  if (window.__tunnelNoFullscreen) return;
  window.__tunnelNoFullscreen = true;
  var resolved = function () { return Promise.resolve(); };
  ['requestFullscreen', 'webkitRequestFullscreen', 'webkitRequestFullScreen', 'mozRequestFullScreen', 'msRequestFullscreen']
    .forEach(function (name) { if (Element.prototype[name]) Element.prototype[name] = resolved; });
  ['webkitEnterFullscreen', 'webkitEnterFullScreen']
    .forEach(function (name) { if (HTMLVideoElement.prototype[name]) HTMLVideoElement.prototype[name] = function () {}; });
  ['fullscreenEnabled', 'webkitFullscreenEnabled', 'mozFullScreenEnabled'].forEach(function (name) {
    try { Object.defineProperty(document, name, { get: function () { return false; }, configurable: true }); } catch (e) {}
  });
  if (document.fullscreenElement && document.exitFullscreen) document.exitFullscreen();
})();";

    // The panel has a single view and no tabs, and Android's WebView silently drops links that ask for a new
    // window (target="_blank", e.g. Mol*'s "Open Mol* Viewer") and window.open() pop-ups. Open them here instead;
    // Back returns to the previous page.
    const string k_OpenLinksHere = @"(function () {
  if (window.__tunnelOpenHere) return;
  window.__tunnelOpenHere = true;
  document.addEventListener('click', function (e) {
    var a = e.target && e.target.closest ? e.target.closest('a[target]') : null;
    if (a && a.href && a.target && a.target.toLowerCase() !== '_self') a.target = '_self';
  }, true);
  var nativeOpen = window.open;
  window.open = function (url, name, features) {
    if (url) { location.href = new URL(url, location.href).href; return window; }
    return nativeOpen ? nativeOpen.apply(window, arguments) : null;
  };
})();";

    // Diagnostics: reports what the page itself sees (presses, clicks, focus, JS errors) back to Unity's log as
    // "[BrowserPage] ...". __GO__/__METHOD__ are filled in by C# (kept local: the TLab focus script owns the
    // page-global `go`/`method`); the tlab bridge delivers via UnitySendMessage.
    const string k_Diagnostics = @"(function () {
  if (window.__tunnelDiag) return;
  window.__tunnelDiag = true;
  var target = '__GO__', handler = '__METHOD__';
  function send(msg) { try { window.tlab.unitySendMessage(target, handler, String(msg).substring(0, 300)); } catch (e) {} }
  function describe(el) {
    if (!el || !el.tagName) return String(el);
    var s = el.tagName.toLowerCase();
    if (el.id) s += '#' + el.id;
    if (el.className && typeof el.className === 'string') s += '.' + el.className.trim().split(/\s+/).slice(0, 2).join('.');
    var label = (el.getAttribute && (el.getAttribute('title') || el.getAttribute('aria-label'))) || (el.innerText || '').trim().substring(0, 30);
    return label ? s + ' ""' + label + '""' : s;
  }
  ['pointerdown', 'touchstart', 'click', 'focusin'].forEach(function (type) {
    document.addEventListener(type, function (e) {
      var extra = e.defaultPrevented ? ' (prevented)' : '';
      send(type + ' -> ' + describe(e.target) + extra + (e.clientX !== undefined ? ' @' + Math.round(e.clientX) + ',' + Math.round(e.clientY) : ''));
    }, true);
  });
  window.addEventListener('error', function (e) { send('JS error: ' + e.message + ' (' + e.filename + ':' + e.lineno + ')'); });
  window.addEventListener('unhandledrejection', function (e) { send('unhandled rejection: ' + (e.reason && e.reason.message || e.reason)); });
  var origError = console.error;
  console.error = function () { send('console.error: ' + Array.prototype.join.call(arguments, ' ')); origError.apply(console, arguments); };
  send('diagnostics active on ' + location.href + ' viewport ' + innerWidth + 'x' + innerHeight + ' dpr ' + devicePixelRatio);
})();";

    [SerializeField]
    BrowserContainer m_Container;

    [SerializeField]
    [Tooltip("Inject a script that logs the page's presses, clicks, focus changes and errors (\"[BrowserPage]\").")]
    bool m_Diagnostics = true;

    /// <summary>Wire to the browser's page-finished event.</summary>
    public void OnPageFinished(string url)
    {
        if (m_Container == null || m_Container.browser == null)
            return;

        Debug.Log("[BrowserPage] page finished: " + url);
        m_Container.browser.EvaluateJS(k_NoFullscreen);
        m_Container.browser.EvaluateJS(k_OpenLinksHere);
        if (m_Diagnostics)
            m_Container.browser.EvaluateJS(k_Diagnostics.Replace("__GO__", gameObject.name).Replace("__METHOD__", nameof(OnPageMessage)));
    }

    /// <summary>Messages posted back by the diagnostics script.</summary>
    public void OnPageMessage(string message) => Debug.Log("[BrowserPage] " + message);
}
