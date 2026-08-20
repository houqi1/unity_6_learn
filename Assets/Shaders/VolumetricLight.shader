Shader "Hidden/VolumetricLight"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off

        Pass
        {
            Name "March"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragMarch
            #pragma target 3.5
            #define VOLUMETRIC_LIGHT_SHADOWS 1
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #include "VolumetricLight.hlsl"

            float4 FragMarch(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                return MarchVolumetric(input.texcoord.xy, input.positionCS.xy);
            }
            ENDHLSL
        }

        Pass
        {
            Name "Spatiotemporal"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragST
            #pragma target 3.5
            #include "VolumetricLight.hlsl"

            float4 FragST(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                return SpatiotemporalResample(input.texcoord.xy);
            }
            ENDHLSL
        }

        Pass
        {
            Name "BlurH"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragBlur
            #pragma target 3.5
            #include "VolumetricLight.hlsl"

            float4 FragBlur(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                return BilateralBlur(input.texcoord.xy);
            }
            ENDHLSL
        }

        Pass
        {
            Name "BlurV"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragBlur
            #pragma target 3.5
            #include "VolumetricLight.hlsl"

            float4 FragBlur(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                return BilateralBlur(input.texcoord.xy);
            }
            ENDHLSL
        }

        Pass
        {
            Name "Composite"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragComposite
            #pragma target 3.5
            #include "VolumetricLight.hlsl"

            float4 FragComposite(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord.xy;
                float3 scene = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv, 0).rgb;
                return CompositeVolumetric(uv, scene);
            }
            ENDHLSL
        }

        Pass
        {
            Name "HistoryCopy"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragCopy
            #pragma target 3.5
            #include "VolumetricLight.hlsl"

            float4 FragCopy(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                return CopyVolume(input.texcoord.xy);
            }
            ENDHLSL
        }

        Pass
        {
            Name "CopySceneDepth"
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragCopySceneDepth
            #pragma target 3.5
            #include "VolumetricLight.hlsl"

            float4 FragCopySceneDepth(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float d = CopySceneDepth(input.texcoord.xy);
                return float4(d, d, d, 1);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
