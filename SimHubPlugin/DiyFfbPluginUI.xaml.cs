using Google.Protobuf;
using Newtonsoft.Json;
using ProtbufTest;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.IO.Ports;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DiyFfb.Controls;
using DiyFfb.GraphEditor;
using DiyFfb.ProfileBrowser;
using DiyFfb.TieredConfig;
using System.Windows.Data;
using vJoyInterfaceWrap;
using Windows.UI.Notifications;

namespace DiyFfb
{
    /// <summary>
    /// Interaction logic for DiyFfbPluginUI.xaml
    /// </summary>
    public partial class DiyFfbPluginUI : UserControl
    {
        public uint indexOfSelectedPedal_u = 0;
        public DiyFfbPlugin Plugin { get; }

        private AxisID selected_axis_id = AxisID.AxisUndefined;
        private FunctionID selected_function_id = FunctionID.Undefined;

        public SortedDictionary<AxisID, Axis> axes { get; } = new SortedDictionary<AxisID, Axis>();
        public SortedDictionary<FunctionID, Function> functions { get; } = new SortedDictionary<FunctionID, Function>();
        public Dictionary<AxisID, FunctionID> last_known_functions { get; } = new Dictionary<AxisID, FunctionID>();
        public System.Collections.ObjectModel.ObservableCollection<OtaTargetEntry> ota_targets { get; } = new System.Collections.ObjectModel.ObservableCollection<OtaTargetEntry>();
        public System.Collections.ObjectModel.ObservableCollection<UiLogEntry> UiLogEntries { get; } = new System.Collections.ObjectModel.ObservableCollection<UiLogEntry>();
        public System.Collections.ObjectModel.ObservableCollection<LogSourceFilterItem> LogSourceFilters { get; } = new System.Collections.ObjectModel.ObservableCollection<LogSourceFilterItem>();
        public ICollectionView UiLogView { get; }
        private readonly Dictionary<AxisID, OtaTargetEntry> ota_axis_targets = new Dictionary<AxisID, OtaTargetEntry>();
        private readonly Dictionary<GatewayID, OtaTargetEntry> ota_gateway_targets = new Dictionary<GatewayID, OtaTargetEntry>();
        private readonly Dictionary<AxisID, string> ota_axis_logs = new Dictionary<AxisID, string>();
        private readonly Dictionary<GatewayID, string> ota_gateway_logs = new Dictionary<GatewayID, string>();
        private readonly Dictionary<string, LogSourceFilterItem> logSourceFilterLookup = new Dictionary<string, LogSourceFilterItem>();
        private readonly Dictionary<AxisID, string> ota_axis_versions = new Dictionary<AxisID, string>();
        private readonly Dictionary<GatewayID, string> ota_gateway_versions = new Dictionary<GatewayID, string>();
        private GatewayID last_gateway_id = GatewayID.GatewayUndefined;
        private readonly HashSet<AxisID> seenAxisSources = new HashSet<AxisID>();
        private readonly HashSet<GatewayID> seenGatewaySources = new HashSet<GatewayID>();
        private OtaSelectionDialog otaSelectionDialog;
        private CancellationTokenSource otaUpdateCancellation;
        private bool otaAclWarned;
        private bool suppressSerialPortSelectionChange;
        private ProtobufSerial<Message> attachedGatewayPort;

        internal vJoyInterfaceWrap.vJoy joystick;

        private readonly SolidColorBrush defaultcolor = new SolidColorBrush(Colors.White);
        private readonly SolidColorBrush lightcolor = new SolidColorBrush(Color.FromRgb(0xD8, 0xE6, 0xFF));

        public SolidColorBrush MouseDownColor { get { return lightcolor; } }
        public SolidColorBrush MouseUpColor { get { return defaultcolor; } }

        private bool persistConfigModifier;
        private bool updatingXPlaneUdp;

        private bool PersistConfig => persistConfigModifier;

        private const int UiLogMaxEntries = 200;
        private const int MaxWifiCredentialLength = 63;
        private const string OtaInfoUrlDefault = "https://github.com/CK-AT/DIY-FFB/raw/refs/heads/main/OTA/update_info.json";

        private LocalOtaServer otaServer;

        private SaveSelectionDialog saveSelectionDialog;
        private LoadSelectionDialog loadSelectionDialog;
        private AxisRequestQueue axisRequestQueue;
        private GraphEditorWindow graphEditorWindow;
        private string lastGraphEditorPath;
        private bool suppressUserProfileSelectionChange;

        private enum UiLogLevel
        {
            Info,
            Warning,
            Error,
            Debug
        }

        public enum UiLogSourceKind
        {
            Plugin,
            Gateway,
            Axis
        }

        public sealed class LogSourceFilterItem : INotifyPropertyChanged
        {
            private bool isSelected;

            public UiLogSourceKind Kind { get; }
            public int? Id { get; }
            public string Label { get; }

            public bool IsSelected
            {
                get => isSelected;
                set
                {
                    if (isSelected == value)
                    {
                        return;
                    }
                    isSelected = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;

            public LogSourceFilterItem(UiLogSourceKind kind, int? id, string label, bool isSelected)
            {
                Kind = kind;
                Id = id;
                Label = label;
                this.isSelected = isSelected;
            }
        }

        public sealed class UiLogEntry
        {
            public string Time { get; set; } = string.Empty;
            public string Level { get; set; } = string.Empty;
            public string Source { get; set; } = string.Empty;
            public string Message { get; set; } = string.Empty;
            public UiLogSourceKind SourceKind { get; set; }
            public int? SourceId { get; set; }
        }

        public DiyFfbPluginUI()
        {
            InitializeComponent();
            UiLogView = CollectionViewSource.GetDefaultView(UiLogEntries);
            UiLogView.Filter = FilterLogEntry;
        }

        public DiyFfbPluginUI(DiyFfbPlugin plugin) : this()
        {
            Plugin = plugin;
            DataContext = this;

            uc_function_config.ABSTestStateChange += OnABSTestStateChange;
            uc_function_config.DebugMessage += OnDebugMessage;
            uc_axis_config.DebugMessage += OnDebugMessage;
            uc_axis_config.KinematicParametersChanged += OnKinematicParametersChanged;
            uc_axis_config.OverrideChanged += RefreshAxisFunctionSelector;
            uc_function_config.SetGui(this, plugin);
            uc_axis_config.SetGui(this, plugin);

            for (FunctionID id = FunctionID.BrakePedal; id <= FunctionID.FlightStickCollective; id++)
            {
                Function function = new Function(id);
                // Config will be populated later in PopulateFunctionConfigsFromBaselines() after manager is initialized
                function.Config = FunctionConfigControl.GetDefaultConfig(id);
                functions[id] = function;
            }

            for (AxisID id = AxisID._1; id <= AxisID._8; id++)
            {
                Axis axis = new Axis(id);
                axis.Config = AxisConfigControl.GetDefaultConfig(id);
                axis.OnlineStateChanged += OnOnlineStateChange;
                axis.RequestDispatcher = EnqueueAxisRequest;
                axes[id] = axis;
            }

            axisRequestQueue = new AxisRequestQueue(this);

            UpdateUploadButtonLabels();
            InitializeLogSourceFilters();

            UpdateSerialPortList();
            InitializeSystemSettings();

            if (plugin != null)
            {
                plugin.ActiveGraphChanged += OnActiveGraphChanged_UI;
                plugin.GraphParamChanged += OnGraphParamChanged_Vehicle;
                plugin.ParamMutesCleared += OnParamMutesCleared_Vehicle;
                plugin.ParamMigrationDetected += OnParamMigrationDetected;
                plugin.FunctionConfigManager.FunctionConfigChanged += OnMergedFunctionConfigChanged;
                plugin.AxisConfigManager.AxisConfigChanged += OnMergedAxisConfigChanged;
            }
            UpdateVehicleTabHeader();
            RefreshVehicleParams();

            if (TryAdoptSingleGateway())
            {
                UpdateSerialPortList();
                ConnectToPort(Plugin.Settings.ESPNow_port);
            }
            else if (Plugin.Settings.Pedal_ESPNow_auto_connect_flag)
            {
                string gatewayPort = Plugin.ResolveGatewayPort();
                if (gatewayPort != null)
                {
                    UpdateSerialPortList();
                    ConnectToPort(gatewayPort);
                }
            }

            // Populate configs from stored baselines BEFORE selecting initial tabs,
            // so SwitchFunction/axis displays merged values instead of defaults
            PopulateFunctionConfigsFromBaselines();
            PopulateAxisConfigsFromBaselines();

            SetInitialSelections();

            // Initialize vehicle label from plugin state (UI may be created after vehicle was detected)
            string initCarId = Plugin.GetActiveCarId();
            if (!string.IsNullOrWhiteSpace(initCarId))
            {
                UpdateActiveAircraftLabel(Plugin.GetActiveCarName(), initCarId, Plugin.GetActiveGameId());
            }

            InitializeVjoyIfEnabled();
        }

        /// <summary>
        /// Populate Function.Config from stored baselines in the manager.
        /// Called after InitializeManagerFromSettings() has loaded baselines.
        /// </summary>
        public void PopulateFunctionConfigsFromBaselines()
        {
            if (Plugin == null)
                return;

            foreach (var kvp in functions)
            {
                var functionId = (int)kvp.Key;
                var function = kvp.Value;

                // Get baseline + overrides from manager
                var config = Plugin.ConfigOrchestrator.GetInitialFunctionConfig(functionId);
                if (config != null)
                {
                    function.Config = config;
                }
            }
        }

        /// <summary>
        /// Populate Axis.Config from stored baselines in the manager.
        /// Called after InitializeManagerFromSettings() has loaded baselines.
        /// </summary>
        public void PopulateAxisConfigsFromBaselines()
        {
            if (Plugin == null)
                return;

            foreach (var kvp in axes)
            {
                var axisId = (int)kvp.Key;
                var axis = kvp.Value;

                var config = Plugin.ConfigOrchestrator.GetInitialAxisConfig(axisId);
                if (config != null)
                {
                    axis.Config = config;
                    axis.HasAxisConfig = true;
                }
            }
        }

        public KinematicParameters GetKinematicParameters(AxisID axis_id)
        {
            if (axes.TryGetValue(axis_id, out Axis axis))
            {
                // Return KP whether from ESP32 config or local axis tab computation.
                // Callers filter out degenerate (zero) values.
                return axis.Config?.KinematicParameters;
            }
            return null;
        }

        /// <summary>The plugin released the gateway port (USB flash); show it as disconnected.</summary>
        public void NotifyGatewayPortClosed()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(NotifyGatewayPortClosed));
                return;
            }

            Label_Status.Text = "Disconnected";
            btn_connect_espnow_port.Content = "Connect";
            SetDebugOutput("Gateway port released for USB flashing.");
        }

        public void NotifyGatewayPortAutoConnected(string portName)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => NotifyGatewayPortAutoConnected(portName));
                return;
            }

            UpdateSerialPortList();

            if (Plugin?.ESPsync_serialPort == null)
            {
                return;
            }

            if (attachedGatewayPort != Plugin.ESPsync_serialPort)
            {
                if (attachedGatewayPort != null)
                {
                    attachedGatewayPort.OnMessage -= OnMessage;
                }
                Plugin.ESPsync_serialPort.OnMessage += OnMessage;
                attachedGatewayPort = Plugin.ESPsync_serialPort;
            }

            UsbPort item = FindPortItem(portName);
            if (item != null)
            {
                suppressSerialPortSelectionChange = true;
                SerialPortSelection_ESPNow.SelectedItem = item;
                suppressSerialPortSelectionChange = false;
            }

            if (Plugin.ESPsync_serialPort.IsOpen)
            {
                Label_Status.Text = "Connected";
                btn_connect_espnow_port.Content = "Disconnect";
                SetDebugOutput($"Auto-connected to {portName}.");
            }
        }

        private void InitializeVjoyIfEnabled()
        {
            if (Plugin.Settings.vjoy_output_flag != 1)
            {
                return;
            }

            joystick = new vJoyInterfaceWrap.vJoy();
            uint vJoystickId = Plugin.Settings.vjoy_order;
            joystick.AcquireVJD(vJoystickId);
            CenterVjoyAxes();
        }

        private void CenterVjoyAxes()
        {
            if (joystick == null)
            {
                return;
            }

            joystick.SetAxis(16384, Plugin.Settings.vjoy_order, HID_USAGES.HID_USAGE_X);
            joystick.SetAxis(16384, Plugin.Settings.vjoy_order, HID_USAGES.HID_USAGE_Y);
            joystick.SetAxis(16384, Plugin.Settings.vjoy_order, HID_USAGES.HID_USAGE_Z);
            joystick.SetAxis(16384, Plugin.Settings.vjoy_order, HID_USAGES.HID_USAGE_RX);
            joystick.SetAxis(16384, Plugin.Settings.vjoy_order, HID_USAGES.HID_USAGE_RY);
            joystick.SetAxis(16384, Plugin.Settings.vjoy_order, HID_USAGES.HID_USAGE_RZ);
        }

        private void SetInitialSelections()
        {
            int functionIndex = 0;
            int axisIndex = 0;
            if (Plugin != null)
            {
                functionIndex = (int)Plugin.Settings.function_tab_selected;
                axisIndex = (int)Plugin.Settings.axis_tab_selected;
            }

            if (tc_function_selection != null)
            {
                tc_function_selection.SelectedIndex = Math.Max(0, Math.Min(functions.Count - 1, functionIndex));
            }
            if (tc_axis_selection != null)
            {
                tc_axis_selection.SelectedIndex = Math.Max(0, Math.Min(axes.Count - 1, axisIndex));
            }

            UpdateFunctionSelection();
            UpdateAxisSelection();
        }

        private void UpdateSerialPortList()
        {
            List<UsbPort> ports = UsbDeviceScanner.GetPorts(forceRefresh: true).ToList();
            if (ports.Count == 0)
            {
                ports.Add(new UsbPort("NA"));
            }

            if (SerialPortSelection_ESPNow == null)
            {
                return;
            }

            suppressSerialPortSelectionChange = true;
            SerialPortSelection_ESPNow.ItemsSource = ports;

            UsbPort saved = Plugin != null ? FindPortItem(Plugin.Settings.ESPNow_port) : null;
            if (saved != null)
            {
                SerialPortSelection_ESPNow.SelectedItem = saved;
            }
            else if (ports.Count == 1 && ports[0].Com == "NA")
            {
                SerialPortSelection_ESPNow.SelectedIndex = 0;
            }
            else
            {
                SerialPortSelection_ESPNow.SelectedIndex = -1;
            }
            suppressSerialPortSelectionChange = false;
        }

        private UsbPort FindPortItem(string com)
        {
            if (string.IsNullOrWhiteSpace(com) || !(SerialPortSelection_ESPNow?.ItemsSource is IEnumerable<UsbPort> items))
            {
                return null;
            }
            return items.FirstOrDefault(p => string.Equals(p.Com, com, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Nothing saved yet and exactly one DIY-FFB gateway plugged in: adopt it.
        /// With several gateways the user picks; we don't guess.
        /// </summary>
        private bool TryAdoptSingleGateway()
        {
            if (Plugin == null || Plugin.Settings.GatewayUsbIndex >= 0 || !string.IsNullOrWhiteSpace(Plugin.Settings.ESPNow_port))
            {
                return false;
            }

            List<UsbPort> gateways = UsbDeviceScanner.GetPorts().Where(p => p.Role == UsbPortRole.Gateway).ToList();
            if (gateways.Count != 1)
            {
                return false;
            }

            Plugin.Settings.GatewayUsbIndex = gateways[0].Index;
            Plugin.Settings.ESPNow_port = gateways[0].Com;
            SetDebugOutput($"Found gateway {gateways[0].Index} on {gateways[0].Com}.");
            return true;
        }

        private void InitializeSystemSettings()
        {
            if (Plugin == null)
            {
                return;
            }

            if (CheckBox_Pedal_ESPNow_autoconnect != null)
            {
                CheckBox_Pedal_ESPNow_autoconnect.IsChecked = Plugin.Settings.Pedal_ESPNow_auto_connect_flag;
            }

            if (textbox_SSID != null)
            {
                textbox_SSID.Text = Plugin.Settings.SSID_string ?? string.Empty;
            }

            if (textbox_PASS != null)
            {
                textbox_PASS.Password = Plugin.Settings.PASS_string ?? string.Empty;
            }

            if (TextBox_OtaCustomUrl != null)
            {
                TextBox_OtaCustomUrl.Text = Plugin.Settings.OtaCustomUrl ?? string.Empty;
            }

            if (TextBox_OtaFfbotaPath != null)
            {
                TextBox_OtaFfbotaPath.Text = Plugin.Settings.OtaLocalFfbotaPath ?? string.Empty;
            }

            if (TextBox_OtaLocalPort != null)
            {
                TextBox_OtaLocalPort.Text = Plugin.Settings.OtaLocalPort.ToString();
            }

            if (OTAUrl_Custom != null)
            {
                OTAUrl_Custom.IsChecked = Plugin.Settings.OtaUseCustomUrl;
            }
            if (OTAUrl_Default != null && OTAUrl_Custom?.IsChecked != true)
            {
                OTAUrl_Default.IsChecked = true;
            }

            if (OTASource_Local != null)
            {
                OTASource_Local.IsChecked = Plugin.Settings.OtaUseLocalSource;
            }
            if (OTASource_Public != null && OTASource_Local?.IsChecked != true)
            {
                OTASource_Public.IsChecked = true;
            }

            UpdateOtaSourceUi();
            UpdateOtaInfoReadout("-", "-", "-");
            if (Plugin.Settings.OtaUseLocalSource && !string.IsNullOrWhiteSpace(Plugin.Settings.OtaLocalFfbotaPath))
            {
                if (TryLoadFfbotaManifest(Plugin.Settings.OtaLocalFfbotaPath, out OtaManifest manifest, out _))
                {
                    UpdateOtaInfoReadout(manifest.Board, manifest.Version, manifest.Md5);
                }
            }

            UpdateActiveAircraftLabel(Plugin.GetActiveCarName(), Plugin.GetActiveCarId(), Plugin.GetActiveGameId());
            RefreshGraphSelectionUI();
            RefreshXPlaneUdpSettings();
            RefreshUserProfileUi();
        }

        public void RefreshGraphSelection()
        {
            RefreshGraphSelectionUI();
        }

        private void RefreshXPlaneUdpSettings()
        {
            if (Plugin == null)
            {
                return;
            }

            updatingXPlaneUdp = true;
            if (CheckBox_XPlaneUdpEnabled != null)
            {
                CheckBox_XPlaneUdpEnabled.IsChecked = Plugin.Settings.XPlaneUdpEnabled;
            }
            if (TextBox_XPlanePort != null)
            {
                TextBox_XPlanePort.Text = Plugin.Settings.XPlaneUdpPort.ToString();
            }
            updatingXPlaneUdp = false;
        }

        private void RefreshUserProfileUi()
        {
            if (Plugin == null || ComboBox_UserProfile == null)
            {
                return;
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Plugin.Settings.UserPreferencesProfiles != null)
            {
                foreach (var key in Plugin.Settings.UserPreferencesProfiles.Keys)
                {
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        names.Add(key);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(Plugin.Settings.CurrentUserProfile))
            {
                names.Add(Plugin.Settings.CurrentUserProfile);
            }

            var windowsUser = System.Environment.UserName;
            if (!string.IsNullOrWhiteSpace(windowsUser))
            {
                names.Add(windowsUser);
            }

            var ordered = names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            suppressUserProfileSelectionChange = true;
            ComboBox_UserProfile.ItemsSource = ordered;
            if (!string.IsNullOrWhiteSpace(Plugin.Settings.CurrentUserProfile))
            {
                ComboBox_UserProfile.SelectedItem = Plugin.Settings.CurrentUserProfile;
            }
            else if (ordered.Count > 0)
            {
                ComboBox_UserProfile.SelectedIndex = 0;
            }

            if (ComboBox_UserProfileHeader != null)
            {
                ComboBox_UserProfileHeader.ItemsSource = ordered;
                ComboBox_UserProfileHeader.SelectedItem = ComboBox_UserProfile.SelectedItem;
            }
            suppressUserProfileSelectionChange = false;

            if (TextBlock_UserProfileInfo != null)
            {
                TextBlock_UserProfileInfo.Text = $"Profiles: {ordered.Count}";
            }
        }

        private void SetCurrentUserProfile(string userProfile)
        {
            if (Plugin == null)
            {
                return;
            }

            Plugin.ConfigOrchestrator.SetCurrentUserProfile(userProfile);
            Plugin.ResetGraphState();
            RefreshUserProfileUi();
            RefreshVehicleParams();

            // Re-render the currently selected function so sliders/labels show new user's values
            UpdateFunctionSelection();
        }

        private void RefreshGraphSelectionUI()
        {
            if (Plugin == null)
            {
                return;
            }

            // Update Vehicle tab info display
            if (TextBlock_VehicleTabActiveGraph != null)
            {
                TextBlock_VehicleTabActiveGraph.Text = Plugin.GetActiveGraphStatus();
            }

            // Note: Do not auto-sync the graph editor window here.
            // The editor should be allowed to browse includes without being forced back to the vehicle graph.
            // Auto-sync only happens when opening the editor window initially.
        }

        private void UpdateSerialPortList_click(object sender, RoutedEventArgs e)
        {
            UpdateSerialPortList();
        }

        private void btn_restart_all_axes_Click(object sender, RoutedEventArgs e)
        {
            int sentCount = 0;
            int totalCount = 0;
            foreach (var axis in axes.Values)
            {
                if (axis.ID == AxisID.AxisUndefined)
                {
                    continue;
                }
                if (!axis.IsOnline)
                {
                    continue;
                }
                totalCount++;
                if (SendAxisRequest(axis.ID, AxisRequestType.Restart, null))
                {
                    sentCount++;
                }
            }

            if (totalCount == 0)
            {
                SetDebugOutput("Restart: No axes configured.", UiLogLevel.Warning);
            }
            else if (sentCount == 0)
            {
                SetDebugOutput("Restart: No axes reachable.", UiLogLevel.Warning);
            }
            else if (sentCount == totalCount)
            {
                SetDebugOutput($"Restart: Sent to {sentCount} axes.");
            }
            else
            {
                SetDebugOutput($"Restart: Sent to {sentCount} of {totalCount} axes.");
            }
        }

        private void ESPNow_SerialPortSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Plugin == null)
            {
                return;
            }

            if (SerialPortSelection_ESPNow.SelectedItem is UsbPort port)
            {
                if (suppressSerialPortSelectionChange)
                {
                    return;
                }
                if (string.IsNullOrWhiteSpace(port.Com) || port.Com == "NA")
                {
                    return;
                }
                Plugin.Settings.ESPNow_port = port.Com;
                Plugin.Settings.GatewayUsbIndex = port.Role == UsbPortRole.Gateway ? port.Index : -1;
                SetDebugOutput($"Gateway port selected: {port.Label}");
            }
        }

        private void CheckBox_Pedal_ESPNow_autoconnect_Checked(object sender, RoutedEventArgs e)
        {
            if (Plugin != null)
            {
                Plugin.Settings.Pedal_ESPNow_auto_connect_flag = true;
            }
        }

        private void CheckBox_Pedal_ESPNow_autoconnect_Unchecked(object sender, RoutedEventArgs e)
        {
            if (Plugin != null)
            {
                Plugin.Settings.Pedal_ESPNow_auto_connect_flag = false;
            }
        }

        private void btn_save_aircraft_ffb_Click(object sender, RoutedEventArgs e)
        {
            if (Plugin == null)
            {
                return;
            }

            string profileKey = Plugin.GetActiveProfileKey();
            if (string.IsNullOrWhiteSpace(profileKey))
            {
                ThemedMessageBox.Show("No active aircraft detected.", "FFB Profiles", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Use a safe filename based on the profile key
            string safeFileName = string.Join("_", profileKey.Split(System.IO.Path.GetInvalidFileNameChars()));
            Microsoft.Win32.SaveFileDialog saveFileDialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "JSON files (*.json)|*.json",
                DefaultExt = "json",
                FileName = $"{safeFileName}_ffb.json"
            };

            if (saveFileDialog.ShowDialog() == true)
            {
                if (!Plugin.Settings.AircraftFfbProfiles.TryGetValue(profileKey, out var profile))
                {
                    ThemedMessageBox.Show("No stored profile for current aircraft.", "FFB Profiles", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var exported = new DiyFfbPluginSettings.ExportedProfile
                {
                    ProfileKey = profileKey,
                    GraphPath = Plugin.GetActiveGraphPath(),
                    ExportedAt = System.DateTime.UtcNow.ToString("o"),
                    Profile = profile
                };
                string json = JsonConvert.SerializeObject(exported, Formatting.Indented);
                System.IO.File.WriteAllText(saveFileDialog.FileName, json);
            }
        }

        private void btn_load_aircraft_ffb_Click(object sender, RoutedEventArgs e)
        {
            if (Plugin == null)
            {
                return;
            }

            string carId = Plugin.GetActiveCarId();
            Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "JSON files (*.json)|*.json",
                DefaultExt = "json",
            };

            if (openFileDialog.ShowDialog() == true)
            {
                string json = System.IO.File.ReadAllText(openFileDialog.FileName);
                DiyFfbPluginSettings.AircraftFfbProfile profile;
                string sourceGraphPath = null;

                // Try new ExportedProfile format first
                var exported = JsonConvert.DeserializeObject<DiyFfbPluginSettings.ExportedProfile>(json);
                if (exported?.Profile != null && exported.Version >= 1)
                {
                    profile = exported.Profile;
                    sourceGraphPath = exported.GraphPath;
                }
                else
                {
                    // Fall back to legacy format (just AircraftFfbProfile)
                    profile = JsonConvert.DeserializeObject<DiyFfbPluginSettings.AircraftFfbProfile>(json);
                }

                if (profile == null)
                {
                    ThemedMessageBox.Show("Invalid profile JSON.", "FFB Profiles", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Check for graph path mismatch
                string currentGraphPath = Plugin.GetActiveGraphPath();
                if (!string.IsNullOrWhiteSpace(sourceGraphPath) && !string.IsNullOrWhiteSpace(currentGraphPath))
                {
                    if (!string.Equals(sourceGraphPath, currentGraphPath, System.StringComparison.OrdinalIgnoreCase))
                    {
                        var mismatchResult = ThemedMessageBox.Show(
                            $"This profile was created for a different graph:\n\n" +
                            $"Profile graph: {sourceGraphPath}\n" +
                            $"Current graph: {currentGraphPath}\n\n" +
                            $"Parameters may not match. Continue anyway?",
                            "Graph Mismatch",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Warning);
                        if (mismatchResult != MessageBoxResult.Yes)
                        {
                            return;
                        }
                    }
                }

                // Overwrite confirmation
                string profileKey = Plugin.GetActiveProfileKey();
                if (!string.IsNullOrWhiteSpace(profileKey) && Plugin.Settings.AircraftFfbProfiles.ContainsKey(profileKey))
                {
                    var overwriteResult = ThemedMessageBox.Show(
                        $"Overwrite existing profile for '{profileKey}'?",
                        "Confirm Overwrite",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);
                    if (overwriteResult != MessageBoxResult.Yes)
                    {
                        return;
                    }
                }

                if (string.IsNullOrWhiteSpace(carId))
                {
                    Plugin.ApplyFfbProfileToCurrentSettings(profile);
                    Plugin.SetPendingFfbProfile(profile);
                    ThemedMessageBox.Show("Loaded profile into current settings (no active aircraft).", "FFB Profiles", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    Plugin.ApplyAircraftFfbProfile(carId, profile);
                }
                UpdateFunctionSelection();
            }
        }

        private void btn_manage_profiles_Click(object sender, RoutedEventArgs e)
        {
            ShowProfileBrowser(ProfileBrowserMode.ManageProfiles);
        }

        private void btn_review_params_Click(object sender, RoutedEventArgs e)
        {
            if (Plugin == null)
            {
                return;
            }

            try
            {
                // Use pending migration result if available, otherwise create empty result
                var result = _pendingMigrationResult;
                if (result == null)
                {
                    string graphPath = Plugin.GetActiveGraphPath();
                    if (string.IsNullOrWhiteSpace(graphPath))
                    {
                        ThemedMessageBox.Show("No graph is currently loaded.", "Review Params", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }

                    result = new ParamMigrationResult
                    {
                        NewHash = GraphHashComputer.ComputeGraphTreeHash(
                            DiyFfbPlugin.ResolveGraphFilePath(graphPath),
                            Plugin.GetActiveVehicleGraph()),
                        CurrentSnapshots = new Dictionary<string, DiyFfbPluginSettings.ParamSnapshot>()
                    };
                }

                var dialog = new GraphEditor.ParamReviewWindow(Plugin, result);
                dialog.Owner = Window.GetWindow(this);
                dialog.ShowDialog();

                // Clear pending result after review
                _pendingMigrationResult = null;
            }
            catch (System.Exception ex)
            {
                ThemedMessageBox.Show($"Error opening review window: {ex.Message}\n\n{ex.StackTrace}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void btn_store_profile_Click(object sender, RoutedEventArgs e)
        {
            if (Plugin == null)
            {
                return;
            }

            string profileKey = Plugin.GetActiveProfileKey();
            if (string.IsNullOrWhiteSpace(profileKey))
            {
                ThemedMessageBox.Show("No active vehicle detected.", "Store Profile", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (Plugin.StoreCurrentProfile())
            {
                ThemedMessageBox.Show($"Profile stored for {profileKey}.", "Store Profile", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void ShowProfileBrowser(ProfileBrowserMode mode)
        {
            if (Plugin == null)
            {
                return;
            }

            try
            {
                var dialog = new ProfileBrowserDialog(
                    Plugin,
                    mode,
                    Plugin.GetActiveGameId(),
                    Plugin.GetActiveCarId());

                var parentWindow = Window.GetWindow(this);
                if (parentWindow != null)
                {
                    dialog.Owner = parentWindow;
                }

                if (dialog.ShowDialog() == true && dialog.SelectedEntry != null)
                {
                    var entry = dialog.SelectedEntry;
                    string graphPath = entry.GraphPath;

                    // For templates, resolve the template path
                    if (entry.TemplateEntry != null)
                    {
                        graphPath = GraphTemplateRegistry.ResolveTemplatePath(
                            entry.TemplateEntry.TemplatePath,
                            AppDomain.CurrentDomain.BaseDirectory);
                    }

                    // "Use Tuning Only" — keep current graph, apply only the source's
                    // tuning. ApplyProfileFromBrowser treats null/empty graphPath as
                    // "don't change graph".
                    if (!dialog.UseSourceGraph)
                    {
                        graphPath = null;
                    }

                    Plugin.ApplyProfileFromBrowser(graphPath, entry.Profile, dialog.UseTuning,
                        dialog.UseFunctionTuning, dialog.UseSimVarBindings, dialog.UseActiveFunctions);
                    RefreshGraphSelection();
                }
            }
            catch (Exception ex)
            {
                ThemedMessageBox.Show($"Error opening Profile Manager:\n\n{ex.Message}\n\n{ex.StackTrace}",
                    "Profile Manager Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void btn_save_ffb_map_Click(object sender, RoutedEventArgs e)
        {
            if (Plugin == null)
            {
                return;
            }

            Microsoft.Win32.SaveFileDialog saveFileDialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "JSON files (*.json)|*.json",
                DefaultExt = "json",
                FileName = "ffb_profiles.json"
            };

            if (saveFileDialog.ShowDialog() == true)
            {
                string json = JsonConvert.SerializeObject(Plugin.Settings.AircraftFfbProfiles, Formatting.Indented);
                System.IO.File.WriteAllText(saveFileDialog.FileName, json);
            }
        }

        public bool ConfirmApplyPendingProfile(string carName, string carId)
        {
            string label = string.IsNullOrWhiteSpace(carName) ? carId : $"{carName} ({carId})";
            string message = $"A pending FFB profile is loaded without an active aircraft.\n\n" +
                             $"Apply it to {label} or discard and use the stored profile?";
            var result = ThemedMessageBox.Show(message, "FFB Profiles", MessageBoxButton.YesNo, MessageBoxImage.Question);
            return result == MessageBoxResult.Yes;
        }

        public bool ConfirmSaveCurrentProfile(string carName, string carId)
        {
            string label = string.IsNullOrWhiteSpace(carName) ? carId : $"{carName} ({carId})";
            string message = $"Save FFB changes for {label} before switching aircraft?\n\n" +
                             "Choose Yes to update the stored profile or No to discard these changes.";
            var result = ThemedMessageBox.Show(message, "FFB Profiles", MessageBoxButton.YesNo, MessageBoxImage.Question);
            return result == MessageBoxResult.Yes;
        }

        private void btn_load_ffb_map_Click(object sender, RoutedEventArgs e)
        {
            if (Plugin == null)
            {
                return;
            }

            Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "JSON files (*.json)|*.json",
                DefaultExt = "json",
            };

            if (openFileDialog.ShowDialog() == true)
            {
                string json = System.IO.File.ReadAllText(openFileDialog.FileName);
                var profiles = JsonConvert.DeserializeObject<System.Collections.Generic.Dictionary<string, DiyFfbPluginSettings.AircraftFfbProfile>>(json);
                if (profiles == null)
                {
                    ThemedMessageBox.Show("Invalid profile map JSON.", "FFB Profiles", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                Plugin.ReplaceAircraftFfbProfiles(profiles);
                RefreshFunctionSelection();
            }
        }

        private void CheckBox_XPlaneUdpEnabled_Checked(object sender, RoutedEventArgs e)
        {
            UpdateXPlaneUdpSettings(true);
        }

        private void CheckBox_XPlaneUdpEnabled_Unchecked(object sender, RoutedEventArgs e)
        {
            UpdateXPlaneUdpSettings(false);
        }

        private void TextBox_XPlanePort_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateXPlaneUdpSettings(CheckBox_XPlaneUdpEnabled?.IsChecked == true);
        }

        private void UpdateXPlaneUdpSettings(bool enabled)
        {
            if (Plugin == null || updatingXPlaneUdp)
            {
                return;
            }

            int port = Plugin.Settings.XPlaneUdpPort;
            if (TextBox_XPlanePort != null && int.TryParse(TextBox_XPlanePort.Text, out int parsedPort))
            {
                port = parsedPort;
            }

            Plugin.ApplyXPlaneUdpSettings(enabled, port);
        }

        private void ComboBox_UserProfile_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (suppressUserProfileSelectionChange)
            {
                return;
            }

            if (ComboBox_UserProfile.SelectedItem is string profileName)
            {
                SetCurrentUserProfile(profileName);
            }
        }

        private void ComboBox_UserProfileHeader_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (suppressUserProfileSelectionChange)
            {
                return;
            }

            if (ComboBox_UserProfileHeader.SelectedItem is string profileName)
            {
                SetCurrentUserProfile(profileName);
            }
        }

        private void OnReviewOverridesClick(object sender, RoutedEventArgs e)
        {
            var dialog = new OverrideReviewDialog(Plugin.ConfigOrchestrator);
            dialog.Owner = Window.GetWindow(this);
            dialog.ShowDialog();
        }

        private void btn_user_profile_refresh_Click(object sender, RoutedEventArgs e)
        {
            RefreshUserProfileUi();
        }

        private void btn_user_profile_create_Click(object sender, RoutedEventArgs e)
        {
            if (TextBox_UserProfileName == null)
            {
                return;
            }

            var profileName = TextBox_UserProfileName.Text?.Trim();
            if (string.IsNullOrWhiteSpace(profileName))
            {
                ThemedMessageBox.Show("Enter a user profile name first.", "User Profiles", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            SetCurrentUserProfile(profileName);
            TextBox_UserProfileName.Text = string.Empty;
        }

        private void TextBox_UserProfileName_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
            {
                return;
            }

            btn_user_profile_create_Click(sender, e);
            e.Handled = true;
        }

        private void btn_user_profile_delete_Click(object sender, RoutedEventArgs e)
        {
            if (Plugin == null)
            {
                return;
            }

            var profileName = ComboBox_UserProfile?.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(profileName))
            {
                return;
            }

            var result = ThemedMessageBox.Show(
                $"Delete user profile '{profileName}'? This removes stored user preference overrides.",
                "User Profiles",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            if (Plugin.Settings.UserPreferencesProfiles != null)
            {
                Plugin.Settings.UserPreferencesProfiles.Remove(profileName);
            }

            var fallback = System.Environment.UserName;
            if (string.Equals(Plugin.Settings.CurrentUserProfile, profileName, StringComparison.OrdinalIgnoreCase))
            {
                SetCurrentUserProfile(fallback);
            }
            else
            {
                RefreshUserProfileUi();
                RefreshVehicleParams();
            }
        }

        private void textbox_SSID_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (Plugin != null && textbox_SSID != null)
            {
                Plugin.Settings.SSID_string = textbox_SSID.Text ?? string.Empty;
            }
        }

        private void textbox_PASS_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (Plugin != null && textbox_PASS != null)
            {
                Plugin.Settings.PASS_string = textbox_PASS.Password ?? string.Empty;
            }
        }

        private void btn_connect_espnow_port_Click(object sender, RoutedEventArgs e)
        {
            if (Plugin != null && Plugin.ESPsync_serialPort != null && Plugin.ESPsync_serialPort.IsOpen)
            {
                DisconnectFromPort();
                return;
            }

            if (SerialPortSelection_ESPNow.SelectedItem is UsbPort port)
            {
                ConnectToPort(port.Com);
            }
        }

        private string GetOtaInfoUrl()
        {
            if (OTAUrl_Custom != null && OTAUrl_Custom.IsChecked == true)
            {
                return TextBox_OtaCustomUrl?.Text?.Trim() ?? string.Empty;
            }

            return OtaInfoUrlDefault;
        }

        private void UpdateOtaSourceUi()
        {
            bool useLocal = OTASource_Local != null && OTASource_Local.IsChecked == true;
            if (Panel_OtaPublic != null)
            {
                Panel_OtaPublic.IsEnabled = !useLocal;
                Panel_OtaPublic.Opacity = useLocal ? 0.5 : 1.0;
            }
            if (Panel_OtaCustomUrl != null)
            {
                Panel_OtaCustomUrl.IsEnabled = !useLocal && OTAUrl_Custom?.IsChecked == true;
                Panel_OtaCustomUrl.Opacity = Panel_OtaCustomUrl.IsEnabled ? 1.0 : 0.5;
            }
            if (Panel_OtaLocal != null)
            {
                Panel_OtaLocal.IsEnabled = useLocal;
                Panel_OtaLocal.Opacity = useLocal ? 1.0 : 0.5;
            }
            if (Panel_OtaLocalPort != null)
            {
                Panel_OtaLocalPort.IsEnabled = useLocal;
                Panel_OtaLocalPort.Opacity = useLocal ? 1.0 : 0.5;
            }
        }

        private void OTAUrl_Default_Checked(object sender, RoutedEventArgs e)
        {
            if (Plugin != null)
            {
                Plugin.Settings.OtaUseCustomUrl = false;
            }
            UpdateOtaSourceUi();
        }

        private void OTAUrl_Custom_Checked(object sender, RoutedEventArgs e)
        {
            if (Plugin != null)
            {
                Plugin.Settings.OtaUseCustomUrl = true;
            }
            UpdateOtaSourceUi();
        }

        private void OTASource_Public_Checked(object sender, RoutedEventArgs e)
        {
            if (Plugin != null)
            {
                Plugin.Settings.OtaUseLocalSource = false;
            }
            UpdateOtaSourceUi();
        }

        private void OTASource_Local_Checked(object sender, RoutedEventArgs e)
        {
            if (Plugin != null)
            {
                Plugin.Settings.OtaUseLocalSource = true;
            }
            UpdateOtaSourceUi();
            string ffbotaPath = TextBox_OtaFfbotaPath?.Text ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(ffbotaPath) && File.Exists(ffbotaPath))
            {
                if (TryLoadFfbotaManifest(ffbotaPath, out OtaManifest manifest, out string error))
                {
                    UpdateOtaInfoReadout(manifest.Board, manifest.Version, manifest.Md5);
                }
                else
                {
                    UpdateOtaInfoReadout("-", "-", "-");
                    SetDebugOutput(error, UiLogLevel.Error);
                }
            }
        }

        private void TextBox_OtaCustomUrl_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (Plugin != null)
            {
                Plugin.Settings.OtaCustomUrl = TextBox_OtaCustomUrl?.Text ?? string.Empty;
            }
        }

        private void TextBox_OtaLocalPort_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (Plugin == null)
            {
                return;
            }

            if (int.TryParse(TextBox_OtaLocalPort?.Text, out int port) && port > 0 && port <= 65535)
            {
                Plugin.Settings.OtaLocalPort = port;
            }
        }

        private void btn_browse_ota_ffbota_Click(object sender, RoutedEventArgs e)
        {
            Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "FFB OTA container (*.ffbota)|*.ffbota",
                DefaultExt = "ffbota",
            };

            if (openFileDialog.ShowDialog() == true)
            {
                TextBox_OtaFfbotaPath.Text = openFileDialog.FileName;
                if (Plugin != null)
                {
                    Plugin.Settings.OtaLocalFfbotaPath = openFileDialog.FileName;
                }
                if (TryLoadFfbotaManifest(openFileDialog.FileName, out OtaManifest manifest, out string error))
                {
                    UpdateOtaInfoReadout(manifest.Board, manifest.Version, manifest.Md5);
                }
                else
                {
                    UpdateOtaInfoReadout("-", "-", "-");
                    SetDebugOutput(error, UiLogLevel.Error);
                }
            }
        }

        private void UpdateOtaInfoReadout(string board, string version, string md5)
        {
            if (TextBlock_OtaBoard != null)
            {
                TextBlock_OtaBoard.Text = string.IsNullOrWhiteSpace(board) ? "-" : board;
            }
            if (TextBlock_OtaVersion != null)
            {
                TextBlock_OtaVersion.Text = string.IsNullOrWhiteSpace(version) ? "-" : version;
            }
            if (TextBlock_OtaMd5 != null)
            {
                TextBlock_OtaMd5.Text = string.IsNullOrWhiteSpace(md5) ? "-" : md5;
            }
        }

        private bool TryLoadFfbotaManifest(string path, out OtaManifest manifest, out string error)
        {
            manifest = new OtaManifest();
            error = string.Empty;
            try
            {
                using (FileStream stream = File.OpenRead(path))
                using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    ZipArchiveEntry manifestEntry = archive.GetEntry("manifest.json");
                    ZipArchiveEntry firmwareEntry = archive.GetEntry("firmware.bin");
                    if (manifestEntry == null || firmwareEntry == null)
                    {
                        error = "ffbota must contain manifest.json and firmware.bin.";
                        return false;
                    }

                    using (StreamReader reader = new StreamReader(manifestEntry.Open()))
                    {
                        string json = reader.ReadToEnd();
                        manifest = JsonConvert.DeserializeObject<OtaManifest>(json) ?? new OtaManifest();
                    }

                    string md5 = manifest.Md5?.Trim();
                    if (string.IsNullOrWhiteSpace(md5))
                    {
                        error = "ffbota manifest.json is missing md5.";
                        return false;
                    }

                    using (Stream firmwareStream = firmwareEntry.Open())
                    using (MD5 md5Hasher = MD5.Create())
                    {
                        byte[] hash = md5Hasher.ComputeHash(firmwareStream);
                        string computed = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                        if (!string.Equals(md5, computed, StringComparison.OrdinalIgnoreCase))
                        {
                            error = $"ffbota MD5 mismatch (manifest {md5}, computed {computed}).";
                            return false;
                        }
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                error = $"ffbota load failed: {ex.Message}";
                return false;
            }
        }

        private bool TryBuildLocalOtaPayload(string ffbotaPath, out byte[] infoJson, out byte[] firmwareBytes, out string infoUrl, out string expectedVersion, out string error)
        {
            infoJson = null;
            firmwareBytes = null;
            infoUrl = string.Empty;
            expectedVersion = string.Empty;
            error = string.Empty;

            if (!TryLoadFfbotaManifest(ffbotaPath, out OtaManifest manifest, out error))
            {
                return false;
            }

            try
            {
                using (FileStream stream = File.OpenRead(ffbotaPath))
                using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    ZipArchiveEntry firmwareEntry = archive.GetEntry("firmware.bin");
                    using (Stream firmwareStream = firmwareEntry.Open())
                    using (MemoryStream buffer = new MemoryStream())
                    {
                        firmwareStream.CopyTo(buffer);
                        firmwareBytes = buffer.ToArray();
                    }
                }
            }
            catch (Exception ex)
            {
                error = $"ffbota read failed: {ex.Message}";
                return false;
            }

            string hostIp = ResolveHostIp();
            int port = Plugin?.Settings?.OtaLocalPort ?? 8000;
            infoUrl = $"http://{hostIp}:{port}/update_info.json";
            string firmwareUrl = $"http://{hostIp}:{port}/firmware.bin";
            var infoPayload = new
            {
                Configurations = new[]
                {
                    new
                    {
                        Board = manifest.Board ?? string.Empty,
                        Version = manifest.Version ?? string.Empty,
                        URL = firmwareUrl,
                        MD5 = manifest.Md5 ?? string.Empty
                    }
                }
            };
            infoJson = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(infoPayload));
            UpdateOtaInfoReadout(manifest.Board, manifest.Version, manifest.Md5);
            expectedVersion = manifest.Version ?? string.Empty;
            return true;
        }

        private string ResolveHostIp()
        {
            try
            {
                using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    socket.Connect("8.8.8.8", 80);
                    if (socket.LocalEndPoint is IPEndPoint endPoint)
                    {
                        return endPoint.Address.ToString();
                    }
                }
            }
            catch
            {
            }
            return "127.0.0.1";
        }

        private void WarnIfOtaUrlAclMissing()
        {
            int port = Plugin?.Settings?.OtaLocalPort ?? 8000;
            string host = ResolveHostIp();
            string prefix = $"http://{host}:{port}/";

            try
            {
                TcpListener listener = new TcpListener(IPAddress.Any, port);
                try
                {
                    listener.Start();
                    listener.Stop();
                }
                finally
                {
                    listener.Stop();
                }
                otaSelectionDialog?.SetBindingUrl(prefix);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                if (!otaAclWarned)
                {
                    LogOta($"OTA local binding failed (port {port} already in use).");
                    otaAclWarned = true;
                }
            }
            catch
            {
                // Ignore other listener issues; OTA start will surface them.
            }
        }

        private void LogOta(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            if (otaSelectionDialog != null)
            {
                otaSelectionDialog.AppendLog(message);
            }

            SetDebugOutput(message);
        }

        private void ShowCopyableMessage(string title, string message)
        {
            Window dialog = new Window
            {
                Title = title,
                Width = 520,
                Height = 220,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Topmost = true,
                ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25)),
                Foreground = Brushes.White,
                Content = new Grid()
            };

            Grid grid = (Grid)dialog.Content;
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBox textBox = new TextBox
            {
                Text = message,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x25, 0x25)),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x4E, 0x4E, 0x4E)),
                Margin = new Thickness(12),
                Foreground = Brushes.White
            };
            Grid.SetRow(textBox, 0);
            grid.Children.Add(textBox);

            Button okButton = new Button
            {
                Content = "OK",
                Width = 80,
                Height = 28,
                Margin = new Thickness(0, 0, 12, 12),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            okButton.Click += (_, __) => dialog.Close();
            Grid.SetRow(okButton, 1);
            grid.Children.Add(okButton);

            dialog.ShowDialog();
        }

        private void btn_start_ota_Click(object sender, RoutedEventArgs e)
        {
            if (Plugin == null || Plugin.ESPsync_serialPort == null || !Plugin.ESPsync_serialPort.IsOpen)
            {
                LogOta("Connect the gateway before starting OTA.");
                return;
            }

            BuildOtaTargets();
            RequestOtaDeviceInfo();

            if (otaSelectionDialog != null)
            {
                otaSelectionDialog.Close();
                otaSelectionDialog = null;
            }

            otaSelectionDialog = new OtaSelectionDialog(ota_targets);
            otaSelectionDialog.StartRequested += OnOtaStartRequested;
            otaSelectionDialog.Closed += (s, args) =>
            {
                otaUpdateCancellation?.Cancel();
                StopLocalOtaServer();
                otaSelectionDialog = null;
            };
            WarnIfOtaUrlAclMissing();
            otaSelectionDialog.Show();
        }

        private void StopLocalOtaServer()
        {
            if (otaServer != null)
            {
                otaServer.Dispose();
                otaServer = null;
            }
        }

        private async void OnOtaStartRequested(object sender, EventArgs e)
        {
            if (Plugin == null || Plugin.ESPsync_serialPort == null || !Plugin.ESPsync_serialPort.IsOpen)
            {
                SetDebugOutput("Connect the gateway before starting OTA.", UiLogLevel.Warning);
                return;
            }

            string ssid = Plugin.Settings.SSID_string ?? string.Empty;
            string pass = Plugin.Settings.PASS_string ?? string.Empty;
            if (ssid.Length > MaxWifiCredentialLength || pass.Length > MaxWifiCredentialLength)
            {
                LogOta($"SSID and password must be {MaxWifiCredentialLength} characters or less.");
                return;
            }

            string infoUrl = GetOtaInfoUrl();
            string expectedVersion = string.Empty;
            if (OTASource_Local != null && OTASource_Local.IsChecked == true)
            {
                string ffbotaPath = TextBox_OtaFfbotaPath?.Text ?? string.Empty;
                if (string.IsNullOrWhiteSpace(ffbotaPath) || !File.Exists(ffbotaPath))
                {
                    LogOta("Select a valid .ffbota file.");
                    return;
                }

                if (!TryBuildLocalOtaPayload(ffbotaPath, out byte[] infoJson, out byte[] firmwareBytes, out string localInfoUrl, out expectedVersion, out string error))
                {
                    LogOta(error);
                    return;
                }

                StopLocalOtaServer();
                try
                {
                    otaServer = new LocalOtaServer(localInfoUrl, infoJson, firmwareBytes, LogOta);
                    otaServer.Start();
                    infoUrl = otaServer.InfoUrl;
                    otaSelectionDialog?.SetBindingUrl(infoUrl);
                    otaSelectionDialog?.AppendLog($"Binding URL: {infoUrl}");
                }
                catch (Exception ex)
                {
                    LogOta($"Failed to start local OTA server: {ex.Message}");
                    StopLocalOtaServer();
                    return;
                }
            }
            else
            {
                StopLocalOtaServer();
                if (string.IsNullOrWhiteSpace(infoUrl))
                {
                    LogOta("Enter a valid OTA URL.");
                    return;
                }

                if (!TryLoadOtaInfoFromUrl(infoUrl, out string board, out string version, out string md5, out expectedVersion, out string error))
                {
                    LogOta(error);
                    return;
                }
                UpdateOtaInfoReadout(board, version, md5);
                otaSelectionDialog?.SetBindingUrl(infoUrl);
            }

            bool allowDowngrades = Checkbox_Force_flash != null && Checkbox_Force_flash.IsChecked == true;
            var selectedAxes = ota_targets.Where(t => t.Selected && t.IsOnline && t.AxisId != AxisID.AxisUndefined).ToList();
            var selectedGateways = ota_targets.Where(t => t.Selected && t.IsOnline && t.GatewayId != GatewayID.GatewayUndefined).ToList();

            otaUpdateCancellation?.Cancel();
            otaUpdateCancellation = new CancellationTokenSource();

            OtaUpdateCoordinator coordinator = new OtaUpdateCoordinator(
                axisId => ota_axis_versions.TryGetValue(axisId, out string version) ? version : "-",
                gatewayId => ota_gateway_versions.TryGetValue(gatewayId, out string version) ? version : "-",
                axisId =>
                {
                    StartOtaUpdate startOtaUpdate = new StartOtaUpdate
                    {
                        WifiInfo = new WifiInfo
                        {
                            Ssid = ssid,
                            Password = pass
                        },
                        InfoJsonUrl = infoUrl,
                        AllowDowngrades = allowDowngrades,
                        Target = OtaTarget.AxesOnly,
                        TargetAxisId = axisId
                    };
                    Message msg = new Message { StartOtaUpdate = startOtaUpdate };
                    Plugin.ESPsync_serialPort.WriteMessage(msg);
                },
                gatewayId =>
                {
                    StartOtaUpdate startOtaUpdate = new StartOtaUpdate
                    {
                        WifiInfo = new WifiInfo
                        {
                            Ssid = ssid,
                            Password = pass
                        },
                        InfoJsonUrl = infoUrl,
                        AllowDowngrades = allowDowngrades,
                        Target = OtaTarget.GatewayOnly
                    };
                    Message msg = new Message { StartOtaUpdate = startOtaUpdate };
                    Plugin.ESPsync_serialPort.WriteMessage(msg);
                },
                message => LogOta(message));

            try
            {
                await coordinator.RunAsync(
                    selectedAxes.Select(entry => entry.AxisId).ToList(),
                    selectedGateways.Select(entry => entry.GatewayId).ToList(),
                    expectedVersion,
                    otaUpdateCancellation.Token);
                LogOta("OTA update flow complete.");
            }
            catch (OperationCanceledException)
            {
                LogOta("OTA update cancelled.");
            }
        }

        private void BuildOtaTargets()
        {
            ota_targets.Clear();
            ota_axis_targets.Clear();
            ota_gateway_targets.Clear();

            if (Plugin?.ESPsync_serialPort != null && Plugin.ESPsync_serialPort.IsOpen)
            {
                GatewayID gatewayId = last_gateway_id == GatewayID.GatewayUndefined ? GatewayID._1 : last_gateway_id;
                var gatewayEntry = new OtaTargetEntry(gatewayId)
                {
                    IsOnline = true,
                    Selected = true
                };
                if (ota_gateway_logs.TryGetValue(gatewayId, out string gatewayLog))
                {
                    gatewayEntry.LatestLog = gatewayLog;
                }
                if (ota_gateway_versions.TryGetValue(gatewayId, out string gatewayVersion))
                {
                    gatewayEntry.LatestVersion = gatewayVersion;
                }
                ota_gateway_targets[gatewayId] = gatewayEntry;
                ota_targets.Add(gatewayEntry);
            }

            foreach (var axis in axes.Values)
            {
                if (!axis.IsOnline)
                {
                    continue;
                }
                var axisEntry = new OtaTargetEntry(axis.ID)
                {
                    IsOnline = true,
                    Selected = true
                };
                if (ota_axis_logs.TryGetValue(axis.ID, out string axisLog))
                {
                    axisEntry.LatestLog = axisLog;
                }
                if (ota_axis_versions.TryGetValue(axis.ID, out string axisVersion))
                {
                    axisEntry.LatestVersion = axisVersion;
                }
                ota_axis_targets[axis.ID] = axisEntry;
                ota_targets.Add(axisEntry);
            }
        }

        private void RequestOtaDeviceInfo()
        {
            if (Plugin?.ESPsync_serialPort == null || !Plugin.ESPsync_serialPort.IsOpen)
            {
                return;
            }

            foreach (var entry in ota_targets)
            {
                Message msg = new Message();
                msg.DeviceInfoRequest = new DeviceInfoRequest();
                if (entry.AxisId != AxisID.AxisUndefined)
                {
                    msg.DeviceInfoRequest.AxisId = entry.AxisId;
                }
                else if (entry.GatewayId != GatewayID.GatewayUndefined)
                {
                    msg.DeviceInfoRequest.GatewayId = entry.GatewayId;
                }
                Plugin.ESPsync_serialPort.WriteMessage(msg);
            }
        }

        private void UpdateOtaAxisLog(AxisLogMessage msg)
        {
            if (msg.AxisId != AxisID.AxisUndefined && ota_axis_targets.TryGetValue(msg.AxisId, out var entry))
            {
                entry.LatestLog = msg.Msg ?? string.Empty;
            }
            if (msg.AxisId != AxisID.AxisUndefined)
            {
                ota_axis_logs[msg.AxisId] = msg.Msg ?? string.Empty;
            }
        }

        private void UpdateOtaGatewayLog(GatewayLogMessage msg)
        {
            if (msg.GatewayId != GatewayID.GatewayUndefined && ota_gateway_targets.TryGetValue(msg.GatewayId, out var entry))
            {
                entry.LatestLog = msg.Msg ?? string.Empty;
            }
            if (msg.GatewayId != GatewayID.GatewayUndefined)
            {
                ota_gateway_logs[msg.GatewayId] = msg.Msg ?? string.Empty;
            }
        }

        private void UpdateOtaDeviceInfo(DeviceInfo info)
        {
            if (info.AxisId != AxisID.AxisUndefined && ota_axis_targets.TryGetValue(info.AxisId, out var axisEntry))
            {
                axisEntry.LatestVersion = string.IsNullOrWhiteSpace(info.FwVersion) ? "-" : info.FwVersion;
                axisEntry.IsOnline = true;
            }
            else if (info.GatewayId != GatewayID.GatewayUndefined && ota_gateway_targets.TryGetValue(info.GatewayId, out var gatewayEntry))
            {
                gatewayEntry.LatestVersion = string.IsNullOrWhiteSpace(info.FwVersion) ? "-" : info.FwVersion;
                gatewayEntry.IsOnline = true;
            }
            if (info.AxisId != AxisID.AxisUndefined)
            {
                ota_axis_versions[info.AxisId] = string.IsNullOrWhiteSpace(info.FwVersion) ? "-" : info.FwVersion;
            }
            else if (info.GatewayId != GatewayID.GatewayUndefined)
            {
                ota_gateway_versions[info.GatewayId] = string.IsNullOrWhiteSpace(info.FwVersion) ? "-" : info.FwVersion;
            }
        }

        private bool TryLoadOtaInfoFromUrl(string url, out string board, out string version, out string md5, out string expectedVersion, out string error)
        {
            board = "-";
            version = "-";
            md5 = "-";
            expectedVersion = string.Empty;
            error = string.Empty;
            try
            {
                using (WebClient client = new WebClient())
                {
                    string json = client.DownloadString(url);
                    var payload = JsonConvert.DeserializeObject<OtaInfoPayload>(json);
                    if (payload?.Configurations == null || payload.Configurations.Count == 0)
                    {
                        error = "OTA JSON has no configurations.";
                        return false;
                    }

                    var boards = payload.Configurations.Select(cfg => cfg.Board).Distinct().ToList();
                    var versions = payload.Configurations.Select(cfg => cfg.Version).Distinct().ToList();
                    var md5s = payload.Configurations.Select(cfg => cfg.Md5).Distinct().ToList();

                    board = boards.Count == 1 ? boards[0] : "Multiple";
                    version = versions.Count == 1 ? versions[0] : "Multiple";
                    md5 = md5s.Count == 1 ? md5s[0] : "Multiple";
                    if (versions.Count > 0)
                    {
                        expectedVersion = versions[0];
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                error = $"Failed to load OTA JSON: {ex.Message}";
                return false;
            }
        }

        private void ConnectToPort(string portName)
        {
            if (Plugin == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(portName) || portName == "NA")
            {
                SetDebugOutput("Select a valid port before connecting.", UiLogLevel.Warning);
                return;
            }

            DisconnectFromPort();

            try
            {
                Plugin.ESPsync_serialPort = new ProtobufSerial<Message>(portName, 3000000);
                Plugin.ESPsync_serialPort.OnMessage += OnMessage;
                Plugin.ESPsync_serialPort.Open();
                Label_Status.Text = "Connected";
                btn_connect_espnow_port.Content = "Disconnect";
                SetDebugOutput($"Connected to {portName}.");
            }
            catch (Exception ex)
            {
                Label_Status.Text = "Disconnected";
                btn_connect_espnow_port.Content = "Connect";
                SetDebugOutput($"Failed to connect to {portName}: {ex.Message}", UiLogLevel.Error);
            }
        }

        private void DisconnectFromPort()
        {
            if (Plugin == null)
            {
                return;
            }

            try
            {
                if (Plugin.ESPsync_serialPort != null)
                {
                    Plugin.ESPsync_serialPort.OnMessage -= OnMessage;
                    Plugin.ESPsync_serialPort.Close();
                }
            }
            catch (Exception ex)
            {
                SetDebugOutput($"Disconnect failed: {ex.Message}", UiLogLevel.Error);
            }

            foreach (Axis axis in axes.Values)
            {
                axis.SerialChannel = null;
            }

            Label_Status.Text = "Disconnected";
            if (btn_connect_espnow_port != null)
            {
                btn_connect_espnow_port.Content = "Connect";
            }
        }

        public void CloseSerialPorts()
        {
            DisconnectFromPort();
        }

        private void UpdateUploadButtonLabels()
        {
            string label = PersistConfig ? "Upload and Persist" : "Upload";
            btn_upload_axis_config.Content = label;
            btn_upload_function_config.Content = label;
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl)
            {
                persistConfigModifier = true;
                UpdateUploadButtonLabels();
            }
        }

        private void OnPreviewKeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl)
            {
                persistConfigModifier = false;
                UpdateUploadButtonLabels();
            }
        }

        private void OnAxisSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Plugin == null)
            {
                return;
            }

            UpdateAxisSelection();
        }

        private void OnFunctionSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Plugin == null)
            {
                return;
            }

            UpdateFunctionSelection();
        }

        /// <summary>
        /// Per-axis memory of which function was last selected in the axis function selector.
        /// -1 means baseline. Persists across axis tab switches within the session.
        /// </summary>
        private readonly Dictionary<AxisID, int> _lastSelectedFunctionPerAxis = new Dictionary<AxisID, int>();

        private void UpdateAxisSelection()
        {
            if (tc_axis_selection == null)
            {
                return;
            }

            if (tc_axis_selection.SelectedItem is KeyValuePair<AxisID, Axis> axisEntry)
            {
                // Save the outgoing axis's function selection
                if (selected_axis_id != AxisID.AxisUndefined)
                {
                    _lastSelectedFunctionPerAxis[selected_axis_id] = uc_axis_config.SelectedFunctionId;
                }

                selected_axis_id = axisEntry.Key;
                if (Plugin != null)
                {
                    Plugin.Settings.axis_tab_selected = (uint)Math.Max(0, (int)selected_axis_id - 1);
                }
                uc_axis_config.UpdateConfig(axisEntry.Value.Config);

                // Restore the incoming axis's last function selection
                if (_lastSelectedFunctionPerAxis.TryGetValue(selected_axis_id, out int funcId)
                    && funcId >= 0 && AxisFunctionSelector?.ItemsSource != null)
                {
                    var items = AxisFunctionSelector.ItemsSource as List<FunctionSelectorItem>;
                    var match = items?.FirstOrDefault(i => i.FunctionId == funcId);
                    if (match != null)
                    {
                        AxisFunctionSelector.SelectedItem = match;
                    }
                }
            }
        }

        private void UpdateFunctionSelection()
        {
            if (tc_function_selection == null)
            {
                return;
            }

            if (tc_function_selection.SelectedItem is KeyValuePair<FunctionID, Function> functionEntry)
            {
                selected_function_id = functionEntry.Key;
                if (Plugin != null)
                {
                    Plugin.Settings.function_tab_selected = (uint)Math.Max(0, (int)selected_function_id - 1);
                    indexOfSelectedPedal_u = Plugin.Settings.function_tab_selected;
                }
                uc_function_config.SwitchFunction(functionEntry.Value);
            }
        }

        public void RefreshFunctionSelection()
        {
            UpdateFunctionSelection();
        }

        private void Function_Tab_seleciton_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
        }

        private void OnABSTestStateChange(bool state)
        {
            if (Plugin != null)
            {
                Plugin.sendAbsSignal = state;
            }
        }

        private void OnDebugMessage(string message)
        {
            SetDebugOutput(message, UiLogLevel.Debug);
        }

        private void btn_clear_log_Click(object sender, RoutedEventArgs e)
        {
            ClearUiLog();
        }

        private void InitializeLogSourceFilters()
        {
            AddLogSourceFilter(UiLogSourceKind.Plugin, null, "Plugin");
        }

        private void AddLogSourceFilter(UiLogSourceKind kind, int? id, string label)
        {
            string key = GetSourceKey(kind, id);
            if (logSourceFilterLookup.ContainsKey(key))
            {
                return;
            }

            var item = new LogSourceFilterItem(kind, id, label, true);
            item.PropertyChanged += OnLogSourceFilterChanged;
            logSourceFilterLookup[key] = item;
            int insertIndex = 0;
            int itemRank = GetLogSourceSortRank(kind, id);
            while (insertIndex < LogSourceFilters.Count)
            {
                var existing = LogSourceFilters[insertIndex];
                if (GetLogSourceSortRank(existing.Kind, existing.Id) > itemRank)
                {
                    break;
                }
                insertIndex++;
            }
            LogSourceFilters.Insert(insertIndex, item);
        }

        private void OnLogSourceFilterChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(LogSourceFilterItem.IsSelected))
            {
                UiLogView?.Refresh();
            }
        }

        private bool FilterLogEntry(object entry)
        {
            if (entry is UiLogEntry logEntry)
            {
                if (LogSourceFilters.Count == 0)
                {
                    return true;
                }

                string key = GetSourceKey(logEntry.SourceKind, logEntry.SourceId);
                if (logSourceFilterLookup.TryGetValue(key, out LogSourceFilterItem filterItem))
                {
                    return filterItem.IsSelected;
                }
            }

            return true;
        }

        private static string GetSourceKey(UiLogSourceKind kind, int? id)
        {
            return id.HasValue ? $"{kind}:{id.Value}" : $"{kind}";
        }

        private static int GetLogSourceSortRank(UiLogSourceKind kind, int? id)
        {
            int kindRank = kind == UiLogSourceKind.Plugin ? 0 : kind == UiLogSourceKind.Gateway ? 1 : 2;
            int idRank = id ?? -1;
            return (kindRank * 100) + idRank;
        }

        private void ClearUiLog()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(ClearUiLog);
                return;
            }

            UiLogEntries.Clear();
        }

        private void SetDebugOutput(string message, UiLogLevel level = UiLogLevel.Info)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => SetDebugOutput(message, level));
                return;
            }

            string resolvedMessage = (message ?? string.Empty).TrimEnd('\r', '\n');
            if (TextBlock_Status != null)
            {
                TextBlock_Status.Text = resolvedMessage;
            }

            if (string.IsNullOrWhiteSpace(resolvedMessage))
            {
                return;
            }

            AppendUiLog(resolvedMessage, level, UiLogSourceKind.Plugin, null);
        }

        private void AppendUiLog(string message, UiLogLevel level, UiLogSourceKind sourceKind, int? sourceId)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => AppendUiLog(message, level, sourceKind, sourceId));
                return;
            }

            var entry = new UiLogEntry
            {
                Time = DateTime.Now.ToString("HH:mm:ss"),
                Level = level.ToString().ToUpperInvariant(),
                Source = BuildSourceLabel(sourceKind, sourceId),
                Message = (message ?? string.Empty).TrimEnd('\r', '\n')
            };
            entry.SourceKind = sourceKind;
            entry.SourceId = sourceId;

            UiLogEntries.Add(entry);
            while (UiLogEntries.Count > UiLogMaxEntries)
            {
                UiLogEntries.RemoveAt(0);
            }

            ListBox_UiLog?.ScrollIntoView(entry);
        }

        private static string BuildSourceLabel(UiLogSourceKind kind, int? id)
        {
            switch (kind)
            {
                case UiLogSourceKind.Gateway:
                    return id.HasValue ? $"Gateway {id.Value}" : "Gateway";
                case UiLogSourceKind.Axis:
                    return id.HasValue ? $"Axis {id.Value}" : "Axis";
                default:
                    return "Plugin";
            }
        }

        private void OnKinematicParametersChanged(KinematicParameters parameters)
        {
            // This event fires from the axis tab's GeneralKinematicsControl, which
            // computes KP for whichever axis is currently selected in the axis tab.
            // Only forward to the function control if that axis is actually linked
            // to the currently selected function — otherwise we'd apply wrong bounds.
            if (selected_function_id == FunctionID.Undefined ||
                !functions.TryGetValue(selected_function_id, out Function function))
                return;

            FunctionConfig cfg = function.Config;
            bool axisLinked = false;
            if (cfg?.Base != null)
            {
                foreach (var axis in cfg.Base.LinkedAxes)
                {
                    if ((axis & AxisID.Mask) == selected_axis_id)
                    {
                        axisLinked = true;
                        break;
                    }
                }
            }
            if (!axisLinked && cfg?.AuxFunction != null)
            {
                foreach (var axis in cfg.AuxFunction.LinkedAxes)
                {
                    if ((axis & AxisID.Mask) == selected_axis_id)
                    {
                        axisLinked = true;
                        break;
                    }
                }
            }

            if (!axisLinked)
                return;

            uc_function_config.OnKinematicParametersChanged(parameters);
        }

        private void OnOnlineStateChange(AxisID axis_id, bool new_online_state)
        {
            string msg = new_online_state
                ? $"Axis {(int)axis_id} Connected"
                : $"Axis {(int)axis_id} Disconnected";

            if (!new_online_state && last_known_functions.ContainsKey(axis_id))
            {
                functions[last_known_functions[axis_id]].OnAxisRemoved(axis_id);
                last_known_functions.Remove(axis_id);
            }

            ToastNotification("Axis Connection", msg);

            if (ota_axis_targets.TryGetValue(axis_id, out OtaTargetEntry entry))
            {
                entry.IsOnline = new_online_state;
            }

            if (new_online_state && axis_id != AxisID.AxisUndefined && seenAxisSources.Add(axis_id))
            {
                AddLogSourceFilter(UiLogSourceKind.Axis, (int)axis_id, $"Axis {(int)axis_id}");
            }

            // A reconnecting axis (after an axis or gateway restart) comes back with
            // its persisted/default config — graph-derived (ConfigOut) and other
            // non-stored fields are lost. Proactively re-push the full merged config
            // we already hold so the axis is restored to exactly its prior state,
            // rather than relying on the device-report + baseline-gated push-back.
            if (new_online_state && axis_id != AxisID.AxisUndefined)
            {
                RepushActiveConfigsForAxis(axis_id);
            }
        }

        // Re-uploads the current merged config (baseline + profile + user + ConfigOut)
        // for every active function bound to the given axis. Used on axis reconnect to
        // restore state the device drops on restart, including ConfigOut-derived fields.
        private void RepushActiveConfigsForAxis(AxisID axisId)
        {
            if (axisId == AxisID.AxisUndefined)
            {
                return;
            }

            foreach (var funcId in functions.Keys)
            {
                int id = (int)funcId;
                if (!Plugin.ConfigOrchestrator.IsFunctionActive(id))
                {
                    continue;
                }

                FunctionConfig merged = Plugin.FunctionConfigManager.GetCurrentConfig(id);
                if (merged == null)
                {
                    continue;
                }

                bool linksThisAxis = merged.Base.LinkedAxes.Any(a => (a & AxisID.Mask) == axisId);
                if (!linksThisAxis)
                {
                    continue;
                }

                EnqueueFunctionConfigUpload(merged, store: false, verify: true);
                Plugin.FunctionConfigManager.MarkAsSent(id, merged);
            }
        }

        private void ToastNotification(string message1, string message2)
        {
            var xml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
            var text = xml.GetElementsByTagName("text");
            text[0].AppendChild(xml.CreateTextNode(message1));
            text[1].AppendChild(xml.CreateTextNode(message2));
            var toast = new ToastNotification(xml);
            toast.ExpirationTime = DateTime.Now.AddSeconds(1);
            toast.Tag = "Pedal_notification";
            ToastNotificationManager.CreateToastNotifier("FFB Pedal Dashboard").Show(toast);
        }

        private void OnMessage(object sender, object message)
        {
            ProtobufSerial<Message> port = sender as ProtobufSerial<Message>;
            Message msg = message as Message;
            if (port == null || msg == null)
            {
                return;
            }

            Dispatcher.Invoke(() => HandleMessage(port, msg));
        }

        private void HandleMessage(ProtobufSerial<Message> port, Message msg)
        {
            axisRequestQueue?.HandleResponse(msg);
            switch (msg.PayloadCase)
            {
                case Message.PayloadOneofCase.AxisState:
                    HandleAxisState(port, msg.AxisState);
                    break;
                case Message.PayloadOneofCase.GatewayState:
                    ProcessGatewayState(port, msg.GatewayState);
                    break;
                case Message.PayloadOneofCase.AxisConfig:
                    RegisterAxisChannel(msg.AxisConfig.AxisId, port);
                    OnAxisConfigUpdate(msg.AxisConfig, fromEsp32: true);
                    break;
                case Message.PayloadOneofCase.FunctionConfig:
                    OnFunctionConfigUpdate(msg.FunctionConfig, fromEsp32: true);
                    break;
                case Message.PayloadOneofCase.AxisLogMessage:
                    int? axisSourceId = msg.AxisLogMessage.AxisId != AxisID.AxisUndefined ? (int)msg.AxisLogMessage.AxisId : (int?)null;
                    AppendUiLog(BuildAxisLogLine(msg.AxisLogMessage), MapLogLevel(msg.AxisLogMessage.Level), UiLogSourceKind.Axis, axisSourceId);
                    UpdateOtaAxisLog(msg.AxisLogMessage);
                    break;
                case Message.PayloadOneofCase.GatewayLogMessage:
                    int? gatewaySourceId = msg.GatewayLogMessage.GatewayId != GatewayID.GatewayUndefined ? (int)msg.GatewayLogMessage.GatewayId : (int?)null;
                    AppendUiLog(BuildGatewayLogLine(msg.GatewayLogMessage), MapLogLevel(msg.GatewayLogMessage.Level), UiLogSourceKind.Gateway, gatewaySourceId);
                    UpdateOtaGatewayLog(msg.GatewayLogMessage);
                    break;
                case Message.PayloadOneofCase.DeviceInfo:
                    UpdateOtaDeviceInfo(msg.DeviceInfo);
                    break;
                case Message.PayloadOneofCase.ActiveFunction:
                    RegisterAxisChannel(msg.ActiveFunction.AxisId, port);
                    UpdateActiveFunction(msg.ActiveFunction);
                    break;
                case Message.PayloadOneofCase.StaticBalanceResult:
                    RegisterAxisChannel(msg.StaticBalanceResult.AxisId, port);
                    if (axes.TryGetValue(msg.StaticBalanceResult.AxisId, out Axis axis))
                    {
                        AxisConfigControl.ApplyStaticBalanceResult(axis.Config, msg.StaticBalanceResult);
                        if (msg.StaticBalanceResult.AxisId == selected_axis_id)
                        {
                            uc_axis_config.OnStaticBalanceResult(msg.StaticBalanceResult);
                        }
                    }
                    break;
                default:
                    break;
            }
        }

        private void HandleAxisState(ProtobufSerial<Message> port, AxisState axisState)
        {
            RegisterAxisChannel(axisState.AxisId, port);
            uc_function_config.OnAxisStateUpdate(axisState);
            uc_axis_config.OnAxisStateUpdate(axisState);
            UpdateVjoy(axisState);
            Plugin?.UpdateAxisState(axisState.AxisId, axisState.Position, axisState.Force);
        }

        public void RequestStaticBalanceCalibration(AxisID axisId)
        {
            if (axisId == AxisID.AxisUndefined)
            {
                SetDebugOutput("Static balance: Axis ID undefined", UiLogLevel.Warning);
                return;
            }
            if (!axes.TryGetValue(axisId, out Axis axis))
            {
                SetDebugOutput($"Static balance: Axis {axisId} not found", UiLogLevel.Warning);
                return;
            }
            EnqueueAxisRequest(axisId, AxisRequestType.StaticBalanceCalibration, null);
        }

        private bool EnqueueAxisRequest(AxisID axisId, AxisRequestType type, Message payload)
        {
            axisRequestQueue?.Enqueue(axisId, type, payload);
            return true;
        }

        public bool SendAxisRequest(AxisID axisId, AxisRequestType type, Message payload)
        {
            Message msg = payload ?? new Message();
            switch (type)
            {
                case AxisRequestType.AxisConfig:
                    msg.AxisAction = new AxisAction { AxisId = axisId, ReturnAxisConfig = true };
                    break;
                case AxisRequestType.FunctionConfig:
                    msg.AxisAction = new AxisAction { AxisId = axisId, ReturnFunctionConfig = true };
                    break;
                case AxisRequestType.ActiveFunction:
                    msg.AxisAction = new AxisAction { AxisId = axisId, ReturnActiveFunction = true };
                    break;
                case AxisRequestType.DeviceInfo:
                    msg.DeviceInfoRequest = new DeviceInfoRequest { AxisId = axisId };
                    break;
                case AxisRequestType.Restart:
                    msg.AxisAction = new AxisAction { AxisId = axisId, Restart = true };
                    break;
                case AxisRequestType.Homing:
                    msg.AxisAction = new AxisAction { AxisId = axisId, StartHoming = true };
                    break;
                case AxisRequestType.StaticBalanceCalibration:
                    msg.AxisAction = new AxisAction { AxisId = axisId, StartStaticBalanceCalibration = true };
                    break;
                case AxisRequestType.AxisConfigUpload:
                case AxisRequestType.FunctionConfigUpload:
                    break;
                default:
                    return false;
            }

            if (axes.TryGetValue(axisId, out Axis axis))
            {
                var serialChannel = axis.SerialChannel;
                if (serialChannel != null && serialChannel != Plugin.ESPsync_serialPort)
                {
                    serialChannel.WriteMessage(msg);
                    return true;
                }
            }
            if (Plugin?.ESPsync_serialPort != null && Plugin.ESPsync_serialPort.IsOpen)
            {
                Plugin.ESPsync_serialPort.WriteMessage(msg);
                return true;
            }
            return false;
        }

        private void EnqueueAxisConfigUpload(AxisID axisId, AxisConfig axisConfig, bool store,
            bool verify = false)
        {
            if (axisId == AxisID.AxisUndefined)
            {
                return;
            }
            AxisConfig configToSend = axisConfig.Clone();
            configToSend.Store = store;
            Message msg = new Message { AxisConfig = configToSend };
            axisRequestQueue?.Enqueue(axisId, AxisRequestType.AxisConfigUpload, msg,
                verifyAfterSend: verify);
        }

        private void EnqueueFunctionConfigUpload(FunctionConfig functionConfig, bool store,
            bool verify = false)
        {
            FunctionConfig configToSend = functionConfig.Clone();
            configToSend.Base.Store = store;
            Message msg = new Message { FunctionConfig = configToSend };
            bool broadcastRequired = false;
            bool verifiedOne = false;
            AxisID firstLinkedAxis = AxisID.AxisUndefined;
            HashSet<AxisID> queuedAxes = new HashSet<AxisID>();
            foreach (var linkedAxisId in configToSend.Base.LinkedAxes)
            {
                var axisId = linkedAxisId & AxisID.Mask;
                if (axisId == AxisID.AxisUndefined)
                {
                    continue;
                }
                if (firstLinkedAxis == AxisID.AxisUndefined)
                {
                    firstLinkedAxis = axisId;
                }
                if (!axes.TryGetValue(axisId, out Axis axis))
                {
                    continue;
                }
                var serialChannel = axis.SerialChannel;
                if (serialChannel != null && serialChannel != Plugin.ESPsync_serialPort)
                {
                    if (queuedAxes.Add(axisId))
                    {
                        // Verify on the first per-axis upload only
                        bool verifyThis = verify && !verifiedOne;
                        axisRequestQueue?.Enqueue(axisId, AxisRequestType.FunctionConfigUpload,
                            msg, verifyAfterSend: verifyThis);
                        if (verifyThis) verifiedOne = true;
                    }
                }
                else
                {
                    broadcastRequired = true;
                }
            }

            if (broadcastRequired)
            {
                axisRequestQueue?.Enqueue(AxisID.AxisUndefined, AxisRequestType.FunctionConfigUpload, msg);
            }

            // Broadcast uploads can't self-verify (AxisUndefined won't match any ESP32 node).
            // Enqueue a separate verification query targeting the first known linked axis.
            if (verify && !verifiedOne && broadcastRequired
                && firstLinkedAxis != AxisID.AxisUndefined)
            {
                axisRequestQueue?.Enqueue(firstLinkedAxis, AxisRequestType.FunctionConfig);
            }
        }

        public void UpdateActiveAircraftLabel(string carName, string carId, string gameId = null)
        {
            if (TextBlock_VehicleTabVehicleId == null)
            {
                return;
            }

            string label = string.IsNullOrWhiteSpace(carName) ? "-" : carName;
            if (!string.IsNullOrWhiteSpace(gameId))
            {
                label += $" ({gameId})";
            }
            TextBlock_VehicleTabVehicleId.Text = label;
            TextBlock_VehicleTabVehicleId.ToolTip = string.IsNullOrWhiteSpace(carId) ? null : carId;
        }

        private void UpdateVjoy(AxisState axisState)
        {
            if (joystick == null || Plugin == null || Plugin.Settings.vjoy_output_flag != 1)
            {
                return;
            }

            float position = axisState.Position;
            if (position < 0f)
            {
                position = 0f;
            }
            else if (position > 1f)
            {
                position = 1f;
            }

            int value = (int)Math.Round(position * 65535f);
            switch (axisState.AxisId)
            {
                case AxisID._1:
                    joystick.SetAxis(value, Plugin.Settings.vjoy_order, HID_USAGES.HID_USAGE_RX);
                    break;
                case AxisID._2:
                    joystick.SetAxis(value, Plugin.Settings.vjoy_order, HID_USAGES.HID_USAGE_RY);
                    break;
                case AxisID._3:
                    joystick.SetAxis(value, Plugin.Settings.vjoy_order, HID_USAGES.HID_USAGE_RZ);
                    break;
            }
        }

        private void RegisterAxisChannel(AxisID axisId, ProtobufSerial<Message> port)
        {
            if (axisId == AxisID.AxisUndefined)
            {
                return;
            }

            if (axes.TryGetValue(axisId, out Axis axis))
            {
                axis.SerialChannel = port;
            }
        }

        private void ProcessGatewayState(ProtobufSerial<Message> port, GatewayState state)
        {
            if (state.GatewayId != GatewayID.GatewayUndefined)
            {
                last_gateway_id = state.GatewayId;
                if (seenGatewaySources.Add(state.GatewayId))
                {
                    AddLogSourceFilter(UiLogSourceKind.Gateway, (int)state.GatewayId, $"Gateway {(int)state.GatewayId}");
                }
            }
            for (AxisID axisId = AxisID._1; axisId <= AxisID._8; axisId++)
            {
                int flag = 1 << ((int)axisId - 1);
                if ((state.AxesPresent & flag) != 0)
                {
                    axes[axisId].SerialChannel = port;
                }
                else
                {
                    axes[axisId].SerialChannel = null;
                }
            }

            if (ota_gateway_targets.TryGetValue(last_gateway_id, out OtaTargetEntry gatewayEntry))
            {
                gatewayEntry.IsOnline = true;
            }
        }

        private string BuildAxisLogLine(AxisLogMessage msg)
        {
            return msg.Msg ?? string.Empty;
        }

        private string BuildGatewayLogLine(GatewayLogMessage msg)
        {
            return msg.Msg ?? string.Empty;
        }

        private UiLogLevel MapLogLevel(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Debug:
                    return UiLogLevel.Debug;
                case LogLevel.Info:
                    return UiLogLevel.Info;
                case LogLevel.Warning:
                    return UiLogLevel.Warning;
                case LogLevel.Error:
                    return UiLogLevel.Error;
                default:
                    return UiLogLevel.Info;
            }
        }

        private void UpdateActiveFunction(ActiveFunction msg)
        {
            if (last_known_functions.ContainsKey(msg.AxisId))
            {
                functions[last_known_functions[msg.AxisId]].OnAxisRemoved(msg.AxisId);
                last_known_functions.Remove(msg.AxisId);
            }

            if (msg.FunctionId != FunctionID.Undefined)
            {
                last_known_functions[msg.AxisId] = msg.FunctionId;
                functions[msg.FunctionId].OnAxisAdded(msg.AxisId);
            }
        }

        private void OnUploadFunctionConfigClicked(object sender, RoutedEventArgs e)
        {
            if (selected_function_id == FunctionID.Undefined)
            {
                SetDebugOutput("No function selected.", UiLogLevel.Warning);
                return;
            }

            FunctionConfig functionConfig = functions[selected_function_id].Config;
            EnqueueFunctionConfigUpload(functionConfig, PersistConfig, verify: true);
        }

        private void OnDownloadFunctionConfigClicked(object sender, RoutedEventArgs e)
        {
            if (selected_function_id == FunctionID.Undefined)
            {
                SetDebugOutput("No function selected.", UiLogLevel.Warning);
                return;
            }

            FunctionConfig functionConfig = functions[selected_function_id].Config;
            AxisID targetAxis = AxisID.AxisUndefined;
            foreach (var linkedAxis in functionConfig.Base.LinkedAxes)
            {
                AxisID axisId = linkedAxis & AxisID.Mask;
                if (axisId == AxisID.AxisUndefined)
                {
                    continue;
                }
                if (axes.TryGetValue(axisId, out Axis axis) && axis.IsOnline)
                {
                    targetAxis = axisId;
                    break;
                }
                if (targetAxis == AxisID.AxisUndefined)
                {
                    targetAxis = axisId;
                }
            }

            if (targetAxis == AxisID.AxisUndefined && selected_axis_id != AxisID.AxisUndefined)
            {
                targetAxis = selected_axis_id;
            }

            if (targetAxis == AxisID.AxisUndefined)
            {
                SetDebugOutput("No axis available for function config download.", UiLogLevel.Warning);
                return;
            }
            if (!axes.TryGetValue(targetAxis, out Axis targetAxisInfo) || !targetAxisInfo.IsOnline)
            {
                SetDebugOutput($"Axis {(int)targetAxis} is offline.", UiLogLevel.Warning);
                return;
            }
            EnqueueAxisRequest(targetAxis, AxisRequestType.FunctionConfig, null);
        }

        private void UploadFunctionConfig(FunctionConfig functionConfig, bool store)
        {
            EnqueueFunctionConfigUpload(functionConfig, store, verify: true);
        }

        private void OnFunctionConfigUpdate(FunctionConfig newFunctionConfig, bool fromEsp32 = false)
        {
            FunctionID newFunctionId = newFunctionConfig.Base.FunctionId;
            if (newFunctionId == FunctionID.Undefined)
            {
                SetDebugOutput("Function ID undefined (ignored)", UiLogLevel.Warning);
                return;
            }

            int funcId = (int)newFunctionId;

            var (authorityOverride, resultConfig) =
                Plugin.ConfigOrchestrator.HandleIncomingFunctionConfig(funcId, newFunctionConfig, fromEsp32);

            if (authorityOverride)
            {
                SetDebugOutput($"Config mismatch for function {funcId}, re-uploading",
                    UiLogLevel.Warning);
                EnqueueFunctionConfigUpload(resultConfig, store: false);
                Plugin.FunctionConfigManager.MarkAsSent(funcId, resultConfig);
                return;
            }

            // Update UI working copy from manager's authoritative merged config
            functions[newFunctionId].Config = resultConfig;

            if (newFunctionId == selected_function_id)
            {
                uc_function_config.SwitchFunction(functions[newFunctionId]);
            }
        }

        private void OnAxisConfigUpdate(AxisConfig newAxisConfig, bool fromEsp32 = false)
        {
            AxisID newAxisId = newAxisConfig.AxisId;
            if (newAxisId != AxisID.AxisUndefined && newAxisId <= AxisID._8)
            {
                int axisIdInt = (int)newAxisId;

                var (authorityOverride, resultConfig) =
                    Plugin.ConfigOrchestrator.HandleIncomingAxisConfig(axisIdInt, newAxisConfig, fromEsp32);

                // Update UI state from result
                axes[newAxisId].Config = resultConfig;
                axes[newAxisId].HasAxisConfig = true;
                if (newAxisId == selected_axis_id)
                    uc_axis_config.UpdateConfig(resultConfig);
                RefreshKinematicParametersIfAffected(newAxisId, resultConfig);

                if (authorityOverride)
                {
                    SetDebugOutput($"Config mismatch for axis {newAxisId}, re-uploading",
                        UiLogLevel.Warning);
                    EnqueueAxisConfigUpload(newAxisId, resultConfig, store: false);
                    Plugin.AxisConfigManager.MarkAsSent(axisIdInt, resultConfig);
                }
            }
            else
            {
                SetDebugOutput($"Invalid axis ID ({(int)newAxisId})", UiLogLevel.Warning);
            }
        }

        /// <summary>
        /// Refresh kinematic parameters in the function config UI if the given axis
        /// is linked to the currently selected function.
        /// </summary>
        private void RefreshKinematicParametersIfAffected(AxisID axisId, AxisConfig axisConfig)
        {
            if (selected_function_id == FunctionID.Undefined ||
                !functions.TryGetValue(selected_function_id, out Function function))
                return;

            FunctionConfig cfg = function.Config;
            bool affectsSelected = false;
            if (cfg?.Base != null)
            {
                foreach (var axis in cfg.Base.LinkedAxes)
                {
                    if ((axis & AxisID.Mask) == axisId)
                    {
                        affectsSelected = true;
                        break;
                    }
                }
            }
            if (!affectsSelected && cfg?.AuxFunction != null)
            {
                foreach (var axis in cfg.AuxFunction.LinkedAxes)
                {
                    if ((axis & AxisID.Mask) == axisId)
                    {
                        affectsSelected = true;
                        break;
                    }
                }
            }
            if (affectsSelected)
            {
                uc_function_config.OnKinematicParametersChanged(axisConfig.KinematicParameters);
            }
        }

        /// <summary>
        /// Called when FunctionConfigManager fires after merging profile/user overrides.
        /// Sends the merged config to ESP32 and updates UI.
        /// </summary>
        private void OnMergedFunctionConfigChanged(object sender, FunctionConfigChangedEventArgs e)
        {
            FunctionID funcId = (FunctionID)e.FunctionId;
            if (funcId == FunctionID.Undefined || !functions.ContainsKey(funcId))
                return;

            bool isActive = Plugin.ConfigOrchestrator.IsFunctionActive(e.FunctionId);

            // Update UI working copy with merged config so SwitchFunction shows correct values.
            // The merged config = base (full ESP32 config) + profile/user overrides, so all fields
            // are present. Non-override-tracked fields come from the base config unchanged.
            functions[funcId].Config = e.NewConfig;

            // Only upload to ESP32 if the function is checked/active for the current profile.
            // Non-active functions still get their internal state updated but don't push to ESP32.
            if (isActive)
            {
                EnqueueFunctionConfigUpload(e.NewConfig, store: false, verify: true);
                Plugin.FunctionConfigManager.MarkAsSent(e.FunctionId, e.NewConfig);
            }
            else
            {
                // Broadcast cleared config so gateway clears its lookup table entry.
                // Zero both controller_output_axis and linked_axes — keeping linked_axes
                // would associate those axes with this disabled function, blocking other
                // functions that share the same physical axes.
                var clearedConfig = e.NewConfig.Clone();
                clearedConfig.Base.ControllerOutputAxis = ControllerAxis.Undefined;
                clearedConfig.Base.LinkedAxes.Clear();
                clearedConfig.Base.LinkedAxes.AddRange(new[] {
                    AxisID.AxisUndefined, AxisID.AxisUndefined,
                    AxisID.AxisUndefined, AxisID.AxisUndefined });
                clearedConfig.Base.Store = false;
                var msg = new Message { FunctionConfig = clearedConfig };
                axisRequestQueue?.Enqueue(AxisID.AxisUndefined, AxisRequestType.FunctionConfigUpload, msg);
                Plugin.FunctionConfigManager.InvalidateLastSent(e.FunctionId);
            }

            // Update UI if this is the selected function
            if (funcId == selected_function_id)
            {
                var func = functions[funcId];
                this.Dispatcher.BeginInvoke(new Action(() =>
                {
                    uc_function_config.SwitchFunction(func);
                }));
            }
        }

        /// <summary>
        /// Called when AxisConfigManager fires after applying function overrides.
        /// Sends the merged config to ESP32 and updates UI.
        /// </summary>
        private void OnMergedAxisConfigChanged(object sender, AxisConfigChangedEventArgs e)
        {
            AxisID axisId = (AxisID)e.AxisId;
            if (axisId == AxisID.AxisUndefined || !axes.ContainsKey(axisId))
                return;

            // Update local cache with merged config
            axes[axisId].Config = e.NewConfig;

            // Send to ESP32 (don't store to EEPROM - these are runtime overrides)
            EnqueueAxisConfigUpload(axisId, e.NewConfig, store: false, verify: true);

            // Update UI if this is the selected axis
            if (axisId == selected_axis_id)
            {
                var config = axes[axisId].Config;
                this.Dispatcher.BeginInvoke(new Action(() =>
                {
                    uc_axis_config.UpdateConfig(config);
                }));
            }
        }

        private void OnLoadConfigClick(object sender, RoutedEventArgs e)
        {
            Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "JSON files (*.json)|*.json",
                DefaultExt = "json",
            };

            if (openFileDialog.ShowDialog() == true)
            {
                try
                {
                    var content = File.ReadAllText(openFileDialog.FileName);
                    // Migrate old flight stick field names in imported files
                    content = DiyFfb.TieredConfig.TieredConfigOrchestrator.MigrateFlightStickJson(content);
                    var jsonParser = new JsonParser(JsonParser.Settings.Default);
                    ConfigItemsList msg = (ConfigItemsList)jsonParser.Parse(content, ConfigItemsList.Descriptor);
                    Dictionary<AxisID, AxisConfig> axisConfigs = new Dictionary<AxisID, AxisConfig>();
                    Dictionary<FunctionID, FunctionConfig> functionConfigs = new Dictionary<FunctionID, FunctionConfig>();

                    foreach (var item in msg.ConfigItems)
                    {
                        switch (item.ItemCase)
                        {
                            case ConfigItem.ItemOneofCase.AxisConfig:
                                axisConfigs[item.AxisConfig.AxisId] = item.AxisConfig;
                                if (axes.TryGetValue(item.AxisConfig.AxisId, out Axis axis))
                                {
                                    axis.SelectedToLoad = true;
                                    axis.SelectableToLoad = true;
                                }
                                break;
                            case ConfigItem.ItemOneofCase.FunctionConfig:
                                functionConfigs[item.FunctionConfig.Base.FunctionId] = item.FunctionConfig;
                                if (functions.TryGetValue(item.FunctionConfig.Base.FunctionId, out Function function))
                                {
                                    function.SelectedToLoad = true;
                                    function.SelectableToLoad = true;
                                }
                                break;
                        }
                    }

                    loadSelectionDialog = new LoadSelectionDialog(this, axisConfigs, functionConfigs);
                    loadSelectionDialog.Closed += OnLoadSelectionClosed;
                    btn_load_axis_config_from_file.IsEnabled = false;
                    btn_load_function_config_from_file.IsEnabled = false;
                    btn_store_function_config_to_file.IsEnabled = false;
                    btn_store_axis_config_to_file.IsEnabled = false;
                    loadSelectionDialog.Show();
                }
                catch (Exception caughtEx)
                {
                    ThemedMessageBox.Show($"Error loading {openFileDialog.FileName}: {caughtEx.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        private void OnLoadSelectionClosed(object sender, EventArgs e)
        {
            if (loadSelectionDialog == null)
            {
                return;
            }

            // Check if the axis tab is editing a function override
            bool axisInOverrideMode = uc_axis_config.EditingMode == AxisEditingMode.FunctionOverride
                                      && uc_axis_config.SelectedFunctionId >= 0;

            foreach (var axis in axes.Values)
            {
                if (loadSelectionDialog.LoadRequested && axis.SelectedToLoad && loadSelectionDialog.axis_configs.TryGetValue(axis.ID, out AxisConfig cfg))
                {
                    // If the selected axis is in function override mode, store imported
                    // config as that function's axis override instead of updating baseline
                    if (axisInOverrideMode && axis.ID == selected_axis_id)
                    {
                        ImportAxisConfigAsOverride(cfg, uc_axis_config.SelectedFunctionId);
                    }
                    else
                    {
                        OnAxisConfigUpdate(cfg);
                    }
                }
                if (loadSelectionDialog.UploadRequested && axis.SelectedToLoad && loadSelectionDialog.axis_configs.TryGetValue(axis.ID, out AxisConfig uploadCfg))
                {
                    EnqueueAxisConfigUpload(axis.ID, uploadCfg, false, verify: true);
                }
                axis.SelectedToLoad = false;
                axis.SelectableToLoad = false;
            }
            foreach (var function in functions.Values)
            {
                if (loadSelectionDialog.LoadRequested && function.SelectedToLoad && loadSelectionDialog.function_configs.TryGetValue(function.ID, out FunctionConfig cfg))
                {
                    OnFunctionConfigUpdate(cfg);
                }
                if (loadSelectionDialog.UploadRequested && function.SelectedToLoad && loadSelectionDialog.function_configs.TryGetValue(function.ID, out FunctionConfig uploadCfg))
                {
                    EnqueueFunctionConfigUpload(uploadCfg, false, verify: true);
                }
                function.SelectedToLoad = false;
                function.SelectableToLoad = false;
            }
            btn_load_axis_config_from_file.IsEnabled = true;
            btn_load_function_config_from_file.IsEnabled = true;
            btn_store_function_config_to_file.IsEnabled = true;
            btn_store_axis_config_to_file.IsEnabled = true;
        }

        /// <summary>
        /// Import an axis config as a function override for the selected axis.
        /// Extracts kinematics, geometry, and static balance from the imported config
        /// and stores them as the function's axis parameter overrides.
        /// </summary>
        private void ImportAxisConfigAsOverride(AxisConfig importedConfig, int functionId)
        {
            int axisId = (int)importedConfig.AxisId;
            var formatter = new Google.Protobuf.JsonFormatter(Google.Protobuf.JsonFormatter.Settings.Default);

            Plugin.ConfigOrchestrator.UpdateAxisParameterOverride(functionId, axisId, overrides =>
            {
                if (importedConfig.KinematicParameters != null)
                    overrides.Kinematics = importedConfig.KinematicParameters.Clone();

                if (importedConfig.GeneralKinematic != null)
                {
                    try { overrides.GeometryJson = formatter.Format(importedConfig.GeneralKinematic); }
                    catch { /* best-effort */ }
                }

                if (importedConfig.StaticBalanceConfig != null)
                    overrides.StaticBalance = importedConfig.StaticBalanceConfig.Clone();
            });

            // Update UI to show the imported override
            axes[importedConfig.AxisId].Config = Plugin.AxisConfigManager.GetCurrentConfig(axisId)
                                                 ?? axes[importedConfig.AxisId].Config;
            uc_axis_config.SwitchToFunction(functionId);
            RefreshAxisFunctionSelector();
        }

        private void OnSaveSelectionClosed(object sender, EventArgs e)
        {
            if (saveSelectionDialog == null)
            {
                return;
            }

            ConfigItemsList items = new ConfigItemsList();
            foreach (var axis in axes.Values)
            {
                if (axis.SelectedToStore)
                {
                    items.ConfigItems.Add(new ConfigItem { AxisConfig = axis.Config });
                    axis.SelectedToStore = false;
                }
            }
            foreach (var function in functions.Values)
            {
                if (function.SelectedToStore)
                {
                    items.ConfigItems.Add(new ConfigItem { FunctionConfig = function.Config });
                    function.SelectedToStore = false;
                }
            }
            if (saveSelectionDialog.SaveRequested)
            {
                JsonFormatter formatter = new JsonFormatter(JsonFormatter.Settings.Default.WithIndentation());
                var output = formatter.Format(items);
                File.WriteAllText(saveSelectionDialog.FileName, output);
            }
            btn_load_axis_config_from_file.IsEnabled = true;
            btn_load_function_config_from_file.IsEnabled = true;
            btn_store_function_config_to_file.IsEnabled = true;
            btn_store_axis_config_to_file.IsEnabled = true;
        }

        private void btn_store_function_config_to_file_Click(object sender, RoutedEventArgs e)
        {
            Microsoft.Win32.SaveFileDialog saveFileDialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "JSON files (*.json)|*.json",
                DefaultExt = "json",
                FileName = "config.json"
            };

            if (saveFileDialog.ShowDialog() == true)
            {
                functions[selected_function_id].SelectedToStore = true;
                saveSelectionDialog = new SaveSelectionDialog(this, saveFileDialog.FileName);
                saveSelectionDialog.Closed += OnSaveSelectionClosed;
                btn_load_axis_config_from_file.IsEnabled = false;
                btn_load_function_config_from_file.IsEnabled = false;
                btn_store_function_config_to_file.IsEnabled = false;
                btn_store_axis_config_to_file.IsEnabled = false;
                saveSelectionDialog.Show();
            }
        }

        private void OnSaveFunctionBaselineClicked(object sender, RoutedEventArgs e)
        {
            if (Plugin == null || !functions.TryGetValue(selected_function_id, out var function) || function == null)
            {
                return;
            }

            // Use function.Config which has ALL current edits (override-tracked + direct edits)
            // The manager's merged config only includes override-tracked fields, losing direct edits to non-wrapped fields
            var configToSave = function.Config?.Clone();

            if (configToSave != null)
            {
                var result = ThemedMessageBox.Show(
                    $"Save {function.Name} configuration as hardware baseline?\n\nAll overrides will be baked into the baseline and cleared.",
                    "Save as Baseline",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (result != MessageBoxResult.Yes)
                    return;

                // Save as baseline (this "bakes" all overrides AND direct edits into the new baseline)
                Plugin.ConfigOrchestrator.SetFunctionBaseline((int)function.ID, configToSave);

                // Clear all overrides since they're now part of the baseline
                Plugin.ConfigOrchestrator.ClearAllFunctionOverrides((int)function.ID);

                // Update manager with new baseline (no overrides)
                Plugin.FunctionConfigManager.SetBaseConfig((int)function.ID, configToSave);
                Plugin.ConfigOrchestrator.ApplyProfileOverridesToFunction((int)function.ID);

                // Refresh badges to remove [U] badge after clearing overrides
                Dispatcher.BeginInvoke(new System.Action(() =>
                {
                    uc_function_config.RefreshAllBadges();
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        private void OnClearFunctionBaselineClicked(object sender, RoutedEventArgs e)
        {
            if (Plugin == null || !functions.TryGetValue(selected_function_id, out var function) || function == null)
                return;

            int funcId = (int)function.ID;
            if (!Plugin.ConfigOrchestrator.HasFunctionBaseline(funcId))
                return;

            var result = ThemedMessageBox.Show(
                $"Clear stored baseline for {function.Name}?\n\nThe next ESP32 upload, import, or manual edit will establish a new baseline.\nOverrides are preserved.",
                "Clear Baseline",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes)
                return;

            Plugin.ConfigOrchestrator.ClearFunctionBaseline(funcId);

            Dispatcher.BeginInvoke(new System.Action(() =>
            {
                uc_function_config.RefreshAllBadges();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        #region Axis Function Selector

        private bool _updatingAxisFunctionSelector = false;

        /// <summary>
        /// Refresh the axis function selector dropdown with functions linking to the selected axis.
        /// </summary>
        private void RefreshAxisFunctionSelector()
        {
            if (AxisFunctionSelector == null || Plugin == null || selected_axis_id == AxisID.AxisUndefined)
                return;

            _updatingAxisFunctionSelector = true;
            try
            {
                var items = new List<FunctionSelectorItem>();
                int axisId = (int)selected_axis_id;

                items.Add(new FunctionSelectorItem
                {
                    FunctionId = -1,
                    DisplayName = "Baseline",
                    HasOverride = false,
                    IsAxisBase = true
                });

                var linkedFunctions = Plugin.ConfigOrchestrator.GetFunctionsLinkingToAxis(axisId);
                foreach (var func in linkedFunctions)
                {
                    items.Add(new FunctionSelectorItem
                    {
                        FunctionId = func.FunctionId,
                        DisplayName = func.FunctionName,
                        HasOverride = func.HasOverride,
                        IsAxisBase = false
                    });
                }

                AxisFunctionSelector.ItemsSource = items;

                // Restore selection to match AxisConfigControl's current state
                var mode = uc_axis_config.EditingMode;
                var funcId = uc_axis_config.SelectedFunctionId;
                if (mode == AxisEditingMode.AxisBase || funcId < 0)
                {
                    AxisFunctionSelector.SelectedIndex = 0;
                }
                else
                {
                    var match = items.FirstOrDefault(i => i.FunctionId == funcId);
                    if (match != null)
                        AxisFunctionSelector.SelectedItem = match;
                    else
                        AxisFunctionSelector.SelectedIndex = 0;
                }

                // Show/hide clear button based on current override state
                bool hasOverride = mode == AxisEditingMode.FunctionOverride
                    && Plugin.ConfigOrchestrator.GetAxisParameterOverride(funcId, axisId) != null;
                BtnClearAxisOverride.IsEnabled = hasOverride;
            }
            finally
            {
                _updatingAxisFunctionSelector = false;
            }
        }

        private void AxisFunctionSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingAxisFunctionSelector || AxisFunctionSelector.SelectedItem == null)
                return;

            var selectedItem = AxisFunctionSelector.SelectedItem as FunctionSelectorItem;
            if (selectedItem == null)
                return;

            if (selectedItem.IsAxisBase)
            {
                uc_axis_config.SwitchToBaseline();
                BtnClearAxisOverride.IsEnabled = false;
            }
            else
            {
                bool hasOverride = uc_axis_config.SwitchToFunction(selectedItem.FunctionId);
                BtnClearAxisOverride.IsEnabled = hasOverride;
            }
        }

        private void OnClearAxisOverrideClicked(object sender, RoutedEventArgs e)
        {
            uc_axis_config.ClearCurrentOverride();
            BtnClearAxisOverride.IsEnabled = false;
            RefreshAxisFunctionSelector();
        }

        private void OnSaveAxisBaselineClicked(object sender, RoutedEventArgs e)
        {
            if (Plugin == null || selected_axis_id == AxisID.AxisUndefined)
                return;

            if (!axes.TryGetValue(selected_axis_id, out var axis) || axis.Config == null)
                return;

            int axisIdInt = (int)selected_axis_id;

            var result = Controls.ThemedMessageBox.Show(
                $"Save Axis {axisIdInt} configuration as hardware baseline?\n\nAxis overrides for this axis will be cleared.",
                "Save as Baseline",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes)
                return;

            var configToSave = axis.Config.Clone();
            Plugin.ConfigOrchestrator.SetAxisBaseline(axisIdInt, configToSave);

            // Clear axis overrides for this axis across all functions
            if (Plugin.Settings?.FunctionAxisOverrides != null)
            {
                var functionIds = Plugin.Settings.FunctionAxisOverrides.Keys.ToList();
                foreach (var funcId in functionIds)
                {
                    Plugin.ConfigOrchestrator.ClearAxisParameterOverride(funcId, axisIdInt);
                }
            }

            uc_axis_config.UpdateConfig(axis.Config);
            RefreshAxisFunctionSelector();
        }

        private void OnClearAxisBaselineClicked(object sender, RoutedEventArgs e)
        {
            if (Plugin == null || selected_axis_id == AxisID.AxisUndefined)
                return;

            int axisIdInt = (int)selected_axis_id;
            if (!Plugin.ConfigOrchestrator.HasAxisBaseline(axisIdInt))
                return;

            var result = Controls.ThemedMessageBox.Show(
                $"Clear stored baseline for Axis {axisIdInt}?\n\nThe next ESP32 upload or import will establish a new baseline.\nOverrides are preserved.",
                "Clear Baseline",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes)
                return;

            Plugin.ConfigOrchestrator.ClearAxisBaseline(axisIdInt);

            if (axes.TryGetValue(selected_axis_id, out var axis) && axis.Config != null)
                uc_axis_config.UpdateConfig(axis.Config);
            RefreshAxisFunctionSelector();
        }

        #endregion

        private void OnOpenGraphEditorClicked(object sender, RoutedEventArgs e)
        {
            if (graphEditorWindow == null)
            {
                graphEditorWindow = new GraphEditorWindow();
                graphEditorWindow.SetPlugin(Plugin);
                graphEditorWindow.SetLiveInputProvider(() => Plugin != null ? Plugin.GetLiveGraphInputs() : null);
                graphEditorWindow.SetMsfsFailedVarProvider(() => Plugin != null ? Plugin.GetMsfsFailedVars() : null);
                graphEditorWindow.SetMsfsWriteFailedVarProvider(() => Plugin != null ? Plugin.GetMsfsWriteFailedVars() : null);
                graphEditorWindow.SetConfigInProvider(() => Plugin != null ? Plugin.GetConfigInInputs() : null);
                string activeGraphPath = Plugin?.GetActiveGraphPath();
                if (!string.IsNullOrWhiteSpace(activeGraphPath))
                {
                    graphEditorWindow.LoadGraphFromPath(activeGraphPath);
                    lastGraphEditorPath = activeGraphPath;
                }
                graphEditorWindow.Closed += (_, __) => graphEditorWindow = null;
                graphEditorWindow.Show();
            }
            else
            {
                graphEditorWindow.SetPlugin(Plugin);
                graphEditorWindow.SetLiveInputProvider(() => Plugin != null ? Plugin.GetLiveGraphInputs() : null);
                graphEditorWindow.SetMsfsFailedVarProvider(() => Plugin != null ? Plugin.GetMsfsFailedVars() : null);
                graphEditorWindow.SetMsfsWriteFailedVarProvider(() => Plugin != null ? Plugin.GetMsfsWriteFailedVars() : null);
                graphEditorWindow.SetConfigInProvider(() => Plugin != null ? Plugin.GetConfigInInputs() : null);
                string activeGraphPath = Plugin?.GetActiveGraphPath();
                if (!string.IsNullOrWhiteSpace(activeGraphPath))
                {
                    graphEditorWindow.LoadGraphFromPath(activeGraphPath);
                    lastGraphEditorPath = activeGraphPath;
                }
                graphEditorWindow.Activate();
            }
        }

        private void OnUploadAxisConfigClicked(object sender, RoutedEventArgs e)
        {
            if (selected_axis_id == AxisID.AxisUndefined)
            {
                SetDebugOutput("No axis selected.", UiLogLevel.Warning);
                return;
            }

            var configToUpload = axes[selected_axis_id].Config;

            // In AxisBase mode the user is tuning the canonical axis baseline (not a
            // per-function override). Persist the working copy as the new baseline
            // before sending so the verify-readback's authority check sees consistent
            // state. Otherwise fields that aren't tracked in AxisParameterOverrides
            // (e.g. OscillationGuard) get reverted because the merged config returned
            // by HandleIncomingAxisConfig is built from the stale baseline only.
            if (uc_axis_config?.EditingMode == AxisEditingMode.AxisBase &&
                Plugin?.ConfigOrchestrator != null)
            {
                Plugin.ConfigOrchestrator.SetAxisBaseline((int)selected_axis_id, configToUpload.Clone());
            }

            EnqueueAxisConfigUpload(selected_axis_id, configToUpload, PersistConfig,
                verify: true);
        }

        private void OnDownloadAxisConfigClicked(object sender, RoutedEventArgs e)
        {
            if (selected_axis_id == AxisID.AxisUndefined)
            {
                SetDebugOutput("No axis selected.", UiLogLevel.Warning);
                return;
            }
            if (!axes.TryGetValue(selected_axis_id, out Axis axis) || !axis.IsOnline)
            {
                SetDebugOutput($"Axis {(int)selected_axis_id} is offline.", UiLogLevel.Warning);
                return;
            }
            EnqueueAxisRequest(selected_axis_id, AxisRequestType.AxisConfig, null);
        }

        private void btn_home_axis_Click(object sender, RoutedEventArgs e)
        {
            if (selected_axis_id == AxisID.AxisUndefined)
            {
                SetDebugOutput("No axis selected.", UiLogLevel.Warning);
                return;
            }
            if (!axes.TryGetValue(selected_axis_id, out Axis axis))
            {
                SetDebugOutput($"Axis {(int)selected_axis_id} not found.", UiLogLevel.Warning);
                return;
            }
            if (!axis.IsOnline)
            {
                SetDebugOutput($"Axis {(int)selected_axis_id} is offline.", UiLogLevel.Warning);
                return;
            }
            if (!SendAxisRequest(selected_axis_id, AxisRequestType.Homing, null))
            {
                SetDebugOutput($"Axis {(int)selected_axis_id}: homing send failed.", UiLogLevel.Error);
                return;
            }
            SetDebugOutput($"Axis {(int)selected_axis_id}: homing started.");
        }

        private void btn_store_axis_config_to_file_Click(object sender, RoutedEventArgs e)
        {
            Microsoft.Win32.SaveFileDialog saveFileDialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "JSON files (*.json)|*.json",
                DefaultExt = "json",
                FileName = "config.json"
            };

            if (saveFileDialog.ShowDialog() == true)
            {
                axes[selected_axis_id].SelectedToStore = true;
                saveSelectionDialog = new SaveSelectionDialog(this, saveFileDialog.FileName);
                saveSelectionDialog.Closed += OnSaveSelectionClosed;
                btn_load_axis_config_from_file.IsEnabled = false;
                btn_load_function_config_from_file.IsEnabled = false;
                btn_store_function_config_to_file.IsEnabled = false;
                btn_store_axis_config_to_file.IsEnabled = false;
                saveSelectionDialog.Show();
            }
        }

        private sealed class OtaManifest
        {
            [JsonProperty("version")]
            public string Version { get; set; } = string.Empty;

            [JsonProperty("board")]
            public string Board { get; set; } = string.Empty;

            [JsonProperty("md5")]
            public string Md5 { get; set; } = string.Empty;
        }

        private sealed class OtaInfoPayload
        {
            [JsonProperty("Configurations")]
            public List<OtaInfoEntry> Configurations { get; set; } = new List<OtaInfoEntry>();
        }

        private sealed class OtaInfoEntry
        {
            [JsonProperty("Board")]
            public string Board { get; set; } = string.Empty;

            [JsonProperty("Version")]
            public string Version { get; set; } = string.Empty;

            [JsonProperty("MD5")]
            public string Md5 { get; set; } = string.Empty;
        }

        private sealed class LocalOtaServer : IDisposable
        {
            private readonly byte[] infoJson;
            private readonly byte[] firmwareBytes;
            private readonly TcpListener listener;
            private readonly Action<string> logger;
            private CancellationTokenSource cancellation;
            private Task loopTask;

            public string InfoUrl { get; }
            public string BindPrefixes { get; }

            public LocalOtaServer(string infoUrl, byte[] infoJson, byte[] firmwareBytes, Action<string> logger)
            {
                if (string.IsNullOrWhiteSpace(infoUrl))
                {
                    throw new ArgumentException("infoUrl must be set.");
                }

                this.infoJson = infoJson ?? Array.Empty<byte>();
                this.firmwareBytes = firmwareBytes ?? Array.Empty<byte>();
                this.logger = logger;
                InfoUrl = infoUrl;

                Uri infoUri = new Uri(infoUrl);
                BindPrefixes = $"{infoUri.Host}:{infoUri.Port}";
                listener = new TcpListener(IPAddress.Any, infoUri.Port);
            }

            public void Start()
            {
                if (cancellation != null)
                {
                    return;
                }

                listener.Start();
                cancellation = new CancellationTokenSource();
                loopTask = Task.Run(() => ListenAsync(cancellation.Token));
            }

            private async Task ListenAsync(CancellationToken token)
            {
                while (!token.IsCancellationRequested)
                {
                    TcpClient client = null;
                    try
                    {
                        client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                        _ = Task.Run(() => HandleClientAsync(client, token), token);
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (InvalidOperationException)
                    {
                        break;
                    }
                }
            }

            public void Dispose()
            {
                cancellation?.Cancel();
                try
                {
                    listener.Stop();
                }
                catch
                {
                }
                try
                {
                    loopTask?.Wait(500);
                }
                catch
                {
                }
                cancellation?.Dispose();
                cancellation = null;
                loopTask = null;
            }

            private async Task HandleClientAsync(TcpClient client, CancellationToken token)
            {
                string logPath = "/";
                int responseBytes = 0;
                using (client)
                using (NetworkStream stream = client.GetStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true))
                {
                    string requestLine = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(requestLine))
                    {
                        return;
                    }

                    string[] parts = requestLine.Split(' ');
                    string path = parts.Length >= 2 ? parts[1] : "/";
                    logPath = path;

                    while (true)
                    {
                        string line = await reader.ReadLineAsync().ConfigureAwait(false);
                        if (string.IsNullOrEmpty(line))
                        {
                            break;
                        }
                    }

                    byte[] body;
                    string contentType;
                    int statusCode = 200;
                    int bytesWritten = 0;

                    if (path == "/" || path.Equals("/update_info.json", StringComparison.OrdinalIgnoreCase))
                    {
                        body = infoJson;
                        contentType = "application/json";
                    }
                    else if (path.Equals("/firmware.bin", StringComparison.OrdinalIgnoreCase))
                    {
                        body = firmwareBytes;
                        contentType = "application/octet-stream";
                    }
                    else
                    {
                        statusCode = 404;
                        body = Encoding.UTF8.GetBytes("Not Found");
                        contentType = "text/plain";
                    }

                    StringBuilder header = new StringBuilder();
                    header.Append($"HTTP/1.1 {statusCode} {(statusCode == 200 ? "OK" : "Not Found")}\r\n");
                    header.Append($"Content-Type: {contentType}\r\n");
                    header.Append($"Content-Length: {body.Length}\r\n");
                    header.Append("Connection: close\r\n");
                    header.Append("\r\n");

                    byte[] headerBytes = Encoding.ASCII.GetBytes(header.ToString());
                    await stream.WriteAsync(headerBytes, 0, headerBytes.Length, token).ConfigureAwait(false);
                    await stream.WriteAsync(body, 0, body.Length, token).ConfigureAwait(false);
                    await stream.FlushAsync(token).ConfigureAwait(false);
                    responseBytes = body.Length;
                }
                // Quiet by default; keep dialog log focused on OTA flow events.
            }
        }

        private void OnActiveGraphChanged_UI(object sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                UpdateVehicleTabHeader();
                RefreshGraphSelectionUI();
                RefreshVehicleParams();
            });
        }

        private void OnParamMigrationDetected(object sender, ParamMigrationResult e)
        {
            Dispatcher.Invoke(() =>
            {
                string summary = BuildMigrationSummary(e);
                ShowParamMigrationNotification(summary, e);
            });
        }

        private string BuildMigrationSummary(ParamMigrationResult result)
        {
            var parts = new List<string>();
            if (result.ChangedDefaults.Count > 0)
                parts.Add($"{result.ChangedDefaults.Count} default(s) changed");
            if (result.ClampedValues.Count > 0)
                parts.Add($"{result.ClampedValues.Count} value(s) clamped");
            if (result.NewOrphans.Count > 0)
                parts.Add($"{result.NewOrphans.Count} new orphan(s)");

            return parts.Count > 0
                ? string.Join(", ", parts)
                : "Graph updated";
        }

        private void ShowParamMigrationNotification(string summary, ParamMigrationResult result)
        {
            // Store result for review window access
            _pendingMigrationResult = result;

            // Show notification in status bar or message box
            ThemedMessageBox.Show(
                $"FFB Graph Changed\n\n{summary}\n\nClick 'Review Params' in the Vehicle tab to see details.",
                "Parameter Migration",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private ParamMigrationResult _pendingMigrationResult;

        private void UpdateVehicleTabHeader()
        {
            if (Plugin == null)
                return;

            var category = Plugin.GetActiveGraphCategory();

            switch (category)
            {
                case GraphCategory.Helicopter:
                    VehicleTabIcon.Source = (ImageSource)FindResource("HelicopterSymbol");
                    VehicleTabLabel.Content = "AIRCRAFT";
                    break;
                case GraphCategory.Aircraft:
                    VehicleTabIcon.Source = (ImageSource)FindResource("AirplaneSymbol");
                    VehicleTabLabel.Content = "AIRCRAFT";
                    break;
                case GraphCategory.Vehicle:
                default:
                    VehicleTabIcon.Source = (ImageSource)FindResource("VehicleSymbol");
                    VehicleTabLabel.Content = "VEHICLE";
                    break;
            }
        }

        #region Vehicle Parameters

        private Dictionary<string, FrameworkElement> vehicleParamControls = new Dictionary<string, FrameworkElement>();
        private Dictionary<string, Label> vehicleParamLabels = new Dictionary<string, Label>();
        private bool isUpdatingVehicleParams = false;

        private bool IsVehicleParam(GraphParam p)
        {
            var group = p.Ui?.Group;

            // Null/empty -> goes to "<unknown>" panel
            if (string.IsNullOrEmpty(group))
                return true;

            // Exclude System
            if ("System".Equals(group, StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }

        public void RefreshVehicleParams()
        {
            VehicleParamsContainer.Children.Clear();
            vehicleParamControls.Clear();
            vehicleParamLabels.Clear();

            if (Plugin == null)
            {
                return;
            }

            // Add Active Functions section at the top
            var activeFunctionsExpander = CreateActiveFunctionsExpander();
            if (activeFunctionsExpander != null)
            {
                VehicleParamsContainer.Children.Add(activeFunctionsExpander);
            }

            // Plan 34: per-vehicle MSFS var binding overrides (only when the active
            // graph declares MsfsVarDef/MsfsVarOut nodes).
            var msfsBindingsExpander = CreateMsfsBindingsExpander();
            if (msfsBindingsExpander != null)
            {
                VehicleParamsContainer.Children.Add(msfsBindingsExpander);
            }

            var allParams = Plugin.GetActiveGraphParams();
            if (allParams == null || allParams.Count == 0)
            {
                if (activeFunctionsExpander == null && msfsBindingsExpander == null)
                {
                    ShowVehicleEmptyState();
                }
                return;
            }

            // Filter to vehicle params only
            var vehicleParams = allParams.Values
                .Where(IsVehicleParam)
                .ToList();

            if (vehicleParams.Count == 0)
            {
                if (activeFunctionsExpander == null)
                {
                    ShowVehicleEmptyState();
                }
                return;
            }

            var orderedNames = Plugin.GetActiveGraphParamOrder();

            // Group by Group property, "<unknown>" for null/empty
            var grouped = vehicleParams
                .GroupBy(p => string.IsNullOrEmpty(p.Ui?.Group) ? "<unknown>" : p.Ui.Group)
                .OrderBy(g => g.Key == "<unknown>" ? "\uFFFF" : g.Key); // <unknown> sorts last

            foreach (var group in grouped)
            {
                var orderedParams = OrderParamsByGraph(orderedNames, group);
                var expander = CreateVehicleGroupExpander(group.Key, orderedParams);
                VehicleParamsContainer.Children.Add(expander);
            }
        }

        private Expander CreateActiveFunctionsExpander()
        {
            // Only show if we have known functions
            if (functions.Count == 0)
                return null;

            var expander = new Expander
            {
                Header = "Active Functions",
                IsExpanded = false,
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Arial Black"),
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 5)
            };

            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x7F, 0x4E, 0x4E, 0x4E)),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10)
            };

            var panel = new StackPanel { Orientation = Orientation.Vertical };

            // Add description
            var description = new TextBlock
            {
                Text = "Select which functions should use profile-specific overrides for this vehicle.",
                Foreground = Brushes.Gray,
                FontFamily = new FontFamily("Arial"),
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            panel.Children.Add(description);

            // Add a checkbox for each known function (skip Undefined)
            foreach (var kvp in functions)
            {
                if (kvp.Key == FunctionID.Undefined)
                    continue;

                var functionPanel = CreateActiveFunctionRow(kvp.Key, kvp.Value);
                panel.Children.Add(functionPanel);
            }

            border.Child = panel;
            expander.Content = border;
            return expander;
        }

        private StackPanel CreateActiveFunctionRow(FunctionID functionId, Function function)
        {
            var container = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(0, 2, 0, 2)
            };

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Height = 24
            };

            bool isActive = Plugin.ConfigOrchestrator.IsFunctionActive((int)functionId);

            var checkbox = new CheckBox
            {
                IsChecked = isActive,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            checkbox.Tag = functionId;
            checkbox.Checked += OnActiveFunctionChecked;
            checkbox.Unchecked += OnActiveFunctionUnchecked;

            var nameLabel = new TextBlock
            {
                Text = function.Name,
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Arial"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Width = 140
            };

            var statusLabel = new TextBlock
            {
                Text = function.IsOnline ? "(online)" : "(offline)",
                Foreground = function.IsOnline ? Brushes.LightGreen : Brushes.Gray,
                FontFamily = new FontFamily("Arial"),
                FontSize = 10,
                FontStyle = FontStyles.Italic,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(5, 0, 0, 0)
            };

            // Add override indicator badge (always create, update visibility dynamically)
            var badge = new TextBlock
            {
                Tag = $"functionBadge_{(int)functionId}",
                FontFamily = new FontFamily("Arial"),
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(5, 0, 0, 0)
            };
            UpdateFunctionLevelBadge(badge, functionId);

            row.Children.Add(checkbox);
            row.Children.Add(nameLabel);
            row.Children.Add(badge);
            row.Children.Add(statusLabel);

            container.Children.Add(row);

            return container;
        }

        /// <summary>
        /// Update the function-level badge that shows [U] or [P] next to the function name.
        /// Shows [U] if any field has User override, [P] if any field has Profile override, hidden otherwise.
        /// </summary>
        private void UpdateFunctionLevelBadge(TextBlock badge, FunctionID functionId)
        {
            var userOverrides = Plugin.ConfigOrchestrator.GetUserFunctionOverrides((int)functionId);
            var profileOverrides = Plugin.ConfigOrchestrator.GetFunctionOverrides((int)functionId);
            bool hasUserOverrides = userOverrides != null && !userOverrides.IsEmpty;
            bool hasProfileOverrides = profileOverrides != null && !profileOverrides.IsEmpty;
            bool isActive = Plugin.ConfigOrchestrator.IsFunctionActive((int)functionId);

            if (hasUserOverrides && isActive)
            {
                badge.Text = "[U]";
                badge.Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0xB5, 0xF6));
                badge.ToolTip = "Has user preference overrides";
                badge.Visibility = Visibility.Visible;
            }
            else if (hasProfileOverrides && isActive)
            {
                badge.Text = "[P]";
                badge.Foreground = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
                badge.ToolTip = "Has vehicle profile overrides";
                badge.Visibility = Visibility.Visible;
            }
            else
            {
                badge.Text = "";
                badge.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// Find a UI element by its Tag value within a container.
        /// </summary>
        private T FindElementByTag<T>(DependencyObject parent, string tag) where T : FrameworkElement
        {
            if (parent == null) return null;

            int childCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childCount; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T element && element.Tag?.ToString() == tag)
                    return element;

                var result = FindElementByTag<T>(child, tag);
                if (result != null)
                    return result;
            }
            return null;
        }

        private void OnActiveFunctionChecked(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox checkbox && checkbox.Tag is FunctionID functionId)
            {
                Plugin.ConfigOrchestrator.SetFunctionActive((int)functionId, true);
            }
        }

        private void OnActiveFunctionUnchecked(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox checkbox && checkbox.Tag is FunctionID functionId)
            {
                Plugin.ConfigOrchestrator.SetFunctionActive((int)functionId, false);
            }
        }

        private void ShowVehicleEmptyState()
        {
            var message = new TextBlock
            {
                Text = "No vehicle parameters defined.\n\nVehicle parameters can be added via graph Param nodes with custom Group values.",
                Foreground = Brushes.Gray,
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(10),
                TextWrapping = TextWrapping.Wrap,
                Width = 400
            };
            VehicleParamsContainer.Children.Add(message);
        }

        private Expander CreateVehicleGroupExpander(string groupName, List<GraphParam> parameters)
        {
            var expander = new Expander
            {
                Header = groupName,
                IsExpanded = true,
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Arial Black"),
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 0)
            };

            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x7F, 0x4E, 0x4E, 0x4E)),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10)
            };

            var panel = new StackPanel { Orientation = Orientation.Vertical };

            foreach (var param in parameters)
            {
                var paramPanel = CreateVehicleParamPanel(param);
                panel.Children.Add(paramPanel);
            }

            border.Child = panel;
            expander.Content = border;
            return expander;
        }

        private static List<GraphParam> OrderParamsByGraph(IReadOnlyList<string> orderedNames, IEnumerable<GraphParam> parameters)
        {
            var map = new Dictionary<string, GraphParam>(StringComparer.OrdinalIgnoreCase);
            foreach (var param in parameters)
            {
                if (!string.IsNullOrWhiteSpace(param?.Name))
                {
                    map[param.Name] = param;
                }
            }

            var ordered = new List<GraphParam>();
            if (orderedNames != null)
            {
                foreach (var name in orderedNames)
                {
                    if (map.TryGetValue(name, out var param))
                    {
                        ordered.Add(param);
                        map.Remove(name);
                    }
                }
            }

            ordered.AddRange(map.Values.OrderBy(p => p.Ui?.Label ?? p.Name));
            return ordered;
        }

        private StackPanel CreateVehicleParamPanel(GraphParam param)
        {
            var panel = new StackPanel
            {
                Width = 400,
                Orientation = Orientation.Vertical,
                Margin = new Thickness(0, 0, 0, 6)
            };

            // Get current value (GetGraphParamValue now does full three-tier resolution)
            double currentValue = Plugin.GetGraphParamValue(param.Name);

            var label = new Label
            {
                Foreground = Brushes.White,
                FontSize = 10,
                FontFamily = new FontFamily("Arial"),
                Content = FormatVehicleParamLabel(param, currentValue),
                Padding = new Thickness(0, 0, 0, 8)
            };
            var muteCheckbox = GraphParamControlBuilder.BuildMuteCheckbox(
                param,
                Plugin.IsParamMuted(param.Name),
                muted => Plugin.SetParamMuted(param.Name, muted),
                (muted, othersOnly) => Plugin.SetAllParamMutes(muted, othersOnly ? param.Name : null));

            double controlWidth = muteCheckbox != null ? 370.0 : 400.0;
            var control = GraphParamControlBuilder.BuildControl(
                param,
                value =>
                {
                    if (!isUpdatingVehicleParams)
                    {
                        Plugin.SetGraphParamValue(param.Name, value);
                        // Update label to show new value
                        if (vehicleParamLabels.TryGetValue(param.Name, out var lbl))
                        {
                            lbl.Content = FormatVehicleParamLabel(param, value);
                        }
                    }
                },
                width: controlWidth,
                initialValue: currentValue
            );

            panel.Children.Add(label);
            if (muteCheckbox != null)
            {
                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                row.Children.Add(control);
                row.Children.Add(muteCheckbox);
                panel.Children.Add(row);
            }
            else
            {
                panel.Children.Add(control);
            }

            vehicleParamControls[param.Name] = control;
            vehicleParamLabels[param.Name] = label;

            return panel;
        }

        // ---- Plan 34: MSFS var binding overrides panel -------------------------

        private Expander CreateMsfsBindingsExpander()
        {
            var cat = Plugin?.EnumerateMsfsBindings();
            if (cat == null || (cat.Reads.Count == 0 && cat.Writes.Count == 0))
                return null;

            var expander = new Expander
            {
                Header = "MSFS Bindings",
                IsExpanded = false,
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Arial Black"),
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 5)
            };

            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x7F, 0x4E, 0x4E, 0x4E)),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10)
            };

            var panel = new StackPanel { Orientation = Orientation.Vertical };

            panel.Children.Add(new TextBlock
            {
                Text = "Override the SimConnect variables this graph reads/writes for this vehicle. "
                     + "Only the sim-side range is editable (Reads: raw→normalized; Writes: graph→sim); "
                     + "the graph-side range is fixed. Leave a field blank to inherit the graph default.",
                Foreground = Brushes.Gray,
                FontFamily = new FontFamily("Arial"),
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            });

            if (cat.Reads.Count > 0)
            {
                panel.Children.Add(CreateMsfsSectionHeader("Read (from sim)"));
                foreach (var info in cat.Reads)
                    panel.Children.Add(CreateMsfsReadRow(info));
            }

            if (cat.Writes.Count > 0)
            {
                panel.Children.Add(CreateMsfsSectionHeader("Write (to sim)"));
                foreach (var info in cat.Writes)
                    panel.Children.Add(CreateMsfsWriteRow(info));
            }

            border.Child = panel;
            expander.Content = border;
            return expander;
        }

        private static TextBlock CreateMsfsSectionHeader(string text) => new TextBlock
        {
            Text = text,
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Arial"),
            FontWeight = FontWeights.Bold,
            FontSize = 11,
            Margin = new Thickness(0, 4, 0, 4)
        };

        // A text box that commits on Enter or lost focus. Empty text = inherit.
        private static TextBox CreateMsfsBindingBox(string initial, string defaultHint, double width, Action commit)
        {
            var box = new TextBox
            {
                Text = initial ?? "",
                Width = width,
                Height = 22,
                FontFamily = new FontFamily("Arial"),
                FontSize = 10,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
                ToolTip = string.IsNullOrEmpty(defaultHint) ? "(no graph default)" : $"Default: {defaultHint}"
            };
            box.LostFocus += (s, e) => commit();
            box.KeyDown += (s, e) => { if (e.Key == Key.Enter) commit(); };
            return box;
        }

        private static string NullToEmpty(string s) => s ?? "";

        private Border CreateMsfsReadRow(MsfsBindingInfo info)
        {
            var ov = Plugin.GetMsfsReadOverride(info.Alias);

            TextBox simVarBox = null, unitBox = null, inMinBox = null, inMaxBox = null;
            // Reads: the raw sim value is the map INPUT, so In* is the overridable
            // sim-side pair; Out* is the graph-side range (inherited, shown fixed).
            Action commit = () => Plugin.SetMsfsReadBinding(
                info.Alias,
                simVarBox.Text,
                unitBox.Text,
                ParseNullableDouble(inMinBox.Text),
                ParseNullableDouble(inMaxBox.Text));

            simVarBox = CreateMsfsBindingBox(NullToEmpty(ov?.SimVar), info.DefaultSimVar, 260, () => commit());
            unitBox = CreateMsfsBindingBox(NullToEmpty(ov?.Unit), info.DefaultUnit, 120, () => commit());
            inMinBox = CreateMsfsBindingBox(FormatNullable(ov?.InMin), FormatDouble(info.DefaultInMin), 58, () => commit());
            inMaxBox = CreateMsfsBindingBox(FormatNullable(ov?.InMax), FormatDouble(info.DefaultInMax), 58, () => commit());

            // raw [min → max]  ⇒  norm (graph-side, fixed)
            var range = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            range.Children.Add(inMinBox);
            range.Children.Add(CreateMsfsGlyph("→"));
            range.Children.Add(inMaxBox);
            range.Children.Add(CreateMsfsFixedRange("⇒ norm", info.DefaultOutMin, info.DefaultOutMax));

            return CreateMsfsCard(info, simVarBox, unitBox, range, () =>
            {
                Plugin.ResetMsfsReadBinding(info.Alias);
                RefreshVehicleParams();
            });
        }

        private Border CreateMsfsWriteRow(MsfsBindingInfo info)
        {
            var ov = Plugin.GetMsfsWriteOverride(info.Alias);

            TextBox simVarBox = null, unitBox = null, outMinBox = null, outMaxBox = null;
            // Writes: the sim value is the map OUTPUT, so Out* is the overridable
            // sim-side pair; In* is the graph-side range (inherited, shown fixed).
            Action commit = () => Plugin.SetMsfsWriteBinding(
                info.Alias,
                simVarBox.Text,
                unitBox.Text,
                ParseNullableDouble(outMinBox.Text),
                ParseNullableDouble(outMaxBox.Text));

            simVarBox = CreateMsfsBindingBox(NullToEmpty(ov?.SimVar), info.DefaultSimVar, 260, () => commit());
            unitBox = CreateMsfsBindingBox(NullToEmpty(ov?.Unit), info.DefaultUnit, 120, () => commit());
            outMinBox = CreateMsfsBindingBox(FormatNullable(ov?.OutMin), FormatDouble(info.DefaultOutMin), 58, () => commit());
            outMaxBox = CreateMsfsBindingBox(FormatNullable(ov?.OutMax), FormatDouble(info.DefaultOutMax), 58, () => commit());

            // graph (graph-side, fixed)  ⇒  sim [min → max]
            var range = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            range.Children.Add(CreateMsfsFixedRange("graph", info.DefaultInMin, info.DefaultInMax));
            range.Children.Add(CreateMsfsGlyph("⇒ sim"));
            range.Children.Add(outMinBox);
            range.Children.Add(CreateMsfsGlyph("→"));
            range.Children.Add(outMaxBox);

            return CreateMsfsCard(info, simVarBox, unitBox, range, () =>
            {
                Plugin.ResetMsfsWriteBinding(info.Alias);
                RefreshVehicleParams();
            });
        }

        // A per-alias card: header (alias + default SimVar + Reset) over a 2-column
        // grid of aligned SimVar / Unit / Range rows.
        private Border CreateMsfsCard(MsfsBindingInfo info, TextBox simVarBox, TextBox unitBox,
            UIElement rangeRow, Action onReset)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var header = CreateMsfsHeader(info, onReset);
            Grid.SetRow(header, 0);
            Grid.SetColumn(header, 0);
            Grid.SetColumnSpan(header, 2);
            grid.Children.Add(header);

            AddMsfsFieldRow(grid, "SimVar", simVarBox);
            AddMsfsFieldRow(grid, "Unit", unitBox);
            AddMsfsFieldRow(grid, "Range", rangeRow);

            return new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x59, 0x2B, 0x2B, 0x2B)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 5, 8, 7),
                Margin = new Thickness(0, 0, 0, 6),
                Child = grid
            };
        }

        private DockPanel CreateMsfsHeader(MsfsBindingInfo info, Action onReset)
        {
            var dock = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 3) };

            var resetBtn = CreateMsfsResetButton(onReset);
            DockPanel.SetDock(resetBtn, Dock.Right);
            dock.Children.Add(resetBtn);

            var wrap = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            wrap.Children.Add(new TextBlock
            {
                Text = info.Alias,
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Arial"),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });
            if (!string.IsNullOrEmpty(info.DefaultSimVar))
            {
                wrap.Children.Add(new TextBlock
                {
                    Text = "   " + info.DefaultSimVar,
                    Foreground = Brushes.Gray,
                    FontFamily = new FontFamily("Arial"),
                    FontSize = 9,
                    FontStyle = FontStyles.Italic,
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "Graph-default SimConnect target for this alias"
                });
            }
            dock.Children.Add(wrap);
            return dock;
        }

        // Append one "label : content" row to a card body grid, aligned on column 0.
        private void AddMsfsFieldRow(Grid grid, string label, UIElement content)
        {
            int r = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var lbl = new TextBlock
            {
                Text = label,
                Foreground = Brushes.Gray,
                FontFamily = new FontFamily("Arial"),
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 3, 6, 0)
            };
            Grid.SetRow(lbl, r);
            Grid.SetColumn(lbl, 0);
            grid.Children.Add(lbl);

            if (content is FrameworkElement fe) fe.Margin = new Thickness(0, 3, 0, 0);
            Grid.SetRow(content, r);
            Grid.SetColumn(content, 1);
            grid.Children.Add(content);
        }

        private static TextBlock CreateMsfsGlyph(string text) => new TextBlock
        {
            Text = text,
            Foreground = Brushes.Gray,
            FontFamily = new FontFamily("Arial"),
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 4, 0)
        };

        // Read-only display of an inherited (graph-side) range the user can't override.
        private static TextBlock CreateMsfsFixedRange(string label, double min, double max) => new TextBlock
        {
            Text = $"{label} {FormatDouble(min)}..{FormatDouble(max)}",
            Foreground = Brushes.DimGray,
            FontFamily = new FontFamily("Arial"),
            FontSize = 10,
            FontStyle = FontStyles.Italic,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 2, 0),
            ToolTip = "Graph-side range (inherited from the graph node; not per-vehicle)"
        };

        private static Button CreateMsfsResetButton(Action onReset)
        {
            var btn = new Button
            {
                Content = "Reset",
                Width = 50,
                Height = 22,
                FontFamily = new FontFamily("Arial"),
                FontSize = 9,
                Margin = new Thickness(6, 0, 0, 0),
                ToolTip = "Clear this override (inherit the graph default)"
            };
            btn.Click += (s, e) => onReset();
            return btn;
        }

        private static double? ParseNullableDouble(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            return double.TryParse(s.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d) ? (double?)d : null;
        }

        private static string FormatNullable(double? v) =>
            v.HasValue ? v.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";

        private static string FormatDouble(double v) =>
            v.ToString(System.Globalization.CultureInfo.InvariantCulture);

        private void OnParamMutesCleared_Vehicle(object sender, EventArgs e)
        {
            // Vehicle-profile switch cleared all mutes; rebuild the panel so
            // checkbox state mirrors the cleared plugin state.
            Dispatcher.BeginInvoke(new Action(RefreshVehicleParams));
        }

        private void OnGraphParamChanged_Vehicle(object sender, GraphParamChangedEventArgs e)
        {
            if (isUpdatingVehicleParams)
                return;

            Dispatcher.Invoke(() =>
            {
                isUpdatingVehicleParams = true;
                try
                {
                    if (vehicleParamControls.TryGetValue(e.ParamName, out var control))
                    {
                        if (control is Slider slider)
                        {
                            slider.Value = e.Value;
                        }
                        else if (control is TextBox textBox)
                        {
                            var allParams = Plugin?.GetActiveGraphParams();
                            int precision = 3;
                            if (allParams != null && allParams.TryGetValue(e.ParamName, out var param))
                            {
                                precision = param.Ui?.Precision ?? 3;
                            }
                            textBox.Text = e.Value.ToString($"F{precision}");
                        }
                        else if (control is CheckBox checkBox)
                        {
                            checkBox.IsChecked = e.Value > 0.5;
                        }
                        else if (control is ComboBox comboBox)
                        {
                            foreach (var item in comboBox.Items)
                            {
                                if (item is GraphParamOption opt
                                    && double.TryParse(opt.Value, System.Globalization.NumberStyles.Float,
                                        System.Globalization.CultureInfo.InvariantCulture, out var optVal)
                                    && Math.Abs(optVal - e.Value) < 1e-6)
                                {
                                    comboBox.SelectedItem = item;
                                    break;
                                }
                            }
                        }
                    }

                    // Update label with new value
                    if (vehicleParamLabels.TryGetValue(e.ParamName, out var label))
                    {
                        var allParams = Plugin?.GetActiveGraphParams();
                        if (allParams != null && allParams.TryGetValue(e.ParamName, out var param))
                        {
                            label.Content = FormatVehicleParamLabel(param, e.Value);
                        }
                    }
                }
                finally
                {
                    isUpdatingVehicleParams = false;
                }
            });
        }

        private string FormatVehicleParamLabel(GraphParam param, double currentValue)
        {
            string label = param.Ui?.Label ?? param.Name;
            string widget = (param.Ui?.Widget ?? "").Trim().ToLowerInvariant();

            string valueStr;
            if (widget == "enum")
            {
                valueStr = FormatEnumValue(param.Ui, currentValue);
            }
            else if (widget == "checkbox")
            {
                valueStr = currentValue > 0.5 ? "On" : "Off";
            }
            else
            {
                int precision = param.Ui?.Precision ?? 3;
                valueStr = currentValue.ToString($"F{precision}");
            }

            if (!string.IsNullOrWhiteSpace(param.Ui?.Units))
            {
                return $"{label}: {valueStr}{param.Ui.Units}";
            }
            return $"{label}: {valueStr}";
        }

        private static string FormatEnumValue(GraphParamUi ui, double currentValue)
        {
            if (ui?.Options == null || ui.Options.Count == 0)
            {
                return currentValue.ToString("F3");
            }

            foreach (var option in ui.Options)
            {
                if (double.TryParse(option.Value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var optVal)
                    && Math.Abs(optVal - currentValue) < 1e-6)
                {
                    return option.Label ?? option.Value;
                }
            }

            return currentValue.ToString("F3");
        }

        #endregion
    }
}
