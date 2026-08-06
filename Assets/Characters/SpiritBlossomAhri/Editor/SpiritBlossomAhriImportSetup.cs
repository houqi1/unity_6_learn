using UnityEditor;
using UnityEngine;

/// <summary>
/// Import settings for SpiritBlossomAhri.
/// Important: do NOT "fix" root negative scale in Blender by Apply Scale —
/// that destroys bind pose. Source GLB ships with a mirrored root scale.
/// Faces: use double-sided materials (Cull Off).
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
        importer.animationCompression = ModelImporterAnimationCompression.Off;
        importer.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
        importer.materialLocation = ModelImporterMaterialLocation.External;
        importer.materialName = ModelImporterMaterialName.BasedOnMaterialName;
        importer.materialSearch = ModelImporterMaterialSearch.Everywhere;
        importer.globalScale = 1f;
        importer.useFileScale = true;
        importer.meshCompression = ModelImporterMeshCompression.Off;
        // Needed for runtime mesh access (e.g. ShellFur / procedural mesh).
        importer.isReadable = true;
        importer.optimizeMeshPolygons = true;
        importer.optimizeMeshVertices = true;
        importer.weldVertices = true;
        importer.importBlendShapes = false;
        importer.importVisibility = false;
        importer.importCameras = false;
        importer.importLights = false;
        importer.preserveHierarchy = true;
        importer.optimizeGameObjects = false;
        importer.skinWeights = ModelImporterSkinWeights.Standard;
    }

    static void OnPostprocessAllAssets(
        string[] importedAssets,
        string[] deletedAssets,
        string[] movedAssets,
        string[] movedFromAssetPaths)
    {
        foreach (var path in importedAssets)
        {
            if (path == ModelPath)
            {
                BuildPrefab();
                break;
            }
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

    static void BuildPrefab()
    {
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
        if (model == null)
            return;

        var projectMats = LoadProjectMaterials();
        var instance = (GameObject)PrefabUtility.InstantiatePrefab(model);
        try
        {
            foreach (var smr in instance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var shared = smr.sharedMaterials;
                if (shared == null || shared.Length == 0)
                    continue;
                var remapped = new Material[shared.Length];
                for (int i = 0; i < shared.Length; i++)
                    remapped[i] = ResolveMaterial(shared[i], i, projectMats);
                smr.sharedMaterials = remapped;
            }

            PrefabUtility.SaveAsPrefabAsset(instance, PrefabPath);
            Debug.Log("[SpiritBlossomAhri] Prefab rebuilt (bind pose preserved). Root may show mirrored scale from source GLB — do not Apply Scale in Blender.");
        }
        finally
        {
            Object.DestroyImmediate(instance);
        }
    }

    [MenuItem("Tools/SpiritBlossomAhri/Reimport Model + Rebuild Prefab")]
    static void ReimportMenu()
    {
        AssetDatabase.ImportAsset(ModelPath, ImportAssetOptions.ForceUpdate);
        BuildPrefab();
        var prefab = AssetDatabase.LoadMainAssetAtPath(PrefabPath);
        Selection.activeObject = prefab != null ? prefab : AssetDatabase.LoadMainAssetAtPath(ModelPath);
        EditorGUIUtility.PingObject(Selection.activeObject);
    }
}
