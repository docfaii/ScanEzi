using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Wpf.Ui.Appearance;

namespace ScanEzi
{
    public partial class App : Application
    {
        /// <summary>
        /// One listener per machine: a second launch would register the scan-button callback a
        /// second time and leave an orphaned tray icon behind.
        /// </summary>
        private const string InstanceMutexName = @"Local\ScanEzi.SingleInstance";

        /// <summary>Set by a second launch to ask the running instance to show its window.</summary>
        private const string ActivateEventName = @"Local\ScanEzi.Activate";

        private Mutex? _instanceMutex;
        private EventWaitHandle? _activateEvent;
        private RegisteredWaitHandle? _activateWait;

        /// <summary>
        /// The navy from the app icon. Windows accent colours vary per machine, so the
        /// brand colour is applied explicitly to keep buttons and switches on-palette.
        /// </summary>
        private static readonly Color BrandAccent = Color.FromRgb(0x1E, 0x6F, 0xA8);

        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ScanEzi",
            "crash.log");

        protected override void OnStartup(StartupEventArgs e)
        {
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
                Log(args.ExceptionObject as Exception);

            DispatcherUnhandledException += (s, args) =>
            {
                Log(args.Exception);
                MessageBox.Show(
                    "ScanEzi hit an unexpected error:\n\n" + args.Exception.Message,
                    "ScanEzi Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                args.Handled = true;
            };

            // A second launch hands its request to the instance already in the tray and exits,
            // so there is never more than one scan-button listener.
            _instanceMutex = new Mutex(true, InstanceMutexName, out bool isFirstInstance);
            if (!isFirstInstance)
            {
                // This instance never owns the mutex, so OnExit must not release it.
                _instanceMutex.Dispose();
                _instanceMutex = null;
                SignalRunningInstance();
                Shutdown();
                return;
            }

            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            _activateWait = ThreadPool.RegisterWaitForSingleObject(
                _activateEvent,
                (state, timedOut) => Dispatcher.BeginInvoke(new Action(() => ActivateMainWindow())),
                null,
                Timeout.Infinite,
                executeOnlyOnce: false);

            base.OnStartup(e);

            ApplicationAccentColorManager.Apply(BrandAccent, ApplicationTheme.Light, false, false);
        }

        /// <summary>
        /// True once Windows is logging off or shutting down: the window must not treat that as
        /// "close to tray" or the session would hang waiting for ScanEzi.
        /// </summary>
        public static bool IsShuttingDown { get; private set; }

        protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
        {
            IsShuttingDown = true;
            base.OnSessionEnding(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {            _activateWait?.Unregister(null);
            _activateEvent?.Dispose();
            _instanceMutex?.ReleaseMutex();
            _instanceMutex?.Dispose();

            base.OnExit(e);
        }

        private static void SignalRunningInstance()
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var running))
                {
                    using (running)
                    {
                        running.Set();
                    }
                }
            }
            catch
            {
                // The other instance is going away; there is nothing useful to do here.
            }
        }

        /// <summary>
        /// Brings the window back from the tray. The window may not exist yet if the second
        /// launch arrives during startup, so retry briefly instead of giving up.
        /// </summary>
        private void ActivateMainWindow(int attempt = 0)
        {
            if (MainWindow is ScanEzi.MainWindow window)
            {
                window.ShowFromTray();
                return;
            }

            if (attempt >= 20) return;

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            timer.Tick += (s, args) =>
            {
                timer.Stop();
                ActivateMainWindow(attempt + 1);
            };
            timer.Start();
        }

        private static void Log(Exception? ex)
        {
            if (ex == null) return;
            try
            {
                var dir = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(LogPath, DateTime.Now + Environment.NewLine + ex + Environment.NewLine + Environment.NewLine);
            }
            catch { }
        }
    }
}