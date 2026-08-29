# Changelog

## 1.1.1 - 2026-08-29

- Made package contents visible in Unity's Project window with `hideInEditor: false`.
- Matched the Exo-Labs package display-name style and added the standard README signature.
- Completed documentation, license, changelog, Unity-release, and author manifest metadata.

## 1.1.0 - 2026-08-29

- Rebranded as ExoLabs PBR Material Importer under the `ExoLabs.PBRMaterialImporter` namespace.
- Added automatic and explicit HDRP/URP material generation without a hard dependency on either pipeline package.
- Added URP metallic and specular workflows, including metallic/AO/smoothness and specular/smoothness packing.
- Renamed the package and assemblies for portable Git/UPM distribution.

## 1.0.0 - 2026-08-29

- Initial drag-and-drop HDRP texture importer.
- Automatic filename grouping and semantic detection.
- HDRP mask packing from separate roughness/smoothness data and common packed layouts.
- Metallic and specular-color workflows.
- External file/folder ingestion, generated output folders, alpha packing, DirectX normal correction, and neutral-map filtering.
