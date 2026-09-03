using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ExoLabs.PBRMaterialImporter
{
    internal static class PbrMaterialBuilder
    {
        const string hdrpShaderName = "HDRP/Lit";
        const string urpShaderName = "Universal Render Pipeline/Lit";

        internal static MaterialImportResult Build(DetectedTextureSet set, MaterialImportSettings settings)
        {
            if (set == null)
                throw new ArgumentNullException(nameof(set));
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            if (!TryResolvePipeline(settings.Pipeline, out RenderPipelineTarget pipeline, out Shader shader, out string pipelineError))
                throw new InvalidOperationException(pipelineError);

            settings.Pipeline = pipeline;
            if (settings.ConfigureSourceImporters)
            {
                foreach (TextureEntry entry in set.Textures)
                {
                    if (entry.Texture != null && entry.Semantic != TextureSemantic.Unknown)
                        TextureImportUtility.ConfigureSource(entry);
                }
            }

            MaterialImportResult result = new MaterialImportResult();
            HashSet<TextureEntry> ignoredEntries = FindNeutralEntries(set, settings, result);
            TextureEntry specular = FirstUsable(set, TextureSemantic.SpecularColor, ignoredEntries);
            bool useSpecularWorkflow = UsesSpecularWorkflow(set, specular, ignoredEntries, result);

            string assetName = TextureNameDetector.SanitiseAssetName(set.MaterialName);
            string outputFolder = TextureImportUtility.ResolveOutputFolder(set, settings);
            string materialPath = TextureImportUtility.ResolveAssetPath(outputFolder, assetName + ".mat", settings.UpdateExistingAssets);
            string surfaceSuffix = pipeline == RenderPipelineTarget.HighDefinition ? "_MaskMap.png" : "_MetallicOcclusionSmoothness.png";
            string surfacePath = TextureImportUtility.ResolveAssetPath(outputFolder, assetName + surfaceSuffix, settings.UpdateExistingAssets);
            string specularPath = TextureImportUtility.ResolveAssetPath(outputFolder, assetName + "_SpecularSmoothness.png", settings.UpdateExistingAssets);
            string baseAlphaPath = TextureImportUtility.ResolveAssetPath(outputFolder, assetName + "_BaseColorAlpha.png", settings.UpdateExistingAssets);

            result.MaterialPath = materialPath;
            TextureEntry baseColorEntry = FirstUsable(set, TextureSemantic.BaseColor, ignoredEntries);
            TextureEntry opacityEntry = FirstUsable(set, TextureSemantic.Opacity, ignoredEntries);
            Texture2D baseColor = baseColorEntry?.Texture;
            if (settings.CombineOpacityWithBaseColor && opacityEntry != null)
            {
                baseColor = TexturePackingUtility.BuildBaseColorAlpha(baseColorEntry, opacityEntry, baseAlphaPath);
                result.BaseColorAlphaPath = baseAlphaPath;
            }

            Texture2D surfaceMap = TexturePackingUtility.BuildMaskMap(set, surfacePath, ignoredEntries, out bool wroteSurfaceMap);
            if (surfaceMap != null)
            {
                result.MaskMapPath = wroteSurfaceMap ? surfacePath : AssetDatabase.GetAssetPath(surfaceMap);
                if (!wroteSurfaceMap)
                {
                    TextureEntry maskEntry = FirstUsable(set, TextureSemantic.HdrpMaskMap, ignoredEntries);
                    if (maskEntry != null)
                        TextureImportUtility.ConfigureSource(maskEntry);
                }
            }

            Texture2D specularSmoothness = null;
            if (pipeline == RenderPipelineTarget.Universal && useSpecularWorkflow && (specular != null || surfaceMap != null))
            {
                specularSmoothness = TexturePackingUtility.BuildSpecularSmoothnessMap(specular, surfaceMap, specularPath);
                result.SpecularSmoothnessPath = specularPath;
            }

            Material material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
            {
                material = new Material(shader) { name = Path.GetFileNameWithoutExtension(materialPath) };
                AssetDatabase.CreateAsset(material, materialPath);
                Undo.RegisterCreatedObjectUndo(material, "Create PBR material");
            }
            else
            {
                Undo.RecordObject(material, "Update PBR material");
                material.shader = shader;
            }

            if (pipeline == RenderPipelineTarget.HighDefinition)
                ConfigureHdrpMaterial(material, set, baseColor, surfaceMap, ignoredEntries, useSpecularWorkflow);
            else
                ConfigureUrpMaterial(material, set, baseColor, surfaceMap, specularSmoothness, ignoredEntries, useSpecularWorkflow, result);

            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssets();
            result.Material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            return result;
        }

        internal static bool TryResolvePipeline(
            RenderPipelineTarget requested,
            out RenderPipelineTarget resolved,
            out Shader shader,
            out string error)
        {
            resolved = requested;
            shader = null;
            error = string.Empty;

            if (requested == RenderPipelineTarget.Auto)
            {
                RenderPipelineAsset activePipeline = GraphicsSettings.currentRenderPipeline;
                string pipelineType = activePipeline == null ? string.Empty : activePipeline.GetType().FullName ?? activePipeline.GetType().Name;
                if (pipelineType.IndexOf("HighDefinition", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    pipelineType.IndexOf("HDRenderPipeline", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    resolved = RenderPipelineTarget.HighDefinition;
                }
                else if (pipelineType.IndexOf("Universal", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    resolved = RenderPipelineTarget.Universal;
                }
                else
                {
                    Shader hdrp = Shader.Find(hdrpShaderName);
                    Shader urp = Shader.Find(urpShaderName);
                    if (hdrp != null && urp == null)
                        resolved = RenderPipelineTarget.HighDefinition;
                    else if (urp != null && hdrp == null)
                        resolved = RenderPipelineTarget.Universal;
                    else
                    {
                        error = "Auto could not identify an active HDRP or URP asset. Select a pipeline explicitly or assign it in Project Settings > Graphics.";
                        return false;
                    }
                }
            }

            string shaderName = resolved == RenderPipelineTarget.Universal ? urpShaderName : hdrpShaderName;
            shader = Shader.Find(shaderName);
            if (shader != null)
                return true;

            error = shaderName + " is unavailable. Install the matching render-pipeline package before creating materials.";
            return false;
        }

        internal static string PipelineLabel(RenderPipelineTarget pipeline)
        {
            return pipeline == RenderPipelineTarget.Universal ? "URP" : "HDRP";
        }

        static HashSet<TextureEntry> FindNeutralEntries(
            DetectedTextureSet set,
            MaterialImportSettings settings,
            MaterialImportResult result)
        {
            HashSet<TextureEntry> ignoredEntries = new HashSet<TextureEntry>();
            if (!settings.DiscardNeutralTextures)
                return ignoredEntries;

            foreach (TextureEntry entry in set.Textures.Where(entry => entry.Texture != null))
            {
                if (!TexturePackingUtility.IsSemanticallyNeutral(entry, out string reason))
                    continue;
                ignoredEntries.Add(entry);
                result.Warnings.Add("Ignored " + entry.Texture.name + " because " + reason + ".");
            }
            return ignoredEntries;
        }

        static bool UsesSpecularWorkflow(
            DetectedTextureSet set,
            TextureEntry specular,
            ISet<TextureEntry> ignoredEntries,
            MaterialImportResult result)
        {
            bool hasMetal = HasMetalInput(set, ignoredEntries);
            bool useSpecular = set.Workflow == MaterialWorkflow.SpecularColor ||
                               (set.Workflow == MaterialWorkflow.Auto && specular != null && !hasMetal);
            if (set.Workflow == MaterialWorkflow.Auto && specular != null && hasMetal)
                result.Warnings.Add("Both metallic and specular inputs were present; the metallic workflow was selected.");
            return useSpecular;
        }

        static void ConfigureHdrpMaterial(
            Material material,
            DetectedTextureSet set,
            Texture2D baseColor,
            Texture2D maskMap,
            ISet<TextureEntry> ignoredEntries,
            bool useSpecularWorkflow)
        {
            TextureEntry normal = FirstUsable(set, TextureSemantic.Normal, ignoredEntries);
            TextureEntry height = FirstUsable(set, TextureSemantic.Height, ignoredEntries);
            TextureEntry emission = FirstUsable(set, TextureSemantic.Emission, ignoredEntries);
            TextureEntry specular = FirstUsable(set, TextureSemantic.SpecularColor, ignoredEntries);

            SetTexture(material, "_BaseColorMap", baseColor);
            SetColour(material, "_BaseColor", Color.white);
            SetTexture(material, "_NormalMap", normal?.Texture);
            SetFloat(material, "_NormalScale", set.NormalScale);
            SetTexture(material, "_MaskMap", maskMap);
            SetFloat(material, "_Metallic", maskMap == null && FirstUsable(set, TextureSemantic.Metallic, ignoredEntries) != null ? 1f : 0f);
            SetFloat(material, "_Smoothness", 0.5f);
            SetFloat(material, "_MetallicRemapMin", 0f);
            SetFloat(material, "_MetallicRemapMax", 1f);
            SetFloat(material, "_AORemapMin", 0f);
            SetFloat(material, "_AORemapMax", 1f);
            SetFloat(material, "_SmoothnessRemapMin", 0f);
            SetFloat(material, "_SmoothnessRemapMax", 1f);

            SetFloat(material, "_MaterialID", useSpecularWorkflow ? 4f : 1f);
            SetTexture(material, "_SpecularColorMap", useSpecularWorkflow ? specular?.Texture : null);
            SetColour(material, "_SpecularColor", Color.white);

            SetTexture(material, "_HeightMap", height?.Texture);
            SetFloat(material, "_HeightMapParametrization", 1f);
            SetFloat(material, "_HeightPoMAmplitude", set.HeightAmplitudeCentimetres);
            SetFloat(material, "_HeightTessAmplitude", set.HeightAmplitudeCentimetres);
            SetFloat(material, "_HeightTessCenter", 0.5f);
            SetFloat(material, "_HeightCenter", 0.5f);
            SetFloat(material, "_HeightAmplitude", set.HeightAmplitudeCentimetres * 0.01f);
            SetFloat(material, "_DisplacementMode", 0f);

            SetTexture(material, "_EmissiveColorMap", emission?.Texture);
            SetColour(material, "_EmissiveColor", emission == null ? Color.black : Color.white);
            SetColour(material, "_EmissiveColorLDR", emission == null ? Color.black : Color.white);
            SetFloat(material, "_UseEmissiveIntensity", 0f);
            material.globalIlluminationFlags = emission == null
                ? MaterialGlobalIlluminationFlags.EmissiveIsBlack
                : MaterialGlobalIlluminationFlags.RealtimeEmissive;

            SurfaceMode surface = ResolveSurfaceMode(set, ignoredEntries);
            bool transparent = surface == SurfaceMode.Transparent;
            bool alphaClipping = surface == SurfaceMode.AlphaClipping;
            SetFloat(material, "_AlphaCutoffEnable", alphaClipping ? 1f : 0f);
            SetFloat(material, "_AlphaCutoff", set.AlphaCutoff);
            SetFloat(material, "_AlphaCutoffShadow", set.AlphaCutoff);
            SetFloat(material, "_AlphaCutoffPrepass", set.AlphaCutoff);
            SetFloat(material, "_AlphaCutoffPostpass", set.AlphaCutoff);
            SetFloat(material, "_BlendMode", 0f);
            SetFloat(material, "_EnableBlendModePreserveSpecularLighting", 1f);

            material.enableInstancing = set.EnableGpuInstancing;
            material.doubleSidedGI = set.DoubleSided;
            SetFloat(material, "_DoubleSidedEnable", set.DoubleSided ? 1f : 0f);
            SetVector(material, "_DoubleSidedConstants", set.DoubleSided ? new Vector4(1f, 1f, -1f, 0f) : new Vector4(1f, 1f, 1f, 0f));

            SetFloat(material, "_SurfaceType", transparent ? 1f : 0f);
            InvokeHdrpMaterialMethod("SetSurfaceType", material, transparent);
            InvokeHdrpMaterialMethod("ValidateMaterial", material);
        }

        static void ConfigureUrpMaterial(
            Material material,
            DetectedTextureSet set,
            Texture2D baseColor,
            Texture2D surfaceMap,
            Texture2D specularSmoothness,
            ISet<TextureEntry> ignoredEntries,
            bool useSpecularWorkflow,
            MaterialImportResult result)
        {
            TextureEntry normal = FirstUsable(set, TextureSemantic.Normal, ignoredEntries);
            TextureEntry emission = FirstUsable(set, TextureSemantic.Emission, ignoredEntries);
            TextureEntry height = FirstUsable(set, TextureSemantic.Height, ignoredEntries);

            SetTexture(material, "_BaseMap", baseColor);
            SetColour(material, "_BaseColor", Color.white);
            SetTexture(material, "_BumpMap", normal?.Texture);
            SetFloat(material, "_BumpScale", set.NormalScale);
            SetKeyword(material, "_NORMALMAP", normal != null);

            SetFloat(material, "_WorkflowMode", useSpecularWorkflow ? 0f : 1f);
            SetKeyword(material, "_SPECULAR_SETUP", useSpecularWorkflow);
            if (useSpecularWorkflow)
            {
                SetTexture(material, "_SpecGlossMap", specularSmoothness);
                SetColour(material, "_SpecColor", Color.white);
                SetTexture(material, "_MetallicGlossMap", null);
                SetKeyword(material, "_METALLICSPECGLOSSMAP", specularSmoothness != null);
            }
            else
            {
                SetTexture(material, "_MetallicGlossMap", surfaceMap);
                SetFloat(material, "_Metallic", surfaceMap == null ? 0f : 1f);
                SetTexture(material, "_SpecGlossMap", null);
                SetKeyword(material, "_METALLICSPECGLOSSMAP", surfaceMap != null);
            }

            SetFloat(material, "_Smoothness", surfaceMap != null || specularSmoothness != null ? 1f : 0.5f);
            SetFloat(material, "_SmoothnessTextureChannel", 0f);
            SetTexture(material, "_OcclusionMap", surfaceMap);
            SetFloat(material, "_OcclusionStrength", 1f);
            SetKeyword(material, "_OCCLUSIONMAP", surfaceMap != null);

            SetTexture(material, "_EmissionMap", emission?.Texture);
            SetColour(material, "_EmissionColor", emission == null ? Color.black : Color.white);
            SetKeyword(material, "_EMISSION", emission != null);
            material.globalIlluminationFlags = emission == null
                ? MaterialGlobalIlluminationFlags.EmissiveIsBlack
                : MaterialGlobalIlluminationFlags.RealtimeEmissive;

            if (height != null)
                result.Warnings.Add("URP/Lit has no built-in height input, so the height map was left unassigned.");

            ConfigureUrpSurface(material, ResolveSurfaceMode(set, ignoredEntries), set.AlphaCutoff, set.DoubleSided);
            InvokePipelineEditorMethod("UnityEditor.BaseShaderGUI", "SetupMaterialBlendMode", material);
            InvokePipelineEditorMethod("UnityEditor.Rendering.Universal.ShaderGUI.LitGUI", "SetMaterialKeywords", material);
            material.enableInstancing = set.EnableGpuInstancing;
            material.doubleSidedGI = set.DoubleSided;
        }

        static void ConfigureUrpSurface(Material material, SurfaceMode surface, float alphaCutoff, bool doubleSided)
        {
            bool transparent = surface == SurfaceMode.Transparent;
            bool alphaClip = surface == SurfaceMode.AlphaClipping;

            SetFloat(material, "_Surface", transparent ? 1f : 0f);
            SetFloat(material, "_Blend", 0f);
            SetFloat(material, "_AlphaClip", alphaClip ? 1f : 0f);
            SetFloat(material, "_Cutoff", alphaCutoff);
            SetFloat(material, "_Cull", doubleSided ? (float)CullMode.Off : (float)CullMode.Back);
            SetFloat(material, "_QueueOffset", 0f);
            SetKeyword(material, "_SURFACE_TYPE_TRANSPARENT", transparent);
            SetKeyword(material, "_ALPHATEST_ON", alphaClip);
            SetKeyword(material, "_ALPHAPREMULTIPLY_ON", false);

            if (transparent)
            {
                SetFloat(material, "_SrcBlend", (float)BlendMode.SrcAlpha);
                SetFloat(material, "_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                SetFloat(material, "_ZWrite", 0f);
                material.SetOverrideTag("RenderType", "Transparent");
                material.renderQueue = (int)RenderQueue.Transparent;
            }
            else
            {
                SetFloat(material, "_SrcBlend", (float)BlendMode.One);
                SetFloat(material, "_DstBlend", (float)BlendMode.Zero);
                SetFloat(material, "_ZWrite", 1f);
                material.SetOverrideTag("RenderType", alphaClip ? "TransparentCutout" : "Opaque");
                material.renderQueue = alphaClip ? (int)RenderQueue.AlphaTest : (int)RenderQueue.Geometry;
            }
        }

        static SurfaceMode ResolveSurfaceMode(DetectedTextureSet set, ISet<TextureEntry> ignoredEntries)
        {
            return set.SurfaceMode == SurfaceMode.Auto
                ? (FirstUsable(set, TextureSemantic.Opacity, ignoredEntries) != null ? SurfaceMode.AlphaClipping : SurfaceMode.Opaque)
                : set.SurfaceMode;
        }

        static void InvokeHdrpMaterialMethod(string methodName, params object[] arguments)
        {
            Type hdMaterial = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("UnityEngine.Rendering.HighDefinition.HDMaterial", false))
                .FirstOrDefault(type => type != null);
            if (hdMaterial == null)
                return;

            MethodInfo method = hdMaterial.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(candidate => candidate.Name == methodName && candidate.GetParameters().Length == arguments.Length);
            method?.Invoke(null, arguments);
        }

        static void InvokePipelineEditorMethod(string typeName, string methodName, params object[] arguments)
        {
            Type type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(typeName, false))
                .FirstOrDefault(candidate => candidate != null);
            if (type == null)
                return;

            MethodInfo method = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(candidate =>
                {
                    ParameterInfo[] parameters = candidate.GetParameters();
                    return candidate.Name == methodName &&
                           parameters.Length >= arguments.Length &&
                           parameters.Skip(arguments.Length).All(parameter => parameter.IsOptional);
                });
            if (method == null)
                return;

            ParameterInfo[] methodParameters = method.GetParameters();
            object[] invocationArguments = new object[methodParameters.Length];
            Array.Copy(arguments, invocationArguments, arguments.Length);
            for (int i = arguments.Length; i < invocationArguments.Length; i++)
                invocationArguments[i] = Type.Missing;
            method.Invoke(null, invocationArguments);
        }

        static bool HasMetalInput(DetectedTextureSet set, ISet<TextureEntry> ignoredEntries)
        {
            return FirstUsable(set, TextureSemantic.Metallic, ignoredEntries) != null ||
                   FirstUsable(set, TextureSemantic.PackedOrm, ignoredEntries) != null ||
                   FirstUsable(set, TextureSemantic.PackedRma, ignoredEntries) != null ||
                   FirstUsable(set, TextureSemantic.PackedMra, ignoredEntries) != null ||
                   FirstUsable(set, TextureSemantic.MetallicRoughness, ignoredEntries) != null ||
                   FirstUsable(set, TextureSemantic.HdrpMaskMap, ignoredEntries) != null;
        }

        static TextureEntry FirstUsable(DetectedTextureSet set, TextureSemantic semantic, ISet<TextureEntry> ignoredEntries)
        {
            return set.Textures.FirstOrDefault(entry =>
                entry.Texture != null &&
                entry.Semantic == semantic &&
                (ignoredEntries == null || !ignoredEntries.Contains(entry)));
        }

        static void SetTexture(Material material, string property, Texture texture)
        {
            if (material.HasProperty(property))
                material.SetTexture(property, texture);
        }

        static void SetFloat(Material material, string property, float value)
        {
            if (material.HasProperty(property))
                material.SetFloat(property, value);
        }

        static void SetColour(Material material, string property, Color value)
        {
            if (material.HasProperty(property))
                material.SetColor(property, value);
        }

        static void SetVector(Material material, string property, Vector4 value)
        {
            if (material.HasProperty(property))
                material.SetVector(property, value);
        }

        static void SetKeyword(Material material, string keyword, bool enabled)
        {
            if (enabled)
                material.EnableKeyword(keyword);
            else
                material.DisableKeyword(keyword);
        }
    }
}