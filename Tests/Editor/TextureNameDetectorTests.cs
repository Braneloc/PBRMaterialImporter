using NUnit.Framework;

namespace ExoLabs.PBRMaterialImporter.Tests
{
    internal sealed class TextureNameDetectorTests
    {
        [TestCase("Stone_BaseColor_4K.png", TextureSemantic.BaseColor, "stone")]
        [TestCase("Stone_NormalDX_4K.tga", TextureSemantic.Normal, "stone")]
        [TestCase("Stone_NormalGL_4K.png", TextureSemantic.Normal, "stone")]
        [TestCase("T_Stone_ORM.png", TextureSemantic.PackedOrm, "stone")]
        [TestCase("Stone_MetallicRoughness.png", TextureSemantic.MetallicRoughness, "stone")]
        [TestCase("Stone_Smoothness.png", TextureSemantic.Smoothness, "stone")]
        [TestCase("aerial_rocks_04_nor_gl_4k.exr", TextureSemantic.Normal, "aerial_rocks_04")]
        public void DetectsCommonPbrNames(string fileName, TextureSemantic expectedSemantic, string expectedStem)
        {
            TextureNameAnalysis result = TextureNameDetector.Analyze(fileName);

            Assert.That(result.Semantic, Is.EqualTo(expectedSemantic));
            Assert.That(result.Stem, Is.EqualTo(expectedStem));
        }

        [Test]
        public void DetectsDirectXNormalFlip()
        {
            Assert.That(TextureNameDetector.Analyze("Fabric_NormalDX.png").FlipNormalGreen, Is.True);
            Assert.That(TextureNameDetector.Analyze("Fabric_NormalGL.png").FlipNormalGreen, Is.False);
        }

        [Test]
        public void MeshySampleBaseAndMapsShareTheSameStem()
        {
            const string sample = "Meshy_AI_Damien_Cross_0829203445_texture";
            TextureNameAnalysis baseColor = TextureNameDetector.Analyze(sample + ".png");
            TextureNameAnalysis normal = TextureNameDetector.Analyze(sample + "_normal.png");
            TextureNameAnalysis metallic = TextureNameDetector.Analyze(sample + "_metallic.png");
            TextureNameAnalysis roughness = TextureNameDetector.Analyze(sample + "_roughness.png");

            Assert.That(baseColor.Semantic, Is.EqualTo(TextureSemantic.Unknown));
            Assert.That(normal.Semantic, Is.EqualTo(TextureSemantic.Normal));
            Assert.That(metallic.Semantic, Is.EqualTo(TextureSemantic.Metallic));
            Assert.That(roughness.Semantic, Is.EqualTo(TextureSemantic.Roughness));
            Assert.That(normal.Stem, Is.EqualTo(baseColor.Stem));
            Assert.That(metallic.Stem, Is.EqualTo(baseColor.Stem));
            Assert.That(roughness.Stem, Is.EqualTo(baseColor.Stem));
        }
    }
}
