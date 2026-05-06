using System;
using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Media;

namespace Hexprite.Services
{
    public enum BitmapDitheringAlgorithm
    {
        FloydSteinberg = 0,
        Binary = 1,
        Bayer = 2,
        Atkinson = 3
    }

    public sealed class BitmapImportSettings
    {
        public int MaxDimension { get; set; } = 128;
        public int Threshold { get; set; } = 128;
        public int AlphaThreshold { get; set; } = 128;
        public bool Invert { get; set; }
        public BitmapDitheringAlgorithm DitheringAlgorithm { get; set; } = BitmapDitheringAlgorithm.FloydSteinberg;
    }

    /// <summary>
    /// Converts an arbitrary bitmap image into a 1-bit (on/off) pixel grid using
    /// nearest-neighbor scaling and configurable dithering.
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
            return ConvertTo1Bit(path, new BitmapImportSettings
            {
                MaxDimension = maxDimension
            });
        }

        public static (bool[] Pixels, int Width, int Height, bool WasScaled) ConvertTo1Bit(
            string path,
            BitmapImportSettings settings)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path cannot be empty.", nameof(path));

            if (!File.Exists(path))
                throw new FileNotFoundException("Image file not found.", path);

            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            if (settings.MaxDimension <= 0)
                throw new ArgumentOutOfRangeException(nameof(settings.MaxDimension), "MaxDimension must be > 0");

            // Robustness: downscale DURING decode so we never allocate gigantic source buffers.
            int origW, origH;
            using (var headerStream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var headerDecoder = BitmapDecoder.Create(
                    headerStream,
                    BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                    BitmapCacheOption.None);

                var headerFrame = headerDecoder.Frames[0];
                origW = headerFrame.PixelWidth;
                origH = headerFrame.PixelHeight;
            }

            if (origW <= 0 || origH <= 0)
                throw new InvalidOperationException($"Invalid image dimensions: {origW}x{origH}");

            int decodeW = origW;
            int decodeH = origH;
            int origMax = Math.Max(origW, origH);
            bool wasScaled = false;
            int threshold = Math.Clamp(settings.Threshold, 0, 255);
            int alphaThreshold = Math.Clamp(settings.AlphaThreshold, 0, 255);

            if (origMax > settings.MaxDimension)
            {
                double scale = settings.MaxDimension / (double)origMax;
                decodeW = Math.Max(1, (int)Math.Round(origW * scale));
                decodeH = Math.Max(1, (int)Math.Round(origH * scale));
                wasScaled = decodeW != origW || decodeH != origH;
            }

            BitmapSource decoded;
            using (var decodeStream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.StreamSource = decodeStream;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bitmap.DecodePixelWidth = decodeW;
                bitmap.DecodePixelHeight = decodeH;
                bitmap.EndInit();
                bitmap.Freeze();
                decoded = bitmap;
            }

            // Normalize to BGRA32 for consistent CopyPixels reads.
            var bgra32 = new FormatConvertedBitmap(decoded, PixelFormats.Bgra32, null, 0);
            bgra32.Freeze();

            int srcW = bgra32.PixelWidth;
            int srcH = bgra32.PixelHeight;
            if (srcW <= 0 || srcH <= 0)
                throw new InvalidOperationException($"Invalid image dimensions: {srcW}x{srcH}");

            // After decode downscaling (if any), keep the decoded resolution as-is.
            int dstW = srcW;
            int dstH = srcH;

            int srcStride = srcW * 4;
            byte[] srcPixels = new byte[srcStride * srcH];
            bgra32.CopyPixels(srcPixels, srcStride, 0);

            // Build a grayscale buffer. Because we already decode at the target size,
            // we can read pixels 1:1 without per-pixel sampling math.
            byte[] gray = new byte[dstW * dstH];
            for (int y = 0; y < dstH; y++)
            {
                int row = y * dstW;
                int srcRow = y * srcStride;

                for (int x = 0; x < dstW; x++)
                {
                    int srcIdx = srcRow + (x * 4);
                    byte b = srcPixels[srcIdx + 0];
                    byte g = srcPixels[srcIdx + 1];
                    byte r = srcPixels[srcIdx + 2];
                    byte a = srcPixels[srcIdx + 3];

                    // Transparent pixels treated as white/off.
                    if (a < alphaThreshold)
                    {
                        gray[row + x] = 255;
                        continue;
                    }

                    // Standard luma formula (rounded).
                    int luma = (int)(0.299 * r + 0.587 * g + 0.114 * b + 0.5);
                    if (settings.Invert)
                        luma = 255 - luma;
                    gray[row + x] = (byte)Math.Clamp(luma, 0, 255);
                }
            }

            bool[] pixels = new bool[dstW * dstH];
            switch (settings.DitheringAlgorithm)
            {
                case BitmapDitheringAlgorithm.Binary:
                    ApplyBinary(gray, pixels, dstW, dstH, threshold);
                    break;
                case BitmapDitheringAlgorithm.Bayer:
                    ApplyBayer(gray, pixels, dstW, dstH, threshold);
                    break;
                case BitmapDitheringAlgorithm.Atkinson:
                    ApplyAtkinson(gray, pixels, dstW, dstH, threshold);
                    break;
                default:
                    ApplyFloydSteinberg(gray, pixels, dstW, dstH, threshold);
                    break;
            }

            return (pixels, dstW, dstH, wasScaled);
        }

        private static bool IsPixelOn(int intensity)
        {
            // Keep existing import polarity for compatibility with rendering/export code.
            return intensity != 0;
        }

        private static void ApplyBinary(byte[] gray, bool[] pixels, int width, int height, int threshold)
        {
            for (int i = 0; i < gray.Length; i++)
            {
                int quantized = gray[i] < threshold ? 0 : 255;
                pixels[i] = IsPixelOn(quantized);
            }
        }

        private static void ApplyBayer(byte[] gray, bool[] pixels, int width, int height, int threshold)
        {
            int[,] matrix4x4 =
            {
                { 0, 8, 2, 10 },
                { 12, 4, 14, 6 },
                { 3, 11, 1, 9 },
                { 15, 7, 13, 5 }
            };

            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    int i = row + x;
                    int bayerOffset = ((matrix4x4[y & 3, x & 3] * 16) - 128) / 2;
                    int localThreshold = Math.Clamp(threshold + bayerOffset, 0, 255);
                    int quantized = gray[i] < localThreshold ? 0 : 255;
                    pixels[i] = IsPixelOn(quantized);
                }
            }
        }

        private static void ApplyAtkinson(byte[] gray, bool[] pixels, int width, int height, int threshold)
        {
            int[] work = new int[gray.Length];
            for (int i = 0; i < gray.Length; i++)
                work[i] = gray[i];

            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    int i = row + x;
                    int oldVal = Math.Clamp(work[i], 0, 255);
                    int newVal = oldVal < threshold ? 0 : 255;
                    pixels[i] = IsPixelOn(newVal);

                    int err = (oldVal - newVal) / 8;
                    if (err == 0)
                        continue;

                    if (x + 1 < width) work[i + 1] += err;
                    if (x + 2 < width) work[i + 2] += err;
                    if (y + 1 < height)
                    {
                        int row1 = i + width;
                        if (x > 0) work[row1 - 1] += err;
                        work[row1] += err;
                        if (x + 1 < width) work[row1 + 1] += err;
                    }
                    if (y + 2 < height) work[i + (2 * width)] += err;
                }
            }
        }

        private static void ApplyFloydSteinberg(byte[] gray, bool[] pixels, int width, int height, int threshold)
        {
            int[] errRow = new int[width + 2];
            int[] errNext = new int[width + 2];

            for (int y = 0; y < height; y++)
            {
                Array.Clear(errNext, 0, errNext.Length);
                int row = y * width;

                for (int x = 0; x < width; x++)
                {
                    int i = row + x;

                    // Apply propagated error (keep in int domain).
                    int oldVal = gray[i] + errRow[x + 1];
                    if (oldVal < 0) oldVal = 0;
                    else if (oldVal > 255) oldVal = 255;

                    int newVal = oldVal < threshold ? 0 : 255;
                    int err = oldVal - newVal;
                    pixels[i] = IsPixelOn(newVal);

                    // Distribute quantization error (integer Floyd–Steinberg).
                    // We store errors as "whole intensity units" (not scaled).
                    if (x + 1 < width) errRow[x + 2] += (err * 7) / 16;
                    errNext[x + 1] += (err * 5) / 16;
                    if (x > 0) errNext[x] += (err * 3) / 16;
                    if (x + 1 < width) errNext[x + 2] += (err * 1) / 16;
                }

                // Advance error buffers.
                var tmp = errRow;
                errRow = errNext;
                errNext = tmp;
            }

        }
    }
}

