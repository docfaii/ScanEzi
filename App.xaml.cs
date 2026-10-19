using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Wpf.Ui.Appearance;

namespace ScanEzi
{
    public partial class App : Application
    {
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

            base.OnStartup(e);

            ApplicationAccentColorManager.Apply(BrandAccent, ApplicationTheme.Light, false, false);
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