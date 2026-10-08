# TpacTool - M2 Export Compatibility

### An unofficial asset explorer for *Mount&Blade II: Bannerlord*, with export to *Medieval II: Total War* skeletons

This fork of [Wuan23/TpacTool](https://github.com/Wuan23/TpacTool) adds:

- an **asset gallery** with thumbnails and **one-click batch export** of models with all of their textures,
- an **A-pose to T-pose** option for rigged exports,
- a **Bannerlord to Medieval 2 rig transfer**: rigged human models are T-posed, fitted onto a Medieval 2 skeleton and re-weighted to its bones, with the skeleton's animations included in the FBX.

The Medieval 2 skeletons and animations bundled in `TpacTool/M2Skeletons` come from the
[Medieval 2 Blender Toolkit](https://github.com/WK-313/Medieval-2-Blender-Toolkit) (WK-313).
See the sections below for details.

[中文](README.zh-CN.md)

---


<a href="https://github.com/Wuan23/TpacTool/releases/latest">
	<img src="https://img.shields.io/github/v/release/Wuan23/TpacTool.svg?style=flat" />
</a>

<a href="https://github.com/Wuan23/TpacTool/blob/master/LICENSE">
	<img src="https://img.shields.io/github/license/hunharibo/TpacTool.svg?style=flat" />
</a>

-------------------

Fork from [szszss/TpacTool](https://github.com/szszss/TpacTool), maintained by [hunharibo](https://github.com/hunharibo/TpacTool)

-------------------

#### New Features

This fork includes the following enhancements:

##### Single Asset Export
Export any single asset (model, material, texture, skeleton, animation, etc.) as a separate .tpac file directly from the asset detail page.

##### Skeleton Visualization
View skeleton bones, joints, and colliders directly in the OpenGL preview. Toggle visibility of skeleton structure, joints, and collision bodies.

<img src="assets/skeleton_preview.png" alt="skeleton_preview" width="800" />

##### AnimationClip Viewer
Browse and view AnimationClip assets with detailed information.

<img src="assets/animationclip_viewer.png" alt="animationclip_viewer" width="800" />

##### Asset Gallery & One-Click Batch Export
Open **View > Asset Gallery…** (Ctrl+G) after loading an asset folder to browse models, textures and materials as thumbnails. Model thumbnails are rendered with their diffuse texture.

- Click tiles to select them, shift-click to select a range, double-click to open an asset in the main window. Selections are kept across the Models / Textures / Materials tabs, and *Show selected only* lists everything you picked.
- Filter by name or package (space separated words must all match) and resize the thumbnails with the slider.
- **Export N selected** writes every selected asset with all of its textures into the output folder:
  - models: `<output>/<model>/<model>.fbx` plus every texture of the model's materials (primary and secondary)
  - materials: `<output>/<material>/` with all of the material's textures
  - textures: `<output>/textures/`
- Rigged models are bound to the human skeleton automatically (horse skeleton for horse / camel gear), or to the skeleton you pick. Mods that ship the human skeleton under another name (e.g. `human_skeleton_notused.004`) are recognised, and when the loaded folder has no human skeleton at all a built-in copy of Bannerlord's 28-bone human skeleton is used. An `export_log.txt` lists what was exported and any failures.

##### A-pose to T-pose
Rigged exports (single model export and the gallery) have a **Convert A-pose to T-pose** option. It rotates each upper arm about the shoulder so it points straight out sideways, optionally straightens the elbow, and re-skins the mesh (positions, normals, tangents and morph targets) so it matches the new bind pose. Arm bones are found by name (`upperarm_l`, `forearm_r`, `UpperArm.L`, `LeftArm`, …); twist bones follow their arm segment. The original data in memory is restored right after the export.

##### Transfer to a Medieval II: Total War skeleton
In the gallery, set **Rig target** to *Medieval 2 skeleton* and pick one of the Medieval 2 Toolkit Blender addon's armatures (Sword, Spear, 2H, Archer, Crossbow, Jav; the addon's `armatures` folder is detected automatically when the addon is installed in Blender). Rigged human models are then:

1. T-posed (the M2 skeletons are in T-pose),
2. moved onto the M2 skeleton - pelvis on bone_pelvis, same facing as the toolkit's armatures in Blender, r_* bones on the bone_R* side (nothing is mirrored),
3. re-weighted onto the M2 bones, merging bones M2 doesn't have:

| Bannerlord | Medieval 2 |
|---|---|
| pelvis, spine | bone_pelvis |
| spine1 | bone_abs |
| spine2, neck | bone_torso |
| head | bone_head |
| l/r_clavicle | bone_L/Rclavical |
| l/r_upperarm_twist, _twist1 | bone_L/Rupperarm |
| l/r_foretwist, _foretwist1 | bone_L/Relbow |
| l/r_hand, finger0 | bone_L/Rhand |
| l/r_thigh / calf | bone_L/RThigh / bone_L/Rlowerleg |
| l/r_foot, toe0 | bone_L/Rfoot |

Bannerlord and M2 proportions differ (the M2 arms are ~6 cm further forward and ~3 cm longer, the legs ~5-10 cm), so the limbs are then fitted onto the M2 joints: each upper arm / forearm / thigh / shin is rotated, moved and stretched along its length so both of its joints land on the M2 joints, hands and feet follow onto their M2 joint, and the clavicle keeps its root on the torso while its tip swings to the M2 shoulder. The torso stays as it is.

The exported FBX carries the M2 skeleton with exactly the toolkit's bone positions and vertex groups named after the M2 bones, so it can be parented to the toolkit's skeleton in Blender directly. The export log lists the bone mapping and the largest joint offset between the two skeletons.

#### Building
The `TpacTool` project is an SDK-style project, so it builds with just the .NET SDK (no Visual Studio needed):

```
dotnet build TpacTool/TpacTool.csproj -c Release
```

The runnable app is written to `TpacTool/bin/Release`.

#### License

This project is licensed under the MIT License. See [LICENSE](LICENSE) for details.
