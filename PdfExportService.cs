using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScanEzi
{
    /// <summary>
    /// Builds a multi-page, image-only PDF without any third-party dependency.
    /// Each page is embedded as a DCTDecode (JPEG) stream, which every PDF reader
    /// supports natively, and sized to the image's physical dimensions via its DPI.
    /// </summary>
    public static class PdfExportService
    {
        private sealed class PageData
        {
            public byte[] Jpeg = Array.Empty<byte>();
            public int Width;
            public int Height;
            public double DpiX;
            public double DpiY;
            public string ColorSpace = "/DeviceRGB";
        }

        private const double A4WidthPt = 595.276;
        private const double A4HeightPt = 841.89;

        public static void CreateFromImages(IReadOnlyList<string> imagePaths, string outputPath, int jpegQuality = 90)
        {
            if (imagePaths == null || imagePaths.Count == 0)
            {
                throw new InvalidOperationException("There are no pages to export.");
            }

            var pages = new List<PageData>(imagePaths.Count);
            foreach (var path in imagePaths)
            {
                pages.Add(LoadPage(path, jpegQuality));
            }

            // Object numbering: 1 = catalog, 2 = page tree, then 3 objects per page.
            var offsets = new Dictionary<int, long>();

            using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);

            void WriteAscii(string text)
            {
                var bytes = Encoding.ASCII.GetBytes(text);
                stream.Write(bytes, 0, bytes.Length);
            }

            void BeginObject(int number, string body)
            {
                offsets[number] = stream.Position;
                WriteAscii($"{number} 0 obj\n{body}\n");
            }

            void EndObject() => WriteAscii("endobj\n");

            void WriteObject(int number, string body)
            {
                BeginObject(number, body);
                EndObject();
            }

            void WriteStreamObject(int number, string dictionary, byte[] data)
            {
                offsets[number] = stream.Position;
                var head = string.IsNullOrEmpty(dictionary)
                    ? $"{number} 0 obj\n<< /Length {data.Length} >>\nstream\n"
                    : $"{number} 0 obj\n<< {dictionary} /Length {data.Length} >>\nstream\n";
                WriteAscii(head);
                stream.Write(data, 0, data.Length);
                WriteAscii("\nendstream\nendobj\n");
            }

            WriteAscii("%PDF-1.4\n");
            // Binary comment marks the file as containing binary data for tools sniffing the header.
            stream.Write(new byte[] { 0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A }, 0, 6);

            var kids = new StringBuilder();
            for (int i = 0; i < pages.Count; i++)
            {
                kids.Append(3 + i * 3).Append(" 0 R ");
            }

            WriteObject(1, "<< /Type /Catalog /Pages 2 0 R >>");
            WriteObject(2, $"<< /Type /Pages /Kids [ {kids.ToString().Trim()} ] /Count {pages.Count} >>");

            for (int i = 0; i < pages.Count; i++)
            {
                var page = pages[i];
                int pageObj = 3 + i * 3;
                int contentObj = pageObj + 1;
                int imageObj = pageObj + 2;

                double dpiX = page.DpiX > 0 ? page.DpiX : 96.0;
                double dpiY = page.DpiY > 0 ? page.DpiY : 96.0;
                double widthPt = page.Width * 72.0 / dpiX;
                double heightPt = page.Height * 72.0 / dpiY;

                // Some images carry a bogus or missing DPI, which would produce a page
                // far larger or smaller than any real sheet. Fall back to A4 then.
                bool plausible = widthPt >= 72 && widthPt <= 2000 && heightPt >= 72 && heightPt <= 2000;
                double pageWidthPt;
                double pageHeightPt;
                if (plausible)
                {
                    pageWidthPt = widthPt;
                    pageHeightPt = heightPt;
                }
                else
                {
                    bool landscape = page.Width > page.Height;
                    pageWidthPt = landscape ? A4HeightPt : A4WidthPt;
                    pageHeightPt = landscape ? A4WidthPt : A4HeightPt;
                }

                // Fit the image inside the page without distortion and centre it.
                double scale = Math.Min(pageWidthPt / widthPt, pageHeightPt / heightPt);
                double drawWidth = widthPt * scale;
                double drawHeight = heightPt * scale;
                double offsetX = (pageWidthPt - drawWidth) / 2.0;
                double offsetY = (pageHeightPt - drawHeight) / 2.0;

                WriteStreamObject(
                    imageObj,
                    $"/Type /XObject /Subtype /Image /Width {page.Width} /Height {page.Height} " +
                    $"/ColorSpace {page.ColorSpace} /BitsPerComponent 8 /Filter /DCTDecode",
                    page.Jpeg);

                var content = Encoding.ASCII.GetBytes(string.Format(
                    CultureInfo.InvariantCulture,
                    "q\n{0:0.###} 0 0 {1:0.###} {2:0.###} {3:0.###} cm\n/Im0 Do\nQ\n",
                    drawWidth,
                    drawHeight,
                    offsetX,
                    offsetY));
                WriteStreamObject(contentObj, "", content);

                WriteObject(
                    pageObj,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "<< /Type /Page /Parent 2 0 R /MediaBox [ 0 0 {0:0.###} {1:0.###} ] " +
                        "/Resources << /XObject << /Im0 {2} 0 R >> >> /Contents {3} 0 R >>",
                        pageWidthPt,
                        pageHeightPt,
                        imageObj,
                        contentObj));
            }

            int objectCount = 2 + pages.Count * 3;
            long xrefOffset = stream.Position;

            WriteAscii($"xref\n0 {objectCount + 1}\n");
            WriteAscii("0000000000 65535 f \n");
            for (int number = 1; number <= objectCount; number++)
            {
                WriteAscii(offsets.TryGetValue(number, out var offset)
                    ? offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n"
                    : "0000000000 65535 f \n");
            }

            WriteAscii($"trailer\n<< /Size {objectCount + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");
        }

        private static PageData LoadPage(string path, int quality)
        {
            var frame = BitmapFrame.Create(
                new Uri(Path.GetFullPath(path)),
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            var page = new PageData
            {
                Width = frame.PixelWidth,
                Height = frame.PixelHeight,
                DpiX = frame.DpiX > 0 ? frame.DpiX : 96.0,
                DpiY = frame.DpiY > 0 ? frame.DpiY : 96.0
            };

            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension == ".jpg" || extension == ".jpeg")
            {
                // Already JPEG - embed the original bytes so nothing is recompressed.
                var original = File.ReadAllBytes(path);
                int components = GetJpegComponentCount(original);
                if (components == 1)
                {
                    page.ColorSpace = "/DeviceGray";
                    page.Jpeg = original;
                    return page;
                }

                if (components == 3)
                {
                    page.ColorSpace = "/DeviceRGB";
                    page.Jpeg = original;
                    return page;
                }
            }

            BitmapSource source = frame;
            if (source.Format != PixelFormats.Bgr24)
            {
                source = new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0);
            }

            var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) };
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var buffer = new MemoryStream();
            encoder.Save(buffer);
            page.Jpeg = buffer.ToArray();

            return page;
        }

        /// <summary>
        /// Reads the component count from the JPEG SOF marker: 1 = greyscale, 3 = YCbCr/RGB.
        /// Returns 0 when the marker cannot be found.
        /// </summary>
        private static int GetJpegComponentCount(byte[] data)
        {
            if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
            {
                return 0;
            }

            int i = 2;
            while (i + 9 < data.Length)
            {
                if (data[i] != 0xFF)
                {
                    i++;
                    continue;
                }

                byte marker = data[i + 1];
                if (marker == 0xFF)
                {
                    i++;
                    continue;
                }

                if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD9))
                {
                    i += 2;
                    continue;
                }

                if (marker == 0xDA)
                {
                    break;
                }

                int segmentLength = (data[i + 2] << 8) | data[i + 3];
                if (segmentLength < 2)
                {
                    break;
                }

                bool isStartOfFrame =
                    marker >= 0xC0 && marker <= 0xCF &&
                    marker != 0xC4 && marker != 0xC8 && marker != 0xCC;

                if (isStartOfFrame)
                {
                    return data[i + 9];
                }

                i += 2 + segmentLength;
            }

            return 0;
        }
    }
}
