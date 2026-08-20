# 光线步进体积光方案

状态：第一期代码已落地。挂载：菜单 `Tools/Volumetric Light/Install To PC Renderer And Scene`。  
适用范围：本仓库 Unity 6.0.48 / URP 17.0.4 Render Graph（非 Compatibility Mode）。  
第一期目标：按**指定的一盏定向光**做世界空间光线步进体积光。方向、树隙光柱都跟这盏灯走，不自动绑场景主光。

---

## 1. 目标与非目标

### 要做成什么样

每个像素从相机沿视线在世界空间里步进。每一步：

1. 用场景深度截断，碰到不透明表面就停（不穿树干、地形）。
2. 散射方向来自**指定的那盏 Light** 的照射方向，不自动读「当前主光」。
3. 树隙光柱必须和这盏灯同向：阴影图按**同一方向**采样。树挡住灯的地方，空气里就是暗缝；缝里漏光的地方就是亮柱。
4. 用密度 × Beer-Lambert 累加单次散射，Henyey-Greenstein 沿这盏灯的方向，逆光时更亮。
5. 半分辨率计算，深度感知模糊后升采样，加到相机颜色上。

默认是 **低密度空气散射 + 树隙光柱**，不是厚雾。

指定光源约定（已拍板）：

- 场景里挂 `VolumetricLightSource`，拖一盏 **Directional Light** 进去（也可以和 Light 挂在同一物体上，自动取）。
- 光线**传播方向** = `specifiedLight.transform.forward`。转这盏灯，体积光和光柱一起转。
- Shader 里 `_LightTravelDir` 是归一化后的传播方向。对着灯看 = 看向 `-_LightTravelDir`。
- 颜色默认用这盏灯的 `color * intensity`，Volume 可覆盖。
- **不**在没指定灯时偷偷改用 `_MainLightPosition`。没指定灯：Pass 不入队，打 warning。

树隙光柱怎么跟指定灯对齐：

| 指定的灯是什么 | 阴影怎么采 |
|---|---|
| 正好是 URP 主定向光 | 复用现成的级联阴影图（方向已经一致，零额外开销） |
| 另一盏定向光 | 按这盏灯的方向渲一张正交阴影图，步进时采这张 |
| 灯关了阴影、或不是定向光 | 第一期不支持；warning，不入队或退化为无光柱并提示 |

### 第一期不做

| 不做 | 原因 |
|---|---|
| 屏幕径向模糊 | 不是 3D 步进 |
| Froxel / 3D 切片体积雾 | 过重 |
| 点光 / 聚光体积 | 第一期只做定向光 |
| 自动跟「场景主光」走 | 必须显式指定一盏灯 |
| 多盏体积光叠加 | 只认一个 Source |
| TAA 历史复用 | 已做屏幕空间时空重采样（非 ReSTIR） |
| 改 `Water.hlsl` / 树着色器 | 光柱第一期不进 SSPR 反射 |

---

## 2. 为什么选这条路

| 方案 | 原理 | 结论 |
|---|---|---|
| A. 屏幕径向模糊 | 沿像素到太阳做 2D 累加 | 排除 |
| B. 全屏相机射线 + **指定灯方向上的** Shadow Map | 深度重建世界点，步进采样密度和同向阴影 | **第一期** |
| C. Froxel | 视锥体素预积分 | 过重 |
| D. 每盏灯画锥体 Mesh | 适合室内多灯 | 第二期 |

核心公式：

```
L = Σ  Li(x_n) · T(x_n) · Δt
T ← T · exp(-σ_t · ρ · Δt)
Li = I_light · V_shadow · ρ · σ_s · Phase_HG(θ)
```

- `I_light`：指定灯的颜色（或 Volume 覆盖）
- `V_shadow`：指定灯方向上的阴影可见度。这是树隙光柱的来源
- `Phase_HG`：相对指定灯 `_LightTravelDir`
- `θ`：视线与「灯射来的方向」的夹角

没有同向阴影，就只剩一层定向辉光，没有树缝里那一条条亮柱。

---

## 3. 项目约束

- Render Graph 主路径。只走 `RecordRenderGraph`。
- Deferred。Depth / Opaque / 主光 4 级联 2048 已开。
- 树已走 ShadowCaster，主光阴影图里已有树影。指定灯 = 主光时直接用。
- SSPR 在 450。体积光在 400。

调度对齐 SSPR；合成对齐官方 `AddBlitPass` + 交换 `cameraColor`。

---

## 4. 插入点

```
指定灯的自定义阴影图（若需要，AfterRenderingShadows 或体积光 Pass 前）
Opaques → Copy Depth / Opaque → Skybox
  → 体积光 March / Blur / Composite   AfterRenderingSkybox (400)
  → SSPR (450)
  → Transparents（水面）
  → Post Process
```

选择 400：深度齐、水面未画、与 SSPR 错开。第一期光柱不进水面反射。

自定义阴影图若要渲，必须在 March 之前完成，且只画不透明 ShadowCaster。

---

## 5. 文件清单

| 路径 | 职责 |
|---|---|
| `Assets/Scripts/Rendering/VolumetricLightFeature.cs` | Renderer Feature + Render Graph |
| `Assets/Scripts/Rendering/VolumetricLightVolume.cs` | Volume 调参 |
| `Assets/Scripts/Rendering/VolumetricLightSource.cs` | 指定那盏 Light + Gizmo |
| `Assets/Shaders/VolumetricLight.shader` | March / BlurH / BlurV / Composite |
| `Assets/Shaders/VolumetricLight.hlsl` | 步进、HG、Beer、采指定灯阴影 |
| `Assets/Shaders/VolumetricLightShadow.shader` | 非主光时渲正交深度（可复用 URP ShadowCaster） |
| 本文档 | 方案 |

抖动用 Interleaved Gradient Noise，不另做蓝噪声贴图。Volume 上有独立的 **Jitter** 开关；关掉后每步采区间中点，颗粒消失，远处可能露出步进条带。

降噪走 **空间邻域重用 + 时间重投影**（Wronski / UE Volumetric Fog / ARM clustered fog 那套），不是 SIGGRAPH Asia 2021 的 Volumetric ReSTIR。ReSTIR 是路径空间水库重采样，给云/烟路径追踪用，和本工程的屏幕空间单次散射步进对不上。Jitter 负责把条带打散成颗粒，时空混合把多帧样本叠回去。

---

## 6. 职责划分

**Feature**  
质量档、注入点、Shader、自定义阴影图分辨率、Debug。

**Volume**  
强度倍率、密度、吸收、各向异性、最大距离、高度衰减、Shadow Strength、是否覆盖灯的颜色、是否物理透过率合成。

**Source**  
- `Light specifiedLight`：指定的定向光  
- 方向永远取这盏灯的 `transform.forward`  
- 多个 Source：取第一个启用的，warning  
- 没指定灯或不是 Directional：不入队，warning

Intensity ≈ 0 或 Volume 未开：不入队。

---

## 7. GPU Pass

```
（可选）Pass0  按指定灯方向渲正交阴影图
        ↓
Depth + 指定灯方向/颜色 + 同向阴影 + IGN
        ↓
Pass1  半分辨率步进  →  HDR Volume RT（RGB=inscatter，A=T）
        ↓
Pass1b 空间 4 tap + 时间重投影（可关）→ 写入 History
        ↓
Pass2  双边模糊（Medium / High）
        ↓
Pass3  合成  scene + inscatter（默认）
```

| 档 | 分辨率 | 步数 | 模糊 | 自定义阴影图 |
|---|---|---|---|---|
| Low | 1/4 | 16 | 关 | 1024 |
| Medium | 1/2 | 32 | 开 | 2048 |
| High | 1/2 | 64 | 开 | 2048 |

Volume RT：`R16G16B16A16_SFloat`。

指定灯 = 主光时跳过 Pass0，直接采 URP 级联阴影。

矩阵与 SSPR 一致：`GL.GetGPUProjectionMatrix(proj, true)`，显式传 `_InverseVP`。  
Scene 相机跑；Preview / Reflection 不跑。

---

## 8. 指定灯与阴影

### 8.1 每帧解析

```csharp
var source = VolumetricLightSource.FindActive();
if (source == null || source.specifiedLight == null ||
    source.specifiedLight.type != LightType.Directional)
    return;

var light = source.specifiedLight;
Vector3 travelDir = light.transform.forward.normalized; // 光线传播方向
Color lightCol = vol.overrideColor.value
    ? vol.color.value
    : light.color * light.intensity;

bool isMain = (light == RenderSettings? /* 用 UniversalRenderPipeline.lightData / visibleLights 判断主光 */);
```

URP 里判断主光：`cameraData` / `frameData.Get<UniversalLightData>()` 的 `mainLightIndex` 对应那盏灯。对得上就走级联，对不上就走自定义正交阴影。

### 8.2 自定义正交阴影（指定灯不是主光时）

- 一张 Depth RT，正交相机：`forward = travelDir`，范围覆盖阴影距离（默认对齐 URP Shadow Distance ≈ 160）。
- 中心：相机位置沿地面投影，或场景包围盒中心。第一期用「主相机位置 + 回退 travelDir * halfRange」，保证相机附近的树在阴影图里。
- 用 URP `ShadowCaster` RendererList 画不透明。
- Shader 里：`shadowCoord = lightVP * float4(p,1)`，比较深度得 `vis`。
- 不要用 `_MainLightPosition`，避免和指定灯拧到一起。

### 8.3 步进核里采阴影

```hlsl
float vis;
if (_UseMainLightCascade > 0.5)
    vis = MainLightRealtimeShadow(TransformWorldToShadowCoord(p));
else
    vis = SampleSpecifiedLightShadow(p); // 自定义正交图

vis = lerp(1.0, vis, _ShadowStrength);

float mu = dot(viewDir, -_LightTravelDir);
float phase = HenyeyGreenstein(mu, _Anisotropy);
float3 lightCol = _LightColor.rgb * _Intensity;
```

`_ShadowStrength = 0`：光柱缝消失，只剩定向辉光。默认 1。

### 8.4 步长 / 深度 / 高度

- 深度重建、`sceneZ / StepCount`、IGN 抖动：不变。
- 高度衰减贴水面 `Y = 3.14`：不变。
- HG 相对指定灯方向：转灯，最亮一侧跟着走。

---

## 9. 模糊与合成

双边模糊 5 tap，深度差超过约 `0.002` 拒采样。  
默认 `scene + inscatter`。勾选 Apply Extinction 才 `scene * T + inscatter`。  
升采样再比一次全分辨率深度，压树边漏光。

---

## 10. Volume 参数

| 参数 | 默认 | 含义 |
|---|---|---|
| Override Color | false | true 时用下面的 Color，否则用指定灯颜色 |
| Color | 暖白 HDR | 仅 Override 时生效 |
| Intensity | 0.6 | 乘在灯颜色上的倍率 |
| Density | 0.08 | 空气密度 |
| Extinction | 0.04 | 吸收 |
| Anisotropy | 0.6 | HG |
| Max Distance | 160 | 射线最远 |
| Height Start / Falloff | 3.14 / 0.08 | 贴地变淡 |
| Shadow Strength | 1 | 树隙对比度，0 = 无光柱 |
| Noise Amp | 0 | 介质 3D 噪声，默认关 |
| Jitter | true | 沿视线抖动采样起点。开时空重采样时建议保持开启 |
| Spatiotemporal Resample | true | 空间邻域 + 时间重投影 |
| Temporal Blend | 0.12 | 当前帧权重，越小越稳越容易拖影 |
| Spatial Radius | 2 | 空间重采样半径（纹素），0 只做时间混合 |
| Apply Extinction | false | 是否用 T 压暗场景 |
| Composite Scale | 1 | 合成微调 |

方向不在 Volume 上调，只转指定的那盏灯。

---

## 11. Debug

| 模式 | 用途 |
|---|---|
| Off | 正常 |
| Inscatter | 只看累加的光 |
| Transmittance | 灰度 T |
| ShadowAlongRay | 射线上平均可见度。树影里应暗 |
| LightDirection | 把指定灯方向映射成颜色。转灯应变 |
| MarchDistance | t / sceneZ |

先 `LightDirection` 确认跟的是指定灯，再 `ShadowAlongRay` 确认光柱缝和这盏灯同向。

---

## 12. Shader Pass

| Pass | 用途 |
|---|---|
| 0 March | 半分辨率步进 |
| 1 BlurH / 2 BlurV | 双边模糊 |
| 3 Composite | 合成 |
| 阴影 | URP ShadowCaster 或专用深度 shader |

材质由 Feature 从 Shader 创建，不拖 Material。

---

## 13. 和现有系统

| 系统 | 关系 |
|---|---|
| 指定的 Directional Light | 唯一方向来源；转它 = 转体积光和树隙 |
| 其他场景灯 | 不驱动体积光 |
| 树 | ShadowCaster 写入「指定灯方向」的阴影图，剪出光柱 |
| SSPR / 水面 | 不改。水面上可见光柱，反射里没有 |
| Bloom | 吃 HDR 光柱，想要的 |

---

## 14. 执行顺序

1. **通路**：半分辨率纯色 blit。  
2. **深度步进**：均匀辉光，贴墙不穿模。  
3. **指定灯方向**：转指定灯，亮侧跟着走；转另一盏没指定的灯，体积光不动。  
4. **同向树隙**：指定灯 = 主光时复用级联，树后出现光柱；换一盏非主光定向灯，自定义阴影图也要剪出同向光柱。Shadow Strength=0 缝消失。  
5. **去条带 + 升采样**。  
6. **挂 PC_Renderer**，与 SSPR 同开。

---

## 15. 风险

| 风险 | 对策 |
|---|---|
| 指定灯不是主光时没有缝 | 第一期就渲正交阴影，不借用主光图 |
| 自定义阴影范围不够 | 范围对齐 Shadow Distance；中心跟主相机 |
| 条带 / 漏光 | IGN + 双边模糊 + 深度感知升采样 |
| 没指定灯 | 不入队 + warning |
| 水面不反射光柱 | 第一期接受 |

---

## 16. 第二期

- 点光 / 聚光体积 + 对应阴影  
- 多 Source 叠加  
- 自定义阴影改级联  
- 光柱进水面  
- TAA 复用  

---

## 17. 已拍板

| 项 | 决定 |
|---|---|
| 方向来源 | 指定的 Directional Light.forward |
| 没指定灯 | 不偷偷用主光，直接跳过 |
| 树隙光柱 | 必须与指定灯同向 |
| 指定灯 = 主光 | 复用 URP 级联 |
| 指定灯 ≠ 主光 | 渲一张正交阴影图（第一期就要） |
| 颜色 | 默认跟指定灯，Volume 可覆盖 |
| 只挂 PC_Renderer | 是 |
| Volume RT | R16G16B16A16_SFloat |
| 步长 | sceneZ / StepCount |
| 抖动 | IGN |
| 默认合成 | 加法 |
| 光柱进水面 | 第一期不做 |
| Scene 视图 | 跑 |
| 多 Source | 只取一个 |

---

## 18. C# 草案

```csharp
public class VolumetricLightSource : MonoBehaviour
{
    public Light specifiedLight; // 必须 Directional
    void Reset() => specifiedLight = GetComponent<Light>();
    public static VolumetricLightSource FindActive() { /* 注册表 */ }
}

public class VolumetricLightVolume : VolumeComponent
{
    public BoolParameter overrideColor = new(false);
    public ColorParameter color = new(new Color(1f, 0.96f, 0.85f), hdr: true, false, true);
    public ClampedFloatParameter intensity = new(0.6f, 0f, 8f);
    public ClampedFloatParameter shadowStrength = new(1f, 0f, 1f);
    public bool IsActive() => intensity.value > 0.001f;
}
```

挂载：`PC_Renderer` 加 Feature；场景里指定一盏定向光；Volume 调密度和强度。

---

## 19. 修订记录

| 日期 | 内容 |
|---|---|
| 2026-08-14 | 初稿。 |
| 2026-08-14 | 方向与场景灯解耦，主光阴影改为可选剪影。 |
| 2026-08-14 | **改为按指定光源方向。** 树隙光柱必须与该灯同向：是主光则复用级联，否则第一期渲正交阴影图。不再借用「另一方向」的主光阴影。 |
| 2026-08-14 | 按文档实现 Feature / Volume / Source / Shader。指定灯=主光走级联，否则渲正交 ShadowCaster 深度图。 |
