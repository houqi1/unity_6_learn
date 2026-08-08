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
| Gravity (static) | Only when Dynamics **off**: `pow(layer, GravityPower)` formula droop |
| Dynamics | Spring / Verlet / **Grass**. Shell = **pure extrude** + chain δ (shader skips GravityBend while chain on) |
| Guide Chain Length | Absolute root→tip chain length (world). **0** = Fur Length × Length Scale |
| Length Scale | Used only when Guide Chain Length is 0 |
| Guide Offset Scale | Shell δ = (chainPos(h) − root) × scale; h = shell layer 0…1 |
| Mode → Grass | HTML Interactive Grass: fixed segment length + hang stiffness |
| Grass Stiffness | Recover speed toward hang ideal (~0.01–0.25 like the HTML slider) |
| Gravity As Rest Pose | Spring/Verlet only. ON: rest = static gravity shell pose; OFF: live g / chase previous. **Ignored in Grass** |
| Follow Tension / Min | Root→tip tension falloff (Spring); Min = tip softness |
| Velocity Damping / Min | Root→tip velocity-keep rise (Spring); Damping = tip max keep |
| Particle Gravity | Spring/Verlet accel scale (try 0.3–2); ignored in Grass |
| Use Procedural Strands | On = hash strands; Off = `_FurMap` density texture |

## Smooth normals → vertex colors

Softer shell extrusion without changing lighting normals.

**Why position weld?** Unity/FBX meshes split vertices at UV seams and hard edges. Averaging only by vertex *index* barely smooths. The baker **welds by position**, then angle-weights face normals (optional max face angle).

1. Menu **Tools → Shell Fur → Bake Smooth Normals To Vertex Colors** (or **From Selection**).
2. Prefer **Create Mesh Copy** for FBX (writes `*_SmoothN_VC.asset`).
3. **Max Smoothing Angle** = 180 for fully smooth; lower keeps hard edges.
4. On material / `ShellFurRenderer`: enable **Use Smooth Normals (Vertex Color)**.

Encoding: `vertexColor.rgb = normalOS * 0.5 + 0.5`.  
Extrusion uses smooth normals; lighting still uses mesh normals.

**Skinned:** VC is not bone-skinned — with the toggle on, `ShellFurRenderer` re-bakes position-welded smooth normals after each `BakeMesh`.

## GPU Skin Shell Fur (Scheme B — production skinned path)

**Compute skins once → DrawMeshInstanced extrudes N shells** (no per-frame smooth recompute).

### CS Fins B2 (true Geometry-Shader migration)

After skinning, a second compute pass walks a **prebuilt edge table**, tests **view-dependent silhouette**, and **emits only erect fins** into a compact vertex buffer (multi-segment + gravity). Draw with **`Graphics.DrawProceduralIndirect`** — no Geometry Shader.

| Stage | Role |
|-------|------|
| Offline | `ShellFurFinEdgeBuilder` → `FinEdge[]` from bind fur mesh |
| CS Skin | bind verts → world `SkinnedVertex` buffer |
| CS Fin | edge silhouette → compact fin triangles (`FinVertex`) + indirect args |
| Draw | shells instanced, then procedural fins (transparent) |

Static-mesh fins on **`ShellFurRenderer`** are unchanged (CPU fin mesh + VS silhouette).

### Setup

1. Enable **Read/Write** on the character FBX (or use the build menu).
2. Optional: **Tools → Shell Fur → Build GPU Skin Fur Mesh From Selection**  
   - Creates a **separate DATA** `.asset` under `Assets/ShellFur/Meshes/`.  
   - **Does not** replace `SkinnedMeshRenderer.sharedMesh` or shrink the character.  
   - Do **not** assign that asset to SMR.Mesh — only to `Bind Fur Mesh Override` if you want.
3. Add component **`ShellFurGpuSkinRenderer`** next to `SkinnedMeshRenderer`.
4. Assign material using shader **`Custom/ShellFurGpuSkinned`**.
5. Assign **Skin Compute** = `Assets/ShellFur/Shaders/ShellFurGpuSkin.compute`.
6. Assign **Fin Compute** = `Assets/ShellFur/Shaders/ShellFurGpuFin.compute` (Editor auto-loads if empty).
7. Set **Fur Material Slots** (e.g. tails `1,2`), enable hide source slots / hide base as needed.
8. Enable **Fins (CS B2)**; tune segments / silhouette / opacity. Play animation — shells + fins follow bones.

Design doc: `Docs/GPU-Skinning-ShellFur-Design.md`.

Legacy BakeMesh path remains on **`ShellFurRenderer`**.

## Skinned mesh (animation) — legacy BakeMesh

Shells support **SkinnedMeshRenderer** with full animation via per-frame `BakeMesh`:

1. Add `ShellFurRenderer` on the same GameObject as the `SkinnedMeshRenderer` (or a parent that finds it in children).
2. Leave **Bake Skinned Every Frame** on.
3. Use **Material Slot Only** if only one material slot should be furry.
4. Assign **Fur Material**; play the Animator — shells follow the skinned pose.

**Fins on this path:** still static-mesh only. For animated fins use **`ShellFurGpuSkinRenderer`** (CS B2).

Tips:

- For off-screen characters that must keep baking, enable **Update When Offscreen** on the `SkinnedMeshRenderer`.
- Bake cost scales with mesh density × shell layers (draw), not with bone count directly.

## Material slots only (Scheme A)

Use fur on **one or more material slots** of a multi-submesh model; the rest stay on the source renderer.

1. Mesh has multiple materials / submeshes (e.g. body + fur regions).
2. On `ShellFurRenderer` enable **Use Material Slot Only**.
3. Set **Fur Material Slots** to the indices (e.g. `1`, `3` — same as `materials[]` / submesh index).
4. Keep **Hide Source Fur Slot** on so those slots are not double-drawn.
5. Assign `Fur Material` (ShellFur); other materials stay on MeshRenderer / SkinnedMeshRenderer.

When the switch is **off**, behaviour is full-mesh fur (and `Disable Source Renderer` applies as before).

Mesh must be **Read/Write** enabled for fins (static). Each listed submesh must exist.

## Fins (shell + fin)

Optional **edge fins** fill silhouette gaps that shells miss when viewed from the side.

1. `ShellFurFinBuilder` walks mesh edges → fin strip mesh (needs **Read/Write** on the mesh).
2. Runtime VS extrudes tips only near the **silhouette** (`faceA/faceB` vs view).
3. Fragment reuses the same density cutoff / lighting as shells (`Custom/ShellFurFin`).

On `ShellFurRenderer`:

| Property | Role |
|----------|------|
| Enable Fins | Toggle fin draw |
| Fin Segments | Quads along each fin (4+ for gravity curves) |
| Rebuild Fin Mesh (context menu) | Regenerate strips after mesh / segment change |
| Silhouette Sharpness | Contour band sensitivity |
| Fin Length Scale | Fin height vs shell length |
| Fin Cast Shadows | Include fins in shadow caster |
| Gravity | Droops multi-segment fins (h² per height row) |

Shells draw first (instanced), then fins (`Graphics.DrawMesh`).

## Files

- `Shaders/ShellFur.shader` + `ShellFur.hlsl` — shell URP passes
- `Shaders/ShellFurFin.shader` — fin forward / shadow (Cull Off)
- `Scripts/ShellFurRenderer.cs` — shell + fin draw
- `Scripts/ShellFurFinBuilder.cs` — edge → fin mesh
- `Editor/ShellFurSetup.cs` — demo object + default assets
- `Materials/ShellFur_Default.mat` — default shell material

## Notes

- Enable **GPU Instancing** on the shell material (script forces it on).
- Source `MeshRenderer` is disabled by default so only shells/fins draw.
- Alpha-to-coverage (`AlphaToMask`) softens strand edges when MSAA is on.
- For mobile, prefer 16–24 shells, lower density, and consider disabling fins at distance.
- Builtin/imported meshes must have **Read/Write Enabled** for automatic fin build.
