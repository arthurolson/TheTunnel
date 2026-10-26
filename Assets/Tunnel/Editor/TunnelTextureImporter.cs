using UnityEditor;
using UnityEngine;

/// <summary>
/// Import settings for the wall textures built into the app (Assets/Tunnel/Resources/TunnelTextures): tiling, full
/// mipmaps and anisotropic filtering for walls seen at grazing angles, at most 2048 px, and no rescaling to a power of
/// two so the image keeps its proportions on the wall. Drop a PNG, JPEG or TIFF into that folder and it's ready.
/// </summary>
class TunnelTextureImporter : AssetPostprocessor
{
    const string k_Folder = "Assets/Tunnel/Resources/TunnelTextures/";

    void OnPreprocessTexture()
    {
        if (!assetPath.StartsWith(k_Folder))
            return;

        var importer = (TextureImporter)assetImporter;
        importer.textureType = TextureImporterType.Default;
        importer.sRGBTexture = true;
        importer.npotScale = TextureImporterNPOTScale.None;
        importer.mipmapEnabled = true;
        importer.wrapMode = TextureWrapMode.Repeat;
        importer.filterMode = FilterMode.Trilinear;
        importer.anisoLevel = 4;
        importer.maxTextureSize = 2048;
        importer.textureCompression = TextureImporterCompression.CompressedHQ;
    }
}
