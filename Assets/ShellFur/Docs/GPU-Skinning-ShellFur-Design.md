# 方案 B：GPU Skinning Shell Fur — 完整设计方案

> 目标：不依赖 `SkinnedMeshRenderer` 出皮，只借用其 **bones + mesh（含权重/bindposes）**；  
> 用 **自定义 VS Linear Blend Skinning + DrawMeshInstanced（层 = instance）** 画 shell 毛发。  
> 平滑挤出法线在 **绑定姿势 bake 一次**，运行时随骨骼矩阵变换，**无需每帧重算平滑**。

---

## 1. 背景与问题

### 1.1 现状路径的问题

| 现状 | 问题 |
|------|------|
| `BakeMesh` 每帧 | CPU/上传开销；bake 出静态 mesh 再 instance |
| 平滑法线存顶点色 | VC **不会**被 skin；蒙皮后方向错误 |
| 每帧 recompute 平滑 | O(三角形 + 位置 weld)，高模贵 |

### 1.2 方案 B 核心思想

业界常见做法（Unity 社区 GPUSkinning / Adam 人群 demo / WebGL skinning 教程一致）：

1. CPU：每帧算 `skinMatrix[i] = bone.localToWorld * bindPose[i]`，写入 **ComputeBuffer / GraphicsBuffer**  
2. GPU VS：读 `BLENDINDICES` + `BLENDWEIGHT`，做 **Linear Blend Skinning (LBS)**  
3. 同一套矩阵变换 **position / normal / smoothNormal**  
4. 再做 shell 挤出与光照  

参考：

- [Unity 讨论：Instancing + bone matrix palette](https://discussions.unity.com/t/experiments-with-instancing-and-other-methods-to-render-massive-numbers-of-skinned-meshes/649925)  
- [chengkehan/GPUSkinning](https://github.com/chengkehan/GPUSkinning) 方式 1：每帧矩阵 → VS skinning  
- [WebGL Skinning fundamentals](https://webglfundamentals.org/webgl/lessons/webgl-skinning.html)：`skin = animatedPose * inverse(bindPose)`  
- Unity `mesh.bindposes` / `SMR.bones` 官方语义  

---

## 2. 架构总览

```
┌─────────────────────────────────────────────────────────────┐
│  Animator / 物理 更新骨骼 Transform                           │
└───────────────────────────┬─────────────────────────────────┘
                            ▼
┌─────────────────────────────────────────────────────────────┐
│  ShellFurGpuSkinSystem (CPU, LateUpdate / 相机前回调)         │
│  - 读取 SMR.bones + mesh.bindposes                           │
│  - skinMatrix[i] = bones[i].localToWorldMatrix * bindposes[i]│
│  - 上传 StructuredBuffer<float4x4> _BoneMatrices             │
│  - 设置 MPB：层数、毛长、slot 等                               │
│  - DrawMeshInstanced(bindPoseMesh, submesh, mat, matrices=I, │
│                      instanceCount=shellCount)               │
│    或 object matrix = SMR.localToWorld（见坐标系约定）         │
└───────────────────────────┬─────────────────────────────────┘
                            ▼
┌─────────────────────────────────────────────────────────────┐
│  Custom/ShellFurGpuSkinned (VS)                              │
│  1) LBS: P,N,Ns ← bind attributes + bones                   │
│  2) layer = f(instanceID)                                    │
│  3) P += Ns * layer * furLength + gravity                    │
│  4) clip / fog / 输出世界法线 N 做光照                        │
│  PS: 复用现有 ShadeShellFur                                   │
└─────────────────────────────────────────────────────────────┘

身体：继续用 SMR + 普通材质（slot 模式）
毛发槽：SMR 上 Skip 占位，避免双画
```

**SMR 职责**：只提供动画骨骼与 mesh 资源，**不负责画毛发几何**。

---

## 3. 坐标系与 Skin 公式（必须统一）

### 3.1 Unity 约定

- `mesh.bindposes[i]`：从 **mesh 本地空间 → 绑定姿势下第 i 根骨局部** 的逆（即 inverse bind matrix）  
- 运行时骨骼：`bones[i].localToWorldMatrix`（世界）  
- **Skin 矩阵（世界空间输出）**：

```text
M_i = bones[i].localToWorldMatrix * mesh.bindposes[i]
```

- 顶点（绑定姿势，mesh 本地）：

```text
P_world = Σ w_j * (M_j * float4(P_bind, 1)).xyz
```

- 法线（只用 3×3，再 normalize）：

```text
N_world = normalize(Σ w_j * mul((float3x3)M_j, N_bind))
```

更严谨时对法线用 `inverse(transpose(mat3(M)))`；含非均匀缩放时应用。  
均匀缩放可简化为 `mat3(M)` 再 normalize。

### 3.2 与 DrawMeshInstanced 的 object matrix

两种等价约定，二选一写死：

| 约定 | bone 矩阵 | Draw 的 matrix | VS 里 |
|------|-----------|----------------|-------|
| **A. Skin 直接出世界** | `M = bone.L2W * bindPose` | **全部用 `Matrix4x4.identity`** | 不再乘 unity_ObjectToWorld |
| **B. Skin 出 mesh 本地** | `M = inv(root.L2W) * bone.L2W * bindPose` | **`SMR.localToWorldMatrix`** | 再乘 ObjectToWorld |

推荐 **约定 A**：少一次矩阵、少踩 root 缩放坑；阴影/光照直接用世界位置。

### 3.3 负缩放 / 镜像骨（Ahri 类资源）

- 动画若带 `scale = -1`，`mat3(M)` 含反射 → 法线/剔除异常  
- 处理策略（实现时必做其一）：  
  1. **源头去掉动画 Scale 曲线 / Blender Apply**（首选）  
  2. VS：`if (determinant(mat3(M)) < 0) flip normal`  
  3. 上传前对 bone 矩阵做 `ScaleAbs` 仅保留旋转+位移（会改动画语义，慎用）

---

## 4. Mesh 数据布局

### 4.1 从 SMR 取什么

| 数据 | 来源 |
|------|------|
| 绑定姿势顶点 | `mesh.vertices` |
| 原始法线（光照） | `mesh.normals` |
| 平滑法线（挤出） | 离线 bake → 见 4.2 |
| UV | `mesh.uv` |
| 三角形 / submesh | `mesh.GetTriangles(slot)` |
| 权重 | `mesh.boneWeights` 或 `GetAllBoneWeights` + `GetBonesPerVertex`（>4 骨时） |
| bindposes | `mesh.bindposes` |
| bones | `skinnedMeshRenderer.bones`（Transform[]） |

**Mesh 必须 Readable**（提取到运行时 mesh 或开启 FBX Read/Write）。

### 4.2 平滑法线 bake（绑定姿势，一次）

沿用已修好的 **位置 weld + 角加权**（`ShellFurNormalUtility`）：

- 输出：`Vector3[] smoothNormalsOS`（绑定姿势、mesh 本地）  
- **不要只依赖顶点色当运行时唯一通道**（除非 VS 里也对 COLOR 做 skin）

推荐写入方式（二选一）：

| 通道 | 优点 | 注意 |
|------|------|------|
| **TEXCOORD2 / TEXCOORD3**（`float3`） | 语义清晰 | 需 `mesh.SetUVs` |
| **tangent.xyz** | 省流 | 不能同时用法线贴图切线 |

打包权重到顶点（若不用原生 skin 流）：

- `uv1 = float4(weight0..3)`  
- `uv2 = float4(index0..3)` 或两个 float2  

Unity mesh 也可保留 `BoneWeight` 结构，自定义 mesh 上传时用 `Mesh.SetBoneWeights`，但 **DrawMeshInstanced 默认不会做 skin**——仍要在 **你们的 shader** 里读这些数据。

**最稳的自定义路径**：导出/生成一份 **专用 fur mesh asset**：

```text
POSITION, NORMAL, TEXCOORD0 (uv),
TEXCOORD1 (boneWeights.xyzw),
TEXCOORD2 (boneIndices as float4),
TEXCOORD3 (smoothNormal.xyz)
```

从原 mesh + submesh 提取，避免和 SMR 画皮数据打架。

### 4.3 多 Slot

- 对每个 fur slot：可 **合并索引到一份 fur-only mesh**，或 **每 slot 一份 mesh + 多次 draw**  
- 推荐：**合并为一份 “FurSkinMesh”**（所有毛发 submesh 三角拼在一起），一次 bone buffer，一次 DrawInstanced  

---

## 5. CPU 系统详细设计

### 5.1 组件划分

```text
ShellFurGpuSkinRenderer : MonoBehaviour
  [SerializeField] SkinnedMeshRenderer sourceSmr;  // 只读骨骼与 sharedMesh
  [SerializeField] Material furMaterial;           // Custom/ShellFurGpuSkinned
  [SerializeField] bool useMaterialSlotOnly;
  [SerializeField] int[] furMaterialSlots;
  [SerializeField] int shellCount;
  [SerializeField] bool hideBaseMesh;
  // 毛长/重力等与现有一致

  Mesh _furBindMesh;              // 绑定姿势毛发网格（含权重+平滑法线）
  GraphicsBuffer _boneBuffer;     // float4x4 × boneCount
  Matrix4x4[] _boneMatrixCache;
  MaterialPropertyBlock _mpb;
```

可选：`ShellFurBindMeshBuilder` 编辑器工具 — 从 SMR 生成 `_furBindMesh` 资产。

### 5.2 每帧更新骨骼（在动画之后）

```text
时机：LateUpdate 或 RenderPipelineManager.beginContextRendering
（必须在 Animator 更新骨骼之后）

for i in 0..boneCount-1:
  if bones[i] == null: M = identity or last
  else: M = bones[i].localToWorldMatrix * bindposes[i]

_boneBuffer.SetData(_boneMatrixCache)
_mpb.SetBuffer("_BoneMatrices", _boneBuffer)
_mpb.SetInt("_BoneCount", boneCount)
```

性能：

- bone 数量通常 50–250，每帧 `SetData` 可接受  
- 可用 `GraphicsBuffer` + 双缓冲  
- 多角色：每角色一份 buffer，或大 buffer + per-draw offset  

### 5.3 绘制

```text
// 约定 A：skin 出世界，object matrix = Identity
matrices[0..shellCount-1] = Matrix4x4.identity

Graphics.DrawMeshInstanced(
  _furBindMesh, 0, furMaterial,
  matrices, shellCount, _mpb,
  shadowCasting, receiveShadows, layer, camera);
```

- `unity_InstanceID` → shell layer  
- **不要**再对 SMR 的毛发槽正常出皮（Skip 材质占位）  

### 5.4 Bounds / 裁剪

- 用 `SMR.bounds`（开 `updateWhenOffscreen` 若需要屏外动画）  
- 或每帧根据 bone 位置扩展 AABB  
- DrawMeshInstanced 不自动用 SMR bounds，需保证 mesh.bounds 足够大或改用 `Graphics.RenderMesh` + 正确 world bounds  

`Graphics.DrawMeshInstanced` 在部分版本对 bounds 使用 mesh.bounds * matrix；Identity 时要用 **已扩展的 bind mesh bounds** 或改为 `RenderMeshInstanced` 并设 `worldBounds = smr.bounds.Expanded(furLength)`。

---

## 6. Shader 详细设计

### 6.1 文件

- `ShellFurGpuSkinned.shader`  
- `ShellFurGpuSkinning.hlsl`（skin 函数）  
- 光照继续 `#include` 现有 `ShadeShellFur` 逻辑（抽公共）  

### 6.2 顶点输入

```hlsl
struct Attributes
{
    float4 positionOS : POSITION;      // bind pose
    float3 normalOS   : NORMAL;        // lighting bind
    float2 uv         : TEXCOORD0;
    float4 boneWeights: TEXCOORD1;     // w0..w3
    float4 boneIndices: TEXCOORD2;     // i0..i3 as float
    float3 smoothOS   : TEXCOORD3;     // smooth extrude normal bind
    // 或 COLOR 存 smooth，但必须在 VS skin
};
```

### 6.3 Skin 核心

```hlsl
StructuredBuffer<float4x4> _BoneMatrices;

float4x4 GetSkinMatrix(float4 weights, float4 indices)
{
    float4x4 m = 0;
    [unroll]
    for (int i = 0; i < 4; i++)
    {
        float w = weights[i];
        if (w > 0)
            m += _BoneMatrices[(int)indices[i]] * w;
    }
    return m;
}

void SkinPN(
    float3 pOS, float3 nOS, float3 sOS,
    float4 weights, float4 indices,
    out float3 pWS, out float3 nWS, out float3 sWS)
{
    float4x4 M = GetSkinMatrix(weights, indices);
    pWS = mul(M, float4(pOS, 1)).xyz;
    float3x3 R = (float3x3)M;
    nWS = normalize(mul(R, nOS));
    sWS = normalize(mul(R, sOS));
    // optional: if (determinant(R) < 0) { nWS = -nWS; sWS = -sWS; }
}
```

### 6.4 Shell 挤出（世界空间）

```hlsl
float layer = GetShellLayer(unity_InstanceID); // 含 hideBase offset
float3 posWS = pWS + sWS * (layer * _FurLength);
// gravity in world:
posWS += normalize(_GravityDir) * (_Gravity * layer * layer * _FurLength);
// → TransformWorldToHClip
// lighting uses nWS (not sWS) unless you prefer soft lighting
```

### 6.5 Instancing

- `#pragma multi_compile_instancing`  
- `unity_ObjectToWorld` 在约定 A 下应为 I；不要再变换一次  
- 或使用 `DrawMeshInstanced` 时传入 identity  

### 6.6 ShadowCaster

- 同一 VS skin + extrude  
- `ApplyShadowBias(posWS, nWS or sWS, lightDir)`  
- 必须绑定同一 `_BoneMatrices`  

### 6.7 与现有光照

- PS 输入：`positionWS, normalWS=nWS, uv, layer`  
- 直接调用现有 `ShadeShellFur`  
- 密度图 / procedural / KK 开关全部保留  

---

## 7. 两级优化（完善性能）

### 7.1 朴素版（先落地）

```text
每个 shell instance 完整 LBS + extrude
开销 ≈ O(V * 4 * shellCount) 矩阵-向量
```

层数 8～16 可先验证正确性。

### 7.2 完善版：Skin 一次，挤出多层

**Compute Shader：**

```text
Input: bind vertices, weights, indices, bone matrices
Output: StructuredBuffer skinned { float3 p; float3 n; float3 s; float2 uv; }

Dispatch(V)
  只做 LBS，不做 shell
```

**Draw：**

```text
DrawProcedural / DrawMesh with vertexID
  p' = p + s * layer * len
  instance = layer
```

或：

```text
Graphics.RenderPrimitivesIndirect
```

这样：

```text
Skin: O(V)
Extrude+VS: O(V * L) 但极轻
```

优于「每层全 skin」，也通常优于「BakeMesh + 全网格平滑 recompute」。

### 7.3 LOD

| 距离 | shellCount | 是否 fin | quality |
|------|------------|----------|---------|
| 近 | 24–32 | 可选 | 4 bone |
| 中 | 12–16 | 关 | 4 bone |
| 远 | 4–8 或 billboard | 关 | 2 bone |

---

## 8. 与现有 ShellFur 功能映射

| 现有功能 | GPU Skin 方案中的位置 |
|----------|----------------------|
| Shell 层数 / hide base | instanceID + offset |
| 密度图 / procedural | 不变（PS） |
| 重力 | 世界空间，skin 之后加 |
| 平滑法线 | bind bake → TEXCOORD，VS skin |
| Slot 多材质 | 提取多 submesh 合并 fur mesh |
| 隐藏源槽 | Skip 材质（已有） |
| Skinned 动画 | Animator → bones → buffer |
| Fin | 二期：边点同样带 weight，VS skin 后挤出 |
| BakeMesh 路径 | 可保留为 Fallback / 编辑器预览 |

---

## 9. 数据与资源管线

### 9.1 编辑器：`Tools/Shell Fur/Build GPU Skin Fur Mesh`

输入：`SkinnedMeshRenderer` 或 FBX  

步骤：

1. 确保 mesh readable（或 Instantiate 副本）  
2. 按 `furMaterialSlots` 提取三角  
3. 生成紧凑顶点（可选 weld 仅用于平滑，**skin 权重按原 index 映射**）  
4. `ShellFurNormalUtility.ComputeSmoothNormals`（位置 weld）  
5. 写入 TEXCOORD1/2/3  
6. 存为 `Assets/.../Ahri_FurGpuSkin.asset`  
7. 记录 `bindposes` 与 bone 路径列表（或运行时仍从 SMR 读 bones）  

**权重提取注意：**  
Unity 导入后顶点可能拆分，`BoneWeight` 与顶点一一对应；提取 submesh 时 **保留原顶点索引的权重**，不要错误平均不同权重的重合点（平滑法线可 weld，权重以原顶点为准分别 skin 再平均位置——标准 LBS 是 per-vertex index，拆分顶点各有自己的权重，一般相同）。

### 9.2 运行时校验

- `bones.Length == bindposes.Length`  
- 权重和 ≈ 1  
- bone 空引用警告  
- material keyword 与 buffer 绑定  

---

## 10. 实现阶段（建议里程碑）

| 阶段 | 交付 | 验收 |
|------|------|------|
| **M1** | Bone buffer 上传 + VS 只 skin 位置（无 shell）画 1 层 | 与 SMR 身体对齐、无炸裂 |
| **M2** | Skin normal + 对比 SMR | 法线方向大体正确 |
| **M3** | Shell 多层 instance + 现有 PS | 动画时毛发跟随 |
| **M4** | 平滑法线通道 skin + 关掉 recompute | 挤出平滑且跟动画 |
| **M5** | 多 slot 合并 mesh + Skip 槽 | Ahri 尾巴多槽 |
| **M6** | ShadowCaster 同 VS | 阴影跟随 |
| **M7** | Compute skin-once（可选） | 性能达标 |
| **M8** | Fin GPU skin（可选） | 轮廓毛 |

---

## 11. 风险清单与对策

| 风险 | 对策 |
|------|------|
| 负缩放动画 | 源修 scale；VS det 翻转；文档说明 |
| bindposes 与 bones 顺序不一致 | 只用同一 mesh 的 bindposes + 同一 SMR.bones |
| Draw 顺序 / 深度被身体挡住 | 毛发略沿 Ns 抬根；或透明队列；保证 draw 在不透明后 |
| Cull Back 双面资源 | 材质 Cull Off（尾巴） |
| Buffer 未绑定粉红/崩溃 | 缺 buffer 时 skip draw |
| 与 SRP Batcher | 用 MPB + buffer 会打断 batch，可接受 |
| 编辑器预览 | 无 Animator 时用 bind pose 矩阵 = bone.L2W * bindPose |

---

## 12. 为何这比「VC + 每帧重算」更合理

| | VC + 重算 | GPU Skin 方案 B |
|--|-----------|-----------------|
| 平滑 | 每帧 O(面) | bake 一次 + O(骨混合) |
| 正确性 | 依赖 recompute | 与骨骼一致 |
| 扩展 | 难做 GPU driven | 易接 compute / 多角色 palette |
| 成本 | 实现简单 | 实现重，长期正确 |

社区 GPUSkinning、人群实例化（矩阵 palette + VS skin）均验证该路线。

---

## 13. 建议的仓库落地结构

```text
Assets/ShellFur/
  Docs/GPU-Skinning-ShellFur-Design.md    ← 本文
  Scripts/
    ShellFurGpuSkinRenderer.cs            ← 主组件
    ShellFurBoneBuffer.cs                 ← 矩阵收集与上传
    ShellFurBindMeshBuilder.cs            ← 编辑器/运行时提取 fur mesh
  Shaders/
    ShellFurGpuSkinned.shader
    ShellFurGpuSkinning.hlsl
  Editor/
    ShellFurBuildGpuSkinMesh.cs           ← 菜单工具
```

与现有 `ShellFurRenderer`（BakeMesh 路径）**并存**：

- `ShellFurRenderer`：静态 / 快速预览 / Fallback  
- `ShellFurGpuSkinRenderer`：角色生产路径  

---

## 14. 一句话总结

**方案 B 的完善形态 = 绑定姿势毛发网格（含 bone weight + 预计算平滑法线）+ 每帧上传 skin 矩阵 buffer + VS 线性混合蒙皮 + instance 层挤出 + 身体仍走 SMR；优化形态再拆成 Compute 蒙皮一次、绘制多层只挤出。**

---

## 15. 实现状态（已落地第二档）

代码已实现 **Compute skin-once + Instanced shell extrude**：

| 文件 | 作用 |
|------|------|
| `Scripts/GpuSkin/ShellFurGpuSkinRenderer.cs` | 主组件 |
| `Scripts/GpuSkin/ShellFurBoneBuffer.cs` | 骨骼矩阵上传 |
| `Scripts/GpuSkin/ShellFurBindMeshBuilder.cs` | 提取 fur mesh + 平滑法线 + 权重 |
| `Scripts/GpuSkin/ShellFurGpuSkinTypes.cs` | CPU/GPU 结构体 |
| `Shaders/ShellFurGpuSkin.compute` | CSSkin 内核 |
| `Shaders/ShellFurGpuSkinned.shader` + `ShellFurGpuSkinning.hlsl` | 读 skinned buffer 挤出画壳 |
| `Editor/ShellFurBuildGpuSkinMesh.cs` | 菜单生成 bind mesh |

### 使用步骤

1. FBX 打开 **Read/Write Enabled**（或用菜单生成 mesh 时自动询问）。
2. 选中角色 → **Tools → Shell Fur → Build GPU Skin Fur Mesh From Selection**（可选）。
3. 在角色上添加 **`ShellFurGpuSkinRenderer`**（与 SMR 同物体或指定 Source）。
4. 指定 **Fur Material** = `Custom/ShellFurGpuSkinned` 材质。
5. 配置 **Fur Material Slots**、Hide Base、Shell Count 等。
6. 将 `ShellFurGpuSkin.compute` 拖到组件 **Skin Compute**（也可自动从 Assets 路径加载）。
7. Play：Animator 驱动骨骼 → Compute 蒙皮 → 多层 shell。

`ShellFurRenderer`（BakeMesh 路径）仍保留作静态/回退。
