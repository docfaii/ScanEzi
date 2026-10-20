using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ScanEzi
{
    /// <summary>A newer release found on GitHub.</summary>
    public sealed class UpdateInfo
    {
        public Version Version { get; init; } = new(0, 0, 0);
        public string TagName { get; init; } = string.Empty;
        public string? ReleaseNotes { get; init; }
        public string AssetName { get; init; } = string.Empty;
        public Uri DownloadUrl { get; init; } = null!;
        public string? Sha256 { get; init; }
        public long SizeBytes { get; init; }
    }

    /// <summary>
    /// Checks GitHub Releases for a newer ScanEzi build, downloads the variant that matches
    /// this installation and hands the swap to a small PowerShell helper so the running
    /// executable can be replaced after the app exits.
    /// </summary>
    public sealed class UpdateService
    {
        private const string LatestReleaseUrl = "https://api.github.com/repos/docfaii/ScanEzi/releases/latest";

        private static readonly HttpClient Http = CreateHttpClient();

        public static Version CurrentVersion { get; } = ReadCurrentVersion();

        /// <summary>Returns the newest applicable release, or null when this install is current.</summary>
        public async Task<UpdateInfo?> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
        {
            using var response = await Http
                .GetAsync(LatestReleaseUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;

            if (IsTrue(root, "draft") || IsTrue(root, "prerelease"))
            {
                return null;
            }

            var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag))
            {
                return null;
            }

            var version = ParseVersion(tag);
            if (version == null || version <= CurrentVersion)
            {
                return null;
            }

            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var notes = root.TryGetProperty("body", out var bodyElement) ? bodyElement.GetString() : null;
            var wantsSelfContained = IsSelfContainedInstall();
            UpdateInfo? preferred = null;
            UpdateInfo? fallback = null;

            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                var url = asset.TryGetProperty("browser_download_url", out var urlElement) ? urlElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                if (!name.StartsWith("ScanEzi-", StringComparison.OrdinalIgnoreCase) ||
                    !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!Uri.TryCreate(url, UriKind.Absolute, out var downloadUrl))
                {
                    continue;
                }

                var info = new UpdateInfo
                {
                    Version = version,
                    TagName = tag,
                    ReleaseNotes = notes,
                    AssetName = name,
                    DownloadUrl = downloadUrl,
                    Sha256 = ReadDigest(asset),
                    SizeBytes = asset.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var size) ? size : 0
                };

                bool isSelfContained = name.Contains("self-contained", StringComparison.OrdinalIgnoreCase);
                if (isSelfContained == wantsSelfContained)
                {
                    preferred = info;
                    break;
                }

                fallback ??= info;
            }

            return preferred ?? fallback;
        }

        /// <summary>Downloads the update and verifies the checksum published with the asset.</summary>
        public async Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            var folder = Path.Combine(Path.GetTempPath(), "ScanEzi", "updates", update.Version.ToString(3));
            Directory.CreateDirectory(folder);
            var zipPath = Path.Combine(folder, update.AssetName);

            using (var response = await Http
                       .GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                       .ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();

                long total = response.Content.Headers.ContentLength ?? update.SizeBytes;
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var output = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    received += read;

                    if (total > 0)
                    {
                        progress?.Report((double)received / total);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(update.Sha256))
            {
                var actual = await ComputeSha256Async(zipPath, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(actual, update.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(zipPath);
                    throw new InvalidDataException("The downloaded update did not match the published checksum.");
                }
            }

            return zipPath;
        }

        /// <summary>
        /// Extracts the update and starts a detached helper that waits for this process to
        /// exit, copies the new files over the install folder and relaunches ScanEzi.
        /// </summary>
        public void ApplyUpdate(string zipPath)
        {
            var appDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            EnsureWritable(appDirectory);

            var staging = Path.Combine(Path.GetTempPath(), "ScanEzi", "staging", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            ZipFile.ExtractToDirectory(zipPath, staging, overwriteFiles: true);

            if (!File.Exists(Path.Combine(staging, "ScanEzi.exe")))
            {
                throw new InvalidDataException("The update package does not contain ScanEzi.exe.");
            }

            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ScanEzi");
            Directory.CreateDirectory(logDirectory);
            var logPath = Path.Combine(logDirectory, "update.log");

            var scriptPath = Path.Combine(Path.GetTempPath(), "ScanEzi", $"apply-update-{Guid.NewGuid():N}.ps1");
            File.WriteAllText(scriptPath, UpdaterScript, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var executablePath = Path.Combine(appDirectory, "ScanEzi.exe");
            var arguments =
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\" " +
                $"-WaitPid {Environment.ProcessId} -SourceDir \"{staging}\" -TargetDir \"{appDirectory}\" " +
                $"-ExePath \"{executablePath}\" -LogPath \"{logPath}\"";

            var startInfo = new ProcessStartInfo("powershell.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath()
            };

            if (Process.Start(startInfo) == null)
            {
                throw new InvalidOperationException("The update helper could not be started.");
            }
        }

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ScanEzi-Updater/1.0");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }

        private static bool IsTrue(JsonElement element, string propertyName) =>
            element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.True;

        private static string? ReadDigest(JsonElement asset)
        {
            if (!asset.TryGetProperty("digest", out var digestElement))
            {
                return null;
            }

            var digest = digestElement.GetString();
            if (string.IsNullOrWhiteSpace(digest))
            {
                return null;
            }

            const string prefix = "sha256:";
            return digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? digest[prefix.Length..].Trim()
                : null;
        }

        private static Version ReadCurrentVersion()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            if (!string.IsNullOrWhiteSpace(informational))
            {
                var metadata = informational.IndexOf('+');
                if (metadata >= 0)
                {
                    informational = informational[..metadata];
                }

                if (Version.TryParse(informational, out var parsed))
                {
                    return Normalize(parsed);
                }
            }

            return Normalize(assembly.GetName().Version ?? new Version(1, 0, 0));
        }

        private static Version? ParseVersion(string tag)
        {
            var text = tag.Trim();
            if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                text = text[1..];
            }

            var suffix = text.IndexOf('-');
            if (suffix >= 0)
            {
                text = text[..suffix];
            }

            return Version.TryParse(text, out var version) ? Normalize(version) : null;
        }

        /// <summary>Drops the revision and treats a missing build number as zero.</summary>
        private static Version Normalize(Version version) =>
            new(version.Major, version.Minor, Math.Max(version.Build, 0));

        /// <summary>
        /// A self-contained publish lists its bundled frameworks in runtimeconfig.json;
        /// a framework-dependent publish lists "frameworks" instead.
        /// </summary>
        private static bool IsSelfContainedInstall()
        {
            try
            {
                var runtimeConfig = Path.Combine(AppContext.BaseDirectory, "ScanEzi.runtimeconfig.json");
                if (!File.Exists(runtimeConfig))
                {
                    return false;
                }

                using var document = JsonDocument.Parse(File.ReadAllText(runtimeConfig));
                return document.RootElement.TryGetProperty("runtimeOptions", out var options) &&
                       options.TryGetProperty("includedFrameworks", out _);
            }
            catch
            {
                return false;
            }
        }

        private static void EnsureWritable(string appDirectory)
        {
            var probe = Path.Combine(appDirectory, $".scanezi-update-{Guid.NewGuid():N}.tmp");
            try
            {
                using var stream = new FileStream(
                    probe,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.DeleteOnClose);
            }
            catch (Exception ex)
            {
                throw new IOException(
                    "ScanEzi cannot update itself because its folder is not writable. " +
                    "Download the new version from the releases page instead. " + ex.Message,
                    ex);
            }
        }

        private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
        {
            await using var stream = File.OpenRead(path);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // A leftover download is harmless; the next attempt overwrites it.
            }
        }

        private const string UpdaterScript = """
param(
    [Parameter(Mandatory = $true)][int]$WaitPid,
    [Parameter(Mandatory = $true)][string]$SourceDir,
    [Parameter(Mandatory = $true)][string]$TargetDir,
    [Parameter(Mandatory = $true)][string]$ExePath,
    [Parameter(Mandatory = $true)][string]$LogPath
)

$ErrorActionPreference = 'Stop'

function Write-UpdateLog([string]$Message) {
    try {
        $folder = Split-Path -Parent $LogPath
        if ($folder -and -not (Test-Path -LiteralPath $folder)) {
            New-Item -ItemType Directory -Path $folder -Force | Out-Null
        }
        Add-Content -LiteralPath $LogPath -Value ("{0}  {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message)
    } catch { }
}

Write-UpdateLog "Updater started; waiting for process $WaitPid."
try { Wait-Process -Id $WaitPid -Timeout 60 -ErrorAction SilentlyContinue } catch { }
Start-Sleep -Milliseconds 800

$applied = $false
for ($attempt = 1; $attempt -le 20; $attempt++) {
    try {
        & robocopy $SourceDir $TargetDir /E /R:3 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
        $code = $LASTEXITCODE
        if ($code -ge 8) { throw "robocopy exited with $code" }
        $applied = $true
        Write-UpdateLog "Files copied on attempt $attempt (robocopy $code)."
        break
    } catch {
        Write-UpdateLog "Copy attempt $attempt failed: $($_.Exception.Message)"
        Start-Sleep -Milliseconds 750
    }
}

Start-Process -FilePath $ExePath -WorkingDirectory $TargetDir
if ($applied) {
    Write-UpdateLog "Started the updated ScanEzi."
} else {
    Write-UpdateLog "Update failed; restarted the existing ScanEzi."
}

Start-Sleep -Seconds 2
try { [System.IO.Directory]::Delete($SourceDir, $true) } catch { }
try { [System.IO.File]::Delete($PSCommandPath) } catch { }
""";
    }
}
