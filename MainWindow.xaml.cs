using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Devices.Enumeration;
using Microsoft.Win32;

namespace ScanEzi
{
    public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
    {
        private readonly ObservableCollection<ScannedImage> _scannedImages = new();
        private readonly ScannerService _scannerService = new();
        private readonly ScannerButtonService _buttonService = new();
        private readonly ImageProcessor _imageProcessor = new();
        private AppSettings _settings;
        private readonly UpdateService _updateService = new();
        private IReadOnlyList<DeviceInformation> _availableScanners = new List<DeviceInformation>();
        private bool _uiReady;
        private bool _applyingSettings;
        private bool _checkingForUpdates;
        private UpdateInfo? _pendingUpdate;
        private string? _preparedUpdatePath;
        private int _capabilityRequestId;
        private readonly Dictionary<ScannedImage, PageTile> _pageTiles = new();
        private readonly HpEasyScanDialogGuard _hpDialogGuard = new();
        private readonly TrayIcon _tray = new();
        private bool _exitRequested;
        private bool _trayNoticeShown;
        private string _buttonListenerStatus = "Scan button starting";
        private string? _lastTrayTooltip;

        /// <summary>
        /// The sides preference the user chose, independent of whether the currently
        /// selected device can honour it. Kept separately so that plugging in (or
        /// selecting) a simplex device does not wipe the saved 2-sided preference.
        /// </summary>
        private bool _preferredDuplex;

        public MainWindow()
        {
            InitializeComponent();
            _settings = AppSettings.Load();
            _preferredDuplex = _settings.DefaultDuplex;
            _uiReady = true;

            VersionText.Text = UpdateService.IsInstalled
                ? $"ScanEzi {UpdateService.CurrentVersion} (installed)"
                : $"ScanEzi {UpdateService.CurrentVersion} (portable)";
            UpdateStatusText.Text = UpdateService.IsInstalled
                ? "Updates download from GitHub Releases and install automatically."
                : "Portable copy \u2014 updates download from GitHub Releases.";

            Loaded += async (s, e) =>
            {
                ApplyLoadedSettings();
                await RefreshScannersAsync();

                // HP's own button launcher shows an "HP Easy Scan is not installed" error when
                // the front-panel button is pressed; keep that dialog off the screen.
                _hpDialogGuard.Start();

                // Listen for the physical Scan button. The service marshals the callback
                // onto the UI thread before invoking the handler.
                _buttonService.StatusChanged += OnButtonServiceStatusChanged;
                _buttonService.Initialize(Dispatcher, OnHardwareScanButtonPressed);

                // The tray icon is what keeps this process - and therefore the listener -
                // alive once the window is closed.
                StartTrayIcon();

                await _buttonService.StartAsync();

                if (_settings.AutoUpdate)
                {
                    await CheckForUpdatesAsync(manual: false);
                }
            };

            Closing += (s, e) =>
            {
                // Closing is the user's way of putting ScanEzi in the tray; the listener has to
                // stay alive for the scanner's Scan button to keep working.
                if (_exitRequested || App.IsShuttingDown || !_settings.MinimizeToTray)
                {
                    return;
                }

                e.Cancel = true;
                HideToTray();
            };

            Closed += (s, e) =>
            {
                _buttonService.StatusChanged -= OnButtonServiceStatusChanged;
                _buttonService.Dispose();
                _hpDialogGuard.Dispose();
                _tray.Dispose();
            };

            // The gallery starts empty, so the save action stays greyed out until a page lands.
            UpdateSaveButtonState();
        }

        private void OnHardwareScanButtonPressed()
        {
            if (QuickScanButton.IsEnabled)
            {
                ScanButton_Click(QuickScanButton, new RoutedEventArgs());
            }
        }

        private void OnButtonServiceStatusChanged(string message)
        {
            _buttonListenerStatus = _buttonService.LastError != null
                ? "Scan button unavailable"
                : "Scan button ready";

            if (ButtonListenerText != null)
            {
                ButtonListenerText.Text = message;
            }

            // A successful registration is not worth replacing the ready/idle status text;
            // only a failure needs to reach the user.
            if (_buttonService.LastError != null)
            {
                StatusText.Text = message;
            }

            UpdateTrayStatus();
        }

        private void StartTrayIcon()
        {
            _tray.Menu = BuildTrayMenu();
            _tray.Activated += ShowFromTray;
            _tray.Show(TrayTooltip());
        }

        private ContextMenu BuildTrayMenu()
        {
            var menu = new ContextMenu();

            var open = new MenuItem { Header = "Open ScanEzi", FontWeight = FontWeights.SemiBold };
            open.Click += (s, e) => ShowFromTray();
            menu.Items.Add(open);

            var scan = new MenuItem { Header = "Scan now" };
            scan.Click += (s, e) => ScanFromTray();
            menu.Items.Add(scan);

            menu.Items.Add(new Separator());

            var quit = new MenuItem { Header = "Quit ScanEzi" };
            quit.Click += (s, e) => QuitFromTray();
            menu.Items.Add(quit);

            menu.Opened += (s, e) => scan.IsEnabled = QuickScanButton.IsEnabled;
            return menu;
        }

        /// <summary>Hides the window without ending the process, so the listener keeps running.</summary>
        private void HideToTray()
        {
            Hide();

            if (_trayNoticeShown)
            {
                return;
            }

            _trayNoticeShown = true;
            _tray.Notify(
                "ScanEzi is still running",
                "Press Scan on the scanner, or open ScanEzi from this icon. Use Quit in its menu to close the app.");
        }

        /// <summary>Brings the window back from the tray. Also called by a second launch.</summary>
        public void ShowFromTray()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(ShowFromTray));
                return;
            }

            Show();
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            Activate();

            // A window restored from the tray can still sit behind the foreground app.
            Topmost = true;
            Topmost = false;
            Focus();
        }

        private void ScanFromTray()
        {
            if (QuickScanButton.IsEnabled)
            {
                ScanButton_Click(QuickScanButton, new RoutedEventArgs());
            }
        }

        private void QuitFromTray()
        {
            _exitRequested = true;
            Close();
        }

        private void TrayToggle_Changed(object sender, RoutedEventArgs e)
        {
            // XAML parses the switch before the constructor has loaded the settings file.
            if (_settings == null)
            {
                return;
            }

            _settings.MinimizeToTray = TrayToggle.IsChecked == true;
            if (!_applyingSettings)
            {
                _settings.Save();
            }
        }

        private void UpdateTrayStatus()
        {
            string tooltip = TrayTooltip();
            if (string.Equals(tooltip, _lastTrayTooltip, StringComparison.Ordinal))
            {
                return;
            }

            _lastTrayTooltip = tooltip;
            _tray.SetTooltip(tooltip);
        }

        private string TrayTooltip()
        {
            int pages = _scannedImages.Count;
            string pagesText = pages == 1 ? "1 page" : $"{pages} pages";
            return $"ScanEzi {UpdateService.CurrentVersion} \u00B7 {_buttonListenerStatus} \u00B7 {pagesText}";
        }

        private void ApplyLoadedSettings()
        {
            _applyingSettings = true;
            try
            {
                foreach (ComboBoxItem item in PaperSizeComboBox.Items)
                {
                    if (item.Content?.ToString() == _settings.DefaultPaperSize)
                    {
                        PaperSizeComboBox.SelectedItem = item;
                        break;
                    }
                }

                DuplexComboBox.SelectedIndex = _preferredDuplex ? 1 : 0;

                FormatComboBox.SelectedIndex = string.Equals(_settings.DefaultFormat, "jpg", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

                DiscardBlankToggle.IsChecked = _settings.DiscardBlankPages;
                AutoOrientToggle.IsChecked = _settings.AutoOrientPages;
                AutoUpdateToggle.IsChecked = _settings.AutoUpdate;
                TrayToggle.IsChecked = _settings.MinimizeToTray;

                foreach (ComboBoxItem item in SourceComboBox.Items)
                {
                    if (item.Content?.ToString() == _settings.DefaultSource)
                    {
                        SourceComboBox.SelectedItem = item;
                        break;
                    }
                }
            }
            finally
            {
                _applyingSettings = false;
            }

            UpdateBadges();
            UpdateSaveButtonLabel();
        }

        private async Task RefreshScannersAsync()
        {
            try
            {
                StatusText.Text = "Searching for scanners...";
                _availableScanners = await _scannerService.GetScannersAsync();

                _applyingSettings = true;
                try
                {
                    ScannerComboBox.Items.Clear();

                    if (_availableScanners.Count == 0)
                    {
                        ScannerComboBox.Items.Add(new ComboBoxItem { Content = "No scanners found" });
                        ScannerComboBox.IsEnabled = false;
                        StatusText.Text = "No scanners connected.";
                    }
                    else
                    {
                        int selectedIndex = 0;
                        for (int i = 0; i < _availableScanners.Count; i++)
                        {
                            var scanner = _availableScanners[i];
                            ScannerComboBox.Items.Add(new ComboBoxItem
                            {
                                Content = scanner.Name,
                                Tag = scanner.Id
                            });

                            if (_settings.DefaultScannerId == scanner.Id)
                            {
                                selectedIndex = i;
                            }
                        }

                        ScannerComboBox.IsEnabled = true;
                        ScannerComboBox.SelectedIndex = selectedIndex;
                        StatusText.Text = $"Ready \u00B7 {_availableScanners.Count} scanner(s) available";
                    }
                }
                finally
                {
                    _applyingSettings = false;
                }

                UpdateActiveDevice();
                await ApplyCapabilitiesAsync();
            }
            catch (Exception ex)
            {
                StatusText.Text = "Error loading scanners.";
                ActiveDeviceText.Text = "Scanner error";
                ShowBanner(true, $"Failed to refresh scanners: {ex.Message}");
            }
        }

        private async void RefreshScannerButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshScannersAsync();
        }

        private void SettingsToggleButton_Click(object sender, RoutedEventArgs e)
        {
            SettingsPanel.Visibility = SettingsPanel.Visibility == Visibility.Visible 
                ? Visibility.Collapsed 
                : Visibility.Visible;
        }

        private void RepoLink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ScanLog.Write($"Failed to open repository link: {ex.Message}");
            }

            e.Handled = true;
        }

        private async void ScannerComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_uiReady) return;

            // Repopulating the list during a refresh raises this event too; the refresh applies
            // the capabilities itself once the new list is in place.
            if (_applyingSettings) return;

            UpdateActiveDevice();
            PersistSettings();
            await ApplyCapabilitiesAsync();
        }

        /// <summary>
        /// Disables the options the selected device cannot do, so the UI never offers
        /// an impossible combination (e.g. duplex on a flatbed-only scanner).
        /// </summary>
        private async Task ApplyCapabilitiesAsync()
        {
            if (!(ScannerComboBox.SelectedItem is ComboBoxItem item) || item.Tag == null) return;

            var scannerId = item.Tag.ToString();
            if (string.IsNullOrEmpty(scannerId)) return;

            // Capability reads are asynchronous, so a slow answer for a previously selected
            // device must not overwrite the options of the device now in the list.
            int requestId = ++_capabilityRequestId;
            var caps = await _scannerService.GetCapabilitiesAsync(scannerId);
            if (requestId != _capabilityRequestId) return;

            if (!caps.Reported)
            {
                // No answer is not the same as "no features". Leaving everything enabled keeps
                // the settings usable instead of greyed out until Refresh is pressed.
                ScanLog.Write($"Capabilities unavailable for '{scannerId}'; leaving all options enabled.");
                CapabilityText.Text = "Couldn't read this device's capabilities \u2014 all options remain available.";
                ApplyCapabilityState(null);
                UpdateBadges();
                return;
            }

            var traits = new List<string>();
            if (caps.HasFeeder) traits.Add(caps.CanDuplex ? "duplex feeder" : "simplex feeder");
            if (caps.HasFlatbed) traits.Add("flatbed");
            CapabilityText.Text = traits.Count > 0
                ? "This device supports: " + string.Join(", ", traits) + "."
                : "This device did not report its capabilities.";

            bool duplexAvailable = caps.HasFeeder && caps.CanDuplex;
            ApplyCapabilityState(caps);

            int desired = SourceComboBox.SelectedIndex;
            int corrected = desired;
            if (desired == 1 && !caps.HasFeeder) corrected = caps.HasFlatbed ? 2 : 0;
            else if (desired == 2 && !caps.HasFlatbed) corrected = caps.HasFeeder ? 1 : 0;

            if (corrected != desired)
            {
                SourceComboBox.SelectedIndex = corrected;
                var replaced = desired == 2 ? "Flatbed" : "Document Feeder";
                StatusText.Text = $"This device has no {replaced.ToLowerInvariant()} \u2014 switched to {GetSelectedSource()}.";
            }

            UpdateBadges();
        }

        /// <summary>
        /// Sets the enabled state of the source-dependent options. A null capability set means
        /// the device could not be queried, in which case every option stays available.
        /// </summary>
        private void ApplyCapabilityState(ScannerCapabilities? caps)
        {
            bool wasApplyingSettings = _applyingSettings;
            _applyingSettings = true;
            try
            {
                DuplexComboBox.IsEnabled = caps == null || (caps.HasFeeder && caps.CanDuplex);

                if (caps != null)
                {
                    // An explicit per-device choice wins; otherwise a duplex feeder starts 2-sided.
                    _preferredDuplex = _settings.GetDuplex(CurrentScannerId, caps.HasFeeder && caps.CanDuplex);

                    // Restore the user's preference when the device can honour it, and fall back
                    // to 1-sided display otherwise -- without overwriting the stored preference.
                    DuplexComboBox.SelectedIndex = DuplexComboBox.IsEnabled && _preferredDuplex ? 1 : 0;
                }

                if (SourceComboBox.Items.Count >= 3)
                {
                    ((ComboBoxItem)SourceComboBox.Items[1]).IsEnabled = caps?.HasFeeder ?? true;
                    ((ComboBoxItem)SourceComboBox.Items[2]).IsEnabled = caps?.HasFlatbed ?? true;
                }
            }
            finally
            {
                _applyingSettings = wasApplyingSettings;
            }
        }

        private void UpdateActiveDevice()
        {
            if (!_uiReady) return;

            if (_availableScanners.Count == 0)
            {
                ActiveDeviceText.Text = "No scanner connected";
                return;
            }

            if (ScannerComboBox.SelectedItem is ComboBoxItem item && item.Tag != null)
            {
                ActiveDeviceText.Text = item.Content?.ToString() ?? "Scanner";
            }
            else
            {
                ActiveDeviceText.Text = "Select a scanner";
            }
        }

        private void PaperSizeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_uiReady) return;
            UpdateBadges();
            PersistSettings();
        }

        private void DuplexComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_uiReady) return;

            if (!_applyingSettings)
            {
                _preferredDuplex = DuplexComboBox.SelectedIndex == 1;
                _settings.SetDuplex(CurrentScannerId, _preferredDuplex);
                _settings.Save();
            }

            UpdateBadges();
            PersistSettings();
        }

        private void SourceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_uiReady) return;
            UpdateBadges();
            PersistSettings();
        }

        private void FormatComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_uiReady) return;
            UpdateSaveButtonLabel();
            UpdateBadges();
            PersistSettings();
        }

        private bool IsPdfOutput => FormatComboBox.SelectedIndex != 1;

        private void DiscardBlankToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (!_uiReady || _applyingSettings) return;
            PersistSettings();

            // Empty pages follow the toggle, but a page the user ticked or unticked by hand
            // keeps its own choice until the toggle changes again.
            bool discard = DiscardBlankToggle.IsChecked == true;
            foreach (var page in _scannedImages.Where(p => p.IsBlank))
            {
                page.IsSelected = !discard;
            }

            UpdateThumbnails();
        }

        private void AutoOrientToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (!_uiReady || _applyingSettings) return;
            PersistSettings();
        }

        private void UpdateSaveButtonLabel()
        {
            int selected = _scannedImages.Count(img => img.IsSelected);
            SaveButton.Content = selected == 0
                ? (IsPdfOutput ? "Save as PDF..." : "Save Images...")
                : (IsPdfOutput
                    ? $"Save {(selected == 1 ? "1 page" : $"{selected} pages")} as PDF..."
                    : $"Save {(selected == 1 ? "1 page" : $"{selected} pages")}...");
            FormatBadge.Text = "Save: " + (IsPdfOutput ? "PDF" : "JPG");
        }

        private void AutoUpdateToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (!_uiReady || _applyingSettings) return;
            _settings.AutoUpdate = AutoUpdateToggle.IsChecked == true;
            _settings.Save();
        }

        private async void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e) =>
            await CheckForUpdatesAsync(manual: true);

        /// <summary>
        /// Looks for a newer release. Automatic checks stay silent unless something is found;
        /// manual checks always report a result.
        /// </summary>
        private async Task CheckForUpdatesAsync(bool manual)
        {
            if (_checkingForUpdates) return;

            // A pending update is already known, so surface its banner instead of re-checking.
            if (_pendingUpdate != null)
            {
                UpdateBanner.Visibility = Visibility.Visible;
                return;
            }

            _checkingForUpdates = true;
            if (manual)
            {
                CheckForUpdatesButton.IsEnabled = false;
                UpdateStatusText.Text = "Checking for updates...";
            }

            try
            {
                var update = await _updateService.CheckForUpdatesAsync();
                if (update == null)
                {
                    UpdateStatusText.Text = $"You're up to date (v{UpdateService.CurrentVersion}).";
                    if (manual)
                    {
                        ShowBanner(false, $"ScanEzi {UpdateService.CurrentVersion} is the latest version.");
                    }
                    return;
                }

                _pendingUpdate = update;
                UpdateStatusText.Text = $"Version {update.Version} is available.";
                UpdateBannerText.Text = $"ScanEzi {update.Version} is available. You're on {UpdateService.CurrentVersion}.";
                UpdateNowButton.Content = "Update now";
                UpdateNowButton.IsEnabled = true;
                UpdateBanner.Visibility = Visibility.Visible;

                if (manual)
                {
                    ShowBanner(false, $"ScanEzi {update.Version} is available. Use the update banner to install it.");
                }

                // Automatic checks fetch the package quietly, then wait for the user to restart.
                if (_settings.AutoUpdate && !manual)
                {
                    await DownloadUpdateAsync(update);
                }
            }
            catch (Exception ex)
            {
                ScanLog.Write($"Update check failed: {ex.Message}");
                UpdateStatusText.Text = "Couldn't check for updates.";
                if (manual)
                {
                    ShowBanner(true, $"Couldn't check for updates: {ex.Message}");
                }
            }
            finally
            {
                _checkingForUpdates = false;
                CheckForUpdatesButton.IsEnabled = true;
            }
        }

        private async Task DownloadUpdateAsync(UpdateInfo update)
        {
            if (_preparedUpdatePath != null) return;

            UpdateNowButton.IsEnabled = false;
            UpdateStatusText.Text = $"Downloading ScanEzi {update.Version}...";

            try
            {
                var progress = new Progress<double>(fraction =>
                {
                    string percent = fraction.ToString("P0");
                    UpdateBannerText.Text = $"Downloading ScanEzi {update.Version}... {percent}";
                    UpdateStatusText.Text = $"Downloading update... {percent}";
                });

                _preparedUpdatePath = await _updateService.DownloadAsync(update, progress);
                UpdateBannerText.Text = $"ScanEzi {update.Version} is ready. Restart to finish installing.";
                UpdateStatusText.Text = $"Version {update.Version} is ready to install.";
                UpdateNowButton.Content = "Restart and update";
                UpdateNowButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                ScanLog.Write($"Update download failed: {ex}");
                UpdateStatusText.Text = "Update download failed.";
                UpdateBannerText.Text = $"Couldn't download ScanEzi {update.Version}: {ex.Message}";
                UpdateNowButton.Content = "Retry";
                UpdateNowButton.IsEnabled = true;
                ShowBanner(true, $"Couldn't download the update: {ex.Message}");
            }
        }

        private async void UpdateNowButton_Click(object sender, RoutedEventArgs e)
        {
            if (_pendingUpdate == null) return;

            if (_preparedUpdatePath == null)
            {
                await DownloadUpdateAsync(_pendingUpdate);
                return;
            }

            try
            {
                UpdateNowButton.IsEnabled = false;
                UpdateStatusText.Text = "Restarting to install the update...";
                _updateService.ApplyUpdate(_preparedUpdatePath);

                // Close-to-tray would otherwise cancel the shutdown and the installer would
                // restart while this process still owns the single-instance mutex.
                _exitRequested = true;
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                ScanLog.Write($"Update install failed: {ex}");
                UpdateNowButton.IsEnabled = true;
                UpdateStatusText.Text = "Couldn't install the update.";
                ShowBanner(true, $"Couldn't install the update: {ex.Message}");
            }
        }

        private void UpdateLaterButton_Click(object sender, RoutedEventArgs e)
        {
            UpdateBanner.Visibility = Visibility.Collapsed;
            UpdateStatusText.Text = $"Version {_pendingUpdate?.Version} is available \u2014 install it from Settings when ready.";
        }

        private void BannerCloseButton_Click(object sender, RoutedEventArgs e)
        {
            StatusBanner.Visibility = Visibility.Collapsed;
        }

        private enum BannerSeverity
        {
            Success,
            Warning,
            Error
        }

        private void ShowBanner(bool isError, string message) =>
            ShowBanner(isError ? BannerSeverity.Error : BannerSeverity.Success, message);

        private void ShowBanner(BannerSeverity severity, string message)
        {
            string backgroundKey;
            string accentKey;
            string glyph;

            switch (severity)
            {
                case BannerSeverity.Error:
                    backgroundKey = "SystemFillColorCriticalBackgroundBrush";
                    accentKey = "SystemFillColorCriticalBrush";
                    glyph = "\uEA39";
                    break;

                case BannerSeverity.Warning:
                    backgroundKey = "SystemFillColorCautionBackgroundBrush";
                    accentKey = "SystemFillColorCautionBrush";
                    glyph = "\uE7BA";
                    break;

                default:
                    backgroundKey = "SystemFillColorSuccessBackgroundBrush";
                    accentKey = "SystemFillColorSuccessBrush";
                    glyph = "\uE73E";
                    break;
            }

            var accent = ResolveBrush(accentKey);
            StatusBanner.Background = ResolveBrush(backgroundKey);
            StatusBanner.BorderBrush = accent;
            BannerIcon.Foreground = accent;
            BannerIcon.Text = glyph;
            BannerText.Text = message;
            StatusBanner.Visibility = Visibility.Visible;
        }

        /// <summary>Looks a brush up by key, falling back to a neutral fill on older theme sets.</summary>
        private Brush ResolveBrush(string key) =>
            TryFindResource(key) as Brush
            ?? TryFindResource("ControlFillColorDefaultBrush") as Brush
            ?? Brushes.Transparent;

        private void HideBanner()
        {
            StatusBanner.Visibility = Visibility.Collapsed;
        }

        private void UpdateBadges()
        {
            if (!_uiReady) return;

            var paper = (PaperSizeComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "A4";
            PaperSizeBadge.Text = "Size: " + paper;

            bool isDuplex = DuplexComboBox.SelectedIndex == 1;
            DuplexBadge.Text = "Sides: " + (isDuplex ? "2-Sided" : "1-Sided");

            SourceBadge.Text = "Source: " + GetSelectedSource();
        }

        private string GetSelectedSource()
        {
            return (SourceComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Auto Detect";
        }

        /// <summary>
        /// Remembers the current device and scan settings so the next launch starts
        /// exactly where the user left off.
        /// </summary>
        private string? CurrentScannerId =>
            (ScannerComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();

        private void PersistSettings()
        {
            if (_applyingSettings) return;

            if (ScannerComboBox.SelectedItem is ComboBoxItem item && item.Tag != null)
            {
                _settings.DefaultScannerId = item.Tag.ToString();
            }

            if (PaperSizeComboBox.SelectedItem is ComboBoxItem paperItem)
            {
                _settings.DefaultPaperSize = paperItem.Content?.ToString() ?? "A4";
            }

            // Sides are persisted per device by SetDuplex at the point of change, so that
            // switching devices does not overwrite another device's remembered choice.
            _settings.DefaultSource = GetSelectedSource();
            _settings.DefaultFormat = IsPdfOutput ? "pdf" : "jpg";
            _settings.DiscardBlankPages = DiscardBlankToggle.IsChecked == true;
            _settings.AutoOrientPages = AutoOrientToggle.IsChecked == true;
            _settings.Save();
        }

        private async void ScanButton_Click(object sender, RoutedEventArgs e)
        {
            QuickScanButton.IsEnabled = false;
            StatusText.Text = "Scanning document...";
            HideBanner();

            try
            {
                string? scannerId = null;

                if (ScannerComboBox.SelectedItem is ComboBoxItem item && item.Tag != null)
                {
                    scannerId = item.Tag.ToString();
                }

                if (string.IsNullOrEmpty(scannerId))
                {
                    if (_availableScanners.Count == 0)
                    {
                        throw new Exception("No scanner selected or available.");
                    }
                    scannerId = _availableScanners[0].Id;
                }

                string paperSize = (PaperSizeComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "A4";
                bool duplex = DuplexComboBox.SelectedIndex == 1;
                string source = GetSelectedSource();

                var outcome = await _scannerService.ScanAsync(scannerId, paperSize, duplex, source);
                var imagePaths = outcome.Files;

                if (imagePaths != null && imagePaths.Count > 0)
                {
                    var acquired = imagePaths.Select(path => new ScannedImage(path)).ToList();
                    bool autoOrient = _settings.AutoOrientPages && AutoOrientation.IsAvailable;

                    StatusText.Text = autoOrient ? "Checking pages and orientation..." : "Checking pages...";

                    // Both passes decode every page, so keep them off the UI thread.
                    await Task.Run(async () =>
                    {
                        foreach (var page in acquired)
                        {
                            page.Kind = PageContentAnalyzer.Classify(page.ImagePath);

                            if (autoOrient && !page.IsBlank)
                            {
                                int angle = await AutoOrientation.DetectRotationAsync(page.ImagePath);
                                if (angle != 0)
                                {
                                    page.Rotation = angle;
                                    page.ImagePath = _imageProcessor.RotateImage(page.ImagePath, angle);
                                }
                            }
                        }
                    });

                    foreach (var scannedImage in acquired)
                    {
                        // Empty pages start unticked so they do not end up in the save, but the
                        // user can always tick one back on.
                        scannedImage.IsSelected = !(_settings.DiscardBlankPages && scannedImage.IsBlank);
                        _scannedImages.Add(scannedImage);
                    }
                    UpdateThumbnails();

                    int blankPages = acquired.Count(page => !page.IsSelected);
                    string sides = outcome.DuplexApplied ? " (2-sided)" : string.Empty;

                    StatusText.Text = blankPages > 0
                        ? $"Scan complete{sides} \u00B7 {acquired.Count} page(s) acquired \u00B7 {blankPages} empty page(s) unticked"
                        : $"Scan complete{sides} \u00B7 {acquired.Count} page(s) acquired";

                    if (!IsVisible)
                    {
                        _tray.Notify(
                            "Scan complete",
                            $"{acquired.Count} page(s) captured. Open ScanEzi to review and save.");
                    }

                    if (!string.IsNullOrWhiteSpace(outcome.Warning))
                    {
                        ShowBanner(BannerSeverity.Warning, outcome.Warning!);
                    }
                }
                else
                {
                    throw new Exception("The scanner returned no pages. Check that a document is loaded and the source matches the device.");
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = "Scan failed";
                ShowBanner(true, ex.Message);

                if (!IsVisible)
                {
                    _tray.Notify("Scan failed", ex.Message);
                }
            }
            finally
            {
                QuickScanButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// Opens the full-size preview for a page, where crop and rotate live so the main
        /// window stays a simple gallery.
        /// </summary>
        private void OpenPreview(ScannedImage page)
        {
            int index = _scannedImages.IndexOf(page);
            if (index < 0)
            {
                return;
            }

            var dialog = new PagePreviewWindow(_scannedImages.ToList(), index) { Owner = this };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            foreach (var edit in dialog.AppliedEdits)
            {
                if (edit.Index < 0 || edit.Index >= _scannedImages.Count)
                {
                    continue;
                }

                var target = _scannedImages[edit.Index];
                bool wasBlank = target.IsBlank;
                target.ImagePath = edit.Path;
                target.Kind = PageContentAnalyzer.Classify(edit.Path);

                // Editing a page can change what it looks like, so keep the tick in step with
                // the fresh classification: a page that stopped being empty joins the save,
                // and one that became empty follows the discard preference.
                if (wasBlank && !target.IsBlank) target.IsSelected = true;
                else if (!wasBlank && target.IsBlank) target.IsSelected = !_settings.DiscardBlankPages;
            }

            UpdateThumbnails();

            int edited = dialog.AppliedEdits.Count;
            StatusText.Text = edited == 1 ? "Page updated." : $"{edited} pages updated.";
        }

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (_scannedImages.Count == 0)
            {
                ShowBanner(true, "There are no scanned images to save yet.");
                return;
            }

            HideBanner();

            var pagesToSave = _scannedImages.Where(img => img.IsSelected).ToList();

            if (pagesToSave.Count == 0)
            {
                ShowBanner(true, "No pages are ticked, so there is nothing to save. Tick a page to include it.");
                return;
            }

            if (IsPdfOutput)
            {
                await SaveAsPdfAsync(pagesToSave);
                return;
            }

            var folderDialog = new OpenFolderDialog
            {
                Title = "Choose a folder to save the scanned images"
            };

            if (folderDialog.ShowDialog() == true)
            {
                try
                {
                    StatusText.Text = "Saving images...";
                    foreach (var img in pagesToSave)
                    {
                        await _imageProcessor.SaveAsJpgAsync(img.ImagePath, folderDialog.FolderName, _settings.DefaultQuality);
                    }

                    int skipped = _scannedImages.Count - pagesToSave.Count;
                    StatusText.Text = $"Saved {pagesToSave.Count} image(s).";
                    ShowBanner(false, $"{pagesToSave.Count} image(s) saved to {folderDialog.FolderName}" + SkippedSuffix(skipped));
                }
                catch (Exception ex)
                {
                    StatusText.Text = "Save failed.";
                    ShowBanner(true, $"Save failed: {ex.Message}");
                }
            }
        }

        private string SkippedSuffix(int skipped) =>
            skipped > 0 ? $" \u00B7 {skipped} page(s) left out" : string.Empty;

        private async Task SaveAsPdfAsync(IReadOnlyList<ScannedImage> pages)
        {
            var dialog = new SaveFileDialog
            {
                Title = "Save scanned pages as PDF",
                Filter = "PDF document (*.pdf)|*.pdf",
                DefaultExt = ".pdf",
                AddExtension = true,
                FileName = $"ScanEzi-{DateTime.Now:yyyyMMdd-HHmm}.pdf"
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            SaveButton.IsEnabled = false;
            try
            {
                StatusText.Text = "Creating PDF...";
                var paths = pages.Select(img => img.ImagePath).ToList();
                var outputPath = dialog.FileName;

                await Task.Run(() => PdfExportService.CreateFromImages(paths, outputPath, _settings.DefaultQuality));

                int skipped = _scannedImages.Count - paths.Count;
                StatusText.Text = $"Saved PDF \u00B7 {paths.Count} page(s).";
                ShowBanner(false, $"{paths.Count} page(s) saved to {outputPath}" + SkippedSuffix(skipped));
            }
            catch (Exception ex)
            {
                StatusText.Text = "Save failed.";
                ShowBanner(true, $"Save failed: {ex.Message}");
            }
            finally
            {
                UpdateSaveButtonState();
            }
        }

        /// <summary>
        /// The save action only lights up when at least one page is ticked, so an empty gallery
        /// (or one where everything was unticked) stays greyed out.
        /// </summary>
        private void UpdateSaveButtonState()
        {
            int selected = _scannedImages.Count(img => img.IsSelected);
            SaveButton.IsEnabled = selected > 0;
            NewScanButton.IsEnabled = _scannedImages.Count > 0;
            UpdateSaveButtonLabel();
            UpdateTrayStatus();
        }

        /// <summary>
        /// Clears the gallery for a fresh document, which is the common case when the same
        /// stack has just been saved and the next one goes into the feeder.
        /// </summary>
        private void NewScanButton_Click(object sender, RoutedEventArgs e)
        {
            if (_scannedImages.Count == 0)
            {
                StatusText.Text = "Ready to scan.";
                return;
            }

            int cleared = _scannedImages.Count;

            foreach (var page in _scannedImages)
            {
                TryDeletePageFile(page.ImagePath);
            }

            _scannedImages.Clear();
            HideBanner();
            UpdateThumbnails();

            StatusText.Text = cleared == 1
                ? "Cleared 1 page \u00B7 ready to scan"
                : $"Cleared {cleared} pages \u00B7 ready to scan";
        }

        /// <summary>
        /// Removes a page's working file, but only when it sits in this app's own temp folder
        /// so a user-chosen file can never be deleted by mistake.
        /// </summary>
        private static void TryDeletePageFile(string path)
        {
            try
            {
                var pageFolder = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ScanEzi"));
                var fullPath = Path.GetFullPath(path);

                // Only files sitting directly in this app's own temp folder are removed, so a
                // user-chosen file (or a similarly named sibling folder) is never touched.
                bool isOwnTempFile = string.Equals(
                    Path.GetDirectoryName(fullPath),
                    pageFolder,
                    StringComparison.OrdinalIgnoreCase);

                if (isOwnTempFile && File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                }
            }
            catch
            {
                // A locked or already-removed temp file must not block the reset.
            }
        }

        private void UpdateThumbnails()
        {
            ThumbnailPanel.Children.Clear();
            _pageTiles.Clear();

            if (_scannedImages.Count == 0)
            {
                EmptyPlaceholder.Visibility = Visibility.Visible;
                GalleryScroll.Visibility = Visibility.Collapsed;
                UpdateSaveButtonState();
                return;
            }

            EmptyPlaceholder.Visibility = Visibility.Collapsed;
            GalleryScroll.Visibility = Visibility.Visible;

            for (int i = 0; i < _scannedImages.Count; i++)
            {
                var img = _scannedImages[i];

                var border = new Border
                {
                    Width = _settings.ThumbnailWidth,
                    Height = _settings.ThumbnailHeight,
                    Margin = new Thickness(8),
                    CornerRadius = new CornerRadius(6),
                    BorderThickness = new Thickness(1),
                    Tag = img,
                    Cursor = System.Windows.Input.Cursors.Hand
                };

                // Set through resource references so the gallery follows the light/dark theme.
                border.SetResourceReference(Border.BackgroundProperty, "ControlFillColorDefaultBrush");
                border.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");

                var stack = new Grid();

                Image? preview = null;
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(img.ImagePath);
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();

                    preview = new Image
                    {
                        Source = bitmap,
                        Stretch = Stretch.Uniform
                    };
                    stack.Children.Add(preview);
                }
                catch
                {
                    stack.Children.Add(new TextBlock
                    {
                        Text = "Preview unavailable",
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = Brushes.Gray
                    });
                }

                var toggle = new CheckBox
                {
                    IsChecked = img.IsSelected,
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "Tick to include this page when saving"
                };

                // Named so the tick state is reachable from accessibility tools and tests.
                toggle.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty, $"PageToggle{i + 1}");
                toggle.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, $"Include page {i + 1} when saving");

                toggle.Click += (s, args) => SetPageSelected(img, toggle.IsChecked == true);

                // The tick sits on a translucent chip so it stays visible over light scans.
                stack.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0xB3, 0x1C, 0x1C, 0x1C)),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(4, 1, 6, 1),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(6),
                    ToolTip = "Tick to include this page when saving",
                    Child = toggle
                });

                stack.Children.Add(BuildPageNumberBadge(i + 1));
                border.Child = stack;

                var page = img;
                border.MouseLeftButtonUp += (s, args) => OpenPreview(page);

                var tile = new PageTile(border, stack, preview, toggle);
                _pageTiles[img] = tile;

                ThumbnailPanel.Children.Add(border);
                RefreshPageTile(img);
            }

            UpdateSaveButtonState();
        }

        /// <summary>
        /// Repaints a single tile from the page's current state, so ticking a page never has to
        /// rebuild (and re-decode) the whole gallery.
        /// </summary>
        private void RefreshPageTile(ScannedImage img)
        {
            if (!_pageTiles.TryGetValue(img, out var tile)) return;

            if (tile.Preview != null)
            {
                tile.Preview.Opacity = img.IsSelected ? 1.0 : 0.25;
            }

            if (tile.Overlay != null)
            {
                tile.Stack.Children.Remove(tile.Overlay);
                tile.Overlay = null;
            }

            if (!img.IsSelected)
            {
                var overlay = BuildExcludedOverlay(img.Kind);
                tile.Overlay = overlay;

                // Below the page-number badge so the number stays readable.
                tile.Stack.Children.Insert(tile.Stack.Children.Count - 1, overlay);
            }

            tile.Border.ToolTip = img.IsSelected
                ? "Click to preview, crop or rotate \u00B7 untick to leave this page out"
                : img.Kind switch
                {
                    PageKind.Black => "Nothing was captured on this page \u2014 tick to save it anyway",
                    PageKind.White => "Blank sheet \u2014 tick to save it anyway",
                    _ => "This page will not be saved \u2014 tick to include it"
                };

            if (tile.Toggle.IsChecked != img.IsSelected)
            {
                tile.Toggle.IsChecked = img.IsSelected;
            }
        }

        private void SetPageSelected(ScannedImage page, bool selected)
        {
            if (page.IsSelected != selected)
            {
                page.IsSelected = selected;
            }

            RefreshPageTile(page);
            UpdateSaveButtonState();

            int selectedCount = _scannedImages.Count(img => img.IsSelected);
            StatusText.Text = selectedCount == 0
                ? "No pages ticked \u00B7 tick a page to include it when saving"
                : $"{selectedCount} of {_scannedImages.Count} page(s) will be saved";
        }

        private static UIElement BuildPageNumberBadge(int number)
        {
            var badge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xB3, 0x1C, 0x1C, 0x1C)),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(7, 1, 7, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(6),
                Child = new TextBlock
                {
                    Text = number.ToString(),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brushes.White
                }
            };

            return badge;
        }

        /// <summary>
        /// Marks a page that will not be saved: a struck-through badge over a dimmed preview,
        /// so the sheet is still visible in the gallery instead of silently disappearing.
        /// </summary>
        private UIElement BuildExcludedOverlay(PageKind kind)
        {
            var overlay = new Grid();
            overlay.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x59, 0x00, 0x00, 0x00))
            });

            var badge = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            badge.Children.Add(new TextBlock
            {
                Text = "\uE711",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 30,
                Foreground = (Brush)FindResource("SystemFillColorCriticalBrush"),
                HorizontalAlignment = HorizontalAlignment.Center
            });

            badge.Children.Add(new TextBlock
            {
                Text = kind switch
                {
                    PageKind.Black => "No scan",
                    PageKind.White => "Blank",
                    _ => "Not saved"
                },
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0)
            });

            overlay.Children.Add(badge);
            return overlay;
        }

        /// <summary>The pieces of a gallery tile that change when a page is ticked.</summary>
        private sealed class PageTile
        {
            public PageTile(Border border, Grid stack, Image? preview, CheckBox toggle)
            {
                Border = border;
                Stack = stack;
                Preview = preview;
                Toggle = toggle;
            }

            public Border Border { get; }

            public Grid Stack { get; }

            public Image? Preview { get; }

            public CheckBox Toggle { get; }

            public UIElement? Overlay { get; set; }
        }
    }

    public class ScannedImage
    {
        public string ImagePath { get; set; }
        public string FileName => Path.GetFileNameWithoutExtension(ImagePath);
        public double Rotation { get; set; }

        /// <summary>How the page content was classified right after it was scanned or edited.</summary>
        public PageKind Kind { get; set; } = PageKind.Content;

        public bool IsBlank => Kind != PageKind.Content;

        /// <summary>
        /// Whether the page is ticked for saving. Empty pages start unticked, but the choice is
        /// always the user's, so a page is never dropped without a visible, reversible mark.
        /// </summary>
        public bool IsSelected { get; set; } = true;

        public ScannedImage(string imagePath)
        {
            ImagePath = imagePath;
            Rotation = 0;
        }
    }
}
