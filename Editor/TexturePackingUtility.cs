using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ExoLabs.PBRMaterialImporter
{
    internal static class TexturePackingUtility
    {
        sealed class PixelData
        {
            internal readonly int Width;
            internal readonly int Height;
            internal readonly Color32[] Pixels;

            internal PixelData(int width, int height, Color32[] pixels)
            {
                Width = width;
                Height = height;
                Pixels = pixels;
            }
        }

        readonly struct ChannelSource
        {
            internal readonly TextureEntry Entry;
            internal readonly TextureChannel Channel;
            internal readonly bool Invert;

            internal ChannelSource(TextureEntry entry, TextureChannel channel, bool invert = false)
            {
                Entry = entry;
                Channel = channel;
                Invert = invert;
            }
        }

        internal static Texture2D BuildMaskMap(
            DetectedTextureSet set,
            string outputPath,
            ISet<TextureEntry> ignoredEntries,
            out bool wroteTexture)
        {
            wroteTexture = false;
            TextureEntry existingMask = FirstUsable(set, TextureSemantic.HdrpMaskMap, ignoredEntries);
            bool hasOverrides = HasAny(set,
                ignoredEntries,
                TextureSemantic.Metallic,
                TextureSemantic.Roughness,
                TextureSemantic.Smoothness,
                TextureSemantic.AmbientOcclusion,
                TextureSemantic.DetailMask,
                TextureSemantic.PackedOrm,
                TextureSemantic.PackedRma,
                TextureSemantic.PackedMra,
                TextureSemantic.MetallicRoughness);

            if (existingMask != null && !hasOverrides)
                return existingMask.Texture;

            ChannelSource? metallic = null;
            ChannelSource? ao = null;
            ChannelSource? detail = null;
            ChannelSource? smoothness = null;

            if (existingMask != null)
            {
                metallic = new ChannelSource(existingMask, TextureChannel.Red);
                ao = new ChannelSource(existingMask, TextureChannel.Green);
                detail = new ChannelSource(existingMask, TextureChannel.Blue);
                smoothness = new ChannelSource(existingMask, TextureChannel.Alpha);
            }

            TextureEntry packed = FirstUsable(set, TextureSemantic.PackedOrm, ignoredEntries) ??
                                  FirstUsable(set, TextureSemantic.PackedRma, ignoredEntries) ??
                                  FirstUsable(set, TextureSemantic.PackedMra, ignoredEntries) ??
                                  FirstUsable(set, TextureSemantic.MetallicRoughness, ignoredEntries);
            if (packed != null)
                ApplyPackedLayout(packed, ref metallic, ref ao, ref smoothness);

            TextureEntry separateMetallic = FirstUsable(set, TextureSemantic.Metallic, ignoredEntries);
            TextureEntry separateAo = FirstUsable(set, TextureSemantic.AmbientOcclusion, ignoredEntries);
            TextureEntry separateDetail = FirstUsable(set, TextureSemantic.DetailMask, ignoredEntries);
            TextureEntry separateRoughness = FirstUsable(set, TextureSemantic.Roughness, ignoredEntries);
            TextureEntry separateSmoothness = FirstUsable(set, TextureSemantic.Smoothness, ignoredEntries);
            if (separateMetallic != null)
                metallic = new ChannelSource(separateMetallic, separateMetallic.Channel);
            if (separateAo != null)
                ao = new ChannelSource(separateAo, separateAo.Channel);
            if (separateDetail != null)
                detail = new ChannelSource(separateDetail, separateDetail.Channel);
            if (separateRoughness != null)
                smoothness = new ChannelSource(separateRoughness, separateRoughness.Channel, true);
            if (separateSmoothness != null)
                smoothness = new ChannelSource(separateSmoothness, separateSmoothness.Channel);

            ChannelSource?[] sources = { metallic, ao, detail, smoothness };
            if (sources.All(source => !source.HasValue))
                return null;

            Dictionary<Texture2D, PixelData> data = LoadUniqueData(sources);
            int width = data.Values.Max(value => value.Width);
            int height = data.Values.Max(value => value.Height);
            Color32[] output = new Color32[width * height];

            for (int y = 0; y < height; y++)
            {
                float v = (y + 0.5f) / height;
                for (int x = 0; x < width; x++)
                {
                    float u = (x + 0.5f) / width;
                    byte m = ToByte(Sample(metallic, data, u, v, 0f));
                    byte o = ToByte(Sample(ao, data, u, v, 1f));
                    byte d = ToByte(Sample(detail, data, u, v, 1f));
                    byte s = ToByte(Sample(smoothness, data, u, v, 0.5f));
                    output[y * width + x] = new Color32(m, o, d, s);
                }
            }

            WritePng(outputPath, output, width, height, true);
            wroteTexture = true;
            return AssetDatabase.LoadAssetAtPath<Texture2D>(outputPath);
        }

        internal static Texture2D BuildBaseColorAlpha(
            TextureEntry baseColor,
            TextureEntry opacity,
            string outputPath)
        {
            if (opacity == null || opacity.Texture == null)
                return baseColor?.Texture;

            ChannelSource opacitySource = new ChannelSource(opacity, opacity.Channel);
            PixelData baseData = baseColor?.Texture == null ? null : ReadPixels(baseColor.Texture, false);
            PixelData opacityData = ReadPixels(opacity.Texture, true);
            int width = baseData?.Width ?? opacityData.Width;
            int height = baseData?.Height ?? opacityData.Height;
            Color32[] output = new Color32[width * height];

            for (int y = 0; y < height; y++)
            {
                float v = (y + 0.5f) / height;
                for (int x = 0; x < width; x++)
                {
                    float u = (x + 0.5f) / width;
                    Color32 colour = baseData == null ? new Color32(255, 255, 255, 255) : SampleColour(baseData, u, v);
                    float alpha = SampleChannel(opacityData, opacitySource.Channel, u, v);
                    colour.a = ToByte(alpha);
                    output[y * width + x] = colour;
                }
            }

            WritePng(outputPath, output, width, height, false, true);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(outputPath);
        }

        internal static Texture2D BuildSpecularSmoothnessMap(
            TextureEntry specular,
            Texture2D surfaceMap,
            string outputPath)
        {
            PixelData specularData = specular?.Texture == null ? null : ReadPixels(specular.Texture, false);
            PixelData surfaceData = surfaceMap == null ? null : ReadPixels(surfaceMap, true);
            if (specularData == null && surfaceData == null)
                return null;

            int width = Math.Max(specularData?.Width ?? 1, surfaceData?.Width ?? 1);
            int height = Math.Max(specularData?.Height ?? 1, surfaceData?.Height ?? 1);
            Color32[] output = new Color32[width * height];
            for (int y = 0; y < height; y++)
            {
                float v = (y + 0.5f) / height;
                for (int x = 0; x < width; x++)
                {
                    float u = (x + 0.5f) / width;
                    Color32 colour = specularData == null
                        ? new Color32(255, 255, 255, 255)
                        : SampleColour(specularData, u, v);
                    colour.a = surfaceData == null
                        ? (byte)128
                        : SampleColour(surfaceData, u, v).a;
                    output[y * width + x] = colour;
                }
            }

            WritePng(outputPath, output, width, height, false);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(outputPath);
        }

        internal static bool IsSemanticallyNeutral(TextureEntry entry, out string reason)
        {
            reason = string.Empty;
            if (entry?.Texture == null)
                return false;

            switch (entry.Semantic)
            {
                case TextureSemantic.Metallic:
                {
                    GetChannelRange(ReadPixels(entry.Texture, true), entry.Channel, out _, out float maximum);
                    if (maximum <= 1f / 255f)
                        reason = "it contains no non-zero metallic values";
                    break;
                }
                case TextureSemantic.AmbientOcclusion:
                case TextureSemantic.Opacity:
                {
                    GetChannelRange(ReadPixels(entry.Texture, true), entry.Channel, out float minimum, out _);
                    if (minimum >= 254f / 255f)
                        reason = entry.Semantic == TextureSemantic.AmbientOcclusion
                            ? "it is fully unoccluded (white)"
                            : "it is fully opaque (white)";
                    break;
                }
                case TextureSemantic.Height:
                {
                    GetChannelRange(ReadPixels(entry.Texture, true), entry.Channel, out float minimum, out float maximum);
                    if (maximum - minimum <= 1f / 255f)
                        reason = "it has no height variation";
                    break;
                }
                case TextureSemantic.Emission:
                {
                    PixelData data = ReadPixels(entry.Texture, false);
                    bool black = data.Pixels.All(pixel => pixel.r <= 1 && pixel.g <= 1 && pixel.b <= 1);
                    if (black)
                        reason = "it contains no emissive colour";
                    break;
                }
            }
            return !string.IsNullOrEmpty(reason);
        }

        static void ApplyPackedLayout(
            TextureEntry packed,
            ref ChannelSource? metallic,
            ref ChannelSource? ao,
            ref ChannelSource? smoothness)
        {
            switch (packed.Semantic)
            {
                case TextureSemantic.PackedOrm:
                    ao = new ChannelSource(packed, TextureChannel.Red);
                    smoothness = new ChannelSource(packed, TextureChannel.Green, true);
                    metallic = new ChannelSource(packed, TextureChannel.Blue);
                    break;
                case TextureSemantic.PackedRma:
                    smoothness = new ChannelSource(packed, TextureChannel.Red, true);
                    metallic = new ChannelSource(packed, TextureChannel.Green);
                    ao = new ChannelSource(packed, TextureChannel.Blue);
                    break;
                case TextureSemantic.PackedMra:
                    metallic = new ChannelSource(packed, TextureChannel.Red);
                    smoothness = new ChannelSource(packed, TextureChannel.Green, true);
                    ao = new ChannelSource(packed, TextureChannel.Blue);
                    break;
                case TextureSemantic.MetallicRoughness:
                    smoothness = new ChannelSource(packed, TextureChannel.Green, true);
                    metallic = new ChannelSource(packed, TextureChannel.Blue);
                    break;
            }
        }

        static Dictionary<Texture2D, PixelData> LoadUniqueData(IEnumerable<ChannelSource?> sources)
        {
            Dictionary<Texture2D, PixelData> result = new Dictionary<Texture2D, PixelData>();
            foreach (ChannelSource source in sources.Where(value => value.HasValue).Select(value => value.Value))
            {
                Texture2D texture = source.Entry.Texture;
                if (texture != null && !result.ContainsKey(texture))
                    result.Add(texture, ReadPixels(texture, true));
            }
            return result;
        }

        static PixelData ReadPixels(Texture2D texture, bool forceLinear)
        {
            string path = AssetDatabase.GetAssetPath(texture);
            if (!(AssetImporter.GetAtPath(path) is TextureImporter importer))
                throw new InvalidOperationException("Texture must be a project asset: " + texture.name);

            bool originalReadable = importer.isReadable;
            bool originalSrgb = importer.sRGBTexture;
            TextureImporterType originalType = importer.textureType;
            TextureImporterCompression originalCompression = importer.textureCompression;
            bool changed = !originalReadable ||
                           originalCompression != TextureImporterCompression.Uncompressed ||
                           (forceLinear && originalSrgb) ||
                           (forceLinear && originalType != TextureImporterType.Default);

            try
            {
                if (changed)
                {
                    importer.isReadable = true;
                    importer.textureCompression = TextureImporterCompression.Uncompressed;
                    if (forceLinear)
                    {
                        importer.textureType = TextureImporterType.Default;
                        importer.sRGBTexture = false;
                    }
                    importer.SaveAndReimport();
                }

                Texture2D readable = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                return new PixelData(readable.width, readable.height, readable.GetPixels32(0));
            }
            finally
            {
                if (changed)
                {
                    importer = AssetImporter.GetAtPath(path) as TextureImporter;
                    if (importer != null)
                    {
                        importer.isReadable = originalReadable;
                        importer.textureCompression = originalCompression;
                        importer.textureType = originalType;
                        importer.sRGBTexture = originalSrgb;
                        importer.SaveAndReimport();
                    }
                }
            }
        }

        static float Sample(ChannelSource? source, IReadOnlyDictionary<Texture2D, PixelData> data, float u, float v, float fallback)
        {
            if (!source.HasValue || source.Value.Entry?.Texture == null)
                return fallback;
            ChannelSource value = source.Value;
            float sample = SampleChannel(data[value.Entry.Texture], value.Channel, u, v);
            return value.Invert ? 1f - sample : sample;
        }

        static float SampleChannel(PixelData data, TextureChannel channel, float u, float v)
        {
            Color32 colour = SampleColour(data, u, v);
            switch (channel)
            {
                case TextureChannel.Green: return colour.g / 255f;
                case TextureChannel.Blue: return colour.b / 255f;
                case TextureChannel.Alpha: return colour.a / 255f;
                case TextureChannel.Luminance: return (0.2126f * colour.r + 0.7152f * colour.g + 0.0722f * colour.b) / 255f;
                default: return colour.r / 255f;
            }
        }

        static Color32 SampleColour(PixelData data, float u, float v)
        {
            float px = Mathf.Clamp(u * data.Width - 0.5f, 0f, data.Width - 1f);
            float py = Mathf.Clamp(v * data.Height - 0.5f, 0f, data.Height - 1f);
            int x0 = Mathf.FloorToInt(px);
            int y0 = Mathf.FloorToInt(py);
            int x1 = Mathf.Min(x0 + 1, data.Width - 1);
            int y1 = Mathf.Min(y0 + 1, data.Height - 1);
            float tx = px - x0;
            float ty = py - y0;
            Color c00 = data.Pixels[y0 * data.Width + x0];
            Color c10 = data.Pixels[y0 * data.Width + x1];
            Color c01 = data.Pixels[y1 * data.Width + x0];
            Color c11 = data.Pixels[y1 * data.Width + x1];
            return (Color32)Color.Lerp(Color.Lerp(c00, c10, tx), Color.Lerp(c01, c11, tx), ty);
        }

        static void WritePng(
            string assetPath,
            Color32[] pixels,
            int width,
            int height,
            bool linear,
            bool alphaIsTransparency = false)
        {
            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false, linear);
            try
            {
                texture.SetPixels32(pixels);
                texture.Apply(false, false);
                string fullPath = TextureImportUtility.AssetPathToFullPath(assetPath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException("Invalid output path."));
                File.WriteAllBytes(fullPath, texture.EncodeToPNG());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }

            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            TextureImportUtility.ConfigureGeneratedTexture(assetPath, !linear, alphaIsTransparency);
        }

        static bool HasAny(DetectedTextureSet set, ISet<TextureEntry> ignoredEntries, params TextureSemantic[] semantics)
        {
            return semantics.Any(semantic => FirstUsable(set, semantic, ignoredEntries) != null);
        }

        static TextureEntry FirstUsable(DetectedTextureSet set, TextureSemantic semantic, ISet<TextureEntry> ignoredEntries)
        {
            return set.Textures.FirstOrDefault(entry =>
                entry.Texture != null &&
                entry.Semantic == semantic &&
                (ignoredEntries == null || !ignoredEntries.Contains(entry)));
        }

        static void GetChannelRange(PixelData data, TextureChannel channel, out float minimum, out float maximum)
        {
            minimum = 1f;
            maximum = 0f;
            foreach (Color32 pixel in data.Pixels)
            {
                float value;
                switch (channel)
                {
                    case TextureChannel.Green: value = pixel.g / 255f; break;
                    case TextureChannel.Blue: value = pixel.b / 255f; break;
                    case TextureChannel.Alpha: value = pixel.a / 255f; break;
                    case TextureChannel.Luminance: value = (0.2126f * pixel.r + 0.7152f * pixel.g + 0.0722f * pixel.b) / 255f; break;
                    default: value = pixel.r / 255f; break;
                }
                minimum = Mathf.Min(minimum, value);
                maximum = Mathf.Max(maximum, value);
            }
        }

        static byte ToByte(float value)
        {
            return (byte)Mathf.Clamp(Mathf.RoundToInt(value * 255f), 0, 255);
        }
    }
}
