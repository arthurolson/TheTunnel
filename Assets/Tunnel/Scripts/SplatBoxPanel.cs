using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The small menu beside a splat-box (see <see cref="SplatBox"/>): "Splats", then one row per portrait, each with its
/// picture: those built into the app (see <see cref="SplatLibrary"/>), then everything in the Tunnel's asset repository
/// the app can show (see <see cref="TunnelAssetCatalog"/>): meshes, images, videos, molecules and viruses, the one shown
/// checked. While the box shows a video, a button under the rows pauses and plays it for everyone. Pressing a row shows that portrait in the box, for
/// everyone. A row says how large a download is the first time, and when a mesh has so many triangles the headset may
/// slow down. Under the rows: how loading is going on this headset, and the credit an asset's license asks for.
/// Its rows are made from the library and the repository's list, so an asset added to the repository appears here with
/// no new build. Each headset has its own copy, facing its player. Made by Tunnel > Containers > Build Containers.
/// </summary>
public class SplatBoxPanel : MonoBehaviour
{
    [SerializeField]
    RectTransform m_Canvas;

    [SerializeField]
    [Tooltip("A row, hidden; one copy is made per portrait.")]
    Button m_RowTemplate;

    [SerializeField]
    TMP_Text m_Title;

    [SerializeField]
    [Tooltip("Height of the title above the rows (canvas units).")]
    float m_Top = 80f;

    [SerializeField]
    [Tooltip("Height of each row (canvas units).")]
    float m_RowHeight = 64f;

    [SerializeField]
    [Tooltip("Its width once it lists the asset repository's meshes, whose titles are long (canvas units).")]
    float m_Width = 620f;

    [SerializeField]
    [Tooltip("Height of the lines under the rows: loading and credits (canvas units).")]
    float m_StatusHeight = 84f;

    [SerializeField]
    [Tooltip("Room under the last row (canvas units).")]
    float m_Bottom = 16f;

    class Row
    {
        public string assetId;
        public string name;
        public TunnelAssetCatalog.Asset asset;
        public Button button;
        public TMP_Text label;
        public TMP_Text note;
    }

    SplatBox m_Box;
    Button m_VideoButton;
    TMP_Text m_VideoLabel;
    bool? m_VideoShownFor;
    readonly List<Row> m_Rows = new();
    TMP_Text m_Status;
    string m_ShownFor;
    string m_StatusFor;

    /// <summary>Its width (meters).</summary>
    public float width => m_Canvas != null ? m_Canvas.sizeDelta.x * m_Canvas.lossyScale.x : 0.16f;

    /// <summary>Makes a row for each portrait the box can show.</summary>
    public void Bind(SplatBox box)
    {
        m_Box = box;
        TunnelAssetCatalog.changed -= Rebuild;
        TunnelAssetCatalog.changed += Rebuild;
        Rebuild();
    }

    void OnDestroy() => TunnelAssetCatalog.changed -= Rebuild;

    void Rebuild()
    {
        if (this == null || m_Box == null)
            return;
        foreach (var row in m_Rows)
            if (row.button != null)
                Destroy(row.button.gameObject);
        m_Rows.Clear();

        // Built in first, then everything in the repository the app can show, not already built in.
        var library = m_Box.library;
        if (library != null)
            foreach (var splat in library.splats)
                if (splat != null && splat.prefab != null)
                    m_Rows.Add(new Row { assetId = string.IsNullOrEmpty(splat.assetId) ? splat.name : splat.assetId, name = splat.name, asset = TunnelAssetCatalog.Get(splat.assetId) });
        foreach (var asset in TunnelAssetCatalog.MenuAssets())
            if (m_Rows.All(r => r.assetId != asset.id))
                m_Rows.Add(new Row { assetId = asset.id, name = string.IsNullOrEmpty(asset.title) ? asset.id : asset.title, asset = asset });

        // Wide enough for the repository's titles.
        if (m_Canvas != null && m_Canvas.sizeDelta.x < m_Width)
        {
            m_Canvas.sizeDelta = new Vector2(m_Width, m_Canvas.sizeDelta.y);
            foreach (var rect in new[] { m_RowTemplate != null ? (RectTransform)m_RowTemplate.transform : null, m_Title != null ? m_Title.rectTransform : null })
                if (rect != null)
                    rect.sizeDelta = new Vector2(m_Width - 40f, rect.sizeDelta.y);
        }

        if (m_RowTemplate != null)
        {
            m_RowTemplate.gameObject.SetActive(false);
            for (int i = 0; i < m_Rows.Count; i++)
            {
                var row = m_Rows[i];
                var button = Instantiate(m_RowTemplate, m_RowTemplate.transform.parent, false);
                button.name = $"Splat Row {row.assetId}";
                button.gameObject.SetActive(true);
                var rect = (RectTransform)button.transform;
                rect.anchoredPosition = new Vector2(0f, -(m_Top + m_RowHeight * (i + 0.5f)));
                string id = row.assetId;
                button.onClick.AddListener(() => m_Box?.Choose(id));
                row.button = button;
                row.label = button.GetComponentInChildren<TMP_Text>(true);
                if (row.label != null)
                {
                    row.label.textWrappingMode = TextWrappingModes.NoWrap;
                    row.label.overflowMode = TextOverflowModes.Ellipsis;
                }
                AddPicture(row, rect);
                AddNote(row);
            }
        }

        // Under the rows, a button that pauses and plays the box's video, shown while it has one.
        if (m_VideoButton == null && m_RowTemplate != null)
        {
            m_VideoButton = Instantiate(m_RowTemplate, m_RowTemplate.transform.parent, false);
            m_VideoButton.name = "Video Play Pause";
            m_VideoButton.onClick.AddListener(() => m_Box?.SetVideoPaused(!m_Box.videoPaused));
            m_VideoLabel = m_VideoButton.GetComponentInChildren<TMP_Text>(true);
        }
        if (m_VideoButton != null)
        {
            var rect = (RectTransform)m_VideoButton.transform;
            rect.anchoredPosition = new Vector2(0f, -(m_Top + m_RowHeight * (m_Rows.Count + 0.5f)));
            m_VideoButton.gameObject.SetActive(false);
        }

        if (m_Status == null && m_Title != null)
        {
            m_Status = Instantiate(m_Title, m_Title.transform.parent, false);
            m_Status.name = "Status";
            m_Status.fontSize = m_Title.fontSize * 0.55f;
            m_Status.enableAutoSizing = false;
            m_Status.textWrappingMode = TextWrappingModes.Normal;
            m_Status.overflowMode = TextOverflowModes.Ellipsis;
            m_Status.alignment = TextAlignmentOptions.TopLeft;
        }
        if (m_Status != null)
        {
            var rect = m_Status.rectTransform;
            rect.anchoredPosition = new Vector2(0f, -(m_Top + m_RowHeight * (m_Rows.Count + 1) + 6f));
            rect.sizeDelta = new Vector2(rect.sizeDelta.x, m_StatusHeight);
        }

        if (m_Canvas != null)
            m_Canvas.sizeDelta = new Vector2(m_Canvas.sizeDelta.x, m_Top + m_RowHeight * (Mathf.Max(m_Rows.Count, 1) + 1) + m_StatusHeight + m_Bottom);
        if (m_Title != null)
            m_Title.text = m_Rows.Count <= 1 ? "Splats  <color=#AAA>(is the headset online?)</color>" : "Splats and assets";
        m_ShownFor = null;
        m_StatusFor = null;
        Refresh();
    }

    /// <summary>The asset's picture at the left of its row, the label moved over to make room.</summary>
    void AddPicture(Row row, RectTransform rowRect)
    {
        float size = m_RowHeight - 14f;
        var pictureObject = new GameObject("Picture", typeof(RectTransform));
        pictureObject.layer = rowRect.gameObject.layer;
        var picture = pictureObject.AddComponent<RawImage>();
        picture.raycastTarget = false;
        picture.color = new Color(1f, 1f, 1f, 0.15f);
        var rect = picture.rectTransform;
        rect.SetParent(rowRect, false);
        rect.anchorMin = rect.anchorMax = new Vector2(0f, 0.5f);
        rect.pivot = new Vector2(0f, 0.5f);
        rect.anchoredPosition = new Vector2(8f, 0f);
        rect.sizeDelta = new Vector2(size, size);
        if (row.label != null)
        {
            var label = row.label.rectTransform;
            label.offsetMin = new Vector2(label.offsetMin.x + size + 12f, label.offsetMin.y);
        }
        _ = LoadPicture(row, picture);
    }

    /// <summary>A small line under the row's name, for its download size and a warning when it is heavy.</summary>
    static void AddNote(Row row)
    {
        if (row.label == null)
            return;
        var note = Instantiate(row.label, row.label.transform.parent, false);
        note.name = "Note";
        note.text = "";
        note.enableAutoSizing = false;
        note.fontSize = row.label.fontSize * 0.55f;
        note.alignment = TextAlignmentOptions.BottomLeft;
        note.margin = new Vector4(row.label.margin.x + 34f, 0f, 0f, 3f);
        note.raycastTarget = false;
        row.note = note;
    }

    static async Awaitable LoadPicture(Row row, RawImage picture)
    {
        var texture = await TunnelAssets.Thumbnail(row.asset);
        if (texture == null || picture == null)
            return;
        picture.texture = texture;
        picture.color = Color.white;
        // Its own proportions, within the square.
        var rect = picture.rectTransform;
        float side = rect.sizeDelta.y;
        float aspect = texture.width / (float)Mathf.Max(1, texture.height);
        rect.sizeDelta = aspect >= 1f ? new Vector2(side, side / aspect) : new Vector2(side * aspect, side);
    }

    void Update() => Refresh();

    // ■ for the portrait shown, □ for the others; under the rows, how loading is going and the credits.
    void Refresh()
    {
        if (m_Box == null)
            return;
        string shown = m_Box.assetId;
        string status = string.IsNullOrEmpty(m_Box.status) ? m_Box.message : m_Box.status;
        if (!string.IsNullOrEmpty(m_Box.attribution))
            status = (string.IsNullOrEmpty(status) ? "" : status + "\n") + $"<color=#CCC>{m_Box.attribution}</color>";
        if (status != m_StatusFor)
        {
            m_StatusFor = status;
            m_ShownFor = null; // a download may have finished: its size note goes
            if (m_Status != null)
                m_Status.text = status;
        }

        // The video button: shown while the box has a video, saying what pressing it does.
        bool? video = m_Box.hasVideo ? m_Box.videoPaused : null;
        if (video != m_VideoShownFor && m_VideoButton != null)
        {
            m_VideoShownFor = video;
            m_VideoButton.gameObject.SetActive(video.HasValue);
            if (m_VideoLabel != null && video.HasValue)
                m_VideoLabel.text = video.Value ? "▶  Play the video, for everyone" : "❚❚  Pause the video, for everyone";
        }

        if (shown != m_ShownFor)
        {
            m_ShownFor = shown;
            foreach (var row in m_Rows)
            {
                if (row.label == null)
                    continue;
                string note = Note(row);
                row.label.text = (row.assetId == shown ? "■  " : "□  ") + row.name;
                // With a note under it, the name moves up to make room.
                var margin = row.label.margin;
                row.label.margin = new Vector4(margin.x, margin.y, margin.z, note.Length > 0 ? 16f : 0f);
                if (row.note != null)
                    row.note.text = note;
            }
        }
    }

    // How large its first download is, and a warning for meshes too large to draw smoothly, on a small second line.
    static string Note(Row row)
    {
        var asset = row.asset;
        if (asset == null || TunnelAssets.BuiltIn(row.assetId) != null)
            return "";
        var notes = new List<string>();
        switch (asset.kind)
        {
            case "image": notes.Add(asset.isSurround ? "360 image" : $"image, {asset.width} x {asset.height}"); break;
            case "video": notes.Add($"{(asset.isSurround ? "360 video" : "video")}, {asset.duration:F0} s{(asset.height > 0 ? $", {asset.height}p" : "")}"); break;
            case "molecule": notes.Add(asset.atoms > 0 ? $"molecule, {asset.atoms:N0} atoms" : "molecule"); break;
            case "virus": notes.Add(asset.instances > 0 ? $"assembly of {asset.instances} proteins" : "virus"); break;
        }
        if (asset.isHeavy)
            notes.Add($"<color=#FFB347>{asset.faces / 1e6f:F1}M triangles: may slow the headset</color>");
        if (asset.kind == "video" && asset.bytes > TunnelAssetSettings.current.cacheVideosUpTo)
            notes.Add("streams");
        else if (!TunnelAssets.IsCached(asset) && asset.bytes > 0)
            notes.Add(asset.bytes < 1_000_000 ? "under 1 MB to download" : $"{asset.bytes / 1e6f:F0} MB to download");
        return notes.Count > 0 ? $"<color=#BBB>{string.Join(", ", notes)}</color>" : "";
    }

#if UNITY_EDITOR
    /// <summary>Set by the builder (Tunnel > Containers > Build Containers).</summary>
    public void Configure(RectTransform canvas, Button rowTemplate, TMP_Text title)
    {
        m_Canvas = canvas;
        m_RowTemplate = rowTemplate;
        m_Title = title;
    }
#endif
}
