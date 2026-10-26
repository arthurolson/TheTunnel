using System.IO;
using System.Xml;
using UnityEditor.Android;

/// <summary>
/// Lets the app read the Quest passthrough cameras (for the floor QR code): adds the camera permissions Meta's
/// Passthrough Camera API needs, and marks Android's camera features optional so the store doesn't treat the camera
/// as a hard requirement.
/// https://developers.meta.com/horizon/documentation/unity/unity-pca-overview/
/// </summary>
class PassthroughCameraManifest : IPostGenerateGradleAndroidProject
{
    const string k_AndroidNamespace = "http://schemas.android.com/apk/res/android";
    static readonly string[] k_Permissions = { "android.permission.CAMERA", "horizonos.permission.HEADSET_CAMERA" };

    public int callbackOrder => 101;

    public void OnPostGenerateGradleAndroidProject(string unityLibraryPath)
    {
        string manifestPath = Path.Combine(unityLibraryPath, "src", "main", "AndroidManifest.xml");
        var doc = new XmlDocument();
        doc.Load(manifestPath);
        XmlElement manifest = doc.DocumentElement;

        foreach (string permission in k_Permissions)
        {
            if (!HasElement(manifest, "uses-permission", permission))
                manifest.AppendChild(CreateElement(doc, "uses-permission", permission));
        }

        foreach (XmlNode node in manifest.ChildNodes)
        {
            if (node is XmlElement element && element.Name == "uses-feature" &&
                element.GetAttribute("name", k_AndroidNamespace).StartsWith("android.hardware.camera"))
                element.SetAttribute("required", k_AndroidNamespace, "false");
        }

        doc.Save(manifestPath);
    }

    static bool HasElement(XmlElement manifest, string tag, string name)
    {
        foreach (XmlNode node in manifest.ChildNodes)
        {
            if (node is XmlElement element && element.Name == tag && element.GetAttribute("name", k_AndroidNamespace) == name)
                return true;
        }

        return false;
    }

    static XmlElement CreateElement(XmlDocument doc, string tag, string name)
    {
        XmlElement element = doc.CreateElement(tag);
        XmlAttribute attribute = doc.CreateAttribute("android", "name", k_AndroidNamespace);
        attribute.Value = name;
        element.Attributes.Append(attribute);
        return element;
    }
}
