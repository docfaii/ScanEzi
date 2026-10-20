using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ScanEzi
{
    /// <summary>
    /// HP registers its own still-image launcher (SDSSLchr.exe) for the Scanjet front-panel
    /// button. When HP Easy Scan is not installed that launcher shows an "HP Easy Scan
    /// Software is not installed" error on every button press, even though the press is
    /// already handled here.
    ///
    /// The registration lives under HKLM, so a per-user app cannot take it over; instead the
    /// error dialog is closed as soon as it appears. Only dialogs owned by HP's launcher are
    /// touched, so nothing else on the desktop is affected.
    /// </summary>
    public sealed class HpEasyScanDialogGuard : IDisposable
    {
        private const string LauncherProcess = "SDSSLchr";
        private const string HpInstallFolder = @"Hewlett-Packard\Scanjet";
        private const string DialogPhrase = "HP Easy Scan";
        private const string DialogClass = "#32770";

        private const int WM_GETTEXT = 0x000D;
        private const int WM_CLOSE = 0x0010;
        private const int SMTO_ABORTIFHUNG = 0x0002;

        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(750);

        private readonly ManualResetEventSlim _stop = new(false);
        private Thread? _thread;
        private bool _disposed;

        /// <summary>How many HP launcher error dialogs have been closed this session.</summary>
        public int DismissedCount { get; private set; }

        public void Start()
        {
            if (_disposed || _thread != null) return;

            _thread = new Thread(Poll)
            {
                IsBackground = true,
                Name = "ScanEzi HP button dialog guard"
            };
            _thread.Start();
        }

        private void Poll()
        {
            while (!_disposed && !_stop.Wait(PollInterval))
            {
                try
                {
                    if (DismissOnce() > 0)
                    {
                        DismissedCount++;
                        ScanLog.Write("Closed HP Easy Scan button error dialog.");
                    }
                }
                catch (Exception ex)
                {
                    // A guard that throws must never take the app down with it.
                    System.Diagnostics.Debug.WriteLine($"HP dialog guard: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Closes every visible error dialog raised by HP's button launcher. Returns how many
        /// windows were asked to close.
        /// </summary>
        internal static int DismissOnce()
        {
            int closed = 0;

            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd)) return true;
                if (!string.Equals(GetClassNameOf(hwnd), DialogClass, StringComparison.Ordinal)) return true;
                if (!IsHpLauncherWindow(hwnd)) return true;
                if (!MentionsHpEasyScan(hwnd)) return true;

                PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                closed++;
                return true;
            }, IntPtr.Zero);

            return closed;
        }

        private static bool IsHpLauncherWindow(IntPtr hwnd)
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return false;

            try
            {
                using var process = Process.GetProcessById((int)pid);
                if (!string.Equals(process.ProcessName, LauncherProcess, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                // When the path is readable, insist on HP's own folder so an unrelated
                // process that happens to share the name is never touched.
                try
                {
                    var path = process.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(path))
                    {
                        return path.Contains(HpInstallFolder, StringComparison.OrdinalIgnoreCase);
                    }
                }
                catch
                {
                    // Access denied on another user's process: the name match is enough.
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool MentionsHpEasyScan(IntPtr hwnd)
        {
            if (GetWindowTextOf(hwnd).Contains(DialogPhrase, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            bool found = false;
            EnumChildWindows(hwnd, (child, _) =>
            {
                if (GetWindowTextOf(child).Contains(DialogPhrase, StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    return false;
                }
                return true;
            }, IntPtr.Zero);

            return found;
        }

        private static string GetClassNameOf(IntPtr hwnd)
        {
            var buffer = new StringBuilder(256);
            GetClassName(hwnd, buffer, buffer.Capacity);
            return buffer.ToString();
        }

        /// <summary>
        /// GetWindowText cannot read another process's controls, so the text is pulled with
        /// a WM_GETTEXT that gives up if the target window is hung.
        /// </summary>
        private static string GetWindowTextOf(IntPtr hwnd)
        {
            var buffer = new StringBuilder(1024);
            SendMessageTimeout(hwnd, WM_GETTEXT, (IntPtr)buffer.Capacity, buffer, SMTO_ABORTIFHUNG, 100, out _);
            return buffer.ToString();
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            _stop.Set();
            _thread?.Join(TimeSpan.FromSeconds(2));
            _thread = null;
            _stop.Dispose();
        }

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr hwnd,
            int message,
            IntPtr wParam,
            StringBuilder lParam,
            int flags,
            int timeout,
            out IntPtr result);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
    }
}
