using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Android;

/// <summary>
/// Reads Quest 3 / 3S passthrough camera images (Meta's Passthrough Camera API, Horizon OS v74+) through Unity's
/// WebCamTexture, and knows the camera's lens (intrinsics) and where it sits on the headset, so a pixel can be turned
/// into a ray in the world. Follows Meta's PassthroughCameraApiSamples (PassthroughCameraUtils). Android only.
/// </summary>
public class PassthroughCameraReader : MonoBehaviour
{
    public const string HeadsetCameraPermission = "horizonos.permission.HEADSET_CAMERA";
    const string k_CameraPermission = "android.permission.CAMERA";
    const string k_SourceKey = "com.meta.extra_metadata.camera_source";
    const string k_PositionKey = "com.meta.extra_metadata.position";

    [SerializeField]
    [Tooltip("Use the left passthrough camera (otherwise the right one).")]
    bool m_LeftCamera = true;

    WebCamTexture m_Texture;
    Vector2 m_FocalLength;
    Vector2 m_PrincipalPoint;
    Vector2Int m_SensorResolution;
    Pose m_HeadFromCamera;
    Color32[] m_Pixels;

    public bool isPlaying => m_Texture != null && m_Texture.isPlaying && m_Texture.width > 16;
    public int width => m_Texture != null ? m_Texture.width : 0;
    public int height => m_Texture != null ? m_Texture.height : 0;
    public string error { get; private set; }

    public static bool hasPermission =>
        Application.platform != RuntimePlatform.Android ||
        (Permission.HasUserAuthorizedPermission(k_CameraPermission) && Permission.HasUserAuthorizedPermission(HeadsetCameraPermission));

    /// <summary>Asks for camera permission if needed, then starts the camera. Reports success through <paramref name="done"/>.</summary>
    public IEnumerator StartCamera(Action<bool> done)
    {
        error = null;
        if (Application.platform != RuntimePlatform.Android)
        {
            error = "The passthrough cameras only work on the headset.";
            done(false);
            yield break;
        }

        if (!hasPermission)
        {
            bool? answered = null;
            var callbacks = new PermissionCallbacks();
            callbacks.PermissionGranted += _ => answered = hasPermission ? true : answered;
            callbacks.PermissionDenied += _ => answered = false;
            callbacks.PermissionDeniedAndDontAskAgain += _ => answered = false;
            // Both in one request: Android allows only one permission request (and callback set) at a time.
            Permission.RequestUserPermissions(new[] { k_CameraPermission, HeadsetCameraPermission }, callbacks);
            float timeout = Time.unscaledTime + 60f;
            while (answered == null && !hasPermission && Time.unscaledTime < timeout)
                yield return null;
            if (!hasPermission)
            {
                error = "Camera permission was not given (Settings > Apps > TheTunnel > Permissions).";
                done(false);
                yield break;
            }
        }

        if (m_Texture == null)
        {
            if (!TryReadCameraInfo(out int deviceIndex))
            {
                done(false);
                yield break;
            }

            var devices = WebCamTexture.devices;
            if (deviceIndex >= devices.Length)
            {
                error = $"Passthrough camera {deviceIndex} isn't available ({devices.Length} cameras).";
                done(false);
                yield break;
            }

            m_Texture = new WebCamTexture(devices[deviceIndex].name, m_SensorResolution.x, m_SensorResolution.y);
        }

        m_Texture.Play();
        float started = Time.unscaledTime;
        while (!isPlaying && Time.unscaledTime - started < 5f)
            yield return null;
        if (!isPlaying)
            error = "The passthrough camera didn't start.";
        else
            Debug.Log($"[QR] camera {m_Texture.deviceName} {width}x{height}, sensor {m_SensorResolution}, f={m_FocalLength}, c={m_PrincipalPoint}, lens pose {m_HeadFromCamera.position} {m_HeadFromCamera.rotation.eulerAngles}");
        done(isPlaying);
    }

    public void Stop()
    {
        if (m_Texture != null && m_Texture.isPlaying)
            m_Texture.Stop();
    }

    void OnDestroy()
    {
        if (m_Texture != null)
        {
            m_Texture.Stop();
            Destroy(m_Texture);
        }
    }

    /// <summary>The current image (Unity order: the first row is the bottom of the image). Reuses one buffer.</summary>
    public Color32[] GetPixels()
    {
        if (!isPlaying)
            return null;
        if (m_Pixels == null || m_Pixels.Length != width * height)
            m_Pixels = new Color32[width * height];
        return m_Texture.GetPixels32(m_Pixels);
    }

    /// <summary>The camera's pose in the world for a given head (main camera) pose.</summary>
    public Pose GetCameraPose(Pose head)
    {
        var position = head.position + head.rotation * m_HeadFromCamera.position;
        var rotation = head.rotation * m_HeadFromCamera.rotation * Quaternion.Euler(180f, 0f, 0f);
        return new Pose(position, rotation);
    }

    /// <summary>
    /// World ray through an image pixel (x right, y down from the top-left of the texture). With
    /// <paramref name="flipY"/> the pixel's y is measured from the bottom instead (the camera's row order is checked
    /// at runtime, see <see cref="QrFloorScanner"/>).
    /// </summary>
    public Ray PixelToRay(Vector2 pixel, Pose cameraPose, bool flipY)
    {
        if (flipY)
            pixel.y = height - pixel.y;

        // The image may be a scaled, centred crop of the sensor's full area.
        float scale = Mathf.Min(m_SensorResolution.x / (float)width, m_SensorResolution.y / (float)height);
        var offset = new Vector2((m_SensorResolution.x - width * scale) * 0.5f, (m_SensorResolution.y - height * scale) * 0.5f);
        Vector2 sensor = offset + pixel * scale;

        var direction = new Vector3(
            (sensor.x - m_PrincipalPoint.x) / m_FocalLength.x,
            (sensor.y - m_PrincipalPoint.y) / m_FocalLength.y,
            1f);
        return new Ray(cameraPose.position, cameraPose.rotation * direction);
    }

    bool TryReadCameraInfo(out int deviceIndex)
    {
        deviceIndex = -1;
        try
        {
            using var playerClass = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = playerClass.GetStatic<AndroidJavaObject>("currentActivity");
            using var manager = activity.Call<AndroidJavaObject>("getSystemService", "camera");
            var ids = manager.Call<string[]>("getCameraIdList");
            int wantedPosition = m_LeftCamera ? 0 : 1;

            for (int i = 0; i < ids.Length; i++)
            {
                using var characteristics = manager.Call<AndroidJavaObject>("getCameraCharacteristics", ids[i]);
                int source = -1, position = -1;
                using (var keys = characteristics.Call<AndroidJavaObject>("getKeys"))
                {
                    int count = keys.Call<int>("size");
                    for (int k = 0; k < count; k++)
                    {
                        using var key = keys.Call<AndroidJavaObject>("get", k);
                        string name = key.Call<string>("getName");
                        // Meta's custom fields are one-element arrays.
                        if (string.Equals(name, k_SourceKey, StringComparison.OrdinalIgnoreCase))
                            source = FirstByte(characteristics, key);
                        else if (string.Equals(name, k_PositionKey, StringComparison.OrdinalIgnoreCase))
                            position = FirstByte(characteristics, key);
                    }
                }

                if (source != 0 || position != wantedPosition)
                    continue; // not a passthrough camera, or the other eye

                var intrinsics = Get<float[]>(characteristics, "LENS_INTRINSIC_CALIBRATION");
                using var sensorSize = Get<AndroidJavaObject>(characteristics, "SENSOR_INFO_PRE_CORRECTION_ACTIVE_ARRAY_SIZE");
                var translation = Get<float[]>(characteristics, "LENS_POSE_TRANSLATION");
                var rotation = Get<float[]>(characteristics, "LENS_POSE_ROTATION");

                m_FocalLength = new Vector2(intrinsics[0], intrinsics[1]);
                m_PrincipalPoint = new Vector2(intrinsics[2], intrinsics[3]);
                m_SensorResolution = new Vector2Int(sensorSize.Get<int>("right"), sensorSize.Get<int>("bottom"));
                // Android's lens pose to Unity's left-handed head space, as in Meta's PassthroughCameraUtils.
                var cameraFromHead = new Quaternion(-rotation[0], -rotation[1], rotation[2], rotation[3]);
                m_HeadFromCamera = new Pose(new Vector3(translation[0], translation[1], -translation[2]), Quaternion.Inverse(cameraFromHead));
                deviceIndex = i;
                return true;
            }

            error = "No passthrough camera found (Quest 3 or 3S with Horizon OS v74 or later is needed).";
        }
        catch (Exception e)
        {
            error = "Couldn't read the passthrough camera: " + e.Message;
        }

        return false;
    }

    static T Get<T>(AndroidJavaObject characteristics, string keyName)
    {
        using var key = characteristics.GetStatic<AndroidJavaObject>(keyName);
        return Get<T>(characteristics, key);
    }

    static T Get<T>(AndroidJavaObject characteristics, AndroidJavaObject key) => characteristics.Call<T>("get", key);

    static int FirstByte(AndroidJavaObject characteristics, AndroidJavaObject key)
    {
        var values = Get<sbyte[]>(characteristics, key);
        return values != null && values.Length == 1 ? values[0] : -1;
    }
}
