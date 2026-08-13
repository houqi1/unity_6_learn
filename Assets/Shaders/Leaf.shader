Shader "Custom/Leaf"
{
    Properties
    {
        [Header(Base)]
        [MainColor] _BaseColor ("Base Color", Color) = (1, 1, 1, 1)
        [MainTexture] _BaseMap ("Albedo (RGB) Alpha (A)", 2D) = "white" {}
        _Cutoff ("Alpha Clip (Shadow/Depth)", Range(0, 1)) = 0.35
        _AlphaScale ("Alpha Scale", Range(0, 2)) = 1

        [Header(Normal Dual Channel)]
        [Normal] _BumpMap ("Normal Map (Dual Channel)", 2D) = "bump" {}
        _BumpScale ("Normal Scale", Range(0, 2)) = 1
        // 0 = RG (BC5 / 手工双通道), 1 = AG (DXT5nm / Unity 默认), 2 = RGB 全通道
        [KeywordEnum(RG, AG, RGB)] _NormalPack ("Normal Pack Mode", Float) = 0

        [Header(Spherical Normal)]
        // 0 = 仅网格+法线贴图；1 = 完全使用球形法线（作用于全部光照）
        _SphereNormalBlend ("Sphere Normal Blend", Range(0, 1)) = 0
        // 球心 = 物体世界坐标(Pivot) + 此偏移（世界空间）
        _SphereCenterOffset ("Sphere Center Offset (WS)", Vector) = (0, 0, 0, 0)

        [Header(Mask)]
        _MaskMap ("Mask (R:AO G:Roughness B:Metallic)", 2D) = "white" {}
        _OcclusionStrength ("Occlusion Strength", Range(0, 1)) = 1
        _Smoothness ("Smoothness", Range(0, 1)) = 0.25
        _Metallic ("Metallic", Range(0, 1)) = 0
        // Mask G 作为粗糙度时，Smoothness = (1 - Roughness) * _Smoothness
        [Toggle(_MASKMAP)] _UseMaskMap ("Use Mask Map", Float) = 1

        [Header(Lighting)]
        // Wrap：0 = 标准 Lambert，越大暗部越被「包」进受光区
        _DiffuseWrap ("Diffuse Wrap", Range(0, 1)) = 0.35
        // Diffuse 不乘灯光颜色，改乘此自定义色（仍受阴影/衰减影响）
        _DiffuseColor ("Diffuse Color", Color) = (1, 1, 1, 1)
        [HDR] _EmissionColor ("Emission Color (HDR)", Color) = (0, 0, 0, 1)

        [Header(Rim Light)]
        // Fresnel 边缘光：视角掠射时亮起（轮廓光）
        [HDR] _RimColor ("Rim Color (HDR)", Color) = (0.55, 0.85, 0.35, 1)
        _RimPower ("Rim Power", Range(0.5, 16)) = 3
        _RimStrength ("Rim Strength", Range(0, 5)) = 1.2
        // 0 = 纯 Rim 色；1 = 乘主光颜色与衰减
        _RimLightBlend ("Rim Light Blend", Range(0, 1)) = 0.35

        [Header(Translucency)]
        _TranslucencyColor ("Translucency Color", Color) = (0.35, 0.55, 0.15, 1)
        _TranslucencyPower ("Translucency Power", Range(0.5, 8)) = 2
        _TranslucencyStrength ("Translucency Strength", Range(0, 2)) = 0.45
        _TranslucencyDistortion ("Translucency Distortion", Range(0, 1)) = 0.2

        [Header(Wind Optional)]
        [Toggle(_WIND)] _UseWind ("Enable Simple Wind", Float) = 0
        _WindStrength ("Wind Strength", Range(0, 1)) = 0.15
        _WindSpeed ("Wind Speed", Range(0, 5)) = 1.2
        _WindFrequency ("Wind Frequency", Range(0.1, 10)) = 1.5

        [Header(Rendering)]
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 0
        [Enum(Off, 0, On, 1)] _ZWrite ("ZWrite", Float) = 0
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest ("ZTest", Float) = 4
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src Blend", Float) = 5
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst Blend", Float) = 10
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        // ------------------------------------------------------------------
        // Forward Lit — Alpha Blend
        // ------------------------------------------------------------------
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Cull [_Cull]
            ZWrite [_ZWrite]
            ZTest [_ZTest]
            Blend [_SrcBlend] [_DstBlend]
            // 预乘关闭：标准 AlphaBlend = SrcAlpha OneMinusSrcAlpha
            BlendOp Add

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex LeafVert
            #pragma fragment LeafFrag

            #pragma multi_compile_instancing
            #pragma multi_compile_fog
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile _ _FORWARD_PLUS
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION

            #pragma shader_feature_local _NORMALPACK_RG _NORMALPACK_AG _NORMALPACK_RGB
            #pragma shader_feature_local _MASKMAP
            #pragma shader_feature_local _WIND

            #define LEAF_FORWARD_PASS 1
            #include "Leaf.hlsl"
            ENDHLSL
        }

        // ------------------------------------------------------------------
        // Shadow Caster — Alpha Clip（阴影需要硬裁切）
        // ------------------------------------------------------------------
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex LeafShadowVert
            #pragma fragment LeafShadowFrag

            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #pragma shader_feature_local _WIND

            #define LEAF_SHADOW_PASS 1
            #include "Leaf.hlsl"
            ENDHLSL
        }

        // ------------------------------------------------------------------
        // Depth Only — Alpha Clip
        // ------------------------------------------------------------------
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex LeafDepthVert
            #pragma fragment LeafDepthFrag

            #pragma multi_compile_instancing
            #pragma shader_feature_local _WIND

            #define LEAF_DEPTH_PASS 1
            #include "Leaf.hlsl"
            ENDHLSL
        }

        // ------------------------------------------------------------------
        // DepthNormals（可选，供 SSAO 等使用）
        // ------------------------------------------------------------------
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex LeafDepthNormalsVert
            #pragma fragment LeafDepthNormalsFrag

            #pragma multi_compile_instancing
            #pragma shader_feature_local _NORMALPACK_RG _NORMALPACK_AG _NORMALPACK_RGB
            #pragma shader_feature_local _WIND

            #define LEAF_DEPTHNORMALS_PASS 1
            #include "Leaf.hlsl"
            ENDHLSL
        }
    }

    FallBack Off
}
