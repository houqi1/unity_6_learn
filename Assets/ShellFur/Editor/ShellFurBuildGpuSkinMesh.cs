using UnityEditor;
using UnityEngine;

/// <summary>
/// Builds an OPTIONAL data mesh for ShellFurGpuSkinRenderer (bind pose + weights + smooth normals).
/// Does NOT modify the character FBX or SkinnedMeshRenderer.sharedMesh.
/// </summary>
public static class ShellFurBuildGpuSkinMesh
{
    [MenuItem("Tools/Shell Fur/Build GPU Skin Fur Mesh From Selection")]
    static void BuildFromSelection()
    {
        var go = Selection.activeGameObject;
        if (go == null)
        {
            EditorUtility.DisplayDialog("Shell Fur", "Select a GameObject with SkinnedMeshRenderer.", "OK");
            return;
        }

        var smr = go.GetComponentInChildren<SkinnedMeshRenderer>();
        if (smr == null || smr.sharedMesh == null)
        {
            EditorUtility.DisplayDialog("Shell Fur", "No SkinnedMeshRenderer with mesh found.", "OK");
            return;
        }

        Mesh src = smr.sharedMesh;
        if (!src.isReadable)
        {
            string path = AssetDatabase.GetAssetPath(src);
            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer != null)
            {
                if (!EditorUtility.DisplayDialog(
                        "Shell Fur",
                        $"Mesh '{src.name}' is not readable.\nEnable Read/Write on the model and reimport?\n\n" +
                        "This only changes import settings — it does not replace your mesh with a partial mesh.",
                        "Enable + Reimport", "Cancel"))
                    return;
                importer.isReadable = true;
                importer.SaveAndReimport();
                src = smr.sharedMesh;
            }

            if (!src.isReadable)
            {
                EditorUtility.DisplayDialog("Shell Fur", "Mesh still not readable.", "OK");
                return;
            }
        }

        // Clarify scope: this asset is DATA for GPU fur, not a replacement character mesh.
        int choice = EditorUtility.DisplayDialogComplex(
            "Shell Fur — Build GPU Skin Data Mesh",
            "This creates a SEPARATE .asset used only by ShellFurGpuSkinRenderer.\n\n" +
            "• Your character FBX / SkinnedMeshRenderer.sharedMesh is NOT modified.\n" +
            "• Do NOT assign the result to SkinnedMeshRenderer.Mesh.\n" +
            "• Optionally assign it to ShellFurGpuSkinRenderer → Bind Fur Mesh Override.\n" +
            "• Or leave override empty: the component builds data at runtime from the full mesh.\n\n" +
            $"Source: {src.name}  |  subMeshCount: {src.subMeshCount}\n\n" +
            "Which submeshes to include in the fur data asset?",
            "All submeshes",
            "Cancel",
            "All except slot 0 (body)");

        if (choice == 1) // Cancel
            return;

        int[] slots = null;
        if (choice == 2 && src.subMeshCount > 1)
        {
            // Exclude body slot 0 — fur-region data only (still a data asset, not the character).
            slots = new int[src.subMeshCount - 1];
            for (int i = 1; i < src.subMeshCount; i++)
                slots[i - 1] = i;
        }
        // choice == 0 → slots null → entire mesh as fur data

        var built = ShellFurBindMeshBuilder.Build(src, slots, 180f);
        if (built.mesh == null)
        {
            EditorUtility.DisplayDialog("Shell Fur", "Build failed (see Console).", "OK");
            return;
        }

        string dir = "Assets/ShellFur/Meshes";
        if (!AssetDatabase.IsValidFolder("Assets/ShellFur"))
            AssetDatabase.CreateFolder("Assets", "ShellFur");
        if (!AssetDatabase.IsValidFolder(dir))
            AssetDatabase.CreateFolder("Assets/ShellFur", "Meshes");

        string assetPath = AssetDatabase.GenerateUniqueAssetPath($"{dir}/{src.name}_FurGpuSkinDATA.asset");
        built.mesh.name = src.name + "_FurGpuSkinDATA";
        AssetDatabase.CreateAsset(built.mesh, assetPath);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        // Keep character selected in Hierarchy; only ping the new data asset in Project.
        EditorGUIUtility.PingObject(built.mesh);
        // Do NOT Selection.activeObject = mesh — that makes it look like the model "became" a partial mesh.

        string slotDesc = slots == null ? "all submeshes" : $"submeshes [{string.Join(",", slots)}]";
        Debug.Log(
            $"[ShellFur] Created fur DATA asset (character mesh unchanged): {assetPath}\n" +
            $"verts={built.bindVertices.Length} bones={built.boneCount} scope={slotDesc}\n" +
            $"SMR.sharedMesh is still: {smr.sharedMesh.name}",
            smr);

        EditorUtility.DisplayDialog(
            "Shell Fur — Done",
            "Created a separate DATA asset:\n" +
            $"{assetPath}\n\n" +
            $"Scope: {slotDesc}\n" +
            $"Verts: {built.bindVertices.Length}\n\n" +
            "Your character model is unchanged.\n" +
            "SMR.Mesh should still be the full FBX mesh.\n\n" +
            "Optional: assign this asset to\n" +
            "ShellFurGpuSkinRenderer → Bind Fur Mesh Override\n" +
            "(or leave empty for runtime build).",
            "OK");
    }
}
