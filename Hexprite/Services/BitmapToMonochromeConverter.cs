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

            if (origMax > maxDimension)
            {
                double scale = maxDimension / (double)origMax;
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
                    if (a < 128)
                    {
                        gray[row + x] = 255;
                        continue;
                    }

                    // Standard luma formula (rounded).
                    int luma = (int)(0.299 * r + 0.587 * g + 0.114 * b + 0.5);
                    gray[row + x] = (byte)Math.Clamp(luma, 0, 255);
                }
            }

            // Floyd–Steinberg dithering to {0,255} then map to bools.
            bool[] pixels = new bool[dstW * dstH];
            int[] errRow = new int[dstW + 2];
            int[] errNext = new int[dstW + 2];

            for (int y = 0; y < dstH; y++)
            {
                Array.Clear(errNext, 0, errNext.Length);
                int row = y * dstW;

                for (int x = 0; x < dstW; x++)
                {
                    int i = row + x;

                    // Apply propagated error (keep in int domain).
                    int oldVal = gray[i] + errRow[x + 1];
                    if (oldVal < 0) oldVal = 0;
                    else if (oldVal > 255) oldVal = 255;

                    int newVal = oldVal < 128 ? 0 : 255;
                    int err = oldVal - newVal;

                    // Hexprite's stored/exported bit polarity treats this branch as the
                    // opposite visual state from what users expect during bitmap import.
                    // Map dark source pixels to the imported "black" result.
                    pixels[i] = newVal != 0;

                    // Distribute quantization error (integer Floyd–Steinberg).
                    // We store errors as "whole intensity units" (not scaled).
                    if (x + 1 < dstW) errRow[x + 2] += (err * 7) / 16;
                    errNext[x + 1] += (err * 5) / 16;
                    if (x > 0) errNext[x] += (err * 3) / 16;
                    if (x + 1 < dstW) errNext[x + 2] += (err * 1) / 16;
                }

                // Advance error buffers.
                var tmp = errRow;
                errRow = errNext;
                errNext = tmp;
            }

            return (pixels, dstW, dstH, wasScaled);
        }
    }
}

