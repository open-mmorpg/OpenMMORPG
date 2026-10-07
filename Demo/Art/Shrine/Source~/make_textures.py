"""Repacks the glb's textures for URP's Lit shader.

    python extract_textures.py && python make_textures.py

glTF packs occlusion-roughness-metallic into one texture's R-G-B. URP wants metallic in R
and smoothness - the complement of roughness - in A. See the comments below for the two
channels that are not a straight copy.
"""

from PIL import Image
import numpy as np

# --- base colour -------------------------------------------------------------
# The atlas is already dilated: the mottle between the islands is bleed fill, not
# background, so the alpha channel marks "outside an island" and carries no
# transparency the material wants. glTF declares the material OPAQUE, so alpha is
# ignored there too; dropped here so nothing downstream reads it as cutout or as a
# smoothness source.
bc = Image.open('basecolor.png').convert('RGB')
bc.save('T_Shrine_BaseColor.png', optimize=True)
print("BaseColor", bc.size, bc.mode)

# --- mask --------------------------------------------------------------------
# glTF packs occlusion-roughness-metallic into R-G-B of one texture; URP's Lit
# wants metallic in R and *smoothness* in A, which is the complement of roughness.
# The occlusion channel here is all zeros - the generator wrote no AO - so G is
# filled white rather than copied: a zeroed G wired into _OcclusionMap would black
# the whole shrine out.
mr = np.asarray(Image.open('metallicroughness.png').convert('RGB'))
h, w, _ = mr.shape
mask = np.zeros((h, w, 4), np.uint8)
mask[..., 0] = mr[..., 2]            # metallic  <- glTF B
mask[..., 1] = 255                   # occlusion <- none authored
mask[..., 2] = 0
mask[..., 3] = 255 - mr[..., 1]      # smoothness <- 1 - roughness
Image.fromarray(mask, 'RGBA').save('T_Shrine_Mask.png', optimize=True)
print("Mask", mask.shape,
      "metallic mean", round(float(mask[...,0].mean()),2),
      "smoothness mean", round(float(mask[...,3].mean()),2),
      "smoothness p5/p95", round(float(np.percentile(mask[...,3],5)),1), round(float(np.percentile(mask[...,3],95)),1))
import os
for f in ('T_Shrine_BaseColor.png','T_Shrine_Mask.png'):
    print(f, os.path.getsize(f)//1024, "KB")
