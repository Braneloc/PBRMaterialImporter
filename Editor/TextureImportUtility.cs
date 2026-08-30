using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ExoLabs.PBRMaterialImporter
{
    internal static class TextureImportUtility
    {
        static readonly HashSet<string> SupportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".tga", ".tif", ".tiff", ".bmp", ".exr", ".hdr", ".psd"
        };

        internal static bool IsSupportedImage(string path)
        {
            return !string.IsNullOrEmpty(path) && SupportedExtensions.Contains(Path.GetExtension(path));
        }

        internal static void ConfigureSource(TextureEntry entry)
        {
            if (entry?.Texture == null)
                return;

            string path = entry.AssetPath;
            if (!(AssetImporter.GetAtPath(path) is TextureImporter importer))
                return;

            bool normal = entry.Semantic == TextureSemantic.Normal;
            bool color = entry.Semantic == TextureSemantic.BaseColor ||
                         entry.Semantic == TextureSemantic.Emission ||
                         entry.Semantic == TextureSemantic.SpecularColor;
            TextureImporterType desiredType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            bool changed = false;

            if (importer.textureType != desiredType)
            {
                importer.textureType = desiredType;
                changed = true;
            }

            if (!normal && importer.sRGBTexture != color)
            {
                importer.sRGBTexture = color;
                changed = true;
            }

            if (normal && importer.flipGreenChannel != entry.FlipNormalGreen)
            {
                importer.flipGreenChannel = entry.FlipNormalGreen;
                changed = true;
            }

            if (!importer.mipmapEnabled)
            {
                importer.mipmapEnabled = true;
                changed = true;
            }

            if (changed)
                importer.SaveAndReimport();
        }

        internal static void ConfigureGeneratedTexture(string assetPath, bool sRgb, bool alphaIsTransparency)
        {
            if (!(AssetImporter.GetAtPath(assetPath) is TextureImporter importer))
                throw new InvalidOperationException("Unity did not create a texture importer for " + assetPath);

            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = sRgb;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = alphaIsTransparency;
            importer.mipmapEnabled = true;
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.filterMode = FilterMode.Trilinear;
            importer.anisoLevel = 4;
            importer.textureCompression = TextureImporterCompression.CompressedHQ;
            importer.SaveAndReimport();
        }

        internal static IReadOnlyList<Texture2D> CollectProjectTextures(IEnumerable<UnityEngine.Object> objects)
        {
            Dictionary<string, Texture2D> textures = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
            foreach (UnityEngine.Object obj in objects ?? Enumerable.Empty<UnityEngine.Object>())
            {
                if (obj is Texture2D texture)
                {
                    string texturePath = AssetDatabase.GetAssetPath(texture);
                    if (!string.IsNullOrEmpty(texturePath))
                        textures[texturePath] = texture;
                    continue;
                }

                string path = AssetDatabase.GetAssetPath(obj);
                if (!AssetDatabase.IsValidFolder(path))
                    continue;

                foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { path }))
                {
                    string texturePath = AssetDatabase.GUIDToAssetPath(guid);
                    Texture2D found = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
                    if (found != null)
                        textures[texturePath] = found;
                }
            }
            return textures.Values.ToList();
        }

        internal static IReadOnlyList<Texture2D> ImportExternalPaths(IEnumerable<string> paths)
        {
            List<string> files = new List<string>();
            string projectRoot = ProjectRoot;

            foreach (string candidate in paths ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(candidate))
                    continue;

                string absolute = Path.GetFullPath(candidate);
                if (absolute.StartsWith(projectRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (File.Exists(absolute) && IsSupportedImage(absolute))
                    files.Add(absolute);
                else if (Directory.Exists(absolute))
                    files.AddRange(Directory.EnumerateFiles(absolute, "*", SearchOption.AllDirectories).Where(IsSupportedImage));
            }

            if (files.Count == 0)
                return Array.Empty<Texture2D>();

            const string destinationFolder = "Assets/PBRMaterialImports/SourceTextures";
            EnsureAssetFolder(destinationFolder);
            List<string> importedPaths = new List<string>();
            try
            {
                AssetDatabase.StartAssetEditing();
                foreach (string sourceFile in files.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    string safeName = TextureNameDetector.SanitizeAssetName(Path.GetFileNameWithoutExtension(sourceFile), "Texture") + Path.GetExtension(sourceFile).ToLowerInvariant();
                    string destination = AssetDatabase.GenerateUniqueAssetPath(destinationFolder + "/" + safeName);
                    File.Copy(sourceFile, AssetPathToFullPath(destination), false);
                    importedPaths.Add(destination);
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            foreach (string importedPath in importedPaths)
                AssetDatabase.ImportAsset(importedPath, ImportAssetOptions.ForceSynchronousImport);
            return importedPaths
                .Select(AssetDatabase.LoadAssetAtPath<Texture2D>)
                .Where(texture => texture != null)
                .ToList();
        }

        internal static string ResolveOutputFolder(DetectedTextureSet set, MaterialImportSettings settings)
        {
            if (settings.OutputMode == OutputMode.CustomFolder)
            {
                if (!IsWritableAssetFolder(settings.CustomOutputFolder))
                    throw new InvalidOperationException("Choose a custom output folder inside Assets.");
                EnsureAssetFolder(settings.CustomOutputFolder);
                return settings.CustomOutputFolder.TrimEnd('/');
            }

            List<string> sourceDirectories = set.SourceDirectories().ToList();
            string commonDirectory = sourceDirectories.Count == 1 && IsWritableAssetFolder(sourceDirectories[0])
                ? sourceDirectories[0]
                : string.Empty;

            if (settings.OutputMode == OutputMode.BesideSourceTextures && !string.IsNullOrEmpty(commonDirectory))
                return commonDirectory;

            if (settings.OutputMode == OutputMode.GeneratedSubfolder && !string.IsNullOrEmpty(commonDirectory))
            {
                string generatedFolder = commonDirectory.TrimEnd('/') + "/" + GetPipelineFolderName(settings.Pipeline);
                EnsureAssetFolder(generatedFolder);
                return generatedFolder;
            }

            string fallback = "Assets/PBRMaterialImports/" + TextureNameDetector.SanitizeAssetName(set.MaterialName);
            EnsureAssetFolder(fallback);
            return fallback;
        }

        internal static string ResolveAssetPath(string folder, string fileName, bool updateExisting)
        {
            string candidate = folder.TrimEnd('/') + "/" + fileName;
            return updateExisting ? candidate : AssetDatabase.GenerateUniqueAssetPath(candidate);
        }

        static string GetPipelineFolderName(RenderPipelineTarget pipeline)
        {
            return pipeline == RenderPipelineTarget.Universal ? "URP" : "HDRP";
        }

        internal static void EnsureAssetFolder(string assetFolder)
        {
            string normalized = (assetFolder ?? string.Empty).Replace('\\', '/').TrimEnd('/');
            if (AssetDatabase.IsValidFolder(normalized))
                return;
            if (!normalized.Equals("Assets", StringComparison.Ordinal) && !normalized.StartsWith("Assets/", StringComparison.Ordinal))
                throw new InvalidOperationException("Output folders must be inside Assets: " + normalized);

            string[] segments = normalized.Split('/');
            string current = segments[0];
            for (int i = 1; i < segments.Length; i++)
            {
                string next = current + "/" + segments[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, segments[i]);
                current = next;
            }
        }

        internal static string AssetPathToFullPath(string assetPath)
        {
            return Path.GetFullPath(Path.Combine(ProjectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar)));
        }

        static bool IsWritableAssetFolder(string path)
        {
            return !string.IsNullOrWhiteSpace(path) &&
                   (path.Equals("Assets", StringComparison.Ordinal) || path.StartsWith("Assets/", StringComparison.Ordinal));
        }

        static string ProjectRoot => Directory.GetParent(Application.dataPath)?.FullName ?? throw new InvalidOperationException("Could not resolve the Unity project root.");
    }
}
