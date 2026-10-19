using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using WpfBitmapFrame = System.Windows.Media.Imaging.BitmapFrame;

namespace ScanEzi
{
    /// <summary>
    /// Finds the orientation a page was scanned in.
    /// The Windows OCR engine reads text far more reliably the right way up, so the page is
    /// recognised in all four rotations and the one that yields the most text wins. That
    /// covers 90, 180 and 270 degree feeds without shipping a language model of our own.
    /// </summary>
    public static class AutoOrientation
    {
        /// <summary>OCR is cheap at this size and text is still large enough to read.</summary>
        private const int SampleEdge = 1200;

        /// <summary>Below this many recognised characters the result is noise, not a page.</summary>
        private const int MinimumScore = 12;

        /// <summary>If the upright reading is already close to the best, the page is fine as it is.</summary>
        private const double KeepUprightRatio = 0.5;

        private static bool? _available;

        /// <summary>False when Windows has no OCR language pack, in which case pages are left alone.</summary>
        public static bool IsAvailable
        {
            get
            {
                if (_available == null)
                {
                    try
                    {
                        _available = OcrEngine.TryCreateFromUserProfileLanguages() != null
                                     || OcrEngine.AvailableRecognizerLanguages.Count > 0;
                    }
                    catch
                    {
                        _available = false;
                    }
                }

                return _available.Value;
            }
        }

        /// <summary>
        /// Returns the clockwise rotation to apply: 0, 90, 180 or 270. Returns 0 whenever the
        /// answer is unclear, because leaving a page as scanned is safer than turning it wrongly.
        /// </summary>
        public static async Task<int> DetectRotationAsync(string imagePath)
        {
            if (!IsAvailable)
            {
                return 0;
            }

            BitmapSource? sample = LoadSample(imagePath);
            if (sample == null)
            {
                return 0;
            }

            var engine = OcrEngine.TryCreateFromUserProfileLanguages()
                         ?? OcrEngine.AvailableRecognizerLanguages
                             .Select(OcrEngine.TryCreateFromLanguage)
                             .FirstOrDefault(e => e != null);

            if (engine == null)
            {
                return 0;
            }

            int[] angles = { 0, 90, 180, 270 };
            var scores = new int[angles.Length];

            for (int i = 0; i < angles.Length; i++)
            {
                try
                {
                    scores[i] = await ScoreAsync(sample, angles[i], engine);
                }
                catch
                {
                    scores[i] = 0;
                }
            }

            int bestIndex = 0;
            for (int i = 1; i < scores.Length; i++)
            {
                if (scores[i] > scores[bestIndex])
                {
                    bestIndex = i;
                }
            }

            int best = scores[bestIndex];

            if (bestIndex == 0 || best < MinimumScore)
            {
                return 0;
            }

            if (scores[0] >= best * KeepUprightRatio)
            {
                return 0;
            }

            return angles[bestIndex];
        }

        /// <summary>Characters of confidently recognised words; longer words are worth more than specks.</summary>
        private static async Task<int> ScoreAsync(BitmapSource source, int angle, OcrEngine engine)
        {
            BitmapSource oriented = angle == 0
                ? source
                : new TransformedBitmap(source, new RotateTransform(angle));

            var result = await engine.RecognizeAsync(await ToSoftwareBitmapAsync(oriented));

            int score = 0;
            foreach (var line in result.Lines)
            {
                foreach (var word in line.Words)
                {
                    if (word.Text.Length >= 3)
                    {
                        score += word.Text.Length;
                    }
                }
            }

            return score;
        }

        private static BitmapSource? LoadSample(string imagePath)
        {
            try
            {
                var frame = WpfBitmapFrame.Create(
                    new Uri(Path.GetFullPath(imagePath)),
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);

                if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0)
                {
                    return null;
                }

                double scale = Math.Min(1.0, (double)SampleEdge / Math.Max(frame.PixelWidth, frame.PixelHeight));
                BitmapSource sample = scale < 1.0
                    ? new TransformedBitmap(frame, new ScaleTransform(scale, scale))
                    : frame;

                var converted = new FormatConvertedBitmap(sample, PixelFormats.Bgra32, null, 0);
                converted.Freeze();
                return converted;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>WPF bitmaps are not visible to the OCR engine, so the sample is round-tripped through PNG.</summary>
        private static async Task<SoftwareBitmap> ToSoftwareBitmapAsync(BitmapSource source)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(WpfBitmapFrame.Create(source));

            using var buffer = new MemoryStream();
            encoder.Save(buffer);
            buffer.Position = 0;

            using var stream = new InMemoryRandomAccessStream();
            var writer = new DataWriter(stream.GetOutputStreamAt(0));
            writer.WriteBytes(buffer.ToArray());
            await writer.StoreAsync();
            writer.DetachStream();
            stream.Seek(0);

            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
            return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        }
    }
}
