using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace ScanEzi
{
    /// <summary>
    /// Receives the scanner's physical Scan button press.
    ///
    /// WIA exposes two objects that look interchangeable but are not:
    ///   * WIA.DeviceManager (wiaaut.dll) is IDispatch-only. It can enumerate devices and
    ///     call RegisterEvent, but that only tells WIA which external app to launch - it
    ///     never delivers a callback into this process.
    ///   * WiaDevMgr (wiaservc.dll) implements the IWiaDevMgr vtable, the only API that can
    ///     deliver an in-process IWiaEventCallback.
    ///
    /// Event selection matters just as much: most scanners do not raise the generic
    /// WIA_EVENT_SCAN_IMAGE. The HP Scanjet 3000 s2, for example, exposes the button as a
    /// vendor event ("Scan button press", {FFAF8DDA-...}) on a second device instance, so
    /// the button events are discovered per device instead of assumed.
    /// </summary>
    public sealed class ScannerButtonService : IDisposable
    {
        /// <summary>WIA_EVENT_SCAN_IMAGE - the generic hardware scan-button event.</summary>
        private static readonly Guid WiaEventScanImage =
            new Guid("A6C5A715-8C6E-11D2-977A-0000F87A926F");

        /// <summary>Vendor "Scan button press" event used by HP Scanjet devices.</summary>
        private static readonly Guid WiaEventScanButtonPress =
            new Guid("FFAF8DDA-BE84-401A-B112-5C4DB66F3910");

        private const int WiaRegisterEventCallback = 0x1;
        private const int WiaUnregisterEventCallback = 0x2;

        private readonly object _sync = new();
        private readonly List<Registration> _registrations = new();

        private IWiaDevMgr? _deviceManager;
        private DeviceEventCallback? _callback;
        private Dispatcher? _dispatcher;
        private Action? _onScanButtonPressed;
        private DateTime _lastFireUtc = DateTime.MinValue;
        private bool _disposed;

        /// <summary>Raised with a human-readable note whenever listener state changes.</summary>
        public event Action<string>? StatusChanged;

        public bool IsRegistered
        {
            get { lock (_sync) return _registrations.Count > 0; }
        }

        public string? LastError { get; private set; }

        /// <summary>Event GUIDs the listener is currently subscribed to.</summary>
        public IReadOnlyList<Guid> SubscribedEvents
        {
            get
            {
                lock (_sync)
                {
                    var list = new List<Guid>();
                    foreach (var r in _registrations) list.Add(r.EventGuid);
                    return list;
                }
            }
        }

        public void Initialize(Dispatcher dispatcher, Action onScanButtonPressed)
        {
            _dispatcher = dispatcher;
            _onScanButtonPressed = onScanButtonPressed;
        }

        /// <summary>
        /// Enumerates WIA devices and registers the callback on every scan-button event they
        /// advertise. Runs off the UI thread because enumeration can block on a slow driver.
        /// </summary>
        public Task StartAsync() => Task.Run(RegisterAllDevices);

        private void RegisterAllDevices()
        {
            if (_disposed) return;

            Log($"Button service starting (build {typeof(ScannerButtonService).Assembly.GetName().Version}).");

            try
            {
                _callback ??= new DeviceEventCallback(this);

                if (_deviceManager == null)
                {
                    // The coclass, not the WIA.DeviceManager automation ProgID.
                    var clsid = new Guid("a1f4e726-8cf1-11d1-bf92-0060081ed811");
                    var type = Type.GetTypeFromCLSID(clsid);
                    if (type == null)
                    {
                        Fail("WiaDevMgr is not registered on this system.");
                        return;
                    }

                    _deviceManager = (IWiaDevMgr)Activator.CreateInstance(type)!;
                }

                var devices = EnumerateDevices();
                if (devices.Count == 0)
                {
                    Fail("No WIA devices found to listen on.");
                    return;
                }

                int registered = 0;
                int candidates = 0;
                foreach (var device in devices)
                {
                    foreach (Guid eventGuid in device.ButtonEvents)
                    {
                        candidates++;
                        if (TryRegister(device.DeviceId, eventGuid)) registered++;
                    }
                }

                if (candidates == 0)
                {
                    Fail("No scan-button event found on any connected scanner.");
                    return;
                }

                if (registered == 0)
                {
                    Fail("The scanner rejected every button-event registration.");
                    return;
                }

                LastError = null;
                Report($"Scanner button listener active ({registered} event(s)).");
            }
            catch (Exception ex)
            {
                Fail($"Button listener failed: {ex.Message}");
            }
        }

        private bool TryRegister(string deviceId, Guid eventGuid)
        {
            lock (_sync)
            {
                foreach (var existing in _registrations)
                {
                    if (string.Equals(existing.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)
                        && existing.EventGuid == eventGuid)
                    {
                        return true;
                    }
                }
            }

            try
            {
                var manager = _deviceManager!;
                Guid guid = eventGuid;

                manager.RegisterEventCallbackInterface(
                    WiaRegisterEventCallback,
                    deviceId,
                    in guid,
                    _callback!,
                    out object eventObject);

                lock (_sync)
                {
                    _registrations.Add(new Registration(deviceId, eventGuid, eventObject));
                }

                Log($"Button listener registered: {deviceId} / {eventGuid}");
                return true;
            }
            catch (Exception ex)
            {
                Log($"Button registration skipped for {deviceId} / {eventGuid}: 0x{ex.HResult:X8} {ex.Message}");
                return false;
            }
        }

        private static List<DeviceEntry> EnumerateDevices()
        {
            var devices = new List<DeviceEntry>();
            try
            {
                var type = Type.GetTypeFromProgID("WIA.DeviceManager");
                if (type == null) return devices;

                dynamic manager = Activator.CreateInstance(type)!;
                dynamic infos = manager.DeviceInfos;
                int count = (int)infos.Count;

                for (int i = 1; i <= count; i++)
                {
                    try
                    {
                        dynamic info = infos.Item(i);
                        string? id = (string?)info.DeviceID;
                        if (string.IsNullOrEmpty(id)) continue;

                        devices.Add(new DeviceEntry(id!, GetButtonEvents(info)));
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Device {i} enumeration failed: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WIA enumeration failed: {ex.Message}");
            }

            return devices;
        }

        /// <summary>
        /// Returns the scan-button events a single device advertises. Devices are probed
        /// rather than assumed, because the button is a vendor event on most HP hardware.
        /// </summary>
        private static List<Guid> GetButtonEvents(dynamic info)
        {
            var events = new List<Guid>();
            try
            {
                dynamic device = info.Connect();
                dynamic wiaEvents = device.Events;
                int count = (int)wiaEvents.Count;

                for (int i = 1; i <= count; i++)
                {
                    try
                    {
                        dynamic wiaEvent = wiaEvents.Item(i);
                        string eventId = (string)wiaEvent.EventID;
                        string name = string.Empty;
                        try { name = (string)wiaEvent.Name; } catch { }
                        if (string.IsNullOrEmpty(name))
                        {
                            try { name = (string)wiaEvent.Description; } catch { }
                        }

                        if (!IsScanButtonEvent(eventId, name)) continue;
                        if (Guid.TryParse(eventId, out Guid guid) && !events.Contains(guid))
                            events.Add(guid);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Event {i} probe failed: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Event enumeration failed: {ex.Message}");
            }

            return events;
        }

        private static bool IsScanButtonEvent(string eventId, string name)
        {
            if (Guid.TryParse(eventId, out Guid guid))
            {
                if (guid == WiaEventScanImage) return true;
                if (guid == WiaEventScanButtonPress) return true;
            }

            // Catches vendor events we do not have a GUID constant for yet.
            return !string.IsNullOrEmpty(name)
                && name.Contains("scan button", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Called by the COM callback wrapper; may arrive on any thread.</summary>
        internal void OnHardwareEvent(string deviceId, string description)
        {
            if (_disposed) return;

            // One press can surface on more than one device instance; collapse duplicates.
            DateTime now = DateTime.UtcNow;
            if ((now - _lastFireUtc).TotalMilliseconds < 1500) return;
            _lastFireUtc = now;

            Log($"Scanner button pressed: device={deviceId} description={description}");

            var dispatcher = _dispatcher;
            var handler = _onScanButtonPressed;
            if (dispatcher == null || handler == null) return;

            dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    handler();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Scan button handler threw: {ex.Message}");
                }
            }));
        }

        private void Report(string message) =>
            _dispatcher?.BeginInvoke(new Action(() => StatusChanged?.Invoke(message)));

        private void Fail(string message)
        {
            LastError = message;
            Log("ERROR " + message);
            Report(message);
        }

        private static string LogPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ScanEzi", "button-service.log");

        /// <summary>Diagnostic trace; a Release build has no debugger to observe.</summary>
        internal static void Log(string message)
        {
            System.Diagnostics.Debug.WriteLine(message);
            try
            {
                string path = LogPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
            catch
            {
                // Diagnostics must never break scanning.
            }
        }

        /// <summary>Releases every registration. Safe to call more than once.</summary>
        public void Cleanup()
        {
            if (_disposed) return;

            List<Registration> pending;
            lock (_sync)
            {
                pending = new List<Registration>(_registrations);
                _registrations.Clear();
            }

            if (_deviceManager != null && _callback != null)
            {
                foreach (var registration in pending)
                {
                    try
                    {
                        Guid guid = registration.EventGuid;
                        _deviceManager.RegisterEventCallbackInterface(
                            WiaUnregisterEventCallback,
                            registration.DeviceId,
                            in guid,
                            _callback,
                            out _);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"Button unregister failed for {registration.DeviceId}: {ex.Message}");
                    }
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            Cleanup();
            _disposed = true;
            _deviceManager = null;
            _callback = null;
        }

        private sealed record DeviceEntry(string DeviceId, List<Guid> ButtonEvents);

        private sealed record Registration(string DeviceId, Guid EventGuid, object EventObject);
    }

    /// <summary>
    /// Managed implementation of IWiaEventCallback (IID ae6287b0-...).
    ///
    /// This must NOT be [ComImport] - the runtime refuses to let managed code implement a
    /// ComImport interface. The Guid attribute alone is enough for the callable wrapper to
    /// advertise the correct IID to WIA.
    /// </summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class DeviceEventCallback : IWiaEventCallback
    {
        private readonly ScannerButtonService _owner;

        public DeviceEventCallback(ScannerButtonService owner) => _owner = owner;

        public void ImageEventCallback(
            in Guid pEventGUID,
            [MarshalAs(UnmanagedType.BStr)] string bstrEventDescription,
            [MarshalAs(UnmanagedType.BStr)] string bstrDeviceID,
            [MarshalAs(UnmanagedType.BStr)] string bstrDeviceDescription,
            uint dwDeviceType,
            [MarshalAs(UnmanagedType.BStr)] string bstrFullItemName,
            ref uint pulEventType,
            uint ulReserved)
        {
            _owner.OnHardwareEvent(bstrDeviceID ?? string.Empty, bstrEventDescription ?? string.Empty);
        }
    }

    [Guid("ae6287b0-0084-11d2-973b-00a0c9068f2e")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IWiaEventCallback
    {
        void ImageEventCallback(
            in Guid pEventGUID,
            [MarshalAs(UnmanagedType.BStr)] string bstrEventDescription,
            [MarshalAs(UnmanagedType.BStr)] string bstrDeviceID,
            [MarshalAs(UnmanagedType.BStr)] string bstrDeviceDescription,
            uint dwDeviceType,
            [MarshalAs(UnmanagedType.BStr)] string bstrFullItemName,
            ref uint pulEventType,
            uint ulReserved);
    }

    /// <summary>
    /// IWiaDevMgr vtable. Method order and arity must match wiaservc.h exactly even for the
    /// members this app never calls, because vtable slots are resolved by position.
    /// </summary>
    [ComImport, Guid("5eb2502a-8cf1-11d1-bf92-0060081ed811")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IWiaDevMgr
    {
        IntPtr EnumDeviceInfo(int lFlag);

        IntPtr CreateDevice([MarshalAs(UnmanagedType.BStr)] string bstrDeviceID);

        IntPtr SelectDeviceDlg(IntPtr hwndParent, int lDeviceType, int lFlags, ref IntPtr pbstrDeviceID);

        IntPtr SelectDeviceDlgID(IntPtr hwndParent, int lDeviceType, int lFlags);

        void GetImageDlg(
            IntPtr hwndParent,
            int lDeviceType,
            int lFlags,
            int lIntent,
            IntPtr pItemRoot,
            [MarshalAs(UnmanagedType.BStr)] string bstrFilename,
            ref Guid pguidFormat);

        void RegisterEventCallbackProgram(
            int lFlags,
            [MarshalAs(UnmanagedType.BStr)] string bstrDeviceID,
            in Guid pEventGUID,
            [MarshalAs(UnmanagedType.BStr)] string bstrCommandLine,
            [MarshalAs(UnmanagedType.BStr)] string bstrName,
            [MarshalAs(UnmanagedType.BStr)] string bstrDescription,
            [MarshalAs(UnmanagedType.BStr)] string bstrIcon);

        void RegisterEventCallbackInterface(
            int lFlags,
            [MarshalAs(UnmanagedType.BStr)] string bstrDeviceID,
            in Guid pEventGUID,
            IWiaEventCallback pIWiaEventCallback,
            [MarshalAs(UnmanagedType.IUnknown)] out object pEventObject);

        void RegisterEventCallbackCLSID(
            int lFlags,
            [MarshalAs(UnmanagedType.BStr)] string bstrDeviceID,
            in Guid pEventGUID,
            in Guid pClsID,
            [MarshalAs(UnmanagedType.BStr)] string bstrName,
            [MarshalAs(UnmanagedType.BStr)] string bstrDescription,
            [MarshalAs(UnmanagedType.BStr)] string bstrIcon);

        void AddDeviceDlg(IntPtr hwndParent, int lFlags);
    }
}
