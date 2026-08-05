using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Bakes angle-weighted smooth normals into mesh vertex colors (RGB = n*0.5+0.5, object space).
/// Menu: Tools / Shell Fur / Bake Smooth Normals To Vertex Colors
/// </summary>
public class ShellFurSmoothNormalBaker : EditorWindow
{
    Mesh _meshAsset;
    GameObject _targetGo;
    bool _createCopy = true;
    bool _limitToSubmeshes;
    int[] _submeshIndices = { 0 };
    [Tooltip("180 = fully smooth (weld by position). Lower = hard edges when face angle exceeds this.")]
    float _maxSmoothingAngle = 180f;
    string _status = "";

    [MenuItem("Tools/Shell Fur/Bake Smooth Normals To Vertex Colors")]
    static void Open()
    {
        var win = GetWindow<ShellFurSmoothNormalBaker>("Smooth Normals → VC");
        win.minSize = new Vector2(360, 280);
        win.Show();
    }

    [MenuItem("Tools/Shell Fur/Bake Smooth Normals (From Selection)")]
    static void BakeFromSelection()
    {
        var go = Selection.activeGameObject;
        Mesh mesh = null;
        string label = "";

        if (go != null)
        {
            var smr = go.GetComponentInChildren<SkinnedMeshRenderer>();
            var mf = go.GetComponentInChildren<MeshFilter>();
            if (smr != null && smr.sharedMesh != null)
            {
                mesh = smr.sharedMesh;
                label = smr.name;
            }
            else if (mf != null && mf.sharedMesh != null)
            {
                mesh = mf.sharedMesh;
                label = mf.name;
            }
        }

        if (mesh == null && Selection.activeObject is Mesh m)
        {
            mesh = m;
            label = mesh.name;
        }

        if (mesh == null)
        {
            EditorUtility.DisplayDialog(
                "Shell Fur",
                "Select a Mesh asset, or a GameObject with MeshFilter / SkinnedMeshRenderer.",
                "OK");
            return;
        }

        string path = BakeAsset(mesh, createCopy: true, submeshIndices: null, maxSmoothingAngleDeg: 180f);
        if (!string.IsNullOrEmpty(path))
        {
            EditorUtility.DisplayDialog("Shell Fur", $"Baked smooth normals to vertex colors:\n{path}\n\nSource: {label}", "OK");
            var baked = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (baked != null)
                Selection.activeObject = baked;
        }
    }

    void OnGUI()
    {
        // Never touch EditorStyles if skin is missing (domain reload / native redraw edge cases).
        if (Event.current == null || GUI.skin == null)
            return;

        GUIStyle titleStyle = null;
        try { titleStyle = EditorStyles.boldLabel; }
        catch { /* no skin */ }
        EditorGUILayout.LabelField("Bake Smooth Normals → Vertex Colors", titleStyle ?? GUIStyle.none);
        EditorGUILayout.HelpBox(
            "True smooth normals: weld vertices that share the same position (required for FBX/Unity split verts), " +
            "then angle-weight face normals. Stored in vertex colors as RGB = normalOS * 0.5 + 0.5.\n\n" +
            "Enable \"Use Smooth Normals (Vertex Color)\" on Shell Fur to extrude along these directions.\n" +
            "Skinned: ShellFurRenderer re-bakes after each BakeMesh when the toggle is on.",
            MessageType.Info);

        _meshAsset = (Mesh)EditorGUILayout.ObjectField("Mesh Asset", _meshAsset, typeof(Mesh), false);
        _targetGo = (GameObject)EditorGUILayout.ObjectField("Or GameObject", _targetGo, typeof(GameObject), true);

        _createCopy = EditorGUILayout.Toggle(
            new GUIContent("Create Mesh Copy", "Recommended for FBX meshes so the original import is not modified."),
            _createCopy);

        _maxSmoothingAngle = EditorGUILayout.Slider(
            new GUIContent("Max Smoothing Angle", "180 = fully smooth. e.g. 60 keeps hard edges above 60°."),
            _maxSmoothingAngle, 1f, 180f);

        _limitToSubmeshes = EditorGUILayout.Toggle("Limit To Submeshes", _limitToSubmeshes);
        if (_limitToSubmeshes)
        {
            int n = _submeshIndices != null ? _submeshIndices.Length : 0;
            n = EditorGUILayout.IntField("Slot Count", Mathf.Max(1, n));
            if (_submeshIndices == null || _submeshIndices.Length != n)
            {
                var next = new int[n];
                for (int i = 0; i < n; i++)
                    next[i] = _submeshIndices != null && i < _submeshIndices.Length ? _submeshIndices[i] : i;
                _submeshIndices = next;
            }
            for (int i = 0; i < _submeshIndices.Length; i++)
                _submeshIndices[i] = EditorGUILayout.IntField($"  Slot {i}", _submeshIndices[i]);
        }

        EditorGUILayout.Space();
        if (GUILayout.Button("Bake", GUILayout.Height(28)))
        {
            Mesh mesh = ResolveMesh(out string err);
            if (mesh == null)
            {
                _status = err;
            }
            else
            {
                int[] slots = _limitToSubmeshes ? _submeshIndices : null;
                string path = BakeAsset(mesh, _createCopy, slots, _maxSmoothingAngle);
                _status = string.IsNullOrEmpty(path) ? "Bake failed (see Console)." : $"OK → {path}";
            }
        }

        if (!string.IsNullOrEmpty(_status))
            EditorGUILayout.HelpBox(_status, MessageType.None);
    }

    Mesh ResolveMesh(out string error)
    {
        error = null;
        if (_meshAsset != null)
            return _meshAsset;

        if (_targetGo != null)
        {
            var smr = _targetGo.GetComponentInChildren<SkinnedMeshRenderer>();
            if (smr != null && smr.sharedMesh != null)
                return smr.sharedMesh;
            var mf = _targetGo.GetComponentInChildren<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
                return mf.sharedMesh;
        }

        error = "Assign a Mesh asset or a GameObject with a mesh.";
        return null;
    }

    /// <returns>Asset path of the mesh that received vertex colors.</returns>
    public static string BakeAsset(Mesh source, bool createCopy, int[] submeshIndices, float maxSmoothingAngleDeg = 180f)
    {
        if (source == null)
            return null;

        string srcPath = AssetDatabase.GetAssetPath(source);
        bool isFbxorModel = !string.IsNullOrEmpty(srcPath) &&
                            (srcPath.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase) ||
                             srcPath.EndsWith(".obj", System.StringComparison.OrdinalIgnoreCase) ||
                             AssetDatabase.IsSubAsset(source));

        Mesh target;
        string savePath;

        if (createCopy || isFbxorModel || string.IsNullOrEmpty(srcPath))
        {
            // Always copy FBX/sub-assets — cannot safely overwrite importer mesh in place.
            target = Object.Instantiate(source);
            target.name = source.name + "_SmoothN_VC";

            string dir = string.IsNullOrEmpty(srcPath)
                ? "Assets/ShellFur/Meshes"
                : Path.GetDirectoryName(srcPath).Replace('\\', '/');
            if (!AssetDatabase.IsValidFolder(dir))
            {
                if (!AssetDatabase.IsValidFolder("Assets/ShellFur"))
                    AssetDatabase.CreateFolder("Assets", "ShellFur");
                if (!AssetDatabase.IsValidFolder("Assets/ShellFur/Meshes"))
                    AssetDatabase.CreateFolder("Assets/ShellFur", "Meshes");
                dir = "Assets/ShellFur/Meshes";
            }

            savePath = AssetDatabase.GenerateUniqueAssetPath($"{dir}/{target.name}.asset");
            try
            {
                ShellFurNormalUtility.BakeSmoothNormalsToVertexColors(target, submeshIndices, maxSmoothingAngleDeg);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[ShellFur] Bake failed: {e.Message}\nIs the mesh readable?");
                Object.DestroyImmediate(target);
                return null;
            }

            AssetDatabase.CreateAsset(target, savePath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[ShellFur] True position-welded smooth normals → VC: {savePath} (angle={maxSmoothingAngleDeg})");
            return savePath;
        }

        target = source;
        savePath = srcPath;
        if (!target.isReadable)
        {
            Debug.LogError($"[ShellFur] Mesh '{target.name}' is not readable. Enable Read/Write or use Create Copy.");
            return null;
        }

        Undo.RegisterCompleteObjectUndo(target, "Bake Smooth Normals To Vertex Colors");
        ShellFurNormalUtility.BakeSmoothNormalsToVertexColors(target, submeshIndices, maxSmoothingAngleDeg);
        EditorUtility.SetDirty(target);
        AssetDatabase.SaveAssets();
        Debug.Log($"[ShellFur] True position-welded smooth normals → VC: {savePath}");
        return savePath;
    }
}
