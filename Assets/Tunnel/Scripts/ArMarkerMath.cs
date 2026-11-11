using System;
using UnityEngine;

/// <summary>
/// The geometry behind the AR marker cube (<see cref="ArMarkerCube"/>): artoolkitX's camera and stereo calibration
/// files made from the passthrough cameras' lenses, and conversions between artoolkitX's poses and Unity's.
/// Plain functions, so they are tested in the Editor with rendered images of the marker.
///
/// Frames used here:
/// - a camera's Unity frame ("UCam"): x right, y up, z forward into the scene (Unity's left-handed axes);
/// - artoolkitX's camera frame (OpenCV): x right, y down, z forward; image rows run from the top;
/// - artoolkitX's returned poses (OpenGL): x right, y up, z backward;
/// - the marker's Unity frame: x right and y up across the printed square as seen from the front, z into the
///   paper (so the marker's face, the cube's up, is local -z).
/// </summary>
public static class ArMarkerMath
{
    /// <summary>A pinhole camera: image size and lens in pixels (principal point measured from the image's top left).</summary>
    [Serializable]
    public struct CameraModel
    {
        public int width, height;
        public float fx, fy, cx, cy;

        public CameraModel(int width, int height, float fx, float fy, float cx, float cy)
        {
            this.width = width;
            this.height = height;
            this.fx = fx;
            this.fy = fy;
            this.cx = cx;
            this.cy = cy;
        }

        /// <summary>This lens for an image of another size, taken as a scaled, centred crop of this one.</summary>
        public CameraModel ForImage(int imageWidth, int imageHeight)
        {
            if (imageWidth == width && imageHeight == height)
                return this;
            float scale = Mathf.Min(width / (float)imageWidth, height / (float)imageHeight);
            float offsetX = (width - imageWidth * scale) * 0.5f, offsetY = (height - imageHeight * scale) * 0.5f;
            return new CameraModel(imageWidth, imageHeight, fx / scale, fy / scale, (cx - offsetX) / scale, (cy - offsetY) / scale);
        }

        public override string ToString() => $"{width}x{height} f=({fx:F1},{fy:F1}) c=({cx:F1},{cy:F1})";
    }

    public static Pose Mul(Pose a, Pose b) => new(a.position + a.rotation * b.position, a.rotation * b.rotation);

    public static Pose Inverse(Pose a)
    {
        var inverse = Quaternion.Inverse(a.rotation);
        return new Pose(inverse * -a.position, inverse);
    }

    public static Vector3 Apply(Pose a, Vector3 point) => a.position + a.rotation * point;

    #region artoolkitX calibration files

    /// <summary>
    /// artoolkitX's camera parameter file (camera_para.dat, distortion version 4: 176 bytes, big-endian) for a lens
    /// without distortion (the Quest's passthrough images arrive undistorted).
    /// </summary>
    public static byte[] CameraParamFile(CameraModel camera)
    {
        var bytes = new byte[176];
        int at = 0;
        PutInt(bytes, ref at, camera.width);
        PutInt(bytes, ref at, camera.height);
        double[] mat =
        {
            camera.fx, 0, camera.cx, 0,
            0, camera.fy, camera.cy, 0,
            0, 0, 1, 0,
        };
        foreach (double value in mat)
            PutDouble(bytes, ref at, value);
        // OpenCV model: k1, k2, p1, p2, fx, fy, cx, cy, and a scale of 1.
        double[] distortion = { 0, 0, 0, 0, camera.fx, camera.fy, camera.cx, camera.cy, 1 };
        foreach (double value in distortion)
            PutDouble(bytes, ref at, value);
        return bytes;
    }

    /// <summary>
    /// artoolkitX's stereo calibration (transL2R.dat: a 3x4 matrix of big-endian doubles, millimetres) from where
    /// the right camera sees the left camera's frame (<paramref name="rightFromLeft"/>, Unity camera frames, metres).
    /// </summary>
    public static byte[] TransL2RFile(Pose rightFromLeft)
    {
        var m = Matrix4x4.Rotate(rightFromLeft.rotation);
        var bytes = new byte[96];
        int at = 0;
        Vector3 t = rightFromLeft.position * 1000f;
        // OpenCV axes are Unity camera axes with y flipped: R_cv = F R F, t_cv = F t.
        float[] f = { 1f, -1f, 1f };
        for (int row = 0; row < 3; row++)
        {
            for (int col = 0; col < 3; col++)
                PutDouble(bytes, ref at, f[row] * f[col] * m[row, col]);
            PutDouble(bytes, ref at, f[row] * t[row]);
        }
        return bytes;
    }

    /// <summary>How the right camera's frame sees the left camera's, from both lenses' poses on the head.</summary>
    public static Pose RightFromLeft(Pose headFromLeft, Pose headFromRight) => Mul(Inverse(headFromRight), headFromLeft);

    static void PutInt(byte[] bytes, ref int at, int value)
    {
        bytes[at++] = (byte)(value >> 24);
        bytes[at++] = (byte)(value >> 16);
        bytes[at++] = (byte)(value >> 8);
        bytes[at++] = (byte)value;
    }

    static void PutDouble(byte[] bytes, ref int at, double value)
    {
        long bits = BitConverter.DoubleToInt64Bits(value);
        for (int shift = 56; shift >= 0; shift -= 8)
            bytes[at++] = (byte)(bits >> shift);
    }

    #endregion

    #region Poses

    /// <summary>
    /// The marker's pose in the camera's Unity frame (metres) from artoolkitX's OpenGL matrix (column-major,
    /// millimetres when the marker's width was given in millimetres).
    /// </summary>
    public static Pose MarkerPoseFromGl(float[] gl, float unitsToMeters = 0.001f)
    {
        // Unity = S * M * S with S = diag(1, 1, -1): the camera's z and the marker's z both flip.
        Vector3 Column(int c) => new(gl[c * 4 + 0], gl[c * 4 + 1], -gl[c * 4 + 2]);
        var yAxis = Column(1);
        var zAxis = -Column(2);
        var position = new Vector3(gl[12], gl[13], -gl[14]) * unitsToMeters;
        if (zAxis.sqrMagnitude < 1e-12f || yAxis.sqrMagnitude < 1e-12f)
            return new Pose(position, Quaternion.identity);
        return new Pose(position, Quaternion.LookRotation(zAxis, yAxis));
    }

    /// <summary>The cube's pose from the marker's: centred on it, its up (y) out of the paper.</summary>
    public static Pose CubeFromMarker(Pose marker) => new(marker.position, marker.rotation * Quaternion.Euler(-90f, 0f, 0f));

    /// <summary>The marker's pose from the cube's (the inverse of <see cref="CubeFromMarker"/>).</summary>
    public static Pose MarkerFromCube(Pose cube) => new(cube.position, cube.rotation * Quaternion.Euler(90f, 0f, 0f));

    /// <summary>Where a point in a camera's Unity frame lands in its image (pixels from the top left); false if behind it.</summary>
    public static bool Project(CameraModel camera, Vector3 pointInCamera, out Vector2 pixel)
    {
        pixel = default;
        if (pointInCamera.z < 0.01f)
            return false;
        pixel = new Vector2(camera.fx * pointInCamera.x / pointInCamera.z + camera.cx,
            -camera.fy * pointInCamera.y / pointInCamera.z + camera.cy);
        return true;
    }

    /// <summary>The ray through a pixel (from the top left), in the camera's Unity frame.</summary>
    public static Vector3 PixelRay(CameraModel camera, Vector2 pixel) =>
        new((pixel.x - camera.cx) / camera.fx, -(pixel.y - camera.cy) / camera.fy, 1f);

    #endregion

    #region Checking an image

    /// <summary>What one camera's image shows of the marker at a given pose.</summary>
    public struct ImageCheck
    {
        /// <summary>The whole marker is in the image.</summary>
        public bool inView;
        /// <summary>Fraction (0-1) of points along its outline where the black border is darker than the white margin.</summary>
        public float edgeMatch;
        /// <summary>The marker's mean edge length in the image (pixels).</summary>
        public float edgePixels;
        /// <summary>How squarely it faces the camera (1 facing, 0 edge-on).</summary>
        public float facing;

        public bool seen => inView && edgeMatch >= 0.7f;
    }

    /// <summary>
    /// Checks a camera's image (8-bit luminance, rows from the top) against the marker's pose in that camera: does its
    /// black border sit where the pose puts it? Used to tell which cameras actually show the marker.
    /// </summary>
    public static ImageCheck CheckImage(CameraModel camera, byte[] luminance, Pose markerInCamera, float markerWidth)
    {
        var check = new ImageCheck();
        float half = markerWidth * 0.5f;
        Vector3[] corners =
        {
            new(-half, half, 0f), new(half, half, 0f), new(half, -half, 0f), new(-half, -half, 0f),
        };
        var pixels = new Vector2[4];
        bool inFront = true;
        for (int i = 0; i < 4; i++)
            inFront &= Project(camera, Apply(markerInCamera, corners[i]), out pixels[i]);
        for (int i = 0; i < 4; i++)
            check.edgePixels += Vector2.Distance(pixels[i], pixels[(i + 1) % 4]) * 0.25f;
        // The whole square and a little of the white paper round it must be in the image for artoolkitX to find it.
        float margin = Mathf.Max(3f, check.edgePixels * 0.2f);
        check.inView = inFront;
        for (int i = 0; i < 4; i++)
        {
            if (pixels[i].x < margin || pixels[i].y < margin || pixels[i].x > camera.width - 1 - margin || pixels[i].y > camera.height - 1 - margin)
                check.inView = false;
        }

        var toCamera = -markerInCamera.position.normalized;
        var face = markerInCamera.rotation * Vector3.back; // the printed side
        check.facing = Mathf.Clamp01(Vector3.Dot(face, toCamera));

        if (luminance == null || luminance.Length < camera.width * camera.height)
            return check;

        // Points along each edge: one inside the black border (an eighth of the width in), one in the white margin
        // (an eighth out).
        int samples = 0, matches = 0;
        float step = markerWidth / 8f;
        for (int edge = 0; edge < 4; edge++)
        {
            var a = corners[edge];
            var b = corners[(edge + 1) % 4];
            var outward = ((a + b) * 0.5f).normalized;
            for (int k = 1; k <= 8; k++)
            {
                var onEdge = Vector3.Lerp(a, b, k / 9f);
                if (!Sample(camera, luminance, Apply(markerInCamera, onEdge - outward * step), out int inside) ||
                    !Sample(camera, luminance, Apply(markerInCamera, onEdge + outward * step), out int outside))
                    continue;
                samples++;
                if (outside - inside > 25)
                    matches++;
            }
        }
        check.edgeMatch = samples == 0 ? 0f : matches / 32f;
        return check;
    }

    static bool Sample(CameraModel camera, byte[] luminance, Vector3 point, out int value)
    {
        value = 0;
        if (!Project(camera, point, out var pixel))
            return false;
        int x = Mathf.RoundToInt(pixel.x), y = Mathf.RoundToInt(pixel.y);
        if (x < 0 || y < 0 || x >= camera.width || y >= camera.height)
            return false;
        value = luminance[y * camera.width + x];
        return true;
    }

    /// <summary>
    /// How much to trust one headset's sighting (0-1): artoolkitX's confidence, how large and how squarely the marker
    /// appears, how well its outline matches the image, and whether both cameras show it.
    /// </summary>
    public static float Quality(float confidence, ImageCheck left, ImageCheck right)
    {
        bool l = left.seen, r = right.seen;
        if (!l && !r)
            return 0f;
        float pixels = l && r ? (left.edgePixels + right.edgePixels) * 0.5f : l ? left.edgePixels : right.edgePixels;
        float facing = l && r ? Mathf.Max(left.facing, right.facing) : l ? left.facing : right.facing;
        float match = l && r ? (left.edgeMatch + right.edgeMatch) * 0.5f : l ? left.edgeMatch : right.edgeMatch;
        float size = Mathf.Clamp01((pixels - 20f) / 80f);
        float angle = Mathf.Clamp01((facing - 0.15f) / 0.55f);
        float stereo = l && r ? 1f : 0.6f;
        return Mathf.Clamp01(Mathf.Clamp01(confidence) * Mathf.Sqrt(size) * angle * match * stereo);
    }

    #endregion
}
