using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ExoLabs.PBRMaterialImporter
{
    internal static class TextureNameDetector
    {
        sealed class Rule
        {
            internal readonly TextureSemantic semantic;
            internal readonly string[][] aliases;
            internal readonly bool flipGreen;
            internal readonly string note;

            internal Rule(TextureSemantic semantic, bool flipGreen, string note, params string[] aliases)
            {
                this.semantic = semantic;
                this.flipGreen = flipGreen;
                this.note = note;
                this.aliases = aliases.Select(alias => alias.Split(' ')).ToArray();
            }
        }

        static readonly Rule[] rules =
        {
            new Rule(TextureSemantic.HdrpMaskMap, false, "HDRP mask map (R metallic, G AO, B detail, A smoothness)", "hdrpmask", "hdrp mask", "maskmap", "mask map"),
            new Rule(TextureSemantic.MetallicRoughness, false, "glTF-style metallic-roughness pack (G roughness, B metallic)", "metallicroughness", "metallic roughness", "metalroughness", "metal roughness"),
            new Rule(TextureSemantic.PackedOrm, false, "ORM/ARM pack (R AO, G roughness, B metallic)", "occlusionroughnessmetallic", "occlusion roughness metallic", "orm", "arm"),
            new Rule(TextureSemantic.PackedRma, false, "RMA pack (R roughness, G metallic, B AO)", "rma"),
            new Rule(TextureSemantic.PackedMra, false, "MRA pack (R metallic, G roughness, B AO)", "mra", "mrao"),
            new Rule(TextureSemantic.Normal, true, "DirectX tangent-space normal; green channel will be flipped", "normaldx", "normal dx", "normal directx", "nordx", "nor dx", "nrmdx", "nrm dx"),
            new Rule(TextureSemantic.Normal, false, "OpenGL tangent-space normal", "normalgl", "normal gl", "normal opengl", "norgl", "nor gl", "nrmgl", "nrm gl"),
            new Rule(TextureSemantic.BaseColor, false, "Base colour", "basecolor", "base color", "basecolour", "base colour", "albedo", "diffuse", "diff", "color", "colour", "col", "d"),
            new Rule(TextureSemantic.Normal, false, "Tangent-space normal", "normalmap", "normal map", "normal", "nrm", "nor", "n"),
            new Rule(TextureSemantic.Metallic, false, "Metallic/metalness", "metallic", "metalness", "metalic", "metal", "met", "m"),
            new Rule(TextureSemantic.Roughness, false, "Roughness; inverted while packing smoothness", "roughness", "rough", "rgh", "r"),
            new Rule(TextureSemantic.Smoothness, false, "Smoothness/gloss", "smoothness", "smooth", "glossiness", "gloss", "glossy", "s"),
            new Rule(TextureSemantic.AmbientOcclusion, false, "Ambient occlusion", "ambientocclusion", "ambient occlusion", "occlusion", "ao", "occ"),
            new Rule(TextureSemantic.DetailMask, false, "HDRP detail mask", "detailmask", "detail mask"),
            new Rule(TextureSemantic.Height, false, "Height/displacement", "displacement", "heightmap", "height map", "height", "disp", "depth"),
            new Rule(TextureSemantic.Emission, false, "Emission", "emissive", "emission", "emiss", "emit"),
            new Rule(TextureSemantic.SpecularColor, false, "Specular colour workflow", "specularcolor", "specular color", "specular", "spec", "reflectivity"),
            new Rule(TextureSemantic.Opacity, false, "Opacity/alpha", "opacity", "transparency", "transparent", "alpha", "cutout", "mask")
        };

        static readonly HashSet<string> utilityTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "texture", "textures", "tex", "map", "maps", "linear", "srgb", "raw", "lod0", "lod1", "lod2"
        };

        internal static TextureNameAnalysis Analyse(string fileName)
        {
            string rawName = Path.GetFileNameWithoutExtension(fileName) ?? string.Empty;
            List<string> originalTokens = Tokenize(rawName);
            List<string> workingTokens = new List<string>(originalTokens);
            RemoveTrailingUtilityTokens(workingTokens);

            foreach (Rule rule in rules)
            {
                foreach (string[] alias in rule.aliases.OrderByDescending(value => value.Length))
                {
                    int matchIndex = FindSuffix(workingTokens, alias);
                    if (matchIndex < 0)
                        continue;

                    List<string> stemTokens = workingTokens.Take(matchIndex).ToList();
                    RemoveTrailingUtilityTokens(stemTokens);
                    TrimNamePrefixes(stemTokens);
                    string stem = MakeStem(stemTokens, rawName);
                    return new TextureNameAnalysis(rule.semantic, stem, TextureChannel.Red, rule.flipGreen, rule.note);
                }
            }

            List<string> fallbackTokens = new List<string>(workingTokens);
            TrimNamePrefixes(fallbackTokens);
            return new TextureNameAnalysis(
                TextureSemantic.Unknown,
                MakeStem(fallbackTokens, rawName),
                TextureChannel.Red,
                false,
                "No known texture role was found in the filename");
        }

        internal static string SanitiseAssetName(string value, string fallback = "Material")
        {
            if (string.IsNullOrWhiteSpace(value))
                return fallback;

            string sanitised = string.Join("_", value.Trim().Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
            sanitised = Regex.Replace(sanitised, @"\s+", "_");
            sanitised = Regex.Replace(sanitised, @"_+", "_").Trim('_', '.');
            return string.IsNullOrEmpty(sanitised) ? fallback : sanitised;
        }

        static List<string> Tokenize(string value)
        {
            string separated = Regex.Replace(value, "([a-z])([A-Z])", "$1 $2");
            separated = Regex.Replace(separated, "([A-Za-z])([0-9])", "$1 $2");
            return Regex.Split(separated.ToLowerInvariant(), "[^a-z0-9]+")
                .Where(token => !string.IsNullOrWhiteSpace(token))
                .ToList();
        }

        static void RemoveTrailingUtilityTokens(List<string> tokens)
        {
            while (tokens.Count > 0)
            {
                string token = tokens[tokens.Count - 1];
                bool resolution = Regex.IsMatch(token, @"^(\d{1,2}k|\d{3,5}px|\d{3,5})$") &&
                                  (!int.TryParse(token, out int numeric) || numeric >= 256);
                bool udim = int.TryParse(token, out int udimValue) && udimValue >= 1001 && udimValue <= 1999;
                if (!utilityTokens.Contains(token) && !resolution && !udim)
                    break;
                tokens.RemoveAt(tokens.Count - 1);
            }
        }

        static int FindSuffix(IReadOnlyList<string> tokens, IReadOnlyList<string> alias)
        {
            if (tokens.Count < alias.Count)
                return -1;
            int start = tokens.Count - alias.Count;
            for (int i = 0; i < alias.Count; i++)
            {
                if (!string.Equals(tokens[start + i], alias[i], StringComparison.OrdinalIgnoreCase))
                    return -1;
            }
            return start;
        }

        static void TrimNamePrefixes(List<string> tokens)
        {
            while (tokens.Count > 1 && (tokens[0] == "t" || tokens[0] == "tex" || tokens[0] == "texture"))
                tokens.RemoveAt(0);
        }

        static string MakeStem(IReadOnlyCollection<string> tokens, string fallback)
        {
            string stem = string.Join("_", tokens);
            if (string.IsNullOrWhiteSpace(stem))
                stem = fallback;
            return SanitiseAssetName(stem, "Material");
        }
    }
}
