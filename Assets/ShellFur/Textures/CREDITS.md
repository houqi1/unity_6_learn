# Fur density textures — sources

## Recommended (default): `FurDensity_Shell.png`

- **Type**: Poisson-disk follicle density (black background, soft white dots)
- **Why**: Evenly spaced strand roots with natural length variation — the classic shell-fur density layout used in real-time fur papers/tutorials
- **Generation**: Bridson Poisson-disk sampling (toroidal / seamless), each point a soft disc with random brightness = strand height
- **License**: Generated for this project (no third-party copyright)

Also available as `FurDensity_Poisson.png` (same content).

## From the web

### ambientCG — Paper001 (CC0)

- **URL**: https://ambientcg.com/view?id=Paper001  
- **License**: [CC0](https://creativecommons.org/publicdomain/zero/1.0/)  
- **Use**: Fine fibrous micro-detail  
- **Processed as**: `FurDensity_AmbientCG_Paper.png` (high-pass + peak threshold → density dots)

### Motion Forge Pictures — Fur Noise Map (free)

- **URL**: https://www.motionforgepictures.com/product/fur-noise-map-texture/  
- **File kept**: `FurNoiseMap_MFP.png`  
- **Note**: Preview-style flowing fur shading map (sphere-looking), **not ideal** as a UV shell density atlas. Kept for reference / flow shading experiments only.

### OpenGameArt — 700+ Noise Textures (CC0)

- **URL**: https://opengameart.org/content/700-noise-textures  
- **Author**: Screaming Brain Studios  
- **License**: CC0  
- Download was incomplete in this session; pack is recommended if you want more grayscale noise variants.

## How to use in Shell Fur

1. Material: **uncheck** `Use Procedural Strands`
2. Assign texture to **Fur Density Map (R)**
3. Raise **Tiling** on `_FurMap` (default material uses 4×4) for denser strands
4. Tune **Base Alpha Cutoff** and **Length Randomness**

Brightness of each dot ≈ max shell height of that strand.
