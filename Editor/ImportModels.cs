using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ExoLabs.PBRMaterialImporter
{
    internal enum TextureSemantic
    {
        Unknown,
        BaseColor,
        Normal,
        Metallic,
        Roughness,
        Smoothness,
        AmbientOcclusion,
        Height,
        Emission,
        Opacity,
        DetailMask,
        SpecularColor,
        PackedOrm,
        PackedRma,
        PackedMra,
        MetallicRoughness,
        HdrpMaskMap
    }

    internal enum TextureChannel
    {
        Red,
        Green,
        Blue,
        Alpha,
        Luminance
    }

    internal enum MaterialWorkflow
    {
        Auto,
        Metallic,
        SpecularColor
    }

    internal enum RenderPipelineTarget
    {
        Auto,
        HighDefinition,
        Universal
    }

    internal enum SurfaceMode
    {
        Auto,
        Opaque,
        AlphaClipping,
        Transparent
    }

    internal enum OutputMode
    {
        GeneratedSubfolder,
        BesideSourceTextures,
        CustomFolder
    }

    [Serializable]
    internal sealed class TextureEntry
    {
        [SerializeField] Texture2D texture;
        [SerializeField] TextureSemantic semantic;
        [SerializeField] TextureChannel channel;
        [SerializeField] bool flipNormalGreen;
        [SerializeField] string detectionNote;

        internal Texture2D Texture
        {
            get => texture;
            set => texture = value;
        }

        internal TextureSemantic Semantic
        {
            get => semantic;
            set => semantic = value;
        }

        internal TextureChannel Channel
        {
            get => channel;
            set => channel = value;
        }

        internal bool FlipNormalGreen
        {
            get => flipNormalGreen;
            set => flipNormalGreen = value;
        }

        internal string DetectionNote
        {
            get => detectionNote;
            set => detectionNote = value;
        }

        internal string AssetPath => texture == null ? string.Empty : AssetDatabase.GetAssetPath(texture);

        internal TextureEntry(Texture2D texture, TextureNameAnalysis analysis)
        {
            this.texture = texture;
            semantic = analysis.Semantic;
            channel = analysis.DefaultChannel;
            flipNormalGreen = analysis.FlipNormalGreen;
            detectionNote = analysis.Note;
        }
    }

    [Serializable]
    internal sealed class DetectedTextureSet
    {
        [SerializeField] string sourceKey;
        [SerializeField] string materialName;
        [SerializeField] bool expanded = true;
        [SerializeField] MaterialWorkflow workflow = MaterialWorkflow.Auto;
        [SerializeField] SurfaceMode surfaceMode = SurfaceMode.Auto;
        [SerializeField] float alphaCutoff = 0.5f;
        [SerializeField] float normalScale = 1f;
        [SerializeField] float heightAmplitudeCentimeters = 2f;
        [SerializeField] bool enableGpuInstancing = true;
        [SerializeField] bool doubleSided;
        [SerializeField] List<TextureEntry> textures = new List<TextureEntry>();

        internal string SourceKey { get => sourceKey; set => sourceKey = value; }
        internal string MaterialName { get => materialName; set => materialName = value; }
        internal bool Expanded { get => expanded; set => expanded = value; }
        internal MaterialWorkflow Workflow { get => workflow; set => workflow = value; }
        internal SurfaceMode SurfaceMode { get => surfaceMode; set => surfaceMode = value; }
        internal float AlphaCutoff { get => alphaCutoff; set => alphaCutoff = Mathf.Clamp01(value); }
        internal float NormalScale { get => normalScale; set => normalScale = Mathf.Max(0f, value); }
        internal float HeightAmplitudeCentimeters { get => heightAmplitudeCentimeters; set => heightAmplitudeCentimeters = Mathf.Max(0f, value); }
        internal bool EnableGpuInstancing { get => enableGpuInstancing; set => enableGpuInstancing = value; }
        internal bool DoubleSided { get => doubleSided; set => doubleSided = value; }
        internal List<TextureEntry> Textures => textures;

        internal DetectedTextureSet(string sourceKey, string materialName)
        {
            this.sourceKey = sourceKey;
            this.materialName = materialName;
        }

        internal TextureEntry First(TextureSemantic semantic)
        {
            return textures.FirstOrDefault(entry => entry.Texture != null && entry.Semantic == semantic);
        }

        internal bool Has(TextureSemantic semantic)
        {
            return First(semantic) != null;
        }

        internal IEnumerable<string> SourceDirectories()
        {
            return textures
                .Where(entry => entry.Texture != null)
                .Select(entry => System.IO.Path.GetDirectoryName(entry.AssetPath)?.Replace('\\', '/'))
                .Where(path => !string.IsNullOrEmpty(path))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        internal List<string> Validate()
        {
            List<string> issues = new List<string>();
            if (string.IsNullOrWhiteSpace(materialName))
                issues.Add("Enter a material name.");
            if (textures.All(entry => entry.Texture == null))
                issues.Add("Add at least one texture.");
            if (textures.Count(entry => entry.Texture != null && entry.Semantic == TextureSemantic.BaseColor) > 1)
                issues.Add("More than one Base Color is assigned; the first will be used.");
            if (Has(TextureSemantic.Roughness) && Has(TextureSemantic.Smoothness))
                issues.Add("Both Roughness and Smoothness are assigned; Smoothness takes priority.");
            if (Has(TextureSemantic.Metallic) && Has(TextureSemantic.SpecularColor) && workflow == MaterialWorkflow.Auto)
                issues.Add("Both Metallic and Specular Color are assigned; Auto uses the metallic workflow.");
            if (textures.Any(entry => entry.Texture != null && entry.Semantic == TextureSemantic.Unknown))
                issues.Add("Unknown textures are ignored until a role is assigned.");
            return issues;
        }
    }

    internal readonly struct TextureNameAnalysis
    {
        internal readonly TextureSemantic Semantic;
        internal readonly string Stem;
        internal readonly TextureChannel DefaultChannel;
        internal readonly bool FlipNormalGreen;
        internal readonly string Note;

        internal TextureNameAnalysis(
            TextureSemantic semantic,
            string stem,
            TextureChannel defaultChannel,
            bool flipNormalGreen,
            string note)
        {
            Semantic = semantic;
            Stem = stem;
            DefaultChannel = defaultChannel;
            FlipNormalGreen = flipNormalGreen;
            Note = note;
        }
    }

    internal sealed class MaterialImportSettings
    {
        internal RenderPipelineTarget Pipeline;
        internal OutputMode OutputMode;
        internal string CustomOutputFolder;
        internal bool ConfigureSourceImporters;
        internal bool CombineOpacityWithBaseColor;
        internal bool DiscardNeutralTextures;
        internal bool UpdateExistingAssets;
    }

    internal sealed class MaterialImportResult
    {
        internal Material Material;
        internal string MaterialPath;
        internal string MaskMapPath;
        internal string SpecularSmoothnessPath;
        internal string BaseColorAlphaPath;
        internal readonly List<string> Warnings = new List<string>();
    }
}
