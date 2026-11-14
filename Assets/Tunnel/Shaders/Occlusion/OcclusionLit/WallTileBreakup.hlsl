#ifndef TUNNEL_WALL_TILE_BREAKUP_INCLUDED
#define TUNNEL_WALL_TILE_BREAKUP_INCLUDED

// Breaks up the repeat of a tiled wall texture such as stucco, so the eye can't pick out the same patch over and over.
// The wall is covered with small hexagons; each shows a randomly rotated and shifted piece of the image, and
// neighbouring hexagons blend softly where they meet (hex-tiling after Mikkelsen, "Practical Real-Time Hex-Tiling",
// JCGT 2022). It costs three texture reads instead of one, and only where _WALL_TILE_BREAKUP is on.

static const float k_HexScale = 3.46410162; // 2 * sqrt(3): a hexagon is a bit under a third of one image across

float2 WallHexHash(float2 p)
{
    float2 r = mul(float2x2(127.1, 311.7, 269.5, 183.3), p);
    return frac(sin(r) * 43758.5453);
}

// Barycentric weights of p in a triangle grid, and the three grid vertices (hexagon centres) around it.
void WallTriangleGrid(float2 p, out float3 w, out float2 v1, out float2 v2, out float2 v3)
{
    float2 skewed = mul(float2x2(1.0, -0.57735027, 0.0, 1.15470054), p * k_HexScale);
    float2 baseId = floor(skewed);
    float3 t = float3(frac(skewed), 0.0);
    t.z = 1.0 - t.x - t.y;
    float s = step(0.0, -t.z);
    float s2 = 2.0 * s - 1.0;
    w = float3(-t.z * s2, s - t.y * s2, s - t.x * s2);
    v1 = baseId + float2(s, s);
    v2 = baseId + float2(s, 1.0 - s);
    v3 = baseId + float2(1.0 - s, s);
}

// One hexagon's piece of the image, rotated by a random angle about the hexagon's centre and shifted randomly.
// p, dpdx and dpdy are in square space (st with the image's aspect taken out), so rotating never shears the image.
half4 WallHexSample(TEXTURE2D_PARAM(tex, samp), float2 p, float2 dpdx, float2 dpdy, float2 v, float2 toSquare)
{
    float2 rnd = WallHexHash(v);
    float angle = rnd.x * 6.28318531;
    float c = cos(angle);
    float s = sin(angle);
    float2x2 rot = float2x2(c, -s, s, c);
    float2 centre = mul(float2x2(1.0, 0.5, 0.0, 1.0 / 1.15470054), v) / k_HexScale;
    float2 q = mul(rot, p - centre) + centre + rnd;
    return SAMPLE_TEXTURE2D_GRAD(tex, samp, q / toSquare, mul(rot, dpdx) / toSquare, mul(rot, dpdy) / toSquare);
}

// The texture at st (already tiled by _ST) with its repeat broken up. aspect = image width / height.
half4 SampleWallTileBreakup(TEXTURE2D_PARAM(tex, samp), float2 st, float aspect)
{
    float2 toSquare = float2(aspect, 1.0);
    float2 p = st * toSquare;
    float2 dpdx = ddx(p);
    float2 dpdy = ddy(p);
    float3 w;
    float2 v1, v2, v3;
    WallTriangleGrid(p, w, v1, v2, v3);
    half4 c1 = WallHexSample(TEXTURE2D_ARGS(tex, samp), p, dpdx, dpdy, v1, toSquare);
    half4 c2 = WallHexSample(TEXTURE2D_ARGS(tex, samp), p, dpdx, dpdy, v2, toSquare);
    half4 c3 = WallHexSample(TEXTURE2D_ARGS(tex, samp), p, dpdx, dpdy, v3, toSquare);

    // Sharpen the blend so most of each hexagon is one clean piece, favouring the brighter piece at the seams.
    const half3 luma = half3(0.299, 0.587, 0.114);
    float3 d = float3(dot(c1.rgb, luma), dot(c2.rgb, luma), dot(c3.rgb, luma));
    d = lerp(1.0, d, 0.6);
    float3 weight = d * pow(saturate(w), 7.0);
    weight /= max(weight.x + weight.y + weight.z, 1e-5);
    return half4(weight.x * c1 + weight.y * c2 + weight.z * c3);
}

#endif
