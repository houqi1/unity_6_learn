# Shell Fur (GPU Instancing)

URP shell-fur implementation for Unity 6. Multiple transparent-cutout **shell layers** are drawn in **one GPU instanced draw call**. The shader uses `unity_InstanceID` as the shell index and extrudes vertices along normals.

## How it works

1. `ShellFurRenderer` calls `Graphics.DrawMeshInstanced` with N copies of the same mesh/matrix.
2. Vertex shader: `layer = instanceID / (shellCount - 1)`, offset = `normal * layer * furLength` + gravity bend.
3. Fragment shader: procedural strand grid (or density texture) discards fragments so only “hair cross-sections” remain.
4. Higher layers use thinner strands and tip color → volumetric fur look.

## Quick start

1. Open the project (URP).
2. Menu: **GameObject → 3D Object → Shell Fur Sphere**
3. Or add `ShellFurRenderer` to any object with a `MeshFilter`, assign `Materials/ShellFur_Default`.

## Tuning

| Property | Role |
|----------|------|
| Shell Count | More layers = smoother fur, higher cost (16–48 typical) |
| Fur Length | Shell extrusion distance |
| Strand Density | Procedural grid frequency |
| Thickness | Strand radius / taper base |
| Gravity | Quadratic tip droop |
| Use Procedural Strands | On = hash strands; Off = `_FurMap` density texture |

## Files

- `Shaders/ShellFur.shader` + `ShellFur.hlsl` — URP forward / shadow / depth
- `Scripts/ShellFurRenderer.cs` — instanced draw
- `Editor/ShellFurSetup.cs` — demo object + default assets
- `Materials/ShellFur_Default.mat` — created via menu if missing

## Notes

- Enable **GPU Instancing** on the material (script forces it on).
- Source `MeshRenderer` is disabled by default so only shells draw.
- Alpha-to-coverage (`AlphaToMask`) softens strand edges when MSAA is on.
- For mobile, prefer 16–24 shells and lower density.
