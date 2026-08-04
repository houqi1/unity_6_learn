using UnityEditor;
using UnityEngine;

/// <summary>
/// Menu utilities to create a demo shell-fur object and default assets.
/// </summary>
[InitializeOnLoad]
public static class ShellFurSetup
{
    const string ShaderName = "Custom/ShellFur";
    const string RootFolder = "Assets/ShellFur";
    const string MaterialsFolder = RootFolder + "/Materials";
    const string TexturesFolder = RootFolder + "/Textures";
    static ShellFurSetup()
    {
        EditorApplication.delayCall += TryAutoCreateDefaultAssets;
    }

    static void TryAutoCreateDefaultAssets()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            return;

        // Create default material once after first import of the package.
        string matPath = MaterialsFolder + "/ShellFur_Default.mat";
        if (AssetDatabase.LoadAssetAtPath<Material>(matPath) != null)
            return;

        if (Shader.Find(ShaderName) == null)
            return;

        GetOrCreateDefaultMaterial();
        Debug.Log("[ShellFur] Default material created at " + matPath);
    }

    [MenuItem("GameObject/3D Object/Shell Fur Sphere", false, 40)]
    public static void CreateShellFurSphere()
    {
        EnsureFolders();
        Material mat = GetOrCreateDefaultMaterial();

        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = "ShellFur_Sphere";
        go.transform.position = Vector3.zero;
        Undo.RegisterCreatedObjectUndo(go, "Create Shell Fur Sphere");

        var renderer = go.GetComponent<MeshRenderer>();
        if (renderer != null)
            renderer.enabled = false;

        var fur = Undo.AddComponent<ShellFurRenderer>(go);
        fur.FurMaterial = mat;
        fur.ShellCount = 32;
        fur.FurLength = 0.1f;

        Selection.activeGameObject = go;
        EditorGUIUtility.PingObject(go);

        if (SceneView.lastActiveSceneView != null)
        {
            SceneView.lastActiveSceneView.FrameSelected();
            SceneView.lastActiveSceneView.Repaint();
        }
    }

    [MenuItem("Assets/Create/Shell Fur/Default Material", false, 120)]
    public static void CreateDefaultMaterialMenu()
    {
        EnsureFolders();
        Material mat = GetOrCreateDefaultMaterial();
        Selection.activeObject = mat;
        EditorGUIUtility.PingObject(mat);
    }

    [MenuItem("Assets/Create/Shell Fur/Noise Density Texture", false, 121)]
    public static void CreateNoiseTextureMenu()
    {
        EnsureFolders();
        Texture2D tex = GetOrCreateNoiseTexture(256, 180f);
        Selection.activeObject = tex;
        EditorGUIUtility.PingObject(tex);
    }

    public static Material GetOrCreateDefaultMaterial()
    {
        EnsureFolders();
        string path = MaterialsFolder + "/ShellFur_Default.mat";
        Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat != null)
            return mat;

        Shader shader = Shader.Find(ShaderName);
        if (shader == null)
        {
            Debug.LogError($"[ShellFur] Shader '{ShaderName}' not found. Wait for compilation, then retry.");
            return null;
        }

        mat = new Material(shader)
        {
            name = "ShellFur_Default",
            enableInstancing = true
        };

        mat.SetColor("_BaseColor", new Color(0.32f, 0.18f, 0.10f, 1f));
        mat.SetColor("_TipColor", new Color(0.90f, 0.72f, 0.45f, 1f));
        mat.SetFloat("_ShellCount", 32f);
        mat.SetFloat("_FurLength", 0.1f);
        mat.SetFloat("_Density", 140f);
        mat.SetFloat("_Thickness", 0.55f);
        mat.SetFloat("_Occlusion", 0.55f);
        mat.SetFloat("_Gravity", 0.4f);
        mat.SetFloat("_Smoothness", 0.12f);
        // Prefer texture density map (Poisson shell density) over procedural hash.
        mat.DisableKeyword("_USE_PROCEDURAL");
        mat.SetFloat("_UseProcedural", 0f);

        Texture2D density = AssetDatabase.LoadAssetAtPath<Texture2D>(TexturesFolder + "/FurDensity_Shell.png");
        if (density == null)
            density = GetOrCreateNoiseTexture(256, 180f);
        if (density != null)
        {
            mat.SetTexture("_FurMap", density);
            mat.SetTextureScale("_FurMap", new Vector2(4f, 4f));
        }

        AssetDatabase.CreateAsset(mat, path);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        return mat;
    }

    public static Texture2D GetOrCreateNoiseTexture(int resolution, float density)
    {
        EnsureFolders();
        string path = TexturesFolder + "/FurDensity_Noise.png";
        Texture2D existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        if (existing != null)
            return existing;

        Texture2D tex = GenerateStrandNoise(resolution, density);
        byte[] png = tex.EncodeToPNG();
        Object.DestroyImmediate(tex);

        System.IO.File.WriteAllBytes(path, png);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer != null)
        {
            importer.sRGBTexture = false;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.mipmapEnabled = true;
            importer.filterMode = FilterMode.Bilinear;
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.SaveAndReimport();
        }

        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    /// <summary>
    /// White dots on black — each bright pixel is a fur strand root for texture-based mode.
    /// </summary>
    public static Texture2D GenerateStrandNoise(int resolution, float dotsPerAxis)
    {
        var tex = new Texture2D(resolution, resolution, TextureFormat.R8, false, true)
        {
            name = "FurDensity_Noise",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Repeat
        };

        Color32[] pixels = new Color32[resolution * resolution];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = new Color32(0, 0, 0, 255);

        int count = Mathf.RoundToInt(dotsPerAxis * dotsPerAxis);
        var rng = new System.Random(1337);
        int radius = Mathf.Max(1, resolution / 256);

        for (int i = 0; i < count; i++)
        {
            int cx = rng.Next(0, resolution);
            int cy = rng.Next(0, resolution);
            byte strength = (byte)rng.Next(140, 256);

            for (int oy = -radius; oy <= radius; oy++)
            {
                for (int ox = -radius; ox <= radius; ox++)
                {
                    if (ox * ox + oy * oy > radius * radius)
                        continue;

                    int x = (cx + ox + resolution) % resolution;
                    int y = (cy + oy + resolution) % resolution;
                    int idx = y * resolution + x;
                    if (pixels[idx].r < strength)
                        pixels[idx] = new Color32(strength, strength, strength, 255);
                }
            }
        }

        tex.SetPixels32(pixels);
        tex.Apply(false, false);
        return tex;
    }

    static void EnsureFolders()
    {
        if (!AssetDatabase.IsValidFolder("Assets/ShellFur"))
            AssetDatabase.CreateFolder("Assets", "ShellFur");
        if (!AssetDatabase.IsValidFolder(MaterialsFolder))
            AssetDatabase.CreateFolder(RootFolder, "Materials");
        if (!AssetDatabase.IsValidFolder(TexturesFolder))
            AssetDatabase.CreateFolder(RootFolder, "Textures");
        if (!AssetDatabase.IsValidFolder(RootFolder + "/Shaders"))
            AssetDatabase.CreateFolder(RootFolder, "Shaders");
        if (!AssetDatabase.IsValidFolder(RootFolder + "/Scripts"))
            AssetDatabase.CreateFolder(RootFolder, "Scripts");
        if (!AssetDatabase.IsValidFolder(RootFolder + "/Editor"))
            AssetDatabase.CreateFolder(RootFolder, "Editor");
    }
}
