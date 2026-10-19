using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScanEzi
{
    public enum PageKind
    {
        Content,
        Black,
        White
    }

    /// <summary>
    /// Decides whether a scanned sheet actually carries a document.
    /// A feeder sensor that failed to capture returns an all-black sheet, and the unused
    /// side of a single-sided sheet scanned in duplex mode returns a blank white one.
    /// Both are excluded from the export but stay visible in the gallery.
    /// </summary>
    public static class PageContentAnalyzer
    {
        // Sampling is deliberately fine: thin text survives a 450 px reduction, but at
        // 160 px it averages away to near-white and a printed page reads as blank.
        private const int SampleEdge = 450;

        // The outer border is trimmed before measuring. It carries the sheet edge, the
        // scan-bed background and any shadow, none of which say anything about content.
        private const double BorderTrimFraction = 0.06;

        // An all-black page is both very dark on average and almost uniformly dark.
        // A genuinely dark photograph still varies a lot, so the deviation check keeps it.
        private const double BlackMeanThreshold = 60;
        private const double BlackInkFraction = 0.92;
        private const double BlackMaxDeviation = 45;

        // A blank sheet is brighter than any printed page and carries essentially no ink
        // by either measure. Real blank scans sit around 253 mean with 0.03% dark pixels,
        // while a printed page sits near 233 with 4.9%, so these thresholds are far apart.
        private const double WhiteMeanThreshold = 244;
        private const double WhiteMaxInkFraction = 0.001;
        private const double WhiteMaxRelativeInkFraction = 0.01;

        private const byte InkLevel = 128;
        private const int PaperRelativeDelta = 40;

        public static PageKind Classify(string imagePath)
        {
            try
            {
                var frame = BitmapFrame.Create(
                    new Uri(Path.GetFullPath(imagePath)),
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);

                if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0)
                {
                    return PageKind.Content;
                }

                double scale = Math.Min(1.0, (double)SampleEdge / Math.Max(frame.PixelWidth, frame.PixelHeight));
                BitmapSource sample = scale < 1.0
                    ? new TransformedBitmap(frame, new ScaleTransform(scale, scale))
                    : frame;

                var gray = new FormatConvertedBitmap(sample, PixelFormats.Gray8, null, 0);
                gray.Freeze();

                int width = gray.PixelWidth;
                int height = gray.PixelHeight;
                if (width <= 0 || height <= 0)
                {
                    return PageKind.Content;
                }

                var pixels = new byte[width * height];
                gray.CopyPixels(pixels, width, 0);

                int margin = (int)(Math.Min(width, height) * BorderTrimFraction);
                int x0 = margin;
                int x1 = Math.Max(x0 + 1, width - margin);
                int y0 = margin;
                int y1 = Math.Max(y0 + 1, height - margin);

                // A 256-bin histogram gives mean, deviation, the paper white level and the
                // ink fractions in one pass, with no sorting of the whole sample.
                var histogram = new long[256];
                long count = 0;
                long sum = 0;
                long sumSquares = 0;

                for (int y = y0; y < y1; y++)
                {
                    int row = y * width;
                    for (int x = x0; x < x1; x++)
                    {
                        byte value = pixels[row + x];
                        histogram[value]++;
                        count++;
                        sum += value;
                        sumSquares += value * value;
                    }
                }

                if (count == 0)
                {
                    return PageKind.Content;
                }

                double mean = (double)sum / count;
                double deviation = Math.Sqrt(Math.Max(0, (double)sumSquares / count - mean * mean));

                long darkPixels = 0;
                for (int i = 0; i < InkLevel; i++)
                {
                    darkPixels += histogram[i];
                }

                double inkFraction = (double)darkPixels / count;

                // The paper's own white level, taken from the bright end so a few specks
                // do not move it. Ink is then judged against that level rather than an
                // absolute value, which keeps the test stable across exposure differences.
                long target = (long)(count * 0.90);
                long running = 0;
                int paper = 255;
                for (int i = 0; i < 256; i++)
                {
                    running += histogram[i];
                    if (running >= target)
                    {
                        paper = i;
                        break;
                    }
                }

                int inkCeiling = Math.Max(0, paper - PaperRelativeDelta);
                long relativeInkPixels = 0;
                for (int i = 0; i < inkCeiling; i++)
                {
                    relativeInkPixels += histogram[i];
                }

                double relativeInkFraction = (double)relativeInkPixels / count;

                if (mean <= BlackMeanThreshold &&
                    inkFraction >= BlackInkFraction &&
                    deviation <= BlackMaxDeviation)
                {
                    return PageKind.Black;
                }

                if (mean >= WhiteMeanThreshold &&
                    inkFraction <= WhiteMaxInkFraction &&
                    relativeInkFraction <= WhiteMaxRelativeInkFraction)
                {
                    return PageKind.White;
                }

                return PageKind.Content;
            }
            catch
            {
                // Never drop a page because the analysis itself failed.
                return PageKind.Content;
            }
        }
    }
}
