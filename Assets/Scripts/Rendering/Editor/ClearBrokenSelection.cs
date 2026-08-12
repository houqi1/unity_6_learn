#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// Domain Reload / 脚本编译后，Inspector 常因选中已失效对象抛出
/// SerializedObjectNotCreatableException / m_Targets MissingReferenceException。
/// 编译完成后自动清空选中，避免 Inspector 对 null target 建 SerializedObject。
/// </summary>
[InitializeOnLoad]
static class ClearBrokenSelection
{
    static ClearBrokenSelection()
    {
        AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
    }

    static void OnAfterAssemblyReload()
    {
        // 延迟一帧，等 Inspector OnEnable 队列消化完再清选中
        EditorApplication.delayCall += () =>
        {
            if (Selection.objects == null || Selection.objects.Length == 0)
                return;

            // 若选中里有已销毁对象，整体清空，打断坏 Inspector 状态
            foreach (var o in Selection.objects)
            {
                if (o == null)
                {
                    Selection.activeObject = null;
                    return;
                }
            }
        };
    }
}
#endif
