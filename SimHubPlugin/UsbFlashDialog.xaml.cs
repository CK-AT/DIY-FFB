using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DiyFfb.Flasher;

namespace DiyFfb
{
    /// <summary>
    /// Flashes one board over USB with the pure-C# ROM flasher (Plan 29 phase 5).
    /// Firmware is a board's published .ffbota or a local one; the plugin
    /// releases the gateway port for the duration.
    /// </summary>
    public partial class UsbFlashDialog : Window
    {
        private readonly DiyFfbPlugin plugin;
        private readonly string infoUrl;
        private readonly string reportedBoard;
        private bool busy;
        private FirmwarePackage localPackage;
        private int lastProgress = -1;
        private string recoveryHintShownFor;

        /// <param name="reportedBoard">Board name the connected devices report, to preselect; may be null.</param>
        public UsbFlashDialog(DiyFfbPlugin plugin, string infoUrl, string reportedBoard, string localPath)
        {
            this.plugin = plugin;
            this.infoUrl = infoUrl;
            this.reportedBoard = reportedBoard;
            InitializeComponent();

            RefreshDevices();
            if (!string.IsNullOrWhiteSpace(localPath) && System.IO.File.Exists(localPath))
            {
                LoadLocal(localPath, select: false);
            }
            LoadPublished();
        }

        // ------------------------------------------------------------ devices

        private void RefreshDevices()
        {
            string selected = (ComboBox_Device.SelectedItem as UsbPort)?.Com;
            List<UsbPort> ports = UsbDeviceScanner.GetPorts(forceRefresh: true).ToList();
            ComboBox_Device.ItemsSource = ports;

            // Keep the user's pick; otherwise prefer the gateway, then any DIY-FFB
            // device, then a board already sitting in the bootloader.
            UsbPort pick =
                ports.FirstOrDefault(p => p.Com == selected)
                ?? ports.FirstOrDefault(p => p.Role == UsbPortRole.Gateway)
                ?? ports.FirstOrDefault(p => p.Role == UsbPortRole.Axis)
                ?? ports.FirstOrDefault(p => p.Role == UsbPortRole.EspRom);

            // Nothing on native USB: a board whose image dies before USB.begin()
            // only shows its CH343. Offer that for recovery (Plan 29 §4.4).
            if (pick == null)
            {
                pick = FindRecoveryPort(ports);
                if (pick != null && pick.Com != recoveryHintShownFor)
                {
                    recoveryHintShownFor = pick.Com;
                    Log($"No DIY-FFB device on USB. Selected {pick.Label} to recover the board through its UART.");
                }
            }
            ComboBox_Device.SelectedItem = pick;
        }

        /// <summary>
        /// The CH343 (WCH, VID 1A86) to fall back to, if there is exactly one.
        /// Nothing on the bus ties a CH343 to a particular board, so with several
        /// the user picks.
        /// </summary>
        private static UsbPort FindRecoveryPort(IEnumerable<UsbPort> ports)
        {
            List<UsbPort> wch = ports.Where(p => p.Role == UsbPortRole.UsbUart && p.Vid == 0x1A86).ToList();
            return wch.Count == 1 ? wch[0] : null;
        }

        private static bool IsAppPort(UsbPort port)
        {
            return port.Role == UsbPortRole.Gateway || port.Role == UsbPortRole.Axis || port.Role == UsbPortRole.Grip;
        }

        private void ComboBox_Device_DropDownOpened(object sender, EventArgs e)
        {
            if (!busy)
            {
                RefreshDevices();
            }
        }

        // ----------------------------------------------------------- firmware

        private async void LoadPublished()
        {
            ComboBox_Published.IsEnabled = false;
            TextBlock_Package.Text = $"Loading published firmware from {infoUrl} ...";
            try
            {
                IReadOnlyList<PublishedFirmware> boards = await Task.Run(() => PublishedFirmware.List(infoUrl));
                ComboBox_Published.ItemsSource = boards;
                ComboBox_Published.SelectedItem =
                    boards.FirstOrDefault(b => string.Equals(b.Board, reportedBoard, StringComparison.OrdinalIgnoreCase))
                    ?? (boards.Count == 1 ? boards[0] : null);
                ComboBox_Published.IsEnabled = boards.Count > 0;
                UpdatePackageText();
            }
            catch (Exception ex)
            {
                TextBlock_Package.Text = $"Couldn't load the published list: {ex.Message}";
                if (localPackage != null)
                {
                    Radio_Local.IsChecked = true;
                }
            }
        }

        private void LoadLocal(string path, bool select)
        {
            try
            {
                localPackage = FirmwarePackage.Load(path);
                TextBox_LocalPath.Text = path;
                if (select)
                {
                    Radio_Local.IsChecked = true;
                }
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is System.IO.InvalidDataException
                                       || ex is UnauthorizedAccessException || ex is System.Runtime.Serialization.SerializationException)
            {
                localPackage = null;
                TextBox_LocalPath.Text = path;
                Log($"Can't use {System.IO.Path.GetFileName(path)}: {ex.Message}");
            }
            UpdatePackageText();
        }

        private void btn_browse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "FFB firmware package (*.ffbota)|*.ffbota",
                DefaultExt = "ffbota",
            };
            if (dialog.ShowDialog(this) == true)
            {
                LoadLocal(dialog.FileName, select: true);
            }
        }

        private void Radio_Source_Checked(object sender, RoutedEventArgs e) => UpdatePackageText();

        private void ComboBox_Published_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdatePackageText();

        private void UpdatePackageText()
        {
            if (TextBlock_Package == null)
            {
                return;  // during InitializeComponent
            }

            if (Radio_Local.IsChecked == true)
            {
                TextBlock_Package.Text = localPackage != null
                    ? $"{localPackage.Board} v{localPackage.Version} ({localPackage.BuildEnv})"
                    : "Pick a .ffbota built by pio run.";
            }
            else
            {
                var fw = ComboBox_Published.SelectedItem as PublishedFirmware;
                TextBlock_Package.Text = fw != null
                    ? $"{fw} — downloaded when you flash"
                    : ComboBox_Published.Items.Count > 0 ? "Pick the board to flash." : TextBlock_Package.Text;
            }
        }

        // -------------------------------------------------------------- flash

        private async void btn_flash_Click(object sender, RoutedEventArgs e)
        {
            var port = ComboBox_Device.SelectedItem as UsbPort;
            if (port == null || port.Com == "NA")
            {
                Log("Pick the device to flash.");
                return;
            }

            bool useLocal = Radio_Local.IsChecked == true;
            var published = ComboBox_Published.SelectedItem as PublishedFirmware;
            if (useLocal ? localPackage == null : published == null)
            {
                Log(useLocal ? "Pick a valid local .ffbota." : "Pick the published board to flash.");
                return;
            }

            string board = useLocal ? localPackage.Board : published.Board;
            if (!string.IsNullOrWhiteSpace(reportedBoard)
                && !string.Equals(board, reportedBoard, StringComparison.OrdinalIgnoreCase)
                && !Confirm($"This firmware is for {board}, but the connected devices report {reportedBoard}.\n\nFlash it anyway?"))
            {
                return;
            }

            bool eraseNvs = Radio_FactoryReset.IsChecked == true;
            if (eraseNvs && !Confirm($"Factory reset erases the stored configuration on {port.Label}.\n\nContinue?"))
            {
                return;
            }

            SetBusy(true);
            TextBox_Log.Clear();
            FirmwarePackage package = null;
            UsbPort recovery = null;
            try
            {
                package = useLocal ? localPackage : await Task.Run(() => DownloadPublished(published));
                await Task.Run(() => Flash(port, package, eraseNvs));
                Log("Done.");
            }
            catch (EspRomConnectionException ex) when (package != null && IsAppPort(port)
                && (recovery = FindRecoveryPort(UsbDeviceScanner.GetPorts(forceRefresh: true))) != null)
            {
                // Native USB gave up (e.g. the new image crashes before USB comes
                // up). The CH343's auto-program wiring doesn't depend on the app.
                Log($"FAILED: {ex.Message}");
                if (Confirm($"Flashing over the board's USB failed.\n\nTry again through {recovery.Label}? " +
                            "That uses the board's UART and works even if its firmware doesn't start."))
                {
                    try
                    {
                        await Task.Run(() => Flash(recovery, package, eraseNvs));
                        Log("Done.");
                    }
                    catch (Exception ex2)
                    {
                        Log($"FAILED: {ex2.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"FAILED: {ex.Message}");
            }
            finally
            {
                SetBusy(false);
                RefreshDevices();
            }
        }

        private FirmwarePackage DownloadPublished(PublishedFirmware published)
        {
            Log($"Downloading {published} ...");
            return published.Download();
        }

        private void Flash(UsbPort port, FirmwarePackage package, bool eraseNvs)
        {
            Log($"Flashing {package.Board} v{package.Version} to {port.Label}.");
            // Release the gateway for the whole flash, including re-enumeration;
            // disposing lets the plugin reconnect if it was connected before.
            using (plugin?.SuspendGateway())
            {
                var flasher = new Esp32S3Flasher(Log) { Progress = OnProgress };
                flasher.Flash(port.Com, package.Image, new FlashOptions { EraseNvs = eraseNvs });
            }
        }

        // ------------------------------------------------------------ helpers

        private void SetBusy(bool value)
        {
            busy = value;
            ComboBox_Device.IsEnabled = !value;
            ComboBox_Published.IsEnabled = !value && ComboBox_Published.Items.Count > 0;
            Radio_Published.IsEnabled = !value;
            Radio_Local.IsEnabled = !value;
            btn_browse.IsEnabled = !value;
            Radio_Update.IsEnabled = !value;
            Radio_FactoryReset.IsEnabled = !value;
            btn_flash.IsEnabled = !value;
            btn_close.IsEnabled = !value;
            if (value)
            {
                lastProgress = -1;
                ProgressBar_Flash.Value = 0;
            }
        }

        private bool Confirm(string message)
        {
            return MessageBox.Show(this, message, "USB Flash", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        }

        private void OnProgress(long done, long total)
        {
            int permille = (int)(1000 * done / Math.Max(1, total));
            if (permille == lastProgress)
            {
                return;
            }
            lastProgress = permille;
            Dispatcher.BeginInvoke(new Action(() => ProgressBar_Flash.Value = permille));
        }

        private void Log(string line)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => Log(line)));
                return;
            }
            TextBox_Log.AppendText(line + Environment.NewLine);
            TextBox_Log.ScrollToEnd();
        }

        private void btn_close_Click(object sender, RoutedEventArgs e) => Close();

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (busy)
            {
                e.Cancel = true;
                Log("Wait for the flash to finish.");
            }
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }
    }
}
