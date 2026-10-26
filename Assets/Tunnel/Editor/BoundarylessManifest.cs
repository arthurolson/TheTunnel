using System.IO;
using System.Xml;
using UnityEditor.Android;

/// <summary>
/// Marks the Android build as a Meta Quest boundaryless app: it launches and runs without a boundary (no setup
/// prompt, no "return to your boundary" warnings). Meta only allows this for apps that are entirely mixed reality.
/// https://developers.meta.com/horizon/documentation/unity/unity-boundaryless/
/// </summary>
class BoundarylessManifest : IPostGenerateGradleAndroidProject
{
    const string k_AndroidNamespace = "http://schemas.android.com/apk/res/android";
    const string k_Feature = "com.oculus.feature.BOUNDARYLESS_APP";

    public int callbackOrder => 100;

    public void OnPostGenerateGradleAndroidProject(string unityLibraryPath)
    {
        string manifestPath = Path.Combine(unityLibraryPath, "src", "main", "AndroidManifest.xml");
        var doc = new XmlDocument();
        doc.Load(manifestPath);
        XmlElement manifest = doc.DocumentElement;

        foreach (XmlNode node in manifest.ChildNodes)
        {
            if (node is XmlElement element && element.Name == "uses-feature" &&
                element.GetAttribute("name", k_AndroidNamespace) == k_Feature)
                return;
        }

        XmlElement feature = doc.CreateElement("uses-feature");
        XmlAttribute name = doc.CreateAttribute("android", "name", k_AndroidNamespace);
        name.Value = k_Feature;
        feature.Attributes.Append(name);
        XmlAttribute required = doc.CreateAttribute("android", "required", k_AndroidNamespace);
        required.Value = "true";
        feature.Attributes.Append(required);
        manifest.AppendChild(feature);

        doc.Save(manifestPath);
    }
}
