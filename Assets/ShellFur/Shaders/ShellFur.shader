Shader "Custom/ShellFur"
{
    Properties
    {
        [Header(Base)]
        [MainColor] _BaseColor ("Base Color (Root)", Color) = (0.35, 0.22, 0.12, 1)
        _TipColor ("Tip Color", Color) = (0.85, 0.65, 0.40, 1)
        [MainTexture] _BaseMap ("Albedo (RGB) Mask (A)", 2D) = "white" {}

        [Header(Fur Pattern)]
        _FurMap ("Fur Density Map (R)", 2D) = "white" {}
        [Toggle(_USE_PROCEDURAL)] _UseProcedural ("Use Procedural Strands", Float) = 1
        _Density ("Strand Density", Range(8, 512)) = 128
        _Thickness ("Strand Thickness", Range(0.05, 1.0)) = 0.55
        _Occlusion ("Root Occlusion", Range(0, 1)) = 0.55
        _AlphaCutoff ("Base Alpha Cutoff", Range(0, 1)) = 0.15
        [Toggle(_USE_TIP_ALPHA_CUTOFF)] _UseTipAlphaCutoff ("Use Tip Alpha Cutoff", Float) = 0
        _TipAlphaCutoff ("Tip Alpha Cutoff", Range(0, 1)) = 1
        [Toggle(_SKIP_SOFT_ALPHA_CLIP)] _SkipSoftAlphaClip ("Skip Soft Alpha Clip", Float) = 0
        [Toggle(_OPAQUE_OUTPUT_ALPHA)] _OpaqueOutputAlpha ("Output Opaque Alpha", Float) = 0
        [Toggle(_OCCLUSION_TO_BASECOLOR)] _OcclusionToBaseColor ("Occlusion To Base Color", Float) = 0
        [Toggle(_USE_UV_BEND)] _UseUvBend ("UV Offset Bend", Float) = 0
        _UVOffset ("UV Bend (XY Dir, Z Power)", Vector) = (1, 0, 2, 0)

        [Header(Shell Shape)]
        _ShellCount ("Shell Count (synced by script)", Float) = 32
        _FurLength ("Fur Length", Range(0.001, 0.5)) = 0.08
        _FurLengthRandom ("Length Randomness", Range(0, 1)) = 0.35
        [Toggle(_USE_SMOOTH_NORMALS_VC)] _UseSmoothNormalsVC ("Use Smooth Normals (Vertex Color)", Float) = 0

        [Header(Physics Bend)]
        _Gravity ("Gravity Strength", Range(0, 2)) = 0.35
        _GravityDir ("Gravity Direction", Vector) = (0, -1, 0, 0)
        _GravityPower ("Gravity Falloff Power", Range(0.5, 4)) = 2

        [Header(Fins Shared)]
        _FinExtrudeWeight ("Fin Extrude Weight", Range(0, 2)) = 1
        _FinSilhouetteSharpness ("Silhouette Sharpness", Range(0.5, 32)) = 8
        _FinSilhouetteBias ("Silhouette Bias", Range(0, 1)) = 0
        _FinSilhouettePower ("Silhouette Power", Range(0.25, 4)) = 1
        _FinBandStrength ("Contour Band Strength", Range(0, 2)) = 0.4
        _FinRootOffset ("Root Depth Offset", Range(0, 0.02)) = 0.0015
        _FinLengthScale ("Fin Length Scale", Range(0.25, 2)) = 1.0
        _FinRootOpacity ("Fin Root Opacity", Range(0, 1)) = 1
        _FinTipOpacity ("Fin Tip Opacity", Range(0, 1)) = 0
        _FinOpacityFadeStart ("Fin Opacity Fade Start", Range(0, 1)) = 0
        _FinOpacityFadeEnd ("Fin Opacity Fade End", Range(0, 1)) = 1
        _FinOpacityPower ("Fin Opacity Power", Range(0.25, 4)) = 1

        [Header(Lighting)]
        _Smoothness ("Smoothness", Range(0, 1)) = 0.15
        [Toggle(_USE_KAJIYA_KAY)] _UseKajiyaKay ("Kajiya-Kay Specular", Float) = 0
        _RimPower ("Rim Power", Range(0.5, 8)) = 2.5
        _RimStrength ("Rim Strength", Range(0, 10)) = 0.25
        _ShadowStrength ("Self Shadow", Range(0, 10)) = 0.35
        _DiffuseBoostMin ("Diffuse Boost Min (Root)", Range(0, 8)) = 1
        _DiffuseBoostMax ("Diffuse Boost Max (Tip)", Range(0, 8)) = 1
        [Toggle(_USE_CUSTOM_LIGHT_DIR)] _UseCustomLightDir ("Use Custom Light Direction", Float) = 0
        _CustomLightDir ("Custom Light Direction (WS)", Vector) = (0.35, 0.8, -0.45, 0)
        [Toggle(_DEBUG_DIFFUSE)] _DebugDiffuse ("Output Lambert Only", Float) = 0
        [Toggle(_DEBUG_NORMALS)] _DebugNormals ("Output Normals (WS)", Float) = 0

        [Header(Rendering)]
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "TransparentCutout"
            "Queue" = "AlphaTest"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        // ------------------------------------------------------------------
        // Forward Lit
        // ------------------------------------------------------------------
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Cull [_Cull]
            ZWrite On
            ZTest LEqual
            AlphaToMask On

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex ShellFurVert
            #pragma fragment ShellFurFrag

            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile _ _FORWARD_PLUS
            #pragma multi_compile_fog
            #pragma shader_feature_local _USE_PROCEDURAL
            #pragma shader_feature_local _USE_TIP_ALPHA_CUTOFF
            #pragma shader_feature_local _SKIP_SOFT_ALPHA_CLIP
            #pragma shader_feature_local _OPAQUE_OUTPUT_ALPHA
            #pragma shader_feature_local _OCCLUSION_TO_BASECOLOR
            #pragma shader_feature_local _USE_UV_BEND
            #pragma shader_feature_local _USE_KAJIYA_KAY
            #pragma shader_feature_local _USE_SMOOTH_NORMALS_VC
            #pragma shader_feature_local _USE_CUSTOM_LIGHT_DIR
            #pragma shader_feature_local _DEBUG_DIFFUSE
            #pragma shader_feature_local _DEBUG_NORMALS

            #include "ShellFur.hlsl"
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
            #pragma target 4.5
            #pragma vertex ShellFurShadowVert
            #pragma fragment ShellFurShadowFrag

            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #pragma shader_feature_local _USE_PROCEDURAL
            #pragma shader_feature_local _USE_TIP_ALPHA_CUTOFF
            #pragma shader_feature_local _SKIP_SOFT_ALPHA_CLIP
            #pragma shader_feature_local _USE_UV_BEND
            #pragma shader_feature_local _USE_SMOOTH_NORMALS_VC

            #include "ShellFur.hlsl"
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
            #pragma target 4.5
            #pragma vertex ShellFurDepthVert
            #pragma fragment ShellFurDepthFrag

            #pragma multi_compile_instancing
            #pragma shader_feature_local _USE_PROCEDURAL
            #pragma shader_feature_local _USE_TIP_ALPHA_CUTOFF
            #pragma shader_feature_local _SKIP_SOFT_ALPHA_CLIP
            #pragma shader_feature_local _USE_UV_BEND
            #pragma shader_feature_local _USE_SMOOTH_NORMALS_VC

            #include "ShellFur.hlsl"
            ENDHLSL
        }
    }

    FallBack Off
}
