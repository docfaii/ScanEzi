using System;
using System.IO;

namespace ScanEzi
{
    /// <summary>
    /// Append-only diagnostics for scan sessions. Scanner drivers are wildly inconsistent,
    /// so a small log is the only way to tell "duplex was never requested" apart from
    /// "duplex was requested and the driver ignored it".
    /// </summary>
    public static class ScanLog
    {
        private static readonly object Gate = new();

        private static string LogPath
        {
            get
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ScanEzi");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "scan.log");
            }
        }

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    var path = LogPath;

                    // Keep the log from growing without bound across long-lived installs.
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > 512 * 1024)
                    {
                        File.Delete(path);
                    }

                    File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
                }
            }
            catch
            {
                // Diagnostics must never break a scan.
            }
        }
    }
}
