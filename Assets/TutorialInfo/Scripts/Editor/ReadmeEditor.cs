using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEditor;

/// <summary>
/// Tutorial readme inspector. Avoids GUIStyle / EditorStyles access outside a valid OnGUI skin
/// (fixes: "Unable to use a named GUIStyle without a current skin" from InspectorWindow.RedrawFromNative).
/// </summary>
[CustomEditor(typeof(Readme))]
public class ReadmeEditor : Editor
{
    static string s_ShowedReadmeSessionStateName = "ReadmeEditor.showedReadme";
    static string s_ReadmeSourceDirectory = "Assets/TutorialInfo";
    const float k_Space = 16f;

    // Do NOT use [InitializeOnLoad] + Selection during domain reload — it opens this
    // inspector via RedrawFromNative before GUI.skin exists.

    static void RemoveTutorial()
    {
        if (EditorUtility.DisplayDialog(
                "Remove Readme Assets",
                $"All contents under {s_ReadmeSourceDirectory} will be removed, are you sure you want to proceed?",
                "Proceed",
                "Cancel"))
        {
            if (Directory.Exists(s_ReadmeSourceDirectory))
            {
                FileUtil.DeleteFileOrDirectory(s_ReadmeSourceDirectory);
                FileUtil.DeleteFileOrDirectory(s_ReadmeSourceDirectory + ".meta");
            }
            else
            {
                Debug.Log($"Could not find the Readme folder at {s_ReadmeSourceDirectory}");
            }

            var readmeAsset = SelectReadme();
            if (readmeAsset != null)
            {
                var path = AssetDatabase.GetAssetPath(readmeAsset);
                FileUtil.DeleteFileOrDirectory(path + ".meta");
                FileUtil.DeleteFileOrDirectory(path);
            }

            AssetDatabase.Refresh();
        }
    }

    static Readme SelectReadme()
    {
        var ids = AssetDatabase.FindAssets("Readme t:Readme");
        if (ids.Length == 1)
        {
            var readmeObject = AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GUIDToAssetPath(ids[0]));
            Selection.objects = new Object[] { readmeObject };
            return (Readme)readmeObject;
        }

        Debug.Log("Couldn't find a readme");
        return null;
    }

    /// <summary>
    /// True only when it is safe to touch EditorStyles / construct GUIStyles.
    /// Accessing EditorStyles outside OnGUI throws the named-GUIStyle error.
    /// </summary>
    static bool GuiStylesAvailable()
    {
        // RedrawFromNative often has no Event and no skin.
        if (Event.current == null)
            return false;
        if (GUI.skin == null)
            return false;

        try
        {
            // Touching EditorStyles.label is what throws without a skin.
            return EditorStyles.label != null;
        }
        catch
        {
            return false;
        }
    }

    protected override void OnHeaderGUI()
    {
        if (!GuiStylesAvailable())
            return;

        var readme = (Readme)target;
        if (readme == null)
            return;

        EnsureStyles();

        var iconWidth = Mathf.Min(EditorGUIUtility.currentViewWidth / 3f - 20f, 128f);

        GUILayout.BeginHorizontal();
        {
            if (readme.icon != null)
            {
                GUILayout.Space(k_Space);
                GUILayout.Label(readme.icon, GUILayout.Width(iconWidth), GUILayout.Height(iconWidth));
            }

            GUILayout.Space(k_Space);
            GUILayout.BeginVertical();
            {
                GUILayout.FlexibleSpace();
                GUILayout.Label(readme.title ?? string.Empty, m_TitleStyle);
                GUILayout.FlexibleSpace();
            }
            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();
        }
        GUILayout.EndHorizontal();
    }

    public override void OnInspectorGUI()
    {
        if (!GuiStylesAvailable())
        {
            // Fallback: no custom styles (still readable, never throws).
            DrawDefaultInspector();
            return;
        }

        var readme = (Readme)target;
        if (readme == null)
            return;

        EnsureStyles();

        if (readme.sections != null)
        {
            foreach (var section in readme.sections)
            {
                if (section == null)
                    continue;

                if (!string.IsNullOrEmpty(section.heading))
                    GUILayout.Label(section.heading, m_HeadingStyle);

                if (!string.IsNullOrEmpty(section.text))
                    GUILayout.Label(section.text, m_BodyStyle);

                if (!string.IsNullOrEmpty(section.linkText))
                {
                    if (LinkLabel(new GUIContent(section.linkText)))
                        Application.OpenURL(section.url);
                }

                GUILayout.Space(k_Space);
            }
        }

        if (GUILayout.Button("Remove Readme Assets", m_ButtonStyle))
            RemoveTutorial();
    }

    GUIStyle m_LinkStyle;
    GUIStyle m_TitleStyle;
    GUIStyle m_HeadingStyle;
    GUIStyle m_BodyStyle;
    GUIStyle m_ButtonStyle;
    bool m_StylesReady;

    void EnsureStyles()
    {
        if (m_StylesReady && m_BodyStyle != null)
            return;

        // Build from current skin only — never cache across domain reloads via SerializeField.
        m_BodyStyle = new GUIStyle(EditorStyles.label)
        {
            wordWrap = true,
            fontSize = 14,
            richText = true
        };

        m_TitleStyle = new GUIStyle(m_BodyStyle) { fontSize = 26 };

        m_HeadingStyle = new GUIStyle(m_BodyStyle)
        {
            fontStyle = FontStyle.Bold,
            fontSize = 18
        };

        m_LinkStyle = new GUIStyle(m_BodyStyle)
        {
            wordWrap = false,
            stretchWidth = false
        };
        m_LinkStyle.normal.textColor = new Color(0f, 120f / 255f, 218f / 255f, 1f);

        m_ButtonStyle = new GUIStyle(EditorStyles.miniButton) { fontStyle = FontStyle.Bold };

        m_StylesReady = true;
    }

    bool LinkLabel(GUIContent label, params GUILayoutOption[] options)
    {
        var position = GUILayoutUtility.GetRect(label, m_LinkStyle, options);

        Handles.BeginGUI();
        Handles.color = m_LinkStyle.normal.textColor;
        Handles.DrawLine(new Vector3(position.xMin, position.yMax), new Vector3(position.xMax, position.yMax));
        Handles.color = Color.white;
        Handles.EndGUI();

        EditorGUIUtility.AddCursorRect(position, MouseCursor.Link);

        return GUI.Button(position, label, m_LinkStyle);
    }
}
