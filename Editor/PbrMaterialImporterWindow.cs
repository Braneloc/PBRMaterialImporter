using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ExoLabs.PBRMaterialImporter
{
    internal sealed class PbrMaterialImporterWindow : EditorWindow
    {
        const float DropAreaHeight = 92f;

        [SerializeField] List<DetectedTextureSet> textureSets = new List<DetectedTextureSet>();
        [SerializeField] RenderPipelineTarget pipeline = RenderPipelineTarget.Auto;
        [SerializeField] OutputMode outputMode = OutputMode.GeneratedSubfolder;
        [SerializeField] DefaultAsset customOutputFolder;
        [SerializeField] bool configureSourceImporters = true;
        [SerializeField] bool combineOpacityWithBaseColor = true;
        [SerializeField] bool discardNeutralTextures = true;
        [SerializeField] bool updateExistingAssets = true;
        [SerializeField] Vector2 scrollPosition;
        [SerializeField] string lastStatus;
        [SerializeField] MessageType lastStatusType = MessageType.Info;

        GUIStyle dropStyle;
        GUIStyle smallNoteStyle;

        [MenuItem("Tools/ExoLabs/PBR Material Importer")]
        internal static PbrMaterialImporterWindow Open()
        {
            PbrMaterialImporterWindow window = GetWindow<PbrMaterialImporterWindow>();
            window.titleContent = new GUIContent("PBR Importer", EditorGUIUtility.IconContent("Material Icon").image);
            window.minSize = new Vector2(570f, 480f);
            window.Show();
            return window;
        }

        [MenuItem("Assets/Create PBR Materials from Textures", false, 2200)]
        static void OpenFromSelection()
        {
            PbrMaterialImporterWindow window = Open();
            window.AddObjects(Selection.objects, Array.Empty<string>());
        }

        [MenuItem("Assets/Create PBR Materials from Textures", true)]
        static bool ValidateOpenFromSelection()
        {
            return TextureImportUtility.CollectProjectTextures(Selection.objects).Count > 0;
        }

        void OnEnable()
        {
            textureSets ??= new List<DetectedTextureSet>();
            titleContent = new GUIContent("PBR Importer", EditorGUIUtility.IconContent("Material Icon").image);
        }

        void OnGUI()
        {
            EnsureStyles();
            DrawHeader();
            DrawDropArea();
            DrawToolbar();
            DrawOutputSettings();

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
            if (textureSets.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "Drop a PBR texture set or a folder above. Filenames are grouped into materials and classified automatically; every role remains editable before creation.",
                    MessageType.Info);
            }
            else
            {
                for (int i = 0; i < textureSets.Count; i++)
                {
                    if (DrawTextureSet(textureSets[i], i))
                    {
                        textureSets.RemoveAt(i);
                        i--;
                    }
                }
            }
            EditorGUILayout.EndScrollView();

            DrawFooter();
        }

        void DrawHeader()
        {
            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("ExoLabs PBR Material Importer", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Substance, Quixel/Megascans, Blender, glTF, Unreal-style, and generic PBR exports",
                EditorStyles.miniLabel);
            EditorGUILayout.Space(3f);
        }

        void DrawDropArea()
        {
            Rect rect = GUILayoutUtility.GetRect(0f, DropAreaHeight, GUILayout.ExpandWidth(true));
            GUI.Box(rect, "Drop textures or folders here\n(Project assets and files from Explorer are supported)", dropStyle);

            Event current = Event.current;
            if (!rect.Contains(current.mousePosition) || (current.type != EventType.DragUpdated && current.type != EventType.DragPerform))
                return;

            bool supported = DragAndDrop.objectReferences.Any(IsTextureOrFolder) ||
                             DragAndDrop.paths.Any(path => TextureImportUtility.IsSupportedImage(path) || Directory.Exists(path));
            DragAndDrop.visualMode = supported ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
            if (current.type == EventType.DragPerform && supported)
            {
                DragAndDrop.AcceptDrag();
                AddObjects(DragAndDrop.objectReferences, DragAndDrop.paths);
            }
            current.Use();
        }

        void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Button("Add Selection", EditorStyles.toolbarButton, GUILayout.Width(100f)))
                    AddObjects(Selection.objects, Array.Empty<string>());
                GUILayout.FlexibleSpace();
                EditorGUILayout.LabelField(textureSets.Count == 1 ? "1 material set" : textureSets.Count + " material sets", EditorStyles.miniLabel, GUILayout.Width(100f));
                using (new EditorGUI.DisabledScope(textureSets.Count == 0))
                {
                    if (GUILayout.Button("Clear", EditorStyles.toolbarButton, GUILayout.Width(52f)))
                    {
                        Undo.RecordObject(this, "Clear PBR importer");
                        textureSets.Clear();
                        lastStatus = string.Empty;
                    }
                }
            }
        }

        void DrawOutputSettings()
        {
            EditorGUILayout.Space(5f);
            EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
            pipeline = (RenderPipelineTarget)EditorGUILayout.EnumPopup(
                new GUIContent("Pipeline", "Auto uses the active render pipeline. Explicit HDRP/URP choices are useful when both packages are installed."),
                pipeline);
            outputMode = (OutputMode)EditorGUILayout.EnumPopup(new GUIContent("Placement", "Where generated mask maps, combined alpha textures, and materials are saved."), outputMode);
            if (outputMode == OutputMode.CustomFolder)
                customOutputFolder = (DefaultAsset)EditorGUILayout.ObjectField("Custom Folder", customOutputFolder, typeof(DefaultAsset), false);

            using (new EditorGUI.IndentLevelScope())
            {
                configureSourceImporters = EditorGUILayout.ToggleLeft(
                    new GUIContent("Configure source texture importers", "Sets color/data color space, normal-map type, mipmaps, and DirectX normal green-channel flipping."),
                    configureSourceImporters);
                combineOpacityWithBaseColor = EditorGUILayout.ToggleLeft(
                    new GUIContent("Pack separate opacity into Base Color alpha", "Creates a derived BaseColorAlpha PNG without modifying the source color texture."),
                    combineOpacityWithBaseColor);
                discardNeutralTextures = EditorGUILayout.ToggleLeft(
                    new GUIContent("Ignore semantically blank maps", "Ignores neutral inputs such as black metallic/emission, white AO/opacity, and height maps with no variation. Source files are never deleted."),
                    discardNeutralTextures);
                updateExistingAssets = EditorGUILayout.ToggleLeft(
                    new GUIContent("Update matching generated assets", "Uses stable filenames and updates existing generated materials and textures. Disable to create uniquely named copies."),
                    updateExistingAssets);
            }

            string placementMessage = outputMode switch
            {
                OutputMode.GeneratedSubfolder => "An HDRP or URP subfolder is created beside each set when all its sources share one folder. Mixed-location sets use Assets/PBRMaterialImports.",
                OutputMode.BesideSourceTextures => "Outputs are written beside the sources when they share one folder. Mixed-location sets use Assets/PBRMaterialImports.",
                _ => "All generated outputs are written to the selected Assets folder."
            };
            EditorGUILayout.LabelField(placementMessage, smallNoteStyle);
            EditorGUILayout.Space(5f);
        }

        bool DrawTextureSet(DetectedTextureSet set, int index)
        {
            bool removeSet = false;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    set.Expanded = EditorGUILayout.Foldout(set.Expanded, set.MaterialName, true, EditorStyles.foldoutHeader);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button(EditorGUIUtility.IconContent("TreeEditor.Trash"), GUIStyle.none, GUILayout.Width(22f), GUILayout.Height(20f)))
                        removeSet = true;
                }

                if (!set.Expanded)
                    return removeSet;

                EditorGUI.BeginChangeCheck();
                set.MaterialName = EditorGUILayout.TextField("Material Name", set.MaterialName);
                using (new EditorGUILayout.HorizontalScope())
                {
                    set.Workflow = (MaterialWorkflow)EditorGUILayout.EnumPopup("Workflow", set.Workflow);
                    set.SurfaceMode = (SurfaceMode)EditorGUILayout.EnumPopup("Surface", set.SurfaceMode);
                }

                if (set.SurfaceMode == SurfaceMode.AlphaClipping || (set.SurfaceMode == SurfaceMode.Auto && set.Has(TextureSemantic.Opacity)))
                    set.AlphaCutoff = EditorGUILayout.Slider("Alpha Cutoff", set.AlphaCutoff, 0f, 1f);
                set.NormalScale = EditorGUILayout.Slider("Normal Scale", set.NormalScale, 0f, 4f);
                if (set.Has(TextureSemantic.Height))
                    set.HeightAmplitudeCentimeters = EditorGUILayout.FloatField("Height Amplitude (cm)", set.HeightAmplitudeCentimeters);
                using (new EditorGUILayout.HorizontalScope())
                {
                    set.EnableGpuInstancing = EditorGUILayout.ToggleLeft("GPU Instancing", set.EnableGpuInstancing, GUILayout.Width(120f));
                    set.DoubleSided = EditorGUILayout.ToggleLeft("Double Sided", set.DoubleSided, GUILayout.Width(110f));
                }
                if (EditorGUI.EndChangeCheck())
                    EditorUtility.SetDirty(this);

                EditorGUILayout.Space(3f);
                EditorGUILayout.LabelField("Texture assignments", EditorStyles.boldLabel);
                for (int textureIndex = 0; textureIndex < set.Textures.Count; textureIndex++)
                {
                    if (DrawTextureEntry(set.Textures[textureIndex]))
                    {
                        set.Textures.RemoveAt(textureIndex);
                        textureIndex--;
                    }
                }

                foreach (string issue in set.Validate())
                    EditorGUILayout.HelpBox(issue, issue.StartsWith("Enter", StringComparison.Ordinal) ? MessageType.Error : MessageType.Warning);
            }
            EditorGUILayout.Space(3f);
            return removeSet;
        }

        bool DrawTextureEntry(TextureEntry entry)
        {
            bool remove = false;
            using (new EditorGUILayout.HorizontalScope())
            {
                entry.Texture = (Texture2D)EditorGUILayout.ObjectField(entry.Texture, typeof(Texture2D), false, GUILayout.Width(180f));
                entry.Semantic = (TextureSemantic)EditorGUILayout.EnumPopup(entry.Semantic, GUILayout.MinWidth(135f));
                if (UsesSingleChannel(entry.Semantic))
                    entry.Channel = (TextureChannel)EditorGUILayout.EnumPopup(entry.Channel, GUILayout.Width(82f));
                else if (entry.Semantic == TextureSemantic.Normal)
                    entry.FlipNormalGreen = GUILayout.Toggle(entry.FlipNormalGreen, new GUIContent("Flip G", "Enable for DirectX-style normal maps."), GUILayout.Width(58f));
                else
                    GUILayout.Space(82f);
                if (GUILayout.Button("×", GUILayout.Width(22f)))
                    remove = true;
            }

            if (!string.IsNullOrEmpty(entry.DetectionNote))
            {
                using (new EditorGUI.IndentLevelScope())
                    EditorGUILayout.LabelField(entry.DetectionNote, smallNoteStyle);
            }
            return remove;
        }

        void DrawFooter()
        {
            if (!string.IsNullOrEmpty(lastStatus))
                EditorGUILayout.HelpBox(lastStatus, lastStatusType);

            bool shaderAvailable = PbrMaterialBuilder.TryResolvePipeline(pipeline, out RenderPipelineTarget resolvedPipeline, out _, out string pipelineError);
            bool customFolderValid = outputMode != OutputMode.CustomFolder ||
                                     (customOutputFolder != null && AssetDatabase.IsValidFolder(AssetDatabase.GetAssetPath(customOutputFolder)) && AssetDatabase.GetAssetPath(customOutputFolder).StartsWith("Assets", StringComparison.Ordinal));
            bool canCreate = textureSets.Count > 0 && shaderAvailable && customFolderValid &&
                             textureSets.All(set => !string.IsNullOrWhiteSpace(set.MaterialName) && set.Textures.Any(entry => entry.Texture != null));

            if (!shaderAvailable)
                EditorGUILayout.HelpBox(pipelineError, MessageType.Error);
            else if (!customFolderValid)
                EditorGUILayout.HelpBox("Choose a custom folder inside Assets.", MessageType.Error);

            using (new EditorGUI.DisabledScope(!canCreate))
            {
                string pipelineLabel = shaderAvailable ? PbrMaterialBuilder.PipelineLabel(resolvedPipeline) : "PBR";
                if (GUILayout.Button(textureSets.Count == 1 ? $"Create {pipelineLabel} Material" : $"Create {pipelineLabel} Materials", GUILayout.Height(34f)))
                    CreateMaterials();
            }
            EditorGUILayout.Space(5f);
        }

        void AddObjects(IEnumerable<UnityEngine.Object> objects, IEnumerable<string> dragPaths)
        {
            IReadOnlyList<Texture2D> projectTextures = TextureImportUtility.CollectProjectTextures(objects);
            IReadOnlyList<Texture2D> externalTextures = TextureImportUtility.ImportExternalPaths(dragPaths);
            List<Texture2D> textures = projectTextures.Concat(externalTextures)
                .Where(texture => texture != null)
                .GroupBy(AssetDatabase.GetAssetPath, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            if (textures.Count == 0)
            {
                lastStatus = "No supported textures were found in that selection.";
                lastStatusType = MessageType.Warning;
                Repaint();
                return;
            }

            Undo.RecordObject(this, "Add textures to PBR importer");
            int added = 0;
            foreach (Texture2D texture in textures)
            {
                string path = AssetDatabase.GetAssetPath(texture);
                if (textureSets.SelectMany(set => set.Textures).Any(entry => string.Equals(entry.AssetPath, path, StringComparison.OrdinalIgnoreCase)))
                    continue;

                TextureNameAnalysis analysis = TextureNameDetector.Analyze(texture.name);
                string directory = (Path.GetDirectoryName(path) ?? "Assets").Replace('\\', '/');
                string sourceKey = directory.ToLowerInvariant() + "|" + analysis.Stem.ToLowerInvariant();
                DetectedTextureSet set = textureSets.FirstOrDefault(candidate => string.Equals(candidate.SourceKey, sourceKey, StringComparison.Ordinal));
                if (set == null)
                {
                    set = new DetectedTextureSet(sourceKey, analysis.Stem);
                    textureSets.Add(set);
                }
                set.Textures.Add(new TextureEntry(texture, analysis));
                added++;
            }

            foreach (DetectedTextureSet set in textureSets)
                InferUnlabelledBaseColor(set);

            textureSets = textureSets
                .OrderBy(set => set.MaterialName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            lastStatus = added == 0 ? "Those textures are already in the importer." : $"Added {added} texture{(added == 1 ? string.Empty : "s")} and detected {textureSets.Count} material set{(textureSets.Count == 1 ? string.Empty : "s")}.";
            lastStatusType = added == 0 ? MessageType.Info : MessageType.None;
            EditorUtility.SetDirty(this);
            Repaint();
        }

        static void InferUnlabelledBaseColor(DetectedTextureSet set)
        {
            if (set.Has(TextureSemantic.BaseColor))
                return;
            List<TextureEntry> unknown = set.Textures.Where(entry => entry.Texture != null && entry.Semantic == TextureSemantic.Unknown).ToList();
            if (unknown.Count != 1)
                return;
            unknown[0].Semantic = TextureSemantic.BaseColor;
            unknown[0].DetectionNote = "Inferred as Base Color because it is the only unlabelled texture in this set";
        }

        void CreateMaterials()
        {
            MaterialImportSettings settings = new MaterialImportSettings
            {
                Pipeline = pipeline,
                OutputMode = outputMode,
                CustomOutputFolder = customOutputFolder == null ? string.Empty : AssetDatabase.GetAssetPath(customOutputFolder),
                ConfigureSourceImporters = configureSourceImporters,
                CombineOpacityWithBaseColor = combineOpacityWithBaseColor,
                DiscardNeutralTextures = discardNeutralTextures,
                UpdateExistingAssets = updateExistingAssets
            };

            List<Material> createdMaterials = new List<Material>();
            List<string> warnings = new List<string>();
            try
            {
                for (int i = 0; i < textureSets.Count; i++)
                {
                    DetectedTextureSet set = textureSets[i];
                    EditorUtility.DisplayProgressBar("Creating PBR materials", set.MaterialName, (float)i / textureSets.Count);
                    MaterialImportResult result = PbrMaterialBuilder.Build(set, settings);
                    if (result.Material != null)
                        createdMaterials.Add(result.Material);
                    warnings.AddRange(result.Warnings.Select(warning => set.MaterialName + ": " + warning));
                }

                Selection.objects = createdMaterials.Cast<UnityEngine.Object>().ToArray();
                if (createdMaterials.Count > 0)
                    EditorGUIUtility.PingObject(createdMaterials[0]);
                lastStatus = $"Created or updated {createdMaterials.Count} {PbrMaterialBuilder.PipelineLabel(settings.Pipeline)} material{(createdMaterials.Count == 1 ? string.Empty : "s")}.";
                if (warnings.Count > 0)
                    lastStatus += " " + string.Join(" ", warnings);
                lastStatusType = warnings.Count > 0 ? MessageType.Warning : MessageType.Info;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                lastStatus = "Import failed: " + exception.Message;
                lastStatusType = MessageType.Error;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        static bool UsesSingleChannel(TextureSemantic semantic)
        {
            return semantic == TextureSemantic.Metallic ||
                   semantic == TextureSemantic.Roughness ||
                   semantic == TextureSemantic.Smoothness ||
                   semantic == TextureSemantic.AmbientOcclusion ||
                   semantic == TextureSemantic.Height ||
                   semantic == TextureSemantic.Opacity ||
                   semantic == TextureSemantic.DetailMask;
        }

        static bool IsTextureOrFolder(UnityEngine.Object obj)
        {
            return obj is Texture2D || AssetDatabase.IsValidFolder(AssetDatabase.GetAssetPath(obj));
        }

        void EnsureStyles()
        {
            dropStyle ??= new GUIStyle(EditorStyles.helpBox)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                wordWrap = true
            };
            smallNoteStyle ??= new GUIStyle(EditorStyles.miniLabel)
            {
                wordWrap = true,
                normal = { textColor = EditorGUIUtility.isProSkin ? new Color(0.67f, 0.67f, 0.67f) : new Color(0.32f, 0.32f, 0.32f) }
            };
        }
    }
}
