using UnityEditor;
using UnityEngine;

/// <summary>
/// Ensures SpiritBlossomAhri FBX imports with Generic rig, animations,
/// independent Body/Tails/Tail materials, and a ready-to-use prefab.
/// </summary>
public class SpiritBlossomAhriImportSetup : AssetPostprocessor
{
    const string ModelPath = "Assets/Characters/SpiritBlossomAhri/SpiritBlossomAhri.fbx";
    const string MaterialsDir = "Assets/Characters/SpiritBlossomAhri/Materials";
    const string PrefabPath = "Assets/Characters/SpiritBlossomAhri/SpiritBlossomAhri.prefab";

    void OnPreprocessModel()
    {
        if (assetPath != ModelPath)
            return;

        var importer = (ModelImporter)assetImporter;
        importer.animationType = ModelImporterAnimationType.Generic;
        importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        importer.importAnimation = true;
        importer.resampleCurves = true;
        // Keep animation curves as exported — do not keyframe-reduce (avoids skinned distortion).
        importer.animationCompression = ModelImporterAnimationCompression.Off;
        importer.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
        importer.materialLocation = ModelImporterMaterialLocation.External;
        importer.materialName = ModelImporterMaterialName.BasedOnMaterialName;
        importer.materialSearch = ModelImporterMaterialSearch.Everywhere;
        importer.globalScale = 1f;
        importer.useFileScale = true;
        importer.meshCompression = ModelImporterMeshCompression.Off;
        importer.isReadable = false;
        importer.optimizeMeshPolygons = true;
        importer.optimizeMeshVertices = true;
        importer.weldVertices = true;
        importer.importBlendShapes = false;
        importer.importVisibility = false;
        importer.importCameras = false;
        importer.importLights = false;
        importer.preserveHierarchy = true;
        importer.skinWeights = ModelImporterSkinWeights.Standard;
        // Keep original bind pose / hierarchy from Blender (includes mirrored root scale).
        importer.optimizeGameObjects = false;
    }

    static void OnPostprocessAllAssets(
        string[] importedAssets,
        string[] deletedAssets,
        string[] movedAssets,
        string[] movedFromAssetPaths)
    {
        foreach (var path in importedAssets)
        {
            if (path != ModelPath)
                continue;

            // No SaveAndReimport here — avoids import loops.
            BuildPrefab();
            break;
        }
    }

    static Material[] LoadProjectMaterials()
    {
        return new[]
        {
            AssetDatabase.LoadAssetAtPath<Material>($"{MaterialsDir}/Ahri_Body_MAT.mat"),
            AssetDatabase.LoadAssetAtPath<Material>($"{MaterialsDir}/Ahri_Tails_MAT.mat"),
            AssetDatabase.LoadAssetAtPath<Material>($"{MaterialsDir}/Ahri_Tail_MAT.mat"),
        };
    }

    static Material ResolveMaterial(Material src, int index, Material[] projectMats)
    {
        var name = src != null ? src.name.Replace(" (Instance)", "") : string.Empty;
        if (!string.IsNullOrEmpty(name))
        {
            if (name.Contains("Body") && projectMats[0] != null) return projectMats[0];
            if (name.Contains("Tails") && projectMats[1] != null) return projectMats[1];
            if (name.Contains("Tail") && projectMats[2] != null) return projectMats[2];
        }

        if (index < projectMats.Length && projectMats[index] != null)
            return projectMats[index];
        return src;
    }

    static void ApplyMaterialsToRenderers(GameObject root, Material[] projectMats)
    {
        foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            var shared = smr.sharedMaterials;
            if (shared == null || shared.Length == 0)
                continue;

            var remapped = new Material[shared.Length];
            for (int i = 0; i < shared.Length; i++)
                remapped[i] = ResolveMaterial(shared[i], i, projectMats);
            smr.sharedMaterials = remapped;
        }
    }

    static void BuildPrefab()
    {
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
        if (model == null)
        {
            Debug.LogWarning("[SpiritBlossomAhri] FBX not found, skip prefab.");
            return;
        }

        var projectMats = LoadProjectMaterials();
        var instance = (GameObject)PrefabUtility.InstantiatePrefab(model);
        try
        {
            ApplyMaterialsToRenderers(instance, projectMats);
            PrefabUtility.SaveAsPrefabAsset(instance, PrefabPath);

            var smr = instance.GetComponentInChildren<SkinnedMeshRenderer>();
            int slots = smr != null && smr.sharedMaterials != null ? smr.sharedMaterials.Length : 0;
            Debug.Log($"[SpiritBlossomAhri] Prefab ready: {PrefabPath} | material slots={slots} | Generic + animations kept.");
        }
        finally
        {
            Object.DestroyImmediate(instance);
        }
    }

    [MenuItem("Tools/SpiritBlossomAhri/Reimport Model + Rebuild Prefab")]
    static void ReimportMenu()
    {
        var importer = AssetImporter.GetAtPath(ModelPath) as ModelImporter;
        if (importer != null)
        {
            importer.SearchAndRemapMaterials(
                ModelImporterMaterialName.BasedOnMaterialName,
                ModelImporterMaterialSearch.Everywhere);
            importer.SaveAndReimport();
        }
        else
        {
            AssetDatabase.ImportAsset(ModelPath, ImportAssetOptions.ForceUpdate);
        }

        BuildPrefab();
        var prefab = AssetDatabase.LoadMainAssetAtPath(PrefabPath);
        Selection.activeObject = prefab != null ? prefab : AssetDatabase.LoadMainAssetAtPath(ModelPath);
        EditorGUIUtility.PingObject(Selection.activeObject);
    }
}
