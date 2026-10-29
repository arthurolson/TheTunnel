using System.IO;
using System.Xml;
using UnityEditor.Android;

/// <summary>
/// Lets the Viewer list the Quest's own photos and videos (screenshots, recordings, Pictures, Movies, DCIM,
/// Download): adds Android's read-photos and read-videos permissions. The app asks for them the first time a Viewer
/// list is opened.
/// </summary>
class ViewerMediaManifest : IPostGenerateGradleAndroidProject
{
    const string k_AndroidNamespace = "http://schemas.android.com/apk/res/android";
    static readonly string[] k_Permissions = { "android.permission.READ_MEDIA_IMAGES", "android.permission.READ_MEDIA_VIDEO" };

    public int callbackOrder => 102;

    public void OnPostGenerateGradleAndroidProject(string unityLibraryPath)
    {
        string manifestPath = Path.Combine(unityLibraryPath, "src", "main", "AndroidManifest.xml");
        var doc = new XmlDocument();
        doc.Load(manifestPath);
        XmlElement manifest = doc.DocumentElement;

        foreach (string permission in k_Permissions)
        {
            bool present = false;
            foreach (XmlNode node in manifest.ChildNodes)
                present |= node is XmlElement e && e.Name == "uses-permission" && e.GetAttribute("name", k_AndroidNamespace) == permission;
            if (present)
                continue;

            XmlElement element = doc.CreateElement("uses-permission");
            XmlAttribute attribute = doc.CreateAttribute("android", "name", k_AndroidNamespace);
            attribute.Value = permission;
            element.Attributes.Append(attribute);
            manifest.AppendChild(element);
        }

        doc.Save(manifestPath);
    }
}
