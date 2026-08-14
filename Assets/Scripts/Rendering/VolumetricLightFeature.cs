using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RendererUtils;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

/// <summary>
/// URP 17 Render Graph 光线步进体积光。方向与树隙跟指定 Directional Light 走。
/// </summary>
[DisallowMultipleRendererFeature("Volumetric Light")]
public class VolumetricLightFeature : ScriptableRendererFeature
{
    public enum Quality
    {
        Low,
        Medium,
        High
    }

    public enum DebugMode
    {
        Off = 0,
        Inscatter = 1,
        Transmittance = 2,
        ShadowAlongRay = 3,
        LightDirection = 4,
        MarchDistance = 5
    }

    [Serializable]
    public class Settings
    {
        public Shader shader;
        public Quality quality = Quality.Medium;
        public DebugMode debugMode = DebugMode.Off;
        public RenderPassEvent injectionPoint = RenderPassEvent.AfterRenderingSkybox;
    }

    public Settings settings = new Settings();

    VolumetricLightPass m_Pass;
    Material m_Material;
    static float s_LastWarnTime;

    public override void Create()
    {
        m_Pass = new VolumetricLightPass();
        m_Pass.renderPassEvent = settings != null
            ? settings.injectionPoint
            : RenderPassEvent.AfterRenderingSkybox;
        RebuildMaterial();
    }

    void RebuildMaterial()
    {
        if (m_Material != null)
        {
            CoreUtils.Destroy(m_Material);
            m_Material = null;
        }

        Shader shader = settings != null ? settings.shader : null;
        if (shader == null)
            shader = Shader.Find("Hidden/VolumetricLight");
        if (shader != null)
            m_Material = CoreUtils.CreateEngineMaterial(shader);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (m_Material == null)
            RebuildMaterial();
        if (m_Material == null)
            return;

        var camType = renderingData.cameraData.cameraType;
        if (camType == CameraType.Preview || camType == CameraType.Reflection)
            return;

        if (UniversalRenderer.IsOffscreenDepthTexture(ref renderingData.cameraData))
            return;

        var stack = VolumeManager.instance.stack;
        var volume = stack != null ? stack.GetComponent<VolumetricLightVolume>() : null;
        if (volume == null || !volume.IsActive())
            return;

        var source = VolumetricLightSource.FindActive();
        if (source == null || source.specifiedLight == null)
        {
            Warn("[VolumetricLight] 未指定 VolumetricLightSource / Light，已跳过。");
            return;
        }

        if (source.specifiedLight.type != LightType.Directional)
        {
            Warn("[VolumetricLight] 指定灯必须是 Directional，已跳过。");
            return;
        }

        if (source.intensity <= 0.001f)
            return;

        m_Pass.Setup(settings, m_Material, volume, source);
        m_Pass.renderPassEvent = settings.injectionPoint;
        m_Pass.ConfigureInput(ScriptableRenderPassInput.Depth);
        m_Pass.requiresIntermediateTexture = true;
        renderer.EnqueuePass(m_Pass);
    }

    protected override void Dispose(bool disposing)
    {
        if (m_Material != null)
        {
            CoreUtils.Destroy(m_Material);
            m_Material = null;
        }
        m_Pass = null;
    }

    static void Warn(string message)
    {
        if (Time.realtimeSinceStartup - s_LastWarnTime < 4f)
            return;
        s_LastWarnTime = Time.realtimeSinceStartup;
        Debug.LogWarning(message);
    }

    sealed class VolumetricLightPass : ScriptableRenderPass
    {
        static readonly int ID_InverseVP = Shader.PropertyToID("_InverseVP");
        static readonly int ID_LightTravelDir = Shader.PropertyToID("_LightTravelDir");
        static readonly int ID_LightColor = Shader.PropertyToID("_LightColor");
        static readonly int ID_Intensity = Shader.PropertyToID("_Intensity");
        static readonly int ID_Density = Shader.PropertyToID("_Density");
        static readonly int ID_Extinction = Shader.PropertyToID("_Extinction");
        static readonly int ID_Anisotropy = Shader.PropertyToID("_Anisotropy");
        static readonly int ID_MaxDistance = Shader.PropertyToID("_MaxDistance");
        static readonly int ID_HeightStart = Shader.PropertyToID("_HeightStart");
        static readonly int ID_HeightFalloff = Shader.PropertyToID("_HeightFalloff");
        static readonly int ID_ShadowStrength = Shader.PropertyToID("_ShadowStrength");
        static readonly int ID_NoiseAmp = Shader.PropertyToID("_NoiseAmp");
        static readonly int ID_ApplyExtinction = Shader.PropertyToID("_ApplyExtinction");
        static readonly int ID_CompositeScale = Shader.PropertyToID("_CompositeScale");
        static readonly int ID_StepCount = Shader.PropertyToID("_StepCount");
        static readonly int ID_Frame = Shader.PropertyToID("_Frame");
        static readonly int ID_AnimTime = Shader.PropertyToID("_AnimTime");
        static readonly int ID_UseMainCascade = Shader.PropertyToID("_UseMainLightCascade");
        static readonly int ID_DebugMode = Shader.PropertyToID("_DebugMode");
        static readonly int ID_DepthReject = Shader.PropertyToID("_DepthReject");
        static readonly int ID_VolumeTexel = Shader.PropertyToID("_VolumeTexelSize");
        static readonly int ID_BlurDir = Shader.PropertyToID("_BlurDirection");
        static readonly int ID_SpecifiedShadowBias = Shader.PropertyToID("_SpecifiedShadowBias");
        static readonly int ID_SpecifiedVP = Shader.PropertyToID("_SpecifiedLightVP");
        static readonly int ID_SpecifiedShadow = Shader.PropertyToID("_SpecifiedShadowMap");
        static readonly int ID_VolumeTex = Shader.PropertyToID("_VolumeTex");
        static readonly int ID_UseCylinder = Shader.PropertyToID("_UseCylinder");
        static readonly int ID_CylinderOrigin = Shader.PropertyToID("_CylinderOrigin");
        static readonly int ID_CylinderDir = Shader.PropertyToID("_CylinderDir");
        static readonly int ID_CylinderRadius = Shader.PropertyToID("_CylinderRadius");
        static readonly int ID_CylinderHeight = Shader.PropertyToID("_CylinderHeight");
        static readonly int ID_CylinderEdgeFade = Shader.PropertyToID("_CylinderEdgeFade");

        static readonly ShaderTagId k_ShadowCaster = new ShaderTagId("ShadowCaster");
        static readonly List<ShaderTagId> k_ShadowTags = new List<ShaderTagId> { k_ShadowCaster };

        Settings m_Settings;
        Material m_Material;
        VolumetricLightVolume m_Volume;
        VolumetricLightSource m_Source;
        Light m_Light;

        public VolumetricLightPass()
        {
            profilingSampler = new ProfilingSampler("VolumetricLight");
        }

        public void Setup(Settings settings, Material material, VolumetricLightVolume volume, VolumetricLightSource source)
        {
            m_Settings = settings ?? new Settings();
            m_Material = material;
            m_Volume = volume;
            m_Source = source;
            m_Light = source != null ? source.specifiedLight : null;
        }

        class SharedParams
        {
            public Material material;
            public Matrix4x4 invVP;
            public Matrix4x4 specifiedVP;
            public Matrix4x4 shadowView;
            public Matrix4x4 shadowProj;
            public Vector3 lightTravelDir;
            public Vector4 lightColor;
            public float intensity;
            public float density;
            public float extinction;
            public float anisotropy;
            public float maxDistance;
            public float heightStart;
            public float heightFalloff;
            public float shadowStrength;
            public float noiseAmp;
            public float applyExtinction;
            public float compositeScale;
            public float stepCount;
            public float frame;
            public float animTime;
            public float useMainCascade;
            public float debugMode;
            public float depthReject;
            public Vector4 volumeTexel;
            public bool useMainCascadeFlag;
            public float useCylinder;
            public Vector3 cylinderOrigin;
            public Vector3 cylinderDir;
            public float cylinderRadius;
            public float cylinderHeight;
            public float cylinderEdgeFade;
        }

        class ShadowPassData
        {
            public RendererListHandle rendererList;
            public Matrix4x4 view;
            public Matrix4x4 proj;
        }

        class BlitPassData
        {
            public Material material;
            public TextureHandle source;
            public TextureHandle volume;
            public TextureHandle specifiedShadow;
            public SharedParams p;
            public int passIndex;
            public Vector2 blurDir;
            public bool bindSpecifiedShadow;
        }

        static void GetQuality(Quality q, int screenW, int screenH,
            out int w, out int h, out int steps, out bool blur, out int shadowSize)
        {
            float scale = q == Quality.Low ? 0.25f : 0.5f;
            steps = q == Quality.Low ? 16 : q == Quality.Medium ? 32 : 64;
            blur = q != Quality.Low;
            shadowSize = q == Quality.Low ? 1024 : 2048;
            w = Mathf.Max(8, (Mathf.RoundToInt(screenW * scale) + 7) / 8 * 8);
            h = Mathf.Max(8, (Mathf.RoundToInt(screenH * scale) + 7) / 8 * 8);
        }

        static void ComputeSpecifiedLightMatrices(Camera cam, Vector3 travelDir, float range,
            out Matrix4x4 view, out Matrix4x4 proj, out Matrix4x4 gpuVP)
        {
            if (travelDir.sqrMagnitude < 1e-8f)
                travelDir = Vector3.down;
            travelDir.Normalize();

            Vector3 up = Mathf.Abs(Vector3.Dot(travelDir, Vector3.up)) > 0.99f ? Vector3.forward : Vector3.up;
            float half = Mathf.Max(1f, range * 0.5f);
            Vector3 pos = cam.transform.position - travelDir * half;
            Quaternion rot = Quaternion.LookRotation(travelDir, up);
            Matrix4x4 world = Matrix4x4.TRS(pos, rot, Vector3.one);
            view = Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) * world.inverse;
            proj = Matrix4x4.Ortho(-half, half, -half, half, 0.1f, range);
            gpuVP = GL.GetGPUProjectionMatrix(proj, true) * view;
        }

        static bool IsMainDirectional(UniversalLightData lightData, Light specified)
        {
            if (specified == null || lightData == null || lightData.mainLightIndex < 0)
                return false;
            if (lightData.mainLightIndex >= lightData.visibleLights.Length)
                return false;
            var vl = lightData.visibleLights[lightData.mainLightIndex];
            return vl.light == specified;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (m_Material == null || m_Volume == null || m_Light == null)
                return;

            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            UniversalLightData lightData = frameData.Get<UniversalLightData>();
            UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();

            if (resourceData.isActiveTargetBackBuffer)
                return;

            TextureHandle cameraColor = resourceData.activeColorTexture;
            TextureHandle depthTex = resourceData.cameraDepthTexture;
            if (!cameraColor.IsValid() || !depthTex.IsValid())
                return;

            var cam = cameraData.camera;
            Vector3 travelDir = m_Light.transform.forward;
            if (travelDir.sqrMagnitude < 1e-8f)
                return;
            travelDir.Normalize();

            Color srcColor = m_Source != null ? m_Source.color : Color.white;
            float srcIntensity = m_Source != null ? Mathf.Max(0f, m_Source.intensity) : 1f;
            Color volTint = m_Volume.color.value;
            Color lightCol = srcColor * volTint;
            float finalIntensity = srcIntensity * m_Volume.intensity.value;

            bool useMain = IsMainDirectional(lightData, m_Light);
            if (useMain && m_Light.shadows == LightShadows.None)
                WarnOnceMainShadowsOff();

            var desc = cameraData.cameraTargetDescriptor;
            GetQuality(m_Settings.quality, desc.width, desc.height,
                out int vw, out int vh, out int steps, out bool doBlur, out int shadowSize);

            float shadowRange = UniversalRenderPipeline.asset != null
                ? UniversalRenderPipeline.asset.shadowDistance
                : m_Volume.maxDistance.value;
            shadowRange = Mathf.Max(shadowRange, m_Volume.maxDistance.value);

            ComputeSpecifiedLightMatrices(cam, travelDir, shadowRange,
                out Matrix4x4 shadowView, out Matrix4x4 shadowProj, out Matrix4x4 specifiedVP);

            Matrix4x4 view = cam.worldToCameraMatrix;
            Matrix4x4 proj = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true);
            Matrix4x4 invVP = (proj * view).inverse;

            var p = new SharedParams
            {
                material = m_Material,
                invVP = invVP,
                specifiedVP = specifiedVP,
                shadowView = shadowView,
                shadowProj = shadowProj,
                lightTravelDir = travelDir,
                lightColor = lightCol,
                intensity = finalIntensity,
                density = m_Volume.density.value,
                extinction = m_Volume.extinction.value,
                anisotropy = m_Volume.anisotropy.value,
                maxDistance = m_Volume.maxDistance.value,
                heightStart = m_Volume.heightStart.value,
                heightFalloff = m_Volume.heightFalloff.value,
                shadowStrength = m_Volume.shadowStrength.value,
                noiseAmp = m_Volume.noiseAmp.value,
                applyExtinction = m_Volume.applyExtinction.value ? 1f : 0f,
                compositeScale = m_Volume.compositeScale.value,
                stepCount = steps,
                frame = Time.frameCount,
                animTime = Time.time,
                useMainCascade = useMain ? 1f : 0f,
                debugMode = (float)m_Settings.debugMode,
                depthReject = 0.002f,
                volumeTexel = new Vector4(1f / vw, 1f / vh, vw, vh),
                useMainCascadeFlag = useMain,
                useCylinder = m_Source != null && m_Source.useCylinderVolume ? 1f : 0f,
                cylinderOrigin = m_Source != null ? m_Source.VolumeTransform.position : Vector3.zero,
                cylinderDir = travelDir,
                cylinderRadius = m_Source != null ? Mathf.Max(0.01f, m_Source.cylinderRadius) : 8f,
                cylinderHeight = m_Source != null ? Mathf.Max(0.01f, m_Source.cylinderHeight) : 40f,
                cylinderEdgeFade = m_Source != null ? Mathf.Max(0f, m_Source.cylinderEdgeFade) : 1f
            };

            CoreUtils.SetKeyword(m_Material, "_MAIN_LIGHT_SHADOWS", useMain);
            CoreUtils.SetKeyword(m_Material, "_MAIN_LIGHT_SHADOWS_CASCADE", useMain);
            bool soft = useMain && UniversalRenderPipeline.asset != null &&
                        UniversalRenderPipeline.asset.supportsSoftShadows;
            CoreUtils.SetKeyword(m_Material, "_SHADOWS_SOFT", soft);

            TextureHandle specifiedShadow = TextureHandle.nullHandle;
            if (!useMain)
            {
                var shadowDesc = new TextureDesc(shadowSize, shadowSize)
                {
                    name = "_VolumetricSpecifiedShadow",
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    clearBuffer = true,
                    msaaSamples = MSAASamples.None,
                    depthBufferBits = DepthBits.Depth32
                };
                specifiedShadow = renderGraph.CreateTexture(shadowDesc);
                RecordShadowPass(renderGraph, renderingData, cameraData, lightData, specifiedShadow, p);
            }

            var volDesc = new TextureDesc(vw, vh)
            {
                name = "_VolumetricLightRT",
                format = GraphicsFormat.R16G16B16A16_SFloat,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                clearBuffer = true,
                clearColor = Color.clear,
                msaaSamples = MSAASamples.None,
                depthBufferBits = DepthBits.None
            };
            TextureHandle volumeRT = renderGraph.CreateTexture(volDesc);
            TextureHandle blurRT = doBlur ? renderGraph.CreateTexture(volDesc) : TextureHandle.nullHandle;

            RecordFullscreenPass(renderGraph, "Volumetric.March", volumeRT, TextureHandle.nullHandle,
                depthTex, specifiedShadow, p, 0, Vector2.zero, useShadow: !useMain);

            if (doBlur)
            {
                RecordFullscreenPass(renderGraph, "Volumetric.BlurH", blurRT, volumeRT,
                    depthTex, specifiedShadow, p, 1, new Vector2(1f, 0f), useShadow: false);
                RecordFullscreenPass(renderGraph, "Volumetric.BlurV", volumeRT, blurRT,
                    depthTex, specifiedShadow, p, 2, new Vector2(0f, 1f), useShadow: false);
            }

            var destDesc = renderGraph.GetTextureDesc(cameraColor);
            destDesc.name = "CameraColor-VolumetricLight";
            destDesc.clearBuffer = false;
            TextureHandle destination = renderGraph.CreateTexture(destDesc);

            RecordCompositePass(renderGraph, cameraColor, destination, volumeRT, depthTex, p);
            resourceData.cameraColor = destination;
        }

        void RecordShadowPass(RenderGraph renderGraph,
            UniversalRenderingData renderingData,
            UniversalCameraData cameraData,
            UniversalLightData lightData,
            TextureHandle shadowRT,
            SharedParams p)
        {
            using (var builder = renderGraph.AddRasterRenderPass<ShadowPassData>(
                       "Volumetric.SpecifiedShadow", out var data, profilingSampler))
            {
                var filter = new FilteringSettings(RenderQueueRange.opaque);
                var draw = RenderingUtils.CreateDrawingSettings(
                    k_ShadowTags, renderingData, cameraData, lightData, SortingCriteria.CommonOpaque);

                var param = new RendererListParams(renderingData.cullResults, draw, filter);
                data.rendererList = renderGraph.CreateRendererList(param);
                data.view = p.shadowView;
                data.proj = p.shadowProj;

                builder.UseRendererList(data.rendererList);
                builder.SetRenderAttachmentDepth(shadowRT, AccessFlags.Write);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (ShadowPassData pass, RasterGraphContext ctx) =>
                {
                    ctx.cmd.ClearRenderTarget(true, false, Color.clear, 0f);
                    ctx.cmd.SetViewProjectionMatrices(pass.view, pass.proj);
                    ctx.cmd.DrawRendererList(pass.rendererList);
                });
            }
        }

        void RecordFullscreenPass(RenderGraph renderGraph, string name, TextureHandle dest,
            TextureHandle sourceVolume, TextureHandle depth, TextureHandle specifiedShadow,
            SharedParams p, int passIndex, Vector2 blurDir, bool useShadow)
        {
            using (var builder = renderGraph.AddRasterRenderPass<BlitPassData>(name, out var data, profilingSampler))
            {
                data.material = p.material;
                data.source = sourceVolume;
                data.volume = sourceVolume;
                data.specifiedShadow = specifiedShadow;
                data.p = p;
                data.passIndex = passIndex;
                data.blurDir = blurDir;
                data.bindSpecifiedShadow = useShadow && specifiedShadow.IsValid();

                if (depth.IsValid())
                    builder.UseTexture(depth, AccessFlags.Read);
                if (sourceVolume.IsValid())
                    builder.UseTexture(sourceVolume, AccessFlags.Read);
                if (data.bindSpecifiedShadow)
                    builder.UseTexture(specifiedShadow, AccessFlags.Read);

                builder.SetRenderAttachment(dest, 0, AccessFlags.Write);
                builder.AllowGlobalStateModification(true);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (BlitPassData pass, RasterGraphContext ctx) =>
                {
                    PushShared(pass.p, ctx.cmd);
                    ctx.cmd.SetGlobalVector(ID_BlurDir, pass.blurDir);
                    if (pass.volume.IsValid())
                        ctx.cmd.SetGlobalTexture(ID_VolumeTex, pass.volume);
                    if (pass.bindSpecifiedShadow)
                        ctx.cmd.SetGlobalTexture(ID_SpecifiedShadow, pass.specifiedShadow, RenderTextureSubElement.Depth);
                    Blitter.BlitTexture(ctx.cmd, new Vector4(1f, 1f, 0f, 0f), pass.material, pass.passIndex);
                });
            }
        }

        void RecordCompositePass(RenderGraph renderGraph, TextureHandle source, TextureHandle dest,
            TextureHandle volumeRT, TextureHandle depth, SharedParams p)
        {
            using (var builder = renderGraph.AddRasterRenderPass<BlitPassData>(
                       "Volumetric.Composite", out var data, profilingSampler))
            {
                data.material = p.material;
                data.source = source;
                data.volume = volumeRT;
                data.p = p;
                data.passIndex = 3;

                builder.UseTexture(source, AccessFlags.Read);
                builder.UseTexture(volumeRT, AccessFlags.Read);
                if (depth.IsValid())
                    builder.UseTexture(depth, AccessFlags.Read);

                builder.SetRenderAttachment(dest, 0, AccessFlags.Write);
                builder.AllowGlobalStateModification(true);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (BlitPassData pass, RasterGraphContext ctx) =>
                {
                    PushShared(pass.p, ctx.cmd);
                    ctx.cmd.SetGlobalTexture(ID_VolumeTex, pass.volume);
                    Blitter.BlitTexture(ctx.cmd, pass.source, new Vector4(1f, 1f, 0f, 0f), pass.material, 3);
                });
            }
        }

        static void PushShared(SharedParams p, RasterCommandBuffer cmd)
        {
            cmd.SetGlobalMatrix(ID_InverseVP, p.invVP);
            cmd.SetGlobalVector(ID_LightTravelDir, p.lightTravelDir);
            cmd.SetGlobalVector(ID_LightColor, p.lightColor);
            cmd.SetGlobalFloat(ID_Intensity, p.intensity);
            cmd.SetGlobalFloat(ID_Density, p.density);
            cmd.SetGlobalFloat(ID_Extinction, p.extinction);
            cmd.SetGlobalFloat(ID_Anisotropy, p.anisotropy);
            cmd.SetGlobalFloat(ID_MaxDistance, p.maxDistance);
            cmd.SetGlobalFloat(ID_HeightStart, p.heightStart);
            cmd.SetGlobalFloat(ID_HeightFalloff, p.heightFalloff);
            cmd.SetGlobalFloat(ID_ShadowStrength, p.shadowStrength);
            cmd.SetGlobalFloat(ID_NoiseAmp, p.noiseAmp);
            cmd.SetGlobalFloat(ID_ApplyExtinction, p.applyExtinction);
            cmd.SetGlobalFloat(ID_CompositeScale, p.compositeScale);
            cmd.SetGlobalFloat(ID_StepCount, p.stepCount);
            cmd.SetGlobalFloat(ID_Frame, p.frame);
            cmd.SetGlobalFloat(ID_AnimTime, p.animTime);
            cmd.SetGlobalFloat(ID_UseMainCascade, p.useMainCascade);
            cmd.SetGlobalFloat(ID_DebugMode, p.debugMode);
            cmd.SetGlobalFloat(ID_DepthReject, p.depthReject);
            cmd.SetGlobalVector(ID_VolumeTexel, p.volumeTexel);
            cmd.SetGlobalFloat(ID_SpecifiedShadowBias, 0.002f);
            cmd.SetGlobalMatrix(ID_SpecifiedVP, p.specifiedVP);
            cmd.SetGlobalFloat(ID_UseCylinder, p.useCylinder);
            cmd.SetGlobalVector(ID_CylinderOrigin, p.cylinderOrigin);
            cmd.SetGlobalVector(ID_CylinderDir, p.cylinderDir);
            cmd.SetGlobalFloat(ID_CylinderRadius, p.cylinderRadius);
            cmd.SetGlobalFloat(ID_CylinderHeight, p.cylinderHeight);
            cmd.SetGlobalFloat(ID_CylinderEdgeFade, p.cylinderEdgeFade);
        }

        static void WarnOnceMainShadowsOff()
        {
            Warn("[VolumetricLight] 指定灯是主光但未开阴影，树隙光柱不会出现。");
        }

        static void Warn(string message)
        {
            if (Time.realtimeSinceStartup - s_LastWarnTime < 4f)
                return;
            s_LastWarnTime = Time.realtimeSinceStartup;
            Debug.LogWarning(message);
        }
    }
}
