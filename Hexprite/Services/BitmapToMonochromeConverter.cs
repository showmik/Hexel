using System;
using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Media;

namespace Hexprite.Services
{
    /// <summary>
    /// Converts an arbitrary bitmap image into a 1-bit (on/off) pixel grid using
    /// nearest-neighbor scaling and Floyd–Steinberg dithering.
    /// </summary>
    public static class BitmapToMonochromeConverter
    {
        /// <summary>
        /// Converts the image at <paramref name="path"/> into monochrome pixels.
        /// Dark pixels map to "on" (true). Transparent pixels map to "off" (false).
        /// </summary>
        /// <returns>
        /// (pixels, width, height, wasScaled)
        /// </returns>
        public static (bool[] Pixels, int Width, int Height, bool WasScaled) ConvertTo1Bit(
            string path,
            int maxDimension)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path cannot be empty.", nameof(path));

            if (!File.Exists(path))
                throw new FileNotFoundException("Image file not found.", path);

            if (maxDimension <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxDimension), "maxDimension must be > 0");

            // Load and normalize to BGRA32 for consistent CopyPixels reads.
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.EndInit();
            bitmap.Freeze();

            var bgra32 = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            bgra32.Freeze();

            int srcW = bgra32.PixelWidth;
            int srcH = bgra32.PixelHeight;
            if (srcW <= 0 || srcH <= 0)
                throw new InvalidOperationException($"Invalid image dimensions: {srcW}x{srcH}");

            // Scale down only when needed to respect the editor max dimension.
            int dstW = srcW;
            int dstH = srcH;
            bool wasScaled = false;

            int srcMax = Math.Max(srcW, srcH);
            if (srcMax > maxDimension)
            {
                double scale = maxDimension / (double)srcMax;
                dstW = Math.Max(1, (int)Math.Round(srcW * scale));
                dstH = Math.Max(1, (int)Math.Round(srcH * scale));
                wasScaled = dstW != srcW || dstH != srcH;
            }

            int srcStride = srcW * 4;
            byte[] srcPixels = new byte[srcStride * srcH];
            bgra32.CopyPixels(srcPixels, srcStride, 0);

            // Build a grayscale float buffer at the target resolution.
            float[] gray = new float[dstW * dstH];
            for (int y = 0; y < dstH; y++)
            {
                // Nearest-neighbor sample position in source pixel space.
                int sy = (int)Math.Round((y + 0.5) * srcH / (double)dstH - 0.5);
                sy = Math.Clamp(sy, 0, srcH - 1);

                int dstRow = y * dstW;
                int srcRow = sy * srcStride;

                for (int x = 0; x < dstW; x++)
                {
                    int sx = (int)Math.Round((x + 0.5) * srcW / (double)dstW - 0.5);
                    sx = Math.Clamp(sx, 0, srcW - 1);

                    int srcIdx = srcRow + (sx * 4);
                    byte b = srcPixels[srcIdx + 0];
                    byte g = srcPixels[srcIdx + 1];
                    byte r = srcPixels[srcIdx + 2];
                    byte a = srcPixels[srcIdx + 3];

                    // Transparent pixels treated as white/off.
                    if (a < 128)
                    {
                        gray[dstRow + x] = 255f;
                        continue;
                    }

                    // Standard luma formula.
                    gray[dstRow + x] = (0.299f * r) + (0.587f * g) + (0.114f * b);
                }
            }

            // Floyd–Steinberg dithering to {0,255} then map to bools.
            bool[] pixels = new bool[dstW * dstH];

            for (int y = 0; y < dstH; y++)
            {
                int row = y * dstW;
                for (int x = 0; x < dstW; x++)
                {
                    int i = row + x;
                    float oldVal = Math.Clamp(gray[i], 0f, 255f);
                    float newVal = oldVal < 128f ? 0f : 255f;
                    float err = oldVal - newVal;

                    gray[i] = newVal;
                    pixels[i] = newVal == 0f; // dark -> on

                    // Distribute quantization error.
                    if (x + 1 < dstW)
                        gray[i + 1] = Math.Clamp(gray[i + 1] + err * (7f / 16f), 0f, 255f);

                    if (y + 1 < dstH)
                    {
                        int nextRow = (y + 1) * dstW;

                        if (x > 0)
                            gray[nextRow + (x - 1)] = Math.Clamp(gray[nextRow + (x - 1)] + err * (3f / 16f), 0f, 255f);

                        gray[nextRow + x] = Math.Clamp(gray[nextRow + x] + err * (5f / 16f), 0f, 255f);

                        if (x + 1 < dstW)
                            gray[nextRow + (x + 1)] = Math.Clamp(gray[nextRow + (x + 1)] + err * (1f / 16f), 0f, 255f);
                    }
                }
            }

            return (pixels, dstW, dstH, wasScaled);
        }
    }
}

