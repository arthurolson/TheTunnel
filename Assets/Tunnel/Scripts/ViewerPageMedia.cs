using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using UnityEngine.Networking;

/// <summary>
/// Finds what the Viewer should show for an address that isn't obviously an image or video file (one copied from
/// the browser, say). Asks the server what the address is: an image or a video is shown as it is; for a web page, the
/// page's own sharing tags name its main video or picture (og:video / og:image, as used for link previews), and the
/// first plain &lt;video&gt; or &lt;img&gt; on it is the fallback.
/// Videos that only play inside their site's own player (YouTube and the like) can't be found this way; the page's
/// picture is offered instead.
/// </summary>
public static class ViewerPageMedia
{
    public class Result
    {
        public string url;
        public bool isVideo;
        public string title;
        public string error;
        public bool videoOnlyInPage;
    }

    const string k_UserAgent = "Mozilla/5.0 (Linux; Android 14; Quest 3) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Mobile Safari/537.36";
    const int k_MaxPageBytes = 3 * 1024 * 1024;

    static readonly Regex k_MetaTag = new(@"<meta\b[^>]*>", RegexOptions.IgnoreCase);
    static readonly Regex k_Attribute = new(@"([a-zA-Z:_-]+)\s*=\s*(""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase);
    static readonly Regex k_Title = new(@"<title[^>]*>([^<]*)</title>", RegexOptions.IgnoreCase);
    static readonly Regex k_VideoTag = new(@"<(?:video|source)\b[^>]*\bsrc\s*=\s*[""']([^""']+)[""'][^>]*>", RegexOptions.IgnoreCase);
    static readonly Regex k_ImageTag = new(@"<img\b[^>]*\bsrc\s*=\s*[""']([^""']+)[""'][^>]*>", RegexOptions.IgnoreCase);

    public static bool HasMediaExtension(string url, out bool isVideo)
    {
        string path = url.Split('?', '#')[0].ToLowerInvariant();
        isVideo = ViewerLibrary.VideoExtensions.Any(path.EndsWith) || path.EndsWith(".m3u8");
        return isVideo || ViewerLibrary.ImageExtensions.Any(path.EndsWith);
    }

    public static IEnumerator Resolve(string url, Action<Result> done)
    {
        // What is it? A HEAD request doesn't download a video just to find out it is one.
        string contentType = null;
        using (var head = UnityWebRequest.Head(url))
        {
            head.SetRequestHeader("User-Agent", k_UserAgent);
            yield return head.SendWebRequest();
            if (head.result == UnityWebRequest.Result.Success)
                contentType = head.GetResponseHeader("Content-Type");
        }

        if (IsMediaType(contentType, out bool video))
        {
            done(new Result { url = url, isVideo = video });
            yield break;
        }

        using var request = UnityWebRequest.Get(url);
        request.SetRequestHeader("User-Agent", k_UserAgent);
        var operation = request.SendWebRequest();
        while (!operation.isDone)
        {
            // Not a page after all (a server that didn't answer HEAD): stop before downloading a whole video.
            if (contentType == null && request.downloadedBytes > 0)
            {
                contentType = request.GetResponseHeader("Content-Type");
                if (IsMediaType(contentType, out video))
                {
                    request.Abort();
                    done(new Result { url = url, isVideo = video });
                    yield break;
                }
            }

            if (request.downloadedBytes > k_MaxPageBytes)
            {
                request.Abort();
                break;
            }

            yield return null;
        }

        if (request.result != UnityWebRequest.Result.Success && request.downloadedBytes == 0)
        {
            done(new Result { error = $"Couldn't open {url}: {request.error}" });
            yield break;
        }

        if (IsMediaType(request.GetResponseHeader("Content-Type"), out video))
        {
            done(new Result { url = url, isVideo = video });
            yield break;
        }

        done(FromPage(request.downloadHandler.text, request.url ?? url));
    }

    static bool IsMediaType(string contentType, out bool isVideo)
    {
        contentType = (contentType ?? string.Empty).ToLowerInvariant();
        isVideo = contentType.StartsWith("video/") || contentType.Contains("mpegurl");
        return isVideo || contentType.StartsWith("image/");
    }

    /// <summary>The page's main video or picture, by its sharing tags, else its first video or picture.</summary>
    static Result FromPage(string html, string pageUrl)
    {
        var meta = k_MetaTag.Matches(html).Cast<Match>().Select(m => Attributes(m.Value)).ToList();

        string Meta(params string[] names)
        {
            foreach (string name in names)
            {
                foreach (var tag in meta)
                {
                    string key = tag.TryGetValue("property", out var p) ? p : tag.TryGetValue("name", out var n) ? n : null;
                    if (key != null && string.Equals(key, name, StringComparison.OrdinalIgnoreCase) && tag.TryGetValue("content", out var content) && content.Length > 0)
                        return content;
                }
            }

            return null;
        }

        string title = Meta("og:title", "twitter:title");
        if (title == null)
        {
            var titleMatch = k_Title.Match(html);
            title = titleMatch.Success ? WebUtility.HtmlDecode(titleMatch.Groups[1].Value).Trim() : null;
        }

        // A video tag counts only if it's a real video file; many sites point it at their own embedded player.
        string video = Meta("og:video:secure_url", "og:video:url", "og:video", "twitter:player:stream");
        string videoType = Meta("og:video:type", "twitter:player:stream:content_type") ?? string.Empty;
        bool videoOnlyInPage = false;
        if (video != null && !videoType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) && !HasMediaExtension(video, out _))
        {
            videoOnlyInPage = true;
            video = null;
        }

        if (video == null)
        {
            var videoTag = k_VideoTag.Matches(html).Cast<Match>().Select(m => m.Groups[1].Value)
                .FirstOrDefault(src => !src.StartsWith("blob:") && HasMediaExtension(src, out bool isVideo) && isVideo);
            video = videoTag;
        }

        if (video != null)
            return new Result { url = Absolute(pageUrl, video), isVideo = true, title = title };

        string image = Meta("og:image:secure_url", "og:image:url", "og:image", "twitter:image", "twitter:image:src");
        if (image == null)
        {
            image = k_ImageTag.Matches(html).Cast<Match>().Select(m => WebUtility.HtmlDecode(m.Groups[1].Value))
                .FirstOrDefault(src => !src.StartsWith("data:") && HasMediaExtension(src, out bool isVideo) && !isVideo);
        }

        if (image != null)
            return new Result { url = Absolute(pageUrl, image), title = title, videoOnlyInPage = videoOnlyInPage };

        return new Result
        {
            error = videoOnlyInPage
                ? "That page's video only plays in its own player. Watch it in the browser instead."
                : "No picture or video found on that page. Open the picture or video on its own in the browser, then Copy.",
        };
    }

    static Dictionary<string, string> Attributes(string tag)
    {
        var attributes = new Dictionary<string, string>();
        foreach (Match a in k_Attribute.Matches(tag))
        {
            string value = a.Groups[3].Success ? a.Groups[3].Value : a.Groups[4].Success ? a.Groups[4].Value : a.Groups[5].Value;
            attributes.TryAdd(a.Groups[1].Value.ToLowerInvariant(), WebUtility.HtmlDecode(value));
        }

        return attributes;
    }

    static string Absolute(string pageUrl, string link) =>
        Uri.TryCreate(new Uri(pageUrl), WebUtility.HtmlDecode(link), out var absolute) ? absolute.AbsoluteUri : link;
}
