# ExoLabs PBR Material Importer

An Editor-only Unity package for turning third-party PBR texture exports into ready-to-use `HDRP/Lit` or `Universal Render Pipeline/Lit` materials. Drop texture files or folders into one window; the tool groups texture sets, detects their roles, packs pipeline-ready maps, configures import settings, and creates materials.

## Requirements

- Unity 2022.3 or newer
- HDRP 14 or newer when creating HDRP materials, or URP 14 or newer when creating URP materials

The importer deliberately has no hard package dependency on either render pipeline. This keeps the Git/UPM package installable in HDRP-only and URP-only projects; the selected pipeline package must already be installed in the destination project.

## Install

To use the embedded copy, keep the complete `PBRMaterialImporter` folder under `Assets`.

For cross-project use, either copy that folder into another project's `Assets` directory or install the Git repository through Unity Package Manager:

1. Open **Window > Package Manager**.
2. Choose **+ > Add package from git URL**.
3. Enter `https://github.com/Braneloc/PBRMaterialImporter.git#v1.1.1`.

The Editor code is isolated in the `ExoLabs.PBRMaterialImporter.Editor` assembly and does not reference project-specific scripts or compile directly against HDRP/URP assemblies.

## Use

1. Open **Tools > Rendering > PBR Material Importer**.
2. Drag textures or folders from the Project window or Windows Explorer onto the drop area. **Assets > Create PBR Materials from Textures** is also available.
3. Leave **Pipeline** on **Auto** to use the active render pipeline, or explicitly select **High Definition** or **Universal**.
4. Review the detected sets. Texture roles, source channels, workflow, surface mode, normal strength, alpha cutoff, and material name remain editable.
5. Choose output placement and click **Create HDRP/URP Material(s)**.

External files are copied into `Assets/PBRMaterialImports/SourceTextures` before processing. Project textures stay where they are.

By default, sources from one directory produce an `HDRP` or `URP` subfolder beside them. Sets assembled from multiple locations fall back to `Assets/PBRMaterialImports/<MaterialName>`. Outputs can instead be written beside the sources or into a selected custom `Assets` folder.

## Recognized workflows

Common suffixes from Substance 3D Painter, Quixel/Megascans, Blender, Poly Haven, glTF, Unreal-style exports, and generic PBR tools are recognized.

| Input role | Example suffixes |
| --- | --- |
| Base color | `BaseColor`, `Albedo`, `Diffuse`, `Diff`, `Color`, `_D` |
| Normal | `Normal`, `NormalGL`, `NormalDX`, `Nor_GL`, `_N` |
| Metal | `Metallic`, `Metalness`, `Metal`, `_M` |
| Rough/smooth | `Roughness`, `Rough`, `Smoothness`, `Glossiness`, `Gloss` |
| Occlusion | `AO`, `AmbientOcclusion`, `Occlusion` |
| Other | `Height`, `Displacement`, `Emission`, `Opacity`, `DetailMask`, `Specular` |

Resolution and UDIM suffixes such as `_4K` and `_1001` do not prevent matching. A single unlabelled texture in a set is inferred as its Base Color, supporting exports where the color texture has no suffix.

### Packed input conversion

The primary generated surface pack uses this layout:

| Channel | Data | HDRP use | URP use |
| --- | --- | --- | --- |
| R | Metallic | Mask Map | Metallic Gloss Map |
| G | Ambient occlusion | Mask Map | Occlusion Map |
| B | Detail mask | Mask Map | Reserved |
| A | Smoothness | Mask Map | Metallic Gloss Map |

For URP metallic workflow, one generated texture is assigned to both Metallic and Occlusion slots. URP samples the channels it needs. For URP specular workflow, the importer generates an sRGB Specular Smoothness texture with specular color in RGB and smoothness in alpha.

The importer converts these common source layouts:

| Source suffix | Source channels |
| --- | --- |
| `ORM` / `ARM` | R=AO, G=roughness, B=metallic |
| `RMA` | R=roughness, G=metallic, B=AO |
| `MRA` / `MRAO` | R=metallic, G=roughness, B=AO |
| `MetallicRoughness` | G=roughness, B=metallic |
| `MaskMap` / `HDRPMask` | R=metallic, G=AO, B=detail, A=smoothness |

Separate roughness is inverted into smoothness. Separate smoothness/gloss is copied directly. Maps with different resolutions are bilinearly resampled to the largest input resolution.

Metallic and specular-color workflows are supported. **Auto** selects specular color only when a specular map exists without metallic input.

## Import safeguards

- **Ignore semantically blank maps** is enabled by default. All-black metallic or emission maps, all-white AO or opacity maps, and height maps with no variation are ignored. Source files are reported but never deleted.
- DirectX normal filenames such as `NormalDX` enable Unity's green-channel flip. OpenGL names leave it unchanged.
- Source textures are made readable and uncompressed only temporarily during CPU packing. Their previous settings are restored afterward.
- Data textures and generated surface packs are imported as linear; color, emission, and specular packs are sRGB; normals use Unity's Normal Map importer.
- Separate opacity is packed into a generated Base Color alpha texture. The original color texture is not modified.
- Height is assigned conservatively in HDRP with displacement disabled. URP/Lit has no built-in height input, so URP imports report and leave height unassigned.

## Generated files

Depending on the inputs and pipeline, each material set can create:

- `<MaterialName>.mat`
- `<MaterialName>_MaskMap.png` for HDRP
- `<MaterialName>_MetallicOcclusionSmoothness.png` for URP metallic workflow
- `<MaterialName>_SpecularSmoothness.png` for URP specular workflow
- `<MaterialName>_BaseColorAlpha.png` when separate opacity is supplied

Stable filenames are updated on repeat imports by default. Disable **Update matching generated assets** to create unique copies.

## License

MIT. See [LICENSE.md](LICENSE.md).

## Party on dudes  
![](https://avatars.githubusercontent.com/u/9757397?s=96&v=4)
