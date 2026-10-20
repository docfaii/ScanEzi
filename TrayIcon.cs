using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace ScanEzi
{
    /// <summary>
    /// Notification-area icon backed by Shell_NotifyIcon.
    ///
    /// The icon is what keeps the process alive and reachable once the window is hidden:
    /// the scan-button listener and the HP dialog guard are process-wide, so as long as this
    /// icon is present a press on the scanner still starts a scan.
    ///
    /// No WinForms or third-party tray component is used - the shell API needs only a
    /// message-only window to receive its callbacks.
    /// </summary>
    public sealed class TrayIcon : IDisposable
    {
        private const int WM_TRAYICON = 0x8000 + 1; // WM_APP + 1
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_LBUTTONDBLCLK = 0x0203;
        private const int WM_RBUTTONUP = 0x0205;
        private const int WM_CONTEXTMENU = 0x007B;
        private const int NIN_SELECT = 0x0400;
        private const int NIN_KEYSELECT = 0x0401;
        private const int NIN_BALLOONUSERCLICK = 0x0405;

        private const int NIM_ADD = 0x00000000;
        private const int NIM_MODIFY = 0x00000001;
        private const int NIM_DELETE = 0x00000002;

        private const int NIF_MESSAGE = 0x00000001;
        private const int NIF_ICON = 0x00000002;
        private const int NIF_TIP = 0x00000004;
        private const int NIF_INFO = 0x00000010;
        private const int NIF_SHOWTIP = 0x00000080;

        private const int NIIF_INFO = 0x00000001;
        private const int NIIF_NOSOUND = 0x00000010;

        private static readonly IntPtr HWND_MESSAGE = new(-3);
        private static readonly int TaskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        private readonly int _iconId = Environment.ProcessId;
        private HwndSource? _source;
        private IntPtr _icon;
        private bool _iconAdded;
        private bool _disposed;
        private string _tooltip = "ScanEzi";

        /// <summary>Left click, double click or balloon click: the caller should show the window.</summary>
        public event Action? Activated;

        /// <summary>Right click: the caller-supplied menu is shown at the cursor.</summary>
        public ContextMenu? Menu { get; set; }

        public bool IsVisible => _iconAdded;

        public void Show(string tooltip)
        {
            if (_disposed) return;

            _tooltip = tooltip;

            if (_source == null)
            {
                var parameters = new HwndSourceParameters("ScanEziTrayWindow")
                {
                    ParentWindow = HWND_MESSAGE,
                    WindowStyle = 0,
                    Width = 0,
                    Height = 0
                };

                _source = new HwndSource(parameters);
                _source.AddHook(WndProc);
                _icon = LoadAppIcon();
            }

            AddIcon();
        }

        public void SetTooltip(string tooltip)
        {
            if (_disposed) return;
            _tooltip = tooltip;
            if (!_iconAdded) return;
            Notify(NIM_MODIFY, NIF_TIP | NIF_SHOWTIP);
        }

        /// <summary>Shows a tray balloon. Explorer ignores these when notifications are off.</summary>
        public void Notify(string title, string message)
        {
            if (_disposed || !_iconAdded) return;

            var data = CreateData();
            data.uFlags = NIF_INFO | NIF_TIP | NIF_SHOWTIP;
            data.szInfoTitle = Truncate(title, 63);
            data.szInfo = Truncate(message, 255);
            data.dwInfoFlags = NIIF_INFO | NIIF_NOSOUND;
            data.uTimeoutOrVersion = 10000;

            if (!Shell_NotifyIcon(NIM_MODIFY, ref data))
            {
                ScanLog.Write($"tray balloon rejected ({Marshal.GetLastWin32Error()}): {title}");
            }
        }

        public void Hide()
        {
            if (!_iconAdded) return;
            Notify(NIM_DELETE, 0);
            _iconAdded = false;
        }

        private void AddIcon()
        {
            var data = CreateData();
            data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
            data.uCallbackMessage = WM_TRAYICON;
            data.hIcon = _icon;

            _iconAdded = Shell_NotifyIcon(NIM_ADD, ref data);
            if (!_iconAdded)
            {
                ScanLog.Write($"tray icon add failed ({Marshal.GetLastWin32Error()})");
            }
        }

        private void Notify(int message, int flags)
        {
            var data = CreateData();
            data.uFlags = flags;
            Shell_NotifyIcon(message, ref data);
        }

        private NOTIFYICONDATAW CreateData() => new()
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _source?.Handle ?? IntPtr.Zero,
            uID = _iconId,
            hIcon = _icon,
            szTip = Truncate(_tooltip, 127),
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == TaskbarCreatedMessage && TaskbarCreatedMessage != 0)
            {
                // Explorer restarted and forgot every icon; put ours back.
                _iconAdded = false;
                AddIcon();
                handled = true;
                return IntPtr.Zero;
            }

            if (msg != WM_TRAYICON)
            {
                return IntPtr.Zero;
            }

            switch (lParam.ToInt32() & 0xFFFF)
            {
                case WM_LBUTTONUP:
                case NIN_SELECT:
                case NIN_KEYSELECT:
                case NIN_BALLOONUSERCLICK:
                    Activated?.Invoke();
                    handled = true;
                    break;

                case WM_LBUTTONDBLCLK:
                    Activated?.Invoke();
                    handled = true;
                    break;

                case WM_RBUTTONUP:
                case WM_CONTEXTMENU:
                    ShowMenu();
                    handled = true;
                    break;
            }

            return IntPtr.Zero;
        }

        private void ShowMenu()
        {
            var menu = Menu;
            if (menu == null) return;

            menu.Placement = PlacementMode.MousePoint;
            menu.IsOpen = true;
        }

        private static IntPtr LoadAppIcon()
        {
            try
            {
                string? path = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(path))
                {
                    var small = new IntPtr[1];
                    if (ExtractIconEx(path, 0, new IntPtr[1], small, 1) > 0 && small[0] != IntPtr.Zero)
                    {
                        return small[0];
                    }
                }
            }
            catch
            {
                // Fall through to the generic application icon.
            }

            return LoadIcon(IntPtr.Zero, new IntPtr(32512)); // IDI_APPLICATION
        }

        private static string Truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Length <= max ? value : value[..max];
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Hide();

            if (_source != null)
            {
                _source.RemoveHook(WndProc);
                _source.Dispose();
                _source = null;
            }

            if (_icon != IntPtr.Zero)
            {
                DestroyIcon(_icon);
                _icon = IntPtr.Zero;
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATAW
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public int uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;

            public int dwState;
            public int dwStateMask;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;

            public int uTimeoutOrVersion;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;

            public int dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATAW data);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, uint count);

        [DllImport("user32.dll")]
        private static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr icon);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegisterWindowMessage(string message);
    }
}
