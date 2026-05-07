using Hexprite.Core;
using Hexprite.Services;
using Hexprite.ViewModels;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Windows.Media;

namespace Hexprite.Rendering
{
    public static class DisplaySimulationRenderer
    {
        private static readonly ConcurrentDictionary<(int Radius, int SigmaMilli), float[]> GaussianKernelCache = new();
        private static float SrgbToLinear(float c)
            => c <= 0.04045f ? (c / 12.92f) : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

        private static float LinearToSrgb(float c)
            => c <= 0.0031308f ? (12.92f * c) : (1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f);

        private static uint PackBgra(byte a, byte r, byte g, byte b)
            => (uint)((a << 24) | (r << 16) | (g << 8) | b);

        private static float Smoothstep(float edge0, float edge1, float x)
        {
            float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
            return t * t * (3f - 2f * t);
        }

        private static float Hash01(int x, int y)
        {
            // Deterministic hash -> [0,1)
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263);
                h = (h ^ (h >> 13)) * 1274126177;
                h ^= (h >> 16);
                return (h & 0x00FFFFFF) / 16777216f;
            }
        }

        public static void Render(
            SpriteState state,
            ISelectionService selectionService,
            int outW,
            int outH,
            Color bgSrgb,
            Color fgSrgb,
            DisplaySimulationPreset preset,
            PreviewQuality quality,
            double strength01,
            double effectiveScale,
            bool isScaleCapped,
            uint[] outBgra)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (selectionService == null) throw new ArgumentNullException(nameof(selectionService));
            if (outBgra == null) throw new ArgumentNullException(nameof(outBgra));
            if (outW <= 0 || outH <= 0) return;
            if (outBgra.Length < outW * outH) return;

            int srcW = state.Width;
            int srcH = state.Height;
            if (srcW <= 0 || srcH <= 0) return;

            float strength = (float)Math.Clamp(strength01, 0.0, 1.0);

            // Preset parameters (tuned for "looks more like hardware", not physically perfect).
            float fill = preset switch
            {
                DisplaySimulationPreset.EPaper => 0.92f,
                DisplaySimulationPreset.Ssd1306OledBlue or DisplaySimulationPreset.Ssd1306OledGreen => 0.78f,
                _ => 0.90f
            };

            float edgeSoftnessPx = preset switch
            {
                DisplaySimulationPreset.Ssd1306OledBlue or DisplaySimulationPreset.Ssd1306OledGreen => 0.22f,
                DisplaySimulationPreset.EPaper => 0.15f,
                _ => 0.18f
            };

            float blurSigma = quality switch
            {
                PreviewQuality.Fast => 0f,
                PreviewQuality.High => 0.85f,
                _ => 0.55f
            };

            float bloomSigma = (quality == PreviewQuality.High) ? 1.6f : 0f;

            float blurStrength = preset switch
            {
                DisplaySimulationPreset.Ssd1306OledBlue or DisplaySimulationPreset.Ssd1306OledGreen => 0.60f,
                DisplaySimulationPreset.EPaper => 0.25f,
                _ => 0.35f
            } * strength;

            float bloomStrength = preset switch
            {
                DisplaySimulationPreset.Ssd1306OledBlue or DisplaySimulationPreset.Ssd1306OledGreen => 0.35f,
                _ => 0.0f
            } * strength;

            // Colors in linear
            var bgLin = (
                r: SrgbToLinear(bgSrgb.R / 255f),
                g: SrgbToLinear(bgSrgb.G / 255f),
                b: SrgbToLinear(bgSrgb.B / 255f));
            var fgLin = (
                r: SrgbToLinear(fgSrgb.R / 255f),
                g: SrgbToLinear(fgSrgb.G / 255f),
                b: SrgbToLinear(fgSrgb.B / 255f));

            float cellW = outW / (float)srcW;
            float cellH = outH / (float)srcH;
            float invCellW = 1f / cellW;
            float invCellH = 1f / cellH;

            // Native-ish scale (around 1x) should be maximally stable and readable.
            // Avoid optical effects that can look broken at this footprint.
            if (effectiveScale <= 1.10 || (cellW <= 1.10f && cellH <= 1.10f))
            {
                RenderNativeScale(
                    state,
                    selectionService,
                    outW,
                    outH,
                    bgSrgb,
                    fgSrgb,
                    preset,
                    strength,
                    outBgra);
                return;
            }

            float cellMin = MathF.Min(cellW, cellH);
            // Progressive detail regime: 0 -> 1 as pixel footprint grows
            float detailRegime = Math.Clamp((cellMin - 1.15f) / 2.35f, 0f, 1f);
            if (isScaleCapped)
                detailRegime *= 0.92f;
            float effectiveScaleFactor = Math.Clamp((float)(effectiveScale / 2.0), 0.75f, 1.15f);

            // Keep deterministic but gently reduce fragile details near cap/small cell sizes.
            float stabilityBias = Math.Clamp((0.8f + (0.2f * detailRegime)) * effectiveScaleFactor, 0.75f, 1f);
            blurStrength *= Math.Clamp(0.70f + (0.30f * detailRegime), 0.70f, 1f);
            bloomStrength *= Math.Clamp(0.35f + (0.65f * detailRegime), 0.35f, 1f) * stabilityBias;

            // When output pixels are near/below source-pixel size, the aperture model
            // can produce unstable artifacts during scale changes. In that range, use a
            // stable nearest-sample simulation with only subtle preset texture/noise.
            if (cellW < 1.15f || cellH < 1.15f)
            {
                RenderLowScaleFallback(
                    state,
                    selectionService,
                    outW,
                    outH,
                    bgSrgb,
                    fgSrgb,
                    preset,
                    strength,
                    detailRegime,
                    outBgra);
                return;
            }

            var pool = ArrayPool<float>.Shared;
            int pxCount = outW * outH;

            // Base intensity (aperture only)
            float[] intensity = pool.Rent(pxCount);
            Array.Clear(intensity, 0, pxCount);

            bool hasFloating = selectionService.IsFloating && selectionService.FloatingPixels != null;

            for (int oy = 0; oy < outH; oy++)
            {
                float sy = (oy + 0.5f) * invCellH - 0.5f;
                int y = (int)MathF.Floor(sy + 0.5f);
                y = Math.Clamp(y, 0, srcH - 1);

                float v = (oy + 0.5f) * invCellH - y; // ~[0,1]
                float dv = MathF.Abs(v - 0.5f);

                for (int ox = 0; ox < outW; ox++)
                {
                    float sx = (ox + 0.5f) * invCellW - 0.5f;
                    int x = (int)MathF.Floor(sx + 0.5f);
                    x = Math.Clamp(x, 0, srcW - 1);

                    float u = (ox + 0.5f) * invCellW - x; // ~[0,1]
                    float du = MathF.Abs(u - 0.5f);

                    int si = (y * srcW) + x;
                    bool on = false;
                    foreach (var layer in state.Layers)
                    {
                        if (!layer.IsVisible) continue;
                        if (layer.Pixels[si]) { on = true; break; }
                    }

                    if (hasFloating)
                    {
                        int fx = x - selectionService.FloatingX;
                        int fy = y - selectionService.FloatingY;
                        if (fx >= 0 && fx < selectionService.FloatingWidth &&
                            fy >= 0 && fy < selectionService.FloatingHeight &&
                            selectionService.FloatingPixels![fx, fy])
                        {
                            on = true;
                        }
                    }

                    if (!on)
                    {
                        intensity[(oy * outW) + ox] = 0f;
                        continue;
                    }

                    // Rounded-rect-ish aperture with soft edges
                    float half = 0.5f * fill;
                    float edge = MathF.Max(0.0001f, edgeSoftnessPx / MathF.Min(cellW, cellH));

                    float ax = Smoothstep(half, half - edge, du);
                    float ay = Smoothstep(half, half - edge, dv);
                    intensity[(oy * outW) + ox] = ax * ay;
                }
            }

            float[]? blurred = null;
            float[]? bloom = null;
            if (blurSigma > 0.0001f && blurStrength > 0.0001f)
                blurred = BlurSeparable(intensity, outW, outH, blurSigma, pool);
            if (bloomSigma > 0.0001f && bloomStrength > 0.0001f)
                bloom = BlurSeparable(intensity, outW, outH, bloomSigma, pool);

            for (int oy = 0; oy < outH; oy++)
            {
                for (int ox = 0; ox < outW; ox++)
                {
                    int i = (oy * outW) + ox;
                    float a = intensity[i];
                    float d = blurred != null ? blurred[i] : 0f;
                    float b = bloom != null ? bloom[i] : 0f;

                    // Combine: base aperture + diffusion + bloom
                    float lit = Math.Clamp(a + blurStrength * d, 0f, 1.35f);
                    float bloomLit = Math.Clamp(bloomStrength * b, 0f, 1.0f);

                    // Optional: simple RGB stripe mask for High quality when pixels are large enough
                    float mr = 1f, mg = 1f, mb = 1f;
                    if (quality == PreviewQuality.High && cellMin >= 3.0f && detailRegime > 0.75f && preset != DisplaySimulationPreset.EPaper)
                    {
                        int stripe = ox % 3;
                        if (stripe == 0) { mg = 0.9f; mb = 0.85f; }
                        else if (stripe == 1) { mr = 0.85f; mb = 0.9f; }
                        else { mr = 0.9f; mg = 0.85f; }
                    }

                    // Texture/noise (mostly for e-paper, subtle elsewhere)
                    float noise = Hash01(ox, oy) - 0.5f;
                    float paperNoise = preset == DisplaySimulationPreset.EPaper
                        ? (0.08f * strength * Math.Clamp(0.55f + (0.45f * detailRegime), 0.55f, 1f))
                        : (0.015f * strength * Math.Clamp(0.45f + (0.55f * detailRegime), 0.45f, 1f));

                    float bgN = paperNoise * noise;
                    float fgN = paperNoise * noise;

                    float r = bgLin.r + bgN;
                    float g = bgLin.g + bgN;
                    float bl = bgLin.b + bgN;

                    // Reduce contrast a bit for e-paper
                    float contrast = preset == DisplaySimulationPreset.EPaper ? (0.86f + 0.08f * (1f - strength)) : 1f;

                    float fr = fgLin.r * mr + fgN;
                    float fg = fgLin.g * mg + fgN;
                    float fb = fgLin.b * mb + fgN;

                    r = r + contrast * lit * (fr - r) + bloomLit * fr;
                    g = g + contrast * lit * (fg - g) + bloomLit * fg;
                    bl = bl + contrast * lit * (fb - bl) + bloomLit * fb;

                    r = Math.Clamp(r, 0f, 1f);
                    g = Math.Clamp(g, 0f, 1f);
                    bl = Math.Clamp(bl, 0f, 1f);

                    byte R = (byte)Math.Clamp((int)MathF.Round(LinearToSrgb(r) * 255f), 0, 255);
                    byte G = (byte)Math.Clamp((int)MathF.Round(LinearToSrgb(g) * 255f), 0, 255);
                    byte B = (byte)Math.Clamp((int)MathF.Round(LinearToSrgb(bl) * 255f), 0, 255);

                    outBgra[i] = PackBgra(255, R, G, B);
                }
            }

            pool.Return(intensity);
            if (blurred != null) pool.Return(blurred);
            if (bloom != null) pool.Return(bloom);
        }

        private static void RenderNativeScale(
            SpriteState state,
            ISelectionService selectionService,
            int outW,
            int outH,
            Color bgSrgb,
            Color fgSrgb,
            DisplaySimulationPreset preset,
            float strength,
            uint[] outBgra)
        {
            int srcW = state.Width;
            int srcH = state.Height;
            float invScaleX = srcW / (float)outW;
            float invScaleY = srcH / (float)outH;

            bool hasFloating = selectionService.IsFloating && selectionService.FloatingPixels != null;
            // Keep only a tiny touch of texture at native scale.
            float noiseAmp = preset == DisplaySimulationPreset.EPaper ? 0.018f * strength : 0.0f;
            float contrast = preset == DisplaySimulationPreset.EPaper ? 0.92f : 1f;

            var bgLin = (
                r: SrgbToLinear(bgSrgb.R / 255f),
                g: SrgbToLinear(bgSrgb.G / 255f),
                b: SrgbToLinear(bgSrgb.B / 255f));
            var fgLin = (
                r: SrgbToLinear(fgSrgb.R / 255f),
                g: SrgbToLinear(fgSrgb.G / 255f),
                b: SrgbToLinear(fgSrgb.B / 255f));

            float microBloomStrength = preset switch
            {
                DisplaySimulationPreset.Ssd1306OledBlue or DisplaySimulationPreset.Ssd1306OledGreen => 0.18f * strength,
                DisplaySimulationPreset.GenericLcd => 0.05f * strength,
                _ => 0.0f
            };

            bool IsOnAt(int x, int y)
            {
                if (x < 0 || x >= srcW || y < 0 || y >= srcH) return false;
                int i = (y * srcW) + x;
                foreach (var layer in state.Layers)
                {
                    if (!layer.IsVisible) continue;
                    if (layer.Pixels[i]) return true;
                }
                if (hasFloating)
                {
                    int fx = x - selectionService.FloatingX;
                    int fy = y - selectionService.FloatingY;
                    if (fx >= 0 && fx < selectionService.FloatingWidth &&
                        fy >= 0 && fy < selectionService.FloatingHeight &&
                        selectionService.FloatingPixels![fx, fy])
                    {
                        return true;
                    }
                }
                return false;
            }

            for (int oy = 0; oy < outH; oy++)
            {
                int sy = Math.Clamp((int)MathF.Round((oy + 0.5f) * invScaleY - 0.5f), 0, srcH - 1);
                for (int ox = 0; ox < outW; ox++)
                {
                    int sx = Math.Clamp((int)MathF.Round((ox + 0.5f) * invScaleX - 0.5f), 0, srcW - 1);
                    bool on = IsOnAt(sx, sy);

                    float n = (Hash01(ox, oy) - 0.5f) * noiseAmp;
                    float r = bgLin.r + n;
                    float g = bgLin.g + n;
                    float b = bgLin.b + n;
                    if (on)
                    {
                        r = r + contrast * (fgLin.r - r);
                        g = g + contrast * (fgLin.g - g);
                        b = b + contrast * (fgLin.b - b);
                        // Slightly lift lit pixels at native scale so the glow reads.
                        float coreLift = microBloomStrength * 0.22f;
                        r += coreLift * fgLin.r;
                        g += coreLift * fgLin.g;
                        b += coreLift * fgLin.b;
                    }

                    if (!on && microBloomStrength > 0f)
                    {
                        int ring1 = 0;
                        int ring2 = 0;
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dy == 0) continue;
                                if (IsOnAt(sx + dx, sy + dy)) ring1++;
                            }
                        }
                        for (int dy = -2; dy <= 2; dy++)
                        {
                            for (int dx = -2; dx <= 2; dx++)
                            {
                                if (Math.Abs(dx) <= 1 && Math.Abs(dy) <= 1) continue;
                                if (dx == 0 && dy == 0) continue;
                                if (IsOnAt(sx + dx, sy + dy)) ring2++;
                            }
                        }
                        if (ring1 > 0 || ring2 > 0)
                        {
                            float halo = microBloomStrength * ((ring1 / 8f) * 0.85f + (ring2 / 16f) * 0.30f);
                            r += halo * fgLin.r;
                            g += halo * fgLin.g;
                            b += halo * fgLin.b;
                        }
                    }

                    r = Math.Clamp(r, 0f, 1f);
                    g = Math.Clamp(g, 0f, 1f);
                    b = Math.Clamp(b, 0f, 1f);

                    byte R = (byte)Math.Clamp((int)MathF.Round(LinearToSrgb(r) * 255f), 0, 255);
                    byte G = (byte)Math.Clamp((int)MathF.Round(LinearToSrgb(g) * 255f), 0, 255);
                    byte B = (byte)Math.Clamp((int)MathF.Round(LinearToSrgb(b) * 255f), 0, 255);
                    outBgra[(oy * outW) + ox] = PackBgra(255, R, G, B);
                }
            }
        }

        private static void RenderLowScaleFallback(
            SpriteState state,
            ISelectionService selectionService,
            int outW,
            int outH,
            Color bgSrgb,
            Color fgSrgb,
            DisplaySimulationPreset preset,
            float strength,
            float detailRegime,
            uint[] outBgra)
        {
            int srcW = state.Width;
            int srcH = state.Height;
            float invScaleX = srcW / (float)outW;
            float invScaleY = srcH / (float)outH;

            bool hasFloating = selectionService.IsFloating && selectionService.FloatingPixels != null;
            float noiseAmp = preset == DisplaySimulationPreset.EPaper
                ? 0.06f * strength * Math.Clamp(0.50f + (0.50f * detailRegime), 0.50f, 1f)
                : 0.01f * strength * Math.Clamp(0.35f + (0.65f * detailRegime), 0.35f, 1f);
            float contrast = preset == DisplaySimulationPreset.EPaper ? (0.88f + 0.08f * (1f - strength)) : 1f;

            var bgLin = (
                r: SrgbToLinear(bgSrgb.R / 255f),
                g: SrgbToLinear(bgSrgb.G / 255f),
                b: SrgbToLinear(bgSrgb.B / 255f));
            var fgLin = (
                r: SrgbToLinear(fgSrgb.R / 255f),
                g: SrgbToLinear(fgSrgb.G / 255f),
                b: SrgbToLinear(fgSrgb.B / 255f));

            for (int oy = 0; oy < outH; oy++)
            {
                int sy = Math.Clamp((int)MathF.Round((oy + 0.5f) * invScaleY - 0.5f), 0, srcH - 1);
                for (int ox = 0; ox < outW; ox++)
                {
                    int sx = Math.Clamp((int)MathF.Round((ox + 0.5f) * invScaleX - 0.5f), 0, srcW - 1);
                    int si = (sy * srcW) + sx;

                    bool on = false;
                    foreach (var layer in state.Layers)
                    {
                        if (!layer.IsVisible) continue;
                        if (layer.Pixels[si]) { on = true; break; }
                    }
                    if (hasFloating)
                    {
                        int fx = sx - selectionService.FloatingX;
                        int fy = sy - selectionService.FloatingY;
                        if (fx >= 0 && fx < selectionService.FloatingWidth &&
                            fy >= 0 && fy < selectionService.FloatingHeight &&
                            selectionService.FloatingPixels![fx, fy])
                        {
                            on = true;
                        }
                    }

                    float n = (Hash01(ox, oy) - 0.5f) * noiseAmp;
                    float r = bgLin.r + n;
                    float g = bgLin.g + n;
                    float b = bgLin.b + n;
                    if (on)
                    {
                        r = r + contrast * (fgLin.r - r);
                        g = g + contrast * (fgLin.g - g);
                        b = b + contrast * (fgLin.b - b);
                    }

                    r = Math.Clamp(r, 0f, 1f);
                    g = Math.Clamp(g, 0f, 1f);
                    b = Math.Clamp(b, 0f, 1f);

                    byte R = (byte)Math.Clamp((int)MathF.Round(LinearToSrgb(r) * 255f), 0, 255);
                    byte G = (byte)Math.Clamp((int)MathF.Round(LinearToSrgb(g) * 255f), 0, 255);
                    byte B = (byte)Math.Clamp((int)MathF.Round(LinearToSrgb(b) * 255f), 0, 255);
                    outBgra[(oy * outW) + ox] = PackBgra(255, R, G, B);
                }
            }
        }

        private static float[] BlurSeparable(float[] src, int w, int h, float sigma, ArrayPool<float> pool)
        {
            int radius = Math.Clamp((int)MathF.Ceiling(sigma * 2.5f), 1, 8);
            float[] kernel = BuildGaussianKernel(radius, sigma);

            int n = w * h;
            float[] tmp = pool.Rent(n);
            float[] dst = pool.Rent(n);

            // Horizontal
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    float acc = 0f;
                    for (int k = -radius; k <= radius; k++)
                    {
                        int xx = Math.Clamp(x + k, 0, w - 1);
                        acc += src[row + xx] * kernel[k + radius];
                    }
                    tmp[row + x] = acc;
                }
            }

            // Vertical
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    float acc = 0f;
                    for (int k = -radius; k <= radius; k++)
                    {
                        int yy = Math.Clamp(y + k, 0, h - 1);
                        acc += tmp[(yy * w) + x] * kernel[k + radius];
                    }
                    dst[row + x] = acc;
                }
            }

            pool.Return(tmp);
            return dst;
        }

        private static float[] BuildGaussianKernel(int radius, float sigma)
        {
            int sigmaMilli = Math.Max(1, (int)MathF.Round(sigma * 1000f));
            if (GaussianKernelCache.TryGetValue((radius, sigmaMilli), out var cached))
                return cached;

            float[] k = new float[(radius * 2) + 1];
            float inv2s2 = 1f / (2f * sigma * sigma);
            float sum = 0f;
            for (int i = -radius; i <= radius; i++)
            {
                float v = MathF.Exp(-(i * i) * inv2s2);
                k[i + radius] = v;
                sum += v;
            }
            if (sum > 0f)
            {
                for (int i = 0; i < k.Length; i++)
                    k[i] /= sum;
            }

            GaussianKernelCache.TryAdd((radius, sigmaMilli), k);
            return k;
        }
    }
}

