#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// 把体积光 Feature 挂到 PC_Renderer，并在场景里接上指定灯 + Volume。
/// </summary>
public static class VolumetricLightInstaller
{
    const string RendererPath = "Assets/Settings/PC_Renderer.asset";
    const string ShaderPath = "Assets/Shaders/VolumetricLight.shader";

    [MenuItem("Tools/Volumetric Light/Install To PC Renderer And Scene")]
    public static void Install()
    {
        var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
        var shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
        if (renderer == null)
        {
            Debug.LogError("[VolumetricLight] 找不到 " + RendererPath);
            return;
        }

        var so = new SerializedObject(renderer);
        var features = so.FindProperty("m_RendererFeatures");
        bool found = false;
        for (int i = 0; i < features.arraySize; i++)
        {
            var feat = features.GetArrayElementAtIndex(i).objectReferenceValue as VolumetricLightFeature;
            if (feat == null)
                continue;
            found = true;
            if (shader != null && feat.settings.shader == null)
            {
                feat.settings.shader = shader;
                EditorUtility.SetDirty(feat);
            }
        }

        if (!found)
        {
            var feature = ScriptableObject.CreateInstance<VolumetricLightFeature>();
            feature.name = "Volumetric Light";
            feature.settings.shader = shader;
            feature.settings.quality = VolumetricLightFeature.Quality.Medium;
            feature.settings.injectionPoint = RenderPassEvent.AfterRenderingSkybox;
            AssetDatabase.AddObjectToAsset(feature, renderer);

            features.arraySize++;
            features.GetArrayElementAtIndex(features.arraySize - 1).objectReferenceValue = feature;

            var map = so.FindProperty("m_RendererFeatureMap");
            if (map != null)
            {
                map.arraySize++;
                map.GetArrayElementAtIndex(map.arraySize - 1).longValue = feature.GetInstanceID();
            }

            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(renderer);
            Debug.Log("[VolumetricLight] 已添加到 PC_Renderer。");
        }
        else
        {
            so.ApplyModifiedProperties();
            Debug.Log("[VolumetricLight] PC_Renderer 上已有 Feature。");
        }

        EnsureSceneHookup();
        AssetDatabase.SaveAssets();
    }

    static void EnsureSceneHookup()
    {
        var lights = Object.FindObjectsByType<Light>(FindObjectsSortMode.None);
        Light directional = null;
        foreach (var l in lights)
        {
            if (l.type == LightType.Directional)
            {
                directional = l;
                break;
            }
        }

        if (directional == null)
        {
            Debug.LogWarning("[VolumetricLight] 场景里没有 Directional Light，请手动指定。");
            return;
        }

        var source = directional.GetComponent<VolumetricLightSource>();
        if (source == null)
            source = directional.gameObject.AddComponent<VolumetricLightSource>();
        source.specifiedLight = directional;
        EditorUtility.SetDirty(source);

        var volumes = Object.FindObjectsByType<Volume>(FindObjectsSortMode.None);
        Volume target = null;
        foreach (var v in volumes)
        {
            if (v.isGlobal)
            {
                target = v;
                break;
            }
        }

        if (target == null)
        {
            var go = new GameObject("Volumetric Light Volume");
            target = go.AddComponent<Volume>();
            target.isGlobal = true;
            target.priority = 1f;
            Undo.RegisterCreatedObjectUndo(go, "Create Volumetric Light Volume");
        }

        var profile = target.profile;
        if (profile == null)
            profile = target.sharedProfile;
        if (profile == null)
        {
            profile = ScriptableObject.CreateInstance<VolumeProfile>();
            target.profile = profile;
        }

        if (!profile.TryGet(out VolumetricLightVolume vol))
            vol = profile.Add<VolumetricLightVolume>(true);
        vol.active = true;
        vol.intensity.overrideState = true;
        vol.intensity.value = 0.6f;
        vol.density.overrideState = true;
        vol.shadowStrength.overrideState = true;
        EditorUtility.SetDirty(profile);
        EditorUtility.SetDirty(target);
        Debug.Log("[VolumetricLight] 已指定灯 " + directional.name + "，并加入 Volume。");
    }
}
#endif
