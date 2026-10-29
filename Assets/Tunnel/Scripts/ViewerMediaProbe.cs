using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// Reads a photo's or video's file header (without decoding it) to tell whether it is spatial (stereo) and how its
/// two views are laid out, so the Quest's own 3D captures land in the Spatial lists and show in 3D:
///  - Videos (MP4/MOV): the stereo tag 3D cameras and editors write (spherical-video "st3d" box, or the older
///    "StereoMode" XML), multi-view HEVC (MV-HEVC, Apple/Meta spatial video: "lhvC"/"vexu"), else a frame at least
///    twice as wide as 16:9 (two views side by side).
///  - Photos (JPEG): an MPO stereo pair (two pictures in one file, the second for the right eye), else a picture at
///    least twice as wide as 16:9. PNG: the same width test.
/// Results are cached by file and modification time.
/// </summary>
public static class ViewerMediaProbe
{
    public struct Info
    {
        public int width;
        public int height;
        public ViewerLibrary.StereoLayout layout;
        /// <summary>MPO: the right eye's picture is a second JPEG inside the file.</summary>
        public bool stereoPair;
        /// <summary>MV-HEVC: the second view is an extra layer the headset's video player can't decode.</summary>
        public bool multiView;

        public bool isSpatial => layout != ViewerLibrary.StereoLayout.Mono || stereoPair || multiView;
    }

    const int k_MaxMoovBytes = 32 * 1024 * 1024;
    const float k_DoubleWideAspect = 3.0f; // two 16:9 views side by side are 3.56; a 16:9 frame is 1.78

    static readonly Dictionary<string, (long modified, Info info)> s_Cache = new();

    public static Info Probe(string path)
    {
        try
        {
            long modified = File.GetLastWriteTimeUtc(path).Ticks;
            if (s_Cache.TryGetValue(path, out var cached) && cached.modified == modified)
                return cached.info;

            string extension = Path.GetExtension(path).ToLowerInvariant();
            Info info = extension switch
            {
                ".jpg" or ".jpeg" or ".mpo" => ProbeJpeg(path),
                ".png" => ProbePng(path),
                _ when Array.IndexOf(ViewerLibrary.VideoExtensions, extension) >= 0 => ProbeVideo(path),
                _ => default,
            };

            if (info.layout == ViewerLibrary.StereoLayout.Mono && !info.stereoPair && info.height > 0
                && (float)info.width / info.height >= k_DoubleWideAspect)
                info.layout = ViewerLibrary.StereoLayout.SideBySide;

            s_Cache[path] = (modified, info);
            return info;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Viewer] can't read {path}: {e.Message}");
            return default;
        }
    }

    #region Video

    static Info ProbeVideo(string path)
    {
        var info = new Info();
        using var stream = File.OpenRead(path);
        byte[] moov = FindTopLevelBox(stream, "moov", k_MaxMoovBytes);
        if (moov == null)
            return info;

        // Video size: the largest track header (sound tracks are 0 x 0).
        foreach (int at in FindAll(moov, "tkhd"))
        {
            int payload = at; // version (1), flags (3), then the fields
            if (payload >= moov.Length)
                continue;
            int offset = payload + (moov[payload] == 1 ? 88 : 76);
            if (offset + 8 > moov.Length)
                continue;
            int width = BigEndian(moov, offset) >> 16;
            int height = BigEndian(moov, offset + 4) >> 16;
            if (width * height > info.width * info.height)
            {
                info.width = width;
                info.height = height;
            }
        }

        // Spherical video V2: st3d = version/flags (4), stereo_mode (1): 1 top-bottom, 2 left-right.
        foreach (int at in FindAll(moov, "st3d"))
        {
            int mode = at + 4 < moov.Length ? moov[at + 4] : 0;
            if (mode == 1)
                info.layout = ViewerLibrary.StereoLayout.TopBottom;
            else if (mode == 2)
                info.layout = ViewerLibrary.StereoLayout.SideBySide;
        }

        if (info.layout == ViewerLibrary.StereoLayout.Mono)
        {
            string text = Encoding.ASCII.GetString(moov);
            if (text.Contains("StereoMode>left-right") || text.Contains("StereoMode>right-left"))
                info.layout = ViewerLibrary.StereoLayout.SideBySide;
            else if (text.Contains("StereoMode>top-bottom"))
                info.layout = ViewerLibrary.StereoLayout.TopBottom;
        }

        info.multiView = FindAll(moov, "lhvC").Count > 0 || FindAll(moov, "vexu").Count > 0;
        return info;
    }

    /// <summary>The payload of a top-level box (moov is at the start or the end of the file).</summary>
    static byte[] FindTopLevelBox(Stream stream, string type, int maxBytes)
    {
        var header = new byte[16];
        long position = 0;
        while (position + 8 <= stream.Length)
        {
            stream.Position = position;
            if (stream.Read(header, 0, 8) < 8)
                return null;
            long size = (uint)BigEndian(header, 0);
            string boxType = Encoding.ASCII.GetString(header, 4, 4);
            int headerSize = 8;
            if (size == 1)
            {
                if (stream.Read(header, 8, 8) < 8)
                    return null;
                size = ((long)(uint)BigEndian(header, 8) << 32) | (uint)BigEndian(header, 12);
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = stream.Length - position;
            }

            if (size < headerSize)
                return null;
            if (boxType == type)
            {
                long length = Math.Min(size - headerSize, maxBytes);
                var payload = new byte[length];
                stream.Position = position + headerSize;
                int read = 0;
                while (read < length)
                {
                    int n = stream.Read(payload, read, (int)(length - read));
                    if (n <= 0)
                        break;
                    read += n;
                }

                return payload;
            }

            position += size;
        }

        return null;
    }

    #endregion

    #region Photos

    static Info ProbePng(string path)
    {
        var info = new Info();
        var header = new byte[24];
        using var stream = File.OpenRead(path);
        if (stream.Read(header, 0, 24) == 24 && header[1] == 'P' && header[2] == 'N' && header[3] == 'G')
        {
            info.width = BigEndian(header, 16);
            info.height = BigEndian(header, 20);
        }

        return info;
    }

    static Info ProbeJpeg(string path)
    {
        var info = new Info();
        byte[] data = File.ReadAllBytes(path);
        info.stereoPair = TryGetStereoPair(data, out _, out _);
        int at = 2;
        while (at + 4 <= data.Length && data[at] == 0xFF)
        {
            byte marker = data[at + 1];
            int length = (data[at + 2] << 8) | data[at + 3];
            // Start of frame (baseline, progressive, ...): height then width.
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC && at + 9 <= data.Length)
            {
                info.height = (data[at + 5] << 8) | data[at + 6];
                info.width = (data[at + 7] << 8) | data[at + 8];
                break;
            }

            if (marker == 0xDA)
                break;
            at += 2 + length;
        }

        return info;
    }

    /// <summary>
    /// An MPO stereo photo's two pictures (left, then right), found through its multi-picture index (APP2 "MPF").
    /// </summary>
    public static bool TryGetStereoPair(byte[] data, out ArraySegment<byte> left, out ArraySegment<byte> right)
    {
        left = right = default;
        int at = 2;
        while (at + 4 <= data.Length && data[at] == 0xFF && data[at + 1] != 0xDA)
        {
            int length = (data[at + 2] << 8) | data[at + 3];
            if (data[at + 1] == 0xE2 && at + 8 <= data.Length && data[at + 4] == 'M' && data[at + 5] == 'P' && data[at + 6] == 'F' && data[at + 7] == 0)
                return ReadMpIndex(data, at + 8, out left, out right);
            at += 2 + length;
        }

        return false;
    }

    static bool ReadMpIndex(byte[] data, int tiff, out ArraySegment<byte> left, out ArraySegment<byte> right)
    {
        left = right = default;
        if (tiff + 8 > data.Length)
            return false;
        bool little = data[tiff] == 'I';
        int Read32(int i) => little ? data[i] | (data[i + 1] << 8) | (data[i + 2] << 16) | (data[i + 3] << 24) : BigEndian(data, i);
        int Read16(int i) => little ? data[i] | (data[i + 1] << 8) : (data[i] << 8) | data[i + 1];

        int ifd = tiff + Read32(tiff + 4);
        if (ifd + 2 > data.Length)
            return false;
        int count = 0, entriesOffset = -1;
        int tags = Read16(ifd);
        for (int t = 0; t < tags; t++)
        {
            int entry = ifd + 2 + t * 12;
            if (entry + 12 > data.Length)
                return false;
            int tag = Read16(entry);
            if (tag == 0xB001)
                count = Read32(entry + 8);
            else if (tag == 0xB002)
                entriesOffset = tiff + Read32(entry + 8);
        }

        if (count < 2 || entriesOffset < 0 || entriesOffset + 32 > data.Length)
            return false;

        // Each entry: attribute (4), size (4), offset from the MPF header (4; 0 for the first picture), 2 x 2.
        int firstSize = Read32(entriesOffset + 4);
        int secondSize = Read32(entriesOffset + 16 + 4);
        int secondOffset = tiff + Read32(entriesOffset + 16 + 8);
        int secondType = Read32(entriesOffset + 16) & 0xFFFFFF;
        // 0x020002 = disparity (stereo) image; some cameras leave the type undefined (0).
        if (secondType != 0x020002 && secondType != 0)
            return false;
        if (firstSize <= 0 || firstSize > data.Length || secondSize <= 0 || secondOffset + secondSize > data.Length)
            return false;

        left = new ArraySegment<byte>(data, 0, firstSize);
        right = new ArraySegment<byte>(data, secondOffset, secondSize);
        return true;
    }

    #endregion

    static int BigEndian(byte[] data, int i) => (data[i] << 24) | (data[i + 1] << 16) | (data[i + 2] << 8) | data[i + 3];

    static List<int> FindAll(byte[] data, string type)
    {
        var hits = new List<int>();
        byte a = (byte)type[0], b = (byte)type[1], c = (byte)type[2], d = (byte)type[3];
        for (int i = 4; i + 4 <= data.Length; i++)
        {
            if (data[i] == a && data[i + 1] == b && data[i + 2] == c && data[i + 3] == d)
                hits.Add(i + 4); // payload starts after the type
        }

        return hits;
    }
}
