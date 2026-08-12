Shader "Custom/TreeTrunk"
{
    Properties
    {
        [Header(Base)]
        [MainColor] _BaseColor ("Base Color", Color) = (1, 1, 1, 1)
        [MainTexture] _BaseMap ("Albedo (RGB)", 2D) = "white" {}

        [Header(Normal Dual Channel)]
        _BumpMap ("Normal Map (Dual Channel)", 2D) = "bump" {}
        _BumpScale ("Normal Scale", Range(0, 2)) = 1
        // 0 = RG (BC5), 1 = AG (DXT5nm), 2 = RGB 全通道
        [KeywordEnum(RG, AG, RGB)] _NormalPack ("Normal Pack Mode", Float) = 0

        [Header(Mask)]
        _MaskMap ("Mask (R:AO G:Roughness B:Metallic)", 2D) = "white" {}
        _OcclusionStrength ("Occlusion Strength", Range(0, 1)) = 1
        _Smoothness ("Smoothness", Range(0, 1)) = 0.2
        _Metallic ("Metallic", Range(0, 1)) = 0
        [Toggle(_MASKMAP)] _UseMaskMap ("Use Mask Map", Float) = 0

        [Header(Lighting)]
        // Wrap：0 = 标准 Lambert，越大暗部越软
        _DiffuseWrap ("Diffuse Wrap", Range(0, 1)) = 0.2
        // Diffuse 不乘灯光颜色，改乘此自定义色（仍受阴影/衰减影响）
        _DiffuseColor ("Diffuse Color", Color) = (1, 1, 1, 1)
        // 基础自发光：albedo * color（始终生效）
        [HDR] _EmissionColor ("Emission Color (HDR)", Color) = (0, 0, 0, 1)

        [Header(Emission Map Optional)]
        // 额外自发光层：与上面的 Emission Color 独立叠加
        [Toggle(_EMISSIONMAP)] _UseEmissionMap ("Use Emission Map", Float) = 0
        _EmissionMap ("Emission Map", 2D) = "black" {}
        [HDR] _EmissionMapColor ("Emission Map Color (HDR)", Color) = (1, 1, 1, 1)

        [Header(Detail Optional)]
        [Toggle(_DETAIL)] _UseDetail ("Use Detail Maps", Float) = 0
        _DetailAlbedoMap ("Detail Albedo", 2D) = "grey" {}
        _DetailNormalMap ("Detail Normal", 2D) = "bump" {}
        _DetailNormalScale ("Detail Normal Scale", Range(0, 2)) = 1
        _DetailAlbedoScale ("Detail Albedo Scale", Range(0, 2)) = 1

        [Header(Rendering)]
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        // ------------------------------------------------------------------
        // Forward Lit — Opaque
        // ------------------------------------------------------------------
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Cull [_Cull]
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex TrunkVert
            #pragma fragment TrunkFrag

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
            #pragma shader_feature_local _EMISSIONMAP
            #pragma shader_feature_local _DETAIL

            #define TRUNK_FORWARD_PASS 1
            #include "TreeTrunk.hlsl"
            ENDHLSL
        }

        // ------------------------------------------------------------------
        // Shadow Caster
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
            #pragma vertex TrunkShadowVert
            #pragma fragment TrunkShadowFrag

            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #define TRUNK_SHADOW_PASS 1
            #include "TreeTrunk.hlsl"
            ENDHLSL
        }

        // ------------------------------------------------------------------
        // Depth Only
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
            #pragma vertex TrunkDepthVert
            #pragma fragment TrunkDepthFrag

            #pragma multi_compile_instancing

            #define TRUNK_DEPTH_PASS 1
            #include "TreeTrunk.hlsl"
            ENDHLSL
        }

        // ------------------------------------------------------------------
        // Depth Normals
        // ------------------------------------------------------------------
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex TrunkDepthNormalsVert
            #pragma fragment TrunkDepthNormalsFrag

            #pragma multi_compile_instancing
            #pragma shader_feature_local _NORMALPACK_RG _NORMALPACK_AG _NORMALPACK_RGB
            #pragma shader_feature_local _DETAIL

            #define TRUNK_DEPTHNORMALS_PASS 1
            #include "TreeTrunk.hlsl"
            ENDHLSL
        }
    }

    FallBack Off
}
