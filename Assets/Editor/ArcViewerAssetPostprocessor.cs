// ==============================================================================
//  ArcViewerAssetPostprocessor - mobile-friendly import of the adopted
//  ArcViewer environment assets (PICO/Android).
// ==============================================================================
//  Best practice for Android/XR: textures compressed (ASTC 6x6) and capped in
//  size -> less memory & bandwidth (smoother, fewer hitches). Affects ONLY
//  Assets/ArcViewerEnvironment/Textures/** (our environment textures), not the
//  rest of the project. `GetVersion` forces a one-time re-import round when
//  this file changes.
//  Context: architecture see PROJECT_NOTES.md (project root).
// ==============================================================================
using UnityEditor;
using UnityEngine;

namespace QuestSaber.EditorTools
{
    public sealed class ArcViewerAssetPostprocessor : AssetPostprocessor
    {
        private const string TargetFolder = "Assets/ArcViewerEnvironment/Textures/";
        private const int MaxSize = 1024;

        /// <summary>Forces a re-import when the postprocessor changes.</summary>
        public override uint GetVersion() => 2;

        private void OnPreprocessTexture()
        {
            string path = assetPath.Replace('\\', '/');
            if (!path.StartsWith(TargetFolder, System.StringComparison.Ordinal)) return;

            var importer = (TextureImporter)assetImporter;
            importer.textureCompression = TextureImporterCompression.Compressed;
            importer.maxTextureSize = Mathf.Min(importer.maxTextureSize, MaxSize);
            importer.mipmapEnabled = true;
            importer.anisoLevel = 1;

            // Android: ASTC 6x6 (Adreno/PICO supports ASTC) - significantly smaller textures.
            var android = importer.GetPlatformTextureSettings("Android");
            android.overridden = true;
            android.maxTextureSize = MaxSize;
            android.format = TextureImporterFormat.ASTC_6x6;
            android.textureCompression = TextureImporterCompression.Compressed;
            android.crunchedCompression = false;
            importer.SetPlatformTextureSettings(android);
        }
    }
}
