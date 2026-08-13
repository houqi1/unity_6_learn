using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 指定体积光跟随的那一盏定向光。forward = 光线传播方向。
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
public class VolumetricLightSource : MonoBehaviour
{
    static readonly List<VolumetricLightSource> s_Active = new List<VolumetricLightSource>();
    static bool s_WarnedMultiple;

    [Tooltip("必须是 Directional Light。可与本物体上的 Light 相同。")]
    public Light specifiedLight;

    void Reset()
    {
        specifiedLight = GetComponent<Light>();
    }

    void OnEnable()
    {
        if (!s_Active.Contains(this))
            s_Active.Add(this);
    }

    void OnDisable()
    {
        s_Active.Remove(this);
    }

    public static VolumetricLightSource FindActive()
    {
        VolumetricLightSource first = null;
        int count = 0;

        for (int i = s_Active.Count - 1; i >= 0; i--)
        {
            var src = s_Active[i];
            if (src == null)
            {
                s_Active.RemoveAt(i);
                continue;
            }

            if (!src.isActiveAndEnabled)
                continue;

            count++;
            if (first == null)
                first = src;
        }

        if (count > 1 && !s_WarnedMultiple)
        {
            s_WarnedMultiple = true;
            Debug.LogWarning("[VolumetricLight] 场景里有多个 VolumetricLightSource，只使用第一个启用的。");
        }

        return first;
    }

    void OnDrawGizmos()
    {
        var light = specifiedLight != null ? specifiedLight : GetComponent<Light>();
        if (light == null)
            return;

        Vector3 origin = light.transform.position;
        Vector3 dir = light.transform.forward;
        Gizmos.color = new Color(1f, 0.85f, 0.2f, 0.9f);
        Gizmos.DrawRay(origin, dir * 8f);
        Gizmos.DrawSphere(origin + dir * 8f, 0.15f);
    }
}
