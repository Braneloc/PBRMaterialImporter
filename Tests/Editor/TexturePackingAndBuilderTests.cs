using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace ExoLabs.PBRMaterialImporter.Tests
{
    internal sealed class TexturePackingAndBuilderTests
    {
        const string TestRoot = "Assets/__PBRMaterialImporterTests";
        const string MeshySampleFolder = "Assets/Meshy_AI_Damien_Cross_0829203445_texture_fbx/Meshy_AI_Damien_Cross_0829203445_texture_fbx";

        [SetUp]
        public void SetUp()
        {
            if (AssetDatabase.IsValidFolder(TestRoot))
                AssetDatabase.DeleteAsset(TestRoot);
            TextureImportUtility.EnsureAssetFolder(TestRoot);
        }

        [TearDown]
        public void TearDown()
        {
            if (AssetDatabase.IsValidFolder(TestRoot))
                AssetDatabase.DeleteAsset(TestRoot);
        }

        [Test]
        public void MaskPackingInvertsRoughnessAndUsesHdrpDefaults()
        {
            Texture2D metallic = CreateTexture("Metallic.png", new[]
            {
                new Color32(0, 0, 0, 255), new Color32(64, 64, 64, 255),
                new Color32(128, 128, 128, 255), new Color32(255, 255, 255, 255)
            });
            Texture2D roughness = CreateTexture("Roughness.png", new[]
            {
                new Color32(0, 0, 0, 255), new Color32(64, 64, 64, 255),
                new Color32(128, 128, 128, 255), new Color32(255, 255, 255, 255)
            });
            DetectedTextureSet set = new DetectedTextureSet("test", "Test");
            set.Textures.Add(CreateEntry(metallic, TextureSemantic.Metallic));
            set.Textures.Add(CreateEntry(roughness, TextureSemantic.Roughness));

            const string outputPath = TestRoot + "/Test_MaskMap.png";
            TexturePackingUtility.BuildMaskMap(set, outputPath, new HashSet<TextureEntry>(), out bool wroteTexture);
            Color32[] pixels = ReadPng(outputPath);

            Assert.That(wroteTexture, Is.True);
            Assert.That(pixels[0].r, Is.EqualTo(0));
            Assert.That(pixels[1].r, Is.EqualTo(64));
            Assert.That(pixels[0].g, Is.EqualTo(255));
            Assert.That(pixels[0].b, Is.EqualTo(255));
            Assert.That(pixels[0].a, Is.EqualTo(255));
            Assert.That(pixels[1].a, Is.EqualTo(191));
            Assert.That(pixels[2].a, Is.EqualTo(127));
            Assert.That(pixels[3].a, Is.EqualTo(0));
        }

        [Test]
        public void BlackMetallicMapIsSemanticallyNeutral()
        {
            Texture2D metallic = CreateTexture("BlankMetallic.png", new[]
            {
                new Color32(0, 0, 0, 255), new Color32(0, 0, 0, 255),
                new Color32(0, 0, 0, 255), new Color32(0, 0, 0, 255)
            });
            TextureEntry entry = CreateEntry(metallic, TextureSemantic.Metallic);

            bool neutral = TexturePackingUtility.IsSemanticallyNeutral(entry, out string reason);

            Assert.That(neutral, Is.True);
            Assert.That(reason, Does.Contain("metallic"));
        }

        [Test]
        public void SeparateOpacityPreservesBaseColorAndWritesAlpha()
        {
            Texture2D baseColor = CreateTexture("Leaves_BaseColor.png", Solid(new Color32(170, 100, 50, 255)));
            Texture2D opacity = CreateTexture("Leaves_Opacity.png", Solid(new Color32(64, 64, 64, 255)));
            TextureEntry baseEntry = CreateEntry(baseColor, TextureSemantic.BaseColor);
            TextureEntry opacityEntry = CreateEntry(opacity, TextureSemantic.Opacity);

            const string outputPath = TestRoot + "/Leaves_BaseColorAlpha.png";
            TexturePackingUtility.BuildBaseColorAlpha(baseEntry, opacityEntry, outputPath);
            Color32[] pixels = ReadPng(outputPath);

            Assert.That(pixels[0].r, Is.EqualTo(170));
            Assert.That(pixels[0].g, Is.EqualTo(100));
            Assert.That(pixels[0].b, Is.EqualTo(50));
            Assert.That(pixels[0].a, Is.EqualTo(64));
        }

        [Test]
        public void BuilderCreatesAnHdrpLitMaterialAndPackedMask()
        {
            Texture2D baseColor = CreateTexture("Fabric_BaseColor.png", Solid(new Color32(170, 100, 50, 255)));
            Texture2D roughness = CreateTexture("Fabric_Roughness.png", Solid(new Color32(64, 64, 64, 255)));
            DetectedTextureSet set = new DetectedTextureSet("test", "Fabric");
            set.Textures.Add(CreateEntry(baseColor, TextureSemantic.BaseColor));
            set.Textures.Add(CreateEntry(roughness, TextureSemantic.Roughness));

            MaterialImportResult result = PbrMaterialBuilder.Build(set, new MaterialImportSettings
            {
                Pipeline = RenderPipelineTarget.HighDefinition,
                OutputMode = OutputMode.CustomFolder,
                CustomOutputFolder = TestRoot,
                ConfigureSourceImporters = true,
                CombineOpacityWithBaseColor = true,
                DiscardNeutralTextures = true,
                UpdateExistingAssets = true
            });

            Assert.That(result.Material, Is.Not.Null);
            Assert.That(result.Material.shader.name, Is.EqualTo("HDRP/Lit"));
            Assert.That(result.Material.GetTexture("_BaseColorMap"), Is.EqualTo(baseColor));
            Assert.That(result.Material.GetTexture("_MaskMap"), Is.Not.Null);
            Assert.That(File.Exists(TextureImportUtility.AssetPathToFullPath(result.MaskMapPath)), Is.True);
        }

        [Test]
        public void MeshyProjectSampleBuildsAsOneCompleteMaterialWhenAvailable()
        {
            if (!AssetDatabase.IsValidFolder(MeshySampleFolder))
                Assert.Ignore("The optional Meshy acceptance fixture is not present in this project.");

            List<Texture2D> textures = AssetDatabase.FindAssets("t:Texture2D", new[] { MeshySampleFolder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<Texture2D>)
                .Where(texture => texture != null)
                .ToList();
            Assert.That(textures, Has.Count.EqualTo(4));

            List<(Texture2D texture, TextureNameAnalysis analysis)> analyses = textures
                .Select(texture => (texture, TextureNameDetector.Analyze(texture.name)))
                .ToList();
            Assert.That(analyses.Select(item => item.analysis.Stem).Distinct().Count(), Is.EqualTo(1));

            string stem = analyses[0].analysis.Stem;
            DetectedTextureSet set = new DetectedTextureSet("meshy-sample", stem);
            foreach ((Texture2D texture, TextureNameAnalysis analysis) in analyses)
                set.Textures.Add(new TextureEntry(texture, analysis));
            set.Textures.Single(entry => entry.Semantic == TextureSemantic.Unknown).Semantic = TextureSemantic.BaseColor;

            MaterialImportResult result = PbrMaterialBuilder.Build(set, new MaterialImportSettings
            {
                Pipeline = RenderPipelineTarget.HighDefinition,
                OutputMode = OutputMode.CustomFolder,
                CustomOutputFolder = TestRoot,
                ConfigureSourceImporters = false,
                CombineOpacityWithBaseColor = true,
                DiscardNeutralTextures = true,
                UpdateExistingAssets = true
            });

            Assert.That(result.Material.shader.name, Is.EqualTo("HDRP/Lit"));
            Assert.That(result.Material.GetTexture("_BaseColorMap"), Is.Not.Null);
            Assert.That(result.Material.GetTexture("_NormalMap"), Is.Not.Null);
            Assert.That(result.Material.GetTexture("_MaskMap"), Is.Not.Null);
            Assert.That(result.Warnings.Any(warning => warning.Contains("metallic")), Is.False, "The non-blank sample metallic data should be retained.");
        }

        [Test]
        public void BuilderCreatesAUrpLitMaterialAndReusesTheSurfacePack()
        {
            if (Shader.Find("Universal Render Pipeline/Lit") == null)
                Assert.Ignore("URP is not installed in this validation project.");

            Texture2D baseColor = CreateTexture("Stone_BaseColor.png", Solid(new Color32(170, 100, 50, 255)));
            Texture2D metallic = CreateTexture("Stone_Metallic.png", Solid(new Color32(192, 192, 192, 255)));
            Texture2D roughness = CreateTexture("Stone_Roughness.png", Solid(new Color32(64, 64, 64, 255)));
            Texture2D ao = CreateTexture("Stone_AO.png", Solid(new Color32(220, 220, 220, 255)));
            DetectedTextureSet set = new DetectedTextureSet("test", "UrpStone");
            set.Textures.Add(CreateEntry(baseColor, TextureSemantic.BaseColor));
            set.Textures.Add(CreateEntry(metallic, TextureSemantic.Metallic));
            set.Textures.Add(CreateEntry(roughness, TextureSemantic.Roughness));
            set.Textures.Add(CreateEntry(ao, TextureSemantic.AmbientOcclusion));

            MaterialImportResult result = PbrMaterialBuilder.Build(set, Settings(RenderPipelineTarget.Universal));

            Assert.That(result.Material.shader.name, Is.EqualTo("Universal Render Pipeline/Lit"));
            Assert.That(result.Material.GetTexture("_BaseMap"), Is.EqualTo(baseColor));
            Assert.That(result.Material.GetTexture("_MetallicGlossMap"), Is.Not.Null);
            Assert.That(result.Material.GetTexture("_OcclusionMap"), Is.SameAs(result.Material.GetTexture("_MetallicGlossMap")));
            Assert.That(result.Material.GetFloat("_Smoothness"), Is.EqualTo(1f), "Packed alpha must not be scaled a second time by URP.");
            Color32 pixel = ReadPng(result.MaskMapPath)[0];
            Assert.That(pixel.r, Is.EqualTo(192));
            Assert.That(pixel.g, Is.EqualTo(220));
            Assert.That(pixel.a, Is.EqualTo(191));
        }

        [Test]
        public void UrpSpecularWorkflowPacksSpecularRgbAndSmoothnessAlpha()
        {
            if (Shader.Find("Universal Render Pipeline/Lit") == null)
                Assert.Ignore("URP is not installed in this validation project.");

            Texture2D specular = CreateTexture("Paint_Specular.png", Solid(new Color32(30, 60, 90, 255)));
            Texture2D roughness = CreateTexture("Paint_Roughness.png", Solid(new Color32(64, 64, 64, 255)));
            DetectedTextureSet set = new DetectedTextureSet("test", "UrpPaint")
            {
                Workflow = MaterialWorkflow.SpecularColor
            };
            set.Textures.Add(CreateEntry(specular, TextureSemantic.SpecularColor));
            set.Textures.Add(CreateEntry(roughness, TextureSemantic.Roughness));

            MaterialImportResult result = PbrMaterialBuilder.Build(set, Settings(RenderPipelineTarget.Universal));
            Color32 pixel = ReadPng(result.SpecularSmoothnessPath)[0];

            Assert.That(result.Material.GetFloat("_WorkflowMode"), Is.EqualTo(0f));
            Assert.That(result.Material.GetTexture("_SpecGlossMap"), Is.Not.Null);
            Assert.That(result.Material.GetFloat("_Smoothness"), Is.EqualTo(1f));
            Assert.That(result.Material.IsKeywordEnabled("_SPECULAR_SETUP"), Is.True);
            Assert.That(pixel.r, Is.EqualTo(30));
            Assert.That(pixel.g, Is.EqualTo(60));
            Assert.That(pixel.b, Is.EqualTo(90));
            Assert.That(pixel.a, Is.EqualTo(191));
        }

        static MaterialImportSettings Settings(RenderPipelineTarget pipeline)
        {
            return new MaterialImportSettings
            {
                Pipeline = pipeline,
                OutputMode = OutputMode.CustomFolder,
                CustomOutputFolder = TestRoot,
                ConfigureSourceImporters = true,
                CombineOpacityWithBaseColor = true,
                DiscardNeutralTextures = true,
                UpdateExistingAssets = true
            };
        }

        static TextureEntry CreateEntry(Texture2D texture, TextureSemantic semantic)
        {
            TextureEntry entry = new TextureEntry(texture, TextureNameDetector.Analyze(texture.name));
            entry.Semantic = semantic;
            entry.Channel = TextureChannel.Red;
            return entry;
        }

        static Texture2D CreateTexture(string fileName, Color32[] pixels)
        {
            Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            string assetPath = TestRoot + "/" + fileName;
            File.WriteAllBytes(TextureImportUtility.AssetPathToFullPath(assetPath), texture.EncodeToPNG());
            Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
        }

        static Color32[] ReadPng(string assetPath)
        {
            Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            try
            {
                ImageConversion.LoadImage(texture, File.ReadAllBytes(TextureImportUtility.AssetPathToFullPath(assetPath)), false);
                return texture.GetPixels32();
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
        }

        static Color32[] Solid(Color32 color)
        {
            return new[] { color, color, color, color };
        }
    }
}
