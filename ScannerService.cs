using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.Scanners;
using Windows.Storage;

namespace ScanEzi
{
    public class ScannerCapabilities
    {
        public bool HasFeeder { get; set; }
        public bool HasFlatbed { get; set; }
        public bool CanDuplex { get; set; }

        /// <summary>
        /// False when the device could not be queried at all. Drivers are often still warming
        /// up right after launch, and "no answer" must not be mistaken for "no features".
        /// </summary>
        public bool Reported { get; set; }
    }

    /// <summary>
    /// What a scan actually did, as opposed to what it was asked to do. The distinction
    /// matters because several drivers accept a duplex request and then quietly scan one
    /// side only.
    /// </summary>
    public class ScanOutcome
    {
        public List<string> Files { get; } = new();

        public ImageScannerScanSource Source { get; set; }

        public bool DuplexRequested { get; set; }

        public bool DuplexApplied { get; set; }

        /// <summary>Non-fatal note for the user, e.g. duplex could not be enabled.</summary>
        public string? Warning { get; set; }
    }

    public class ScannerService
    {
        public async Task<bool> HasScannerAsync()
        {
            var selector = ImageScanner.GetDeviceSelector();
            var devices = await DeviceInformation.FindAllAsync(selector);
            return devices.Count > 0;
        }

        public async Task<IReadOnlyList<DeviceInformation>> GetScannersAsync()
        {
            var selector = ImageScanner.GetDeviceSelector();
            return await DeviceInformation.FindAllAsync(selector);
        }

        public async Task<ScannerCapabilities> GetCapabilitiesAsync(string scannerId, int attempts = 3)
        {
            // A driver that has just been enumerated frequently refuses the first query, which
            // is why the settings used to stay greyed out until Refresh was pressed.
            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                var caps = await ReadCapabilitiesAsync(scannerId);
                if (caps.Reported) return caps;

                if (attempt < attempts)
                {
                    await Task.Delay(250 * attempt);
                }
            }

            return new ScannerCapabilities();
        }

        private static async Task<ScannerCapabilities> ReadCapabilitiesAsync(string scannerId)
        {
            var caps = new ScannerCapabilities();
            try
            {
                var scanner = await ImageScanner.FromIdAsync(scannerId);
                if (scanner == null) return caps;

                caps.HasFeeder = scanner.IsScanSourceSupported(ImageScannerScanSource.Feeder);
                caps.HasFlatbed = scanner.IsScanSourceSupported(ImageScannerScanSource.Flatbed);

                if (caps.HasFeeder)
                {
                    try
                    {
                        var feeder = scanner.FeederConfiguration;
                        caps.CanDuplex = feeder != null && feeder.CanScanDuplex;
                    }
                    catch
                    {
                        caps.CanDuplex = false;
                    }
                }

                caps.Reported = true;
            }
            catch (Exception ex)
            {
                ScanLog.Write($"Capability read failed for '{scannerId}': {ex.GetType().Name}: {ex.Message}");
            }

            return caps;
        }

        public async Task<ScanOutcome> ScanAsync(string scannerId, string paperSize = "A4", bool duplex = false, string source = "Auto Detect")
        {
            var outcome = new ScanOutcome { DuplexRequested = duplex };

            var scannerInstance = await ImageScanner.FromIdAsync(scannerId);
            if (scannerInstance == null)
                throw new Exception("Unable to connect to the scanner. Make sure it is powered on and connected.");

            var tempDir = Path.Combine(Path.GetTempPath(), "ScanEzi");
            Directory.CreateDirectory(tempDir);
            var storageFolder = await StorageFolder.GetFolderFromPathAsync(tempDir);

            var scanSource = ResolveSource(scannerInstance, source, duplex);
            outcome.Source = scanSource;

            ScanLog.Write($"scan start  device='{scannerId}'  source={scanSource}  requested={source}  duplexRequested={duplex}  paper={paperSize}");

            if (duplex && scanSource != ImageScannerScanSource.Feeder)
            {
                outcome.Warning = "2-sided scanning needs the document feeder, so this page was scanned 1-sided.";
            }

            outcome.DuplexApplied = ApplyFeederSettings(scannerInstance, scanSource, paperSize, duplex, outcome);

            ScanLog.Write($"settings applied  duplexApplied={outcome.DuplexApplied}");

            var result = await scannerInstance.ScanFilesToFolderAsync(scanSource, storageFolder);
            if (result == null || result.ScannedFiles.Count == 0)
            {
                ScanLog.Write("scan returned no files");
                throw new Exception(DescribeEmptyResult(scanSource, duplex));
            }

            ScanLog.Write($"driver returned {result.ScannedFiles.Count} file(s)");

            foreach (var file in result.ScannedFiles)
            {
                if (IsUsableImage(file.Path))
                {
                    AddPages(file.Path, outcome.Files);
                }
                else
                {
                    // Drivers can emit zero-pixel pages (usually an empty feeder);
                    // drop them instead of showing a broken thumbnail.
                    ScanLog.Write($"discarding unusable page '{Path.GetFileName(file.Path)}' ({new FileInfo(file.Path).Length} bytes)");
                    TryDelete(file.Path);
                }
            }

            if (outcome.Files.Count == 0)
            {
                throw new Exception(DescribeEmptyResult(scanSource, duplex));
            }

            if (outcome.DuplexRequested && outcome.DuplexApplied && outcome.Files.Count == 1)
            {
                // One page for a 2-sided request is legitimate for a single-sided sheet,
                // but it is also the classic symptom of a driver ignoring duplex.
                outcome.Warning ??= "The scanner returned a single page for a 2-sided scan. If the sheet is printed on both sides, the driver may not support duplex through this API.";
                ScanLog.Write("duplex requested and applied, but only one page came back");
            }

            ScanLog.Write($"scan done  usable={outcome.Files.Count}  duplexApplied={outcome.DuplexApplied}");
            return outcome;
        }

        /// <summary>
        /// Adds the page(s) contained in <paramref name="path"/>. Some WIA drivers answer a
        /// 2-sided request with a single multi-page file instead of one file per side, so
        /// multi-frame images are split into one file per page.
        /// </summary>
        private static void AddPages(string path, List<string> pages)
        {
            var produced = new List<string>();

            try
            {
                using var stream = File.OpenRead(path);
                var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                    stream,
                    System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                    System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);

                if (decoder.Frames.Count <= 1)
                {
                    pages.Add(path);
                    return;
                }

                ScanLog.Write($"file '{Path.GetFileName(path)}' holds {decoder.Frames.Count} page(s); splitting");

                var directory = Path.GetDirectoryName(path) ?? Path.GetTempPath();
                var stem = Path.GetFileNameWithoutExtension(path);

                for (int i = 0; i < decoder.Frames.Count; i++)
                {
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(decoder.Frames[i]));

                    var target = Path.Combine(directory, $"{stem}_p{i + 1}.png");
                    using (var output = File.Create(target))
                    {
                        encoder.Save(output);
                    }

                    produced.Add(target);
                }

                pages.AddRange(produced);
                TryDelete(path);
            }
            catch (Exception ex)
            {
                ScanLog.Write($"page expansion failed for '{Path.GetFileName(path)}': {ex.GetType().Name}");

                // Keep whatever was written; only fall back to the original if nothing was.
                if (produced.Count > 0) pages.AddRange(produced);
                else pages.Add(path);
            }
        }

        private static bool IsUsableImage(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length < 1024) return false;

                using var stream = File.OpenRead(path);
                var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                    stream,
                    System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation,
                    System.Windows.Media.Imaging.BitmapCacheOption.None);

                if (decoder.Frames.Count == 0) return false;
                var frame = decoder.Frames[0];
                return frame.PixelWidth > 0 && frame.PixelHeight > 0;
            }
            catch
            {
                return false;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }

        private static ImageScannerScanSource ResolveSource(ImageScanner scanner, string requested, bool duplex)
        {
            bool hasFeeder = scanner.IsScanSourceSupported(ImageScannerScanSource.Feeder);
            bool hasFlatbed = scanner.IsScanSourceSupported(ImageScannerScanSource.Flatbed);

            if (requested == "Document Feeder")
            {
                if (!hasFeeder)
                    throw new Exception("This scanner has no document feeder. Choose Flatbed or Auto Detect.");
                return ImageScannerScanSource.Feeder;
            }

            if (requested == "Flatbed")
            {
                if (!hasFlatbed)
                    throw new Exception("This scanner has no flatbed. Choose Document Feeder or Auto Detect.");
                return ImageScannerScanSource.Flatbed;
            }

            // Auto: duplex only makes sense on the feeder, otherwise prefer the feeder
            // (document scanners) and fall back to the flatbed.
            if (duplex)
            {
                if (hasFeeder) return ImageScannerScanSource.Feeder;
                throw new Exception("2-sided scanning requires a document feeder. This scanner only offers the flatbed.");
            }

            if (hasFeeder) return ImageScannerScanSource.Feeder;
            if (hasFlatbed) return ImageScannerScanSource.Flatbed;
            if (scanner.IsScanSourceSupported(ImageScannerScanSource.AutoConfigured))
                return ImageScannerScanSource.AutoConfigured;
            return ImageScannerScanSource.Default;
        }

        /// <summary>
        /// Applies page size and duplex to the feeder configuration and reports back whether
        /// duplex really stuck, since several drivers accept the write and silently ignore it.
        /// </summary>
        private static bool ApplyFeederSettings(
            ImageScanner scanner,
            ImageScannerScanSource source,
            string paperSize,
            bool duplex,
            ScanOutcome outcome)
        {
            if (source != ImageScannerScanSource.Feeder) return false;

            ImageScannerFeederConfiguration? config;
            try
            {
                config = scanner.FeederConfiguration;
            }
            catch (Exception ex)
            {
                ScanLog.Write($"FeederConfiguration unavailable: {ex.GetType().Name}: {ex.Message}");
                if (duplex) outcome.Warning = "This device did not expose feeder settings, so the page was scanned 1-sided.";
                return false;
            }

            if (config == null) return false;

            RaisePageLimit(config, outcome);

            bool duplexApplied = false;
            if (duplex)
            {
                try
                {
                    if (!config.CanScanDuplex)
                    {
                        ScanLog.Write("CanScanDuplex=false; duplex not attempted");
                        outcome.Warning = "This scanner reports that it cannot scan both sides, so the page was scanned 1-sided.";
                    }
                    else
                    {
                        config.Duplex = true;

                        // Read back: a driver that cannot really duplex will have reset it.
                        duplexApplied = config.Duplex;
                        ScanLog.Write($"duplex set -> read back {config.Duplex}");

                        if (!duplexApplied)
                        {
                            outcome.Warning = "The scanner accepted the 2-sided setting but reported it back as 1-sided, so the page was scanned 1-sided.";
                        }
                    }
                }
                catch (Exception ex)
                {
                    ScanLog.Write($"duplex set failed: {ex.GetType().Name}: {ex.Message}");
                    outcome.Warning = $"This scanner rejected the 2-sided setting ({ex.Message.Trim()}), so the page was scanned 1-sided.";
                }
            }

            var mediaSize = paperSize switch
            {
                "Letter" => Windows.Graphics.Printing.PrintMediaSize.NorthAmericaLetter,
                "Legal" => Windows.Graphics.Printing.PrintMediaSize.NorthAmericaLegal,
                _ => Windows.Graphics.Printing.PrintMediaSize.IsoA4
            };

            try
            {
                if (config.CanAutoDetectPageSize)
                {
                    config.AutoDetectPageSize = false;
                }

                config.PageSize = mediaSize;
            }
            catch (Exception ex)
            {
                // Honour the user's size if possible, otherwise fall back to driver
                // auto-detection rather than scanning a zero-size page.
                ScanLog.Write($"PageSize set failed: {ex.GetType().Name}: {ex.Message}");
                try
                {
                    if (config.CanAutoDetectPageSize) config.AutoDetectPageSize = true;
                }
                catch { }
            }

            try
            {
                var applied = config.PageSize;
                ScanLog.Write($"page size requested {mediaSize}, driver reports {applied}");

                if (applied != mediaSize)
                {
                    outcome.Warning ??= $"This scanner did not accept the {paperSize} page size, so it used its own default instead.";
                }
            }
            catch (Exception ex)
            {
                ScanLog.Write($"PageSize read-back failed: {ex.GetType().Name}");
            }

            // Page-size changes are allowed to reset duplex on some drivers, so re-assert it.
            if (duplexApplied)
            {
                try
                {
                    if (!config.Duplex)
                    {
                        config.Duplex = true;
                        ScanLog.Write($"duplex re-asserted after page size -> {config.Duplex}");
                    }
                    duplexApplied = config.Duplex;
                }
                catch (Exception ex)
                {
                    ScanLog.Write($"duplex re-assert failed: {ex.GetType().Name}");
                    duplexApplied = false;
                }
            }

            return duplexApplied;
        }

        /// <summary>
        /// Several WIA drivers default <c>MaxNumberOfPages</c> to 1, which silently caps a
        /// 2-sided or multi-sheet feeder run to a single page. Drivers disagree about the
        /// ceiling, so walk down until one sticks.
        /// </summary>
        private static void RaisePageLimit(ImageScannerFeederConfiguration config, ScanOutcome outcome)
        {
            uint current;
            try
            {
                current = config.MaxNumberOfPages;
            }
            catch (Exception ex)
            {
                ScanLog.Write($"MaxNumberOfPages read failed: {ex.GetType().Name}");
                return;
            }

            if (current >= 50)
            {
                ScanLog.Write($"MaxNumberOfPages already {current}");
                return;
            }

            foreach (var candidate in new uint[] { 100, 50, 25, 20, 10, 5, 2 })
            {
                if (candidate <= current) break;

                try
                {
                    config.MaxNumberOfPages = candidate;
                    var applied = config.MaxNumberOfPages;

                    if (applied > current)
                    {
                        ScanLog.Write($"MaxNumberOfPages {current} -> {applied}");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    ScanLog.Write($"MaxNumberOfPages {candidate} rejected: {ex.GetType().Name}");
                }
            }

            ScanLog.Write($"MaxNumberOfPages could not be raised above {current}");
            outcome.Warning ??= "This scanner would not accept a multi-page limit, so it may return only the first page.";
        }

        private static string DescribeEmptyResult(ImageScannerScanSource source, bool duplex)
        {
            if (source == ImageScannerScanSource.Feeder)
            {
                return duplex
                    ? "The document feeder returned no pages. Load the sheets in the feeder and try again."
                    : "The document feeder returned no pages. Load a page in the feeder and try again. If the device has no feeder, set Source to Flatbed.";
            }

            return "The scanner returned no pages. Place the document on the glass, close the lid, and try again.";
        }
    }
}
