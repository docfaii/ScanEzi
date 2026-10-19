using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ScanEzi
{
    public class ImageProcessor
    {
        /// <summary>
        /// Rotates an image by the specified angle in degrees (90, 180, 270) and returns new file path.
        /// </summary>
        public string RotateImage(string sourcePath, double angle)
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(sourcePath);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();

            var transformed = new TransformedBitmap(bitmap, new System.Windows.Media.RotateTransform(angle));

            var encoder = new JpegBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(transformed));

            var tempPath = Path.Combine(Path.GetDirectoryName(sourcePath)!,
                $"rot_{Path.GetFileNameWithoutExtension(sourcePath)}.jpg");

            using var stream = File.Create(tempPath);
            encoder.Save(stream);

            return tempPath;
        }

        /// <summary>
        /// Crops an image to the specified rectangle and returns new file path.
        /// </summary>
        public string CropImage(string sourcePath, Int32Rect cropRect)
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(sourcePath);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();

            var cropped = new CroppedBitmap(bitmap, cropRect);

            var encoder = new JpegBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(cropped));

            var tempPath = Path.Combine(Path.GetDirectoryName(sourcePath)!,
                $"crop_{Path.GetFileNameWithoutExtension(sourcePath)}.jpg");

            using var stream = File.Create(tempPath);
            encoder.Save(stream);

            return tempPath;
        }

        /// <summary>
        /// Saves an image as JPG with specified quality to destination folder.
        /// </summary>
        public async Task SaveAsJpgAsync(string sourcePath, string destinationDirectory, int quality = 90)
        {
            var fileName = Path.GetFileNameWithoutExtension(sourcePath) + ".jpg";
            var destPath = Path.Combine(destinationDirectory, fileName);

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(sourcePath);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();

            var encoder = new JpegBitmapEncoder { QualityLevel = quality };
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            using var stream = File.Create(destPath);
            encoder.Save(stream);

            await Task.CompletedTask;
        }
    }
}