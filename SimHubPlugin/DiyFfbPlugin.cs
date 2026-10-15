using GameReaderCommon;
using NCalc;
using ProtbufTest;


//using log4net.Plugin;
using SimHub.Plugins;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Windows.Media;
using DiyFfb.GraphEditor;
using DiyFfb.Msfs;
using DiyFfb.TieredConfig;
using Windows.UI.Notifications;
using IPlugin = SimHub.Plugins.IPlugin;
namespace DiyFfb
{
    /// <summary>
    /// Category of the active graph, used to customize the Vehicle/Aircraft tab.
    /// </summary>
    public enum GraphCategory
    {
        Vehicle,      // Default: car icon, "VEHICLE" label
        Aircraft,     // Airplane icon, "AIRCRAFT" label (FlightStick*, FlightPedals*)
        Helicopter    // Helicopter icon, "AIRCRAFT" label (FlightStickCollective)
    }

    [PluginDescription("This Plugin handles DIY FFB axes and gateways, communicating via USB.")]
    [PluginAuthor("OpenSource")]
    [PluginName("DIY FFB plugin")]
    public class DiyFfbPlugin : IPlugin, IDataPlugin, IWPFSettingsV2
    {
        DiyFfbPluginUI ui;

        public bool sendAbsSignal = false;
        public byte rpm_last_value = 0 ;
        public double g_force_last_value = 128;
        public byte Road_impact_last = 0;
        public byte game_running_index = 0 ;
        public uint testValue = 0;
        public uint[] profile_flag = new uint[4] { 0,0,0,0};
        public uint[] select_button_flag = new uint[2] { 0, 0, };// define the up and down selection
        public uint slotA_flag = 0;
        public uint slotB_flag = 0;
        public uint slotC_flag = 0;
        public uint slotD_flag = 0;
        public uint sendconfig_flag = 0;
        public uint in_game_flag = 0; // check current game is off or pause
        public string current_profile = "NA" ;
        public uint profile_index = 0;
        //public uint Page_update_flag = 0;
        public bool binding_check=false;
        public bool pedal_select_update_flag = false;
        public string current_pedal = "NA";
        public string current_action = "NA";
        public bool Page_update_flag =false;
        public uint overlay_display = 0;
        public string simhub_theme_color = "#7E87CEFA";
        public uint debug_value = 0;
        public bool clear_action = false;
        public byte pedal_state_in_ratio = 0;
        public bool Sync_esp_connection_flag=false;
        public byte PedalErrorCode = 0;
        public byte PedalErrorIndex = 0;
        public byte[] random_pedal_action_interval=new byte[3] { 50,51,53};
        public string Simhub_version = "";
        public bool Version_Check_Simhub_MSFS = false;

        //effect trigger timer
        DateTime[] Action_currentTime = new DateTime[3];
        DateTime[] Action_lastTime = new DateTime[3];
        

        // ABS trigger timer
        DateTime absTrigger_currentTime = DateTime.Now;
        DateTime absTrigger_lastTime = DateTime.Now;

        //G force timer
        DateTime GTrigger_currentTime = DateTime.Now;
        DateTime GTrigger_lastTime = DateTime.Now;

        //Road effect
        DateTime RoadTrigger_currentTime = DateTime.Now;
        DateTime RoadTrigger_lastTime = DateTime.Now;
        //https://www.c-sharpcorner.com/uploadfile/eclipsed4utoo/communicating-with-serial-port-in-C-Sharp/
        public SerialPort[] _serialPort = new SerialPort[8] {new SerialPort("COM7", 921600, Parity.None, 8, StopBits.One),
            new SerialPort("COM7", 921600, Parity.None, 8, StopBits.One),
            new SerialPort("COM7", 921600, Parity.None, 8, StopBits.One),new SerialPort("COM7", 921600, Parity.None, 8, StopBits.One),
        new SerialPort("COM7", 921600, Parity.None, 8, StopBits.One),
            new SerialPort("COM7", 921600, Parity.None, 8, StopBits.One),
            new SerialPort("COM7", 921600, Parity.None, 8, StopBits.One),new SerialPort("COM7", 921600, Parity.None, 8, StopBits.One)};

        public ProtobufSerial<Message> ESPsync_serialPort = new ProtobufSerial<Message>("COM7", 3000000);
        private const int GatewayReconnectIntervalMs = 2000;
        private Timer gatewayReconnectTimer;
        private int gatewayReconnectBusy = 0;
        private const uint XPlanePacketMagic = 0x46464244;
        private const ushort XPlanePacketVersion = 4;
        private const int XPlaneMaxRotors = 4;
        private const int XPlanePacketSizeBytes = 212;
        private const double XPlaneTelemetryFreshnessMs = 200.0;
        private const double XPlaneRotorWindowSeconds = 3.0;
        private readonly object xplaneLock = new object();
        private UdpClient xplaneUdpClient;
        private Thread xplaneUdpThread;
        private CancellationTokenSource xplaneUdpCts;
        private XPlaneUdpPacket latestXPlanePacket;
        private uint xplaneLastSequence;
        private int xplaneDropouts;
        private DateTime xplaneLastReceivedUtc = DateTime.MinValue;
        private DateTime xplaneLastSendUtc = DateTime.MinValue;

        // Plan 17/19: MSFS telemetry snapshot. Derivations (BladeAlph / VRS /
        // Slap / Propwash / Torque) run in BuildMsfsInputs using per-aircraft
        // graph params for tuning.
        private const double MsfsTelemetryFreshnessMs = 200.0;
        private readonly object msfsLock = new object();
        private MsfsUdpPacket latestMsfsPacket;
        // Plan 19: pure-C# in-process SimConnect client — the single MSFS
        // transport. Populates latestMsfsPacket under msfsLock so downstream
        // (GetLatestMsfsPacket / BuildMsfsInputs) sees one consistent snapshot.
        private MsfsSimConnectClient _msfsClient;
        private string activeCarId;
        private string activeCarName;
        private string activeGameId;
        private string activeGraphKey;
        private string activeGraphPath;
        private GraphDefinition activeVehicleGraph;
        private GraphValidationResult activeGraphValidation;
        private DiyFfb.GraphTest.GraphDefinition activeGraphRuntime;
        private DiyFfb.GraphTest.GraphCompiledEvaluator activeGraphEvaluator;
        private DiyFfb.GraphTest.GraphIncludeResolver activeGraphResolver;
        private DiyFfb.GraphTest.IncludeContextCache activeIncludeContextCache;
        private readonly Dictionary<string, double> graphInputs = new Dictionary<string, double>();
        private readonly Dictionary<string, double> graphParams = new Dictionary<string, double>();
        private long _lastGraphEvalTicks;

        // Grip-button held-state table (plan 11). Populated by SimHub input-mapping
        // callbacks: inputPressed → true, inputReleased → false. BuildGripInputs reads
        // the bool directly. SimHub's AddInputMapping primitive enforces held semantics,
        // so no heartbeat/timeout heuristic is needed.
        private readonly Dictionary<string, bool> _gripHeld = new Dictionary<string, bool>();
        private DiyFfb.GraphTest.GraphEvaluationResult lastGraphEvaluation;
        private readonly Dictionary<string, double> _lastConfigOutValues = new Dictionary<string, double>();

        // Plan 24: MsfsVarOut write dispatch state. _lastMsfsVarOutValues holds the
        // last MAPPED value sent per alias (change-detection, 1e-6 like ConfigOut).
        // _msfsVarOutMaps is the alias→range-map/prefix lookup, published as an
        // atomic immutable snapshot (built on the scan thread, read on the eval
        // thread — see UpdateMsfsWritableVars). _lastSeenWriteGeneration tracks the
        // client's WriteGeneration to force a full re-push on reconnect/re-register.
        private readonly Dictionary<string, double> _lastMsfsVarOutValues = new Dictionary<string, double>();
        private volatile Dictionary<string, MsfsVarOutMap> _msfsVarOutMaps =
            new Dictionary<string, MsfsVarOutMap>(StringComparer.Ordinal);
        private int _lastSeenWriteGeneration;

        // Plan 34: per-alias read (MsfsVarDef) range map, published atomically by
        // UpdateMsfsCustomVars and read on the eval thread by NormalizeMsfsReadValue.
        // The raw sim value (In*) is normalized to the graph-side range (Out*). An
        // identity map (0..1 → 0..1) is treated as passthrough so existing read ports
        // that carry raw units (RPM, altitude, …) are never clamped.
        private volatile Dictionary<string, MsfsVarInMap> _msfsVarInMaps =
            new Dictionary<string, MsfsVarInMap>(StringComparer.Ordinal);

        // Per-port range map + transport prefix for one MsfsVarOut alias.
        private sealed class MsfsVarOutMap
        {
            public double InMin, InMax, OutMin, OutMax;
            // B: input events and K: key/sim events are actuations, not idempotent
            // state assignments — exempt from the forced re-push on reconnect (RD6).
            public bool IsActuation;
        }

        // Per-port range map for one MsfsVarDef read alias.
        private sealed class MsfsVarInMap
        {
            public double InMin, InMax, OutMin, OutMax;
        }

        // Plan 34: normalize one incoming MsfsVarDef read value by its per-alias range
        // map. Passthrough when no map is registered or the map is the identity default
        // (protects raw-unit read ports from being clamped into 0..1). Math lives in
        // MsfsBindingResolver so it is unit-testable without SimConnect.
        internal double NormalizeMsfsReadValue(string alias, double raw)
        {
            var maps = _msfsVarInMaps;
            if (maps != null && alias != null && maps.TryGetValue(alias, out var m))
            {
                return MsfsBindingResolver.NormalizeRead(raw, m.InMin, m.InMax, m.OutMin, m.OutMax);
            }
            return raw;
        }
        private static Func<GameData, string> gameIdGetter;
        private DiyFfbPluginSettings.AircraftFfbProfile pendingFfbProfile;
        private bool hasPendingFfbProfile;
        private readonly HashSet<FunctionID> disabledOutputFunctions = new HashSet<FunctionID>();

        // Safety damper state (plan 10 §3.7). Runtime-only, defaults to ENGAGED on every
        // SimHub start. When engaged, FlightFFB frame emission is gated for all four
        // flight functions; the ESP32 falls back to FlightControlConfig.damping (rest
        // value) within ~200ms, holding the stick at safe damping until the user
        // disengages via the FlightControl.SafetyDamperToggle action.
        private bool _flightSafetyDamperEngaged = true;
        private static readonly FunctionID[] _flightFunctionIds = new[] {
            FunctionID.FlightStickPitch, FunctionID.FlightStickRoll,
            FunctionID.FlightStickCollective, FunctionID.FlightPedals,
        };
        public event System.Action<bool> FlightSafetyDamperChanged;
        public bool IsFlightSafetyDamperEngaged() => _flightSafetyDamperEngaged;
        public void SetFlightSafetyDamperEngaged(bool engaged)
        {
            if (_flightSafetyDamperEngaged == engaged) return;
            _flightSafetyDamperEngaged = engaged;
            foreach (var fid in _flightFunctionIds)
                SetFunctionOutputDisabled(fid, engaged);
            try { FlightSafetyDamperChanged?.Invoke(engaged); } catch { }
        }
        private readonly object outputDisableLock = new object();
        private readonly Queue<RotorRpmSample>[] rotorRpmHistory = new Queue<RotorRpmSample>[XPlaneMaxRotors];
        private int lastAutoRotorIndex = 0;
        private bool hasAutoRotorIndex = false;

        // Tiered config managers for profile/user overrides
        private readonly FunctionConfigManager _functionConfigManager = new FunctionConfigManager();
        private readonly AxisConfigManager _axisConfigManager = new AxisConfigManager();
        private TieredConfigOrchestrator _configOrchestrator;

        /// <summary>
        /// Orchestrates tiered config lifecycle (Baseline → Profile → User).
        /// </summary>
        public TieredConfigOrchestrator ConfigOrchestrator => _configOrchestrator;

        /// <summary>
        /// Manages function config lifecycle for profile/user overrides.
        /// </summary>
        public FunctionConfigManager FunctionConfigManager => _functionConfigManager;

        /// <summary>
        /// Manages axis config lifecycle for function overrides.
        /// </summary>
        public AxisConfigManager AxisConfigManager => _axisConfigManager;

        internal sealed class XPlaneUdpPacket
        {
            public uint Sequence;
            public float IasKts;
            public float TasMps;
            public float AlphaDeg;
            public float BetaDeg;
            public float PRate;
            public float QRate;
            public float RRate;
            public float ElevDefDeg;
            public float AilDefDeg;
            public float RudDefDeg;
            public float ElevTrimNorm;
            public float AilTrimNorm;
            public float RudTrimNorm;
            public float GNrml;
            public float[] TorqueNm = new float[XPlaneMaxRotors];
            public float[] OmegaRad = new float[XPlaneMaxRotors];
            public float[] PropRatio = new float[XPlaneMaxRotors];
            public float LAero;
            public float MAero;
            public float NAero;
            public bool OnGround;
            // v4: rotor vibration signals
            public float[] CyclicElevBladAlph = new float[XPlaneMaxRotors];
            public float[] CyclicAilnBladAlph = new float[XPlaneMaxRotors];
            public float[] RotorBladeSlapRat = new float[XPlaneMaxRotors];
            public float[] VortexRingState = new float[XPlaneMaxRotors];
            public float[] PropwashMtrSec = new float[XPlaneMaxRotors];
            public DateTime ReceivedUtc;
        }

        // Plan 17: MSFS UDP packet — v1, 152 bytes.
        // Magic 'MFFB' = 0x4D464642. Bridge ships raw SimVars only;
        // derivations live in GraphSignals.BuildMsfsInputs so per-aircraft
        // tuning happens through the existing graph-param mechanism.
        internal sealed class MsfsUdpPacket
        {
            public uint Sequence;
            // Native non-rotor SimVars
            public float IasKts;
            public float TasKts;
            public float AlphaDeg;
            public float BetaDeg;
            public float PRateRadS;
            public float QRateRadS;
            public float RRateRadS;
            public float GForce;
            public float VviWorldFps;
            public float VelocityBodyXFps;
            public float VelocityBodyYFps;
            public float VelocityBodyZFps;
            public float GroundVelocityKts;
            public float PitchRad;
            public float BankRad;
            public float TotalWeightLb;
            public float AmbientDensitySlugsFt3;
            // Native rotor / heli SimVars
            public float MainRotorRpm;
            public float TailRotorRpm;
            public float EngTorquePct;
            public float CollectivePosPct;
            public float TailRotorPedalPct;
            public float TailRotorBladePitchPct;
            public float RotorCollectiveBladePitchPct;
            public float RotorCyclicBladePitchPct;
            public float RotorCyclicBladeMaxPitchPosRad;
            public float DiskPitchAngleRad;
            public float DiskBankAngleRad;
            public float DiskConingPct;
            public float RotorLateralTrimPct;
            public float RotorLongitudinalTrimPct;
            public float RotorRotationAngleRad;
            // Fixed-wing trims (future use)
            public float ElevTrimPct;
            public float AilTrimPct;
            public float RudTrimPct;
            public bool OnGround;
            // Plan 23: graph-declared custom vars, keyed by alias. Null/empty
            // when no MsfsVarDef vars are declared or none survived registration.
            public IReadOnlyDictionary<string, double> Custom;
            public DateTime ReceivedUtc;
        }

        private struct RotorRpmSample
        {
            public DateTime Utc;
            public float Rpm;
        }

        //for (byte pedalIdx_lcl = 0; pedalIdx_lcl< 3; pedalIdx_lcl++)
        //{
        //    _serialPortt[pedalIdx_lcl].RtsEnable = false;
        //    _serialPort[pedalIdx_lcl].DtrEnable = true;
        //}   







        public bool[] connectSerialPort = { false, false, false };


        public DiyFfbPluginSettings Settings;



        /// <summary>
        /// Instance of the current plugin manager
        /// </summary>
        public PluginManager PluginManager { get; set; }

        /// <summary>
        /// Gets the left menu icon. Icon must be 24x24 and compatible with black and white display.
        /// </summary>
        public ImageSource PictureIcon => this.ToIcon(Properties.Resources.menuicon);

        /// <summary>
        /// Gets a short plugin title to show in left menu. Return null if you want to use the title as defined in PluginName attribute.
        /// </summary>
        public string LeftMenuTitle => "DIY FFB Dashboard";

        public string Ncalc_reading(String expression)
        {
            string value = "";
            try
            {
                NCalc.Expression exp = new NCalc.Expression(expression);
                exp.ResolveParameter += delegate (string name, ParameterResolveArgs rarg)
                {
                    rarg.Result = () => PluginManager.GetPropertyValue(name);
                };

                if (exp.HasErrors() == false)
                {
                    value = exp.Evaluate().ToString();
                }
                else
                {
                    value = "Error";
                }

            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error(ex.Message);
            }

            return value;
        }

        private void UpdateFFBData(GameData data)
        {
            for (uint function_idx = 0; function_idx < Settings.function_settings.Length; function_idx++)
            {
                FunctionID function_id = (FunctionID)function_idx + 1;
                Message tmp = new Message();
                tmp.FfbAction = new FFBAction();
                tmp.FfbAction.FunctionId = function_id;
                switch (function_id)
                {
                    case FunctionID.BrakePedal:
                        tmp.FfbAction.AutomotivePedal = new AutomotivePedalFFBAction();
                        if (data.NewData?.ABSActive > 0)
                        {
                            tmp.FfbAction.AutomotivePedal.TriggerAbs = true;
                            if (ESPsync_serialPort.IsOpen)
                            {
                                ESPsync_serialPort.WriteMessage(tmp);
                            }
                        }
                        break;
                    case FunctionID.AcceleratorPedal:
                        tmp.FfbAction.AutomotivePedal = new AutomotivePedalFFBAction();
                        break;
                    case FunctionID.ClutchPedal:
                        tmp.FfbAction.AutomotivePedal = new AutomotivePedalFFBAction();
                        break;
                }
            }
        }

        /// <summary>
        /// Called one time per game data update, contains all normalized game data,
        /// raw data are intentionnally "hidden" under a generic object type (A plugin SHOULD NOT USE IT)
        ///
        /// This method is on the critical path, it must execute as fast as possible and avoid throwing any error
        ///
        /// </summary>
        /// <param name="pluginManager"></param>
        /// <param name="data">Current game data, including current and previous data frame.</param>
        /// 
        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
			
			bool sendAbsSignal_local_b = false;
            bool sendTcSignal_local_b = false;
            double RPM_value =0;
            double RPM_MAX = 0;
            double _G_force = 128;
            byte WS_value = 0;
            byte Road_impact_value = 0;
            byte CV1_value = 0;
            byte CV2_value = 0;
            double MSFS_RPM_Value_Simhub = 0;
            double RUDDER_DEFLECTION_Simhub = 0;
            double RELATIVE_WIND_VELOCITY_BODY_Z_Simhub = 0;
            double ACCELERATION_BODY_Z_Simhub = 0;
            double ACCELERATION_BODY_Y_Simhub = 0;
            bool MSFS_running_simhub = false;
            
            //bool WS_flag = false;

            if (data.NewData != null)
            {
                string gameId = GetGameIdSafe(data);
                HandleGameChange(gameId);
                HandleAircraftChange(data, gameId);
            }

            if (data.GamePaused | (!data.GameRunning))
            {
                in_game_flag = 0;
            }
            else 
            {
                in_game_flag = 1;
            }
            //for (uint pedalIdx = 0; pedalIdx < 3; pedalIdx++)
            //{
            //    if (_serialPort[pedalIdx].IsOpen)
            //    {
            //        int receivedLength = _serialPort[pedalIdx].BytesToRead;

            //        settings.TextBox_debugOutput.Text = "Test";
            //    }
            //}


            // Send ABS signal when triggered by the game
            if (data.GameRunning)
            {
                if (data.OldData != null && data.NewData != null)
                {
                    if (data.NewData.ABSActive > 0)
                    {
                        sendAbsSignal_local_b = true;
                    }

                    if (data.NewData.TCActive > 0)
                    {
                        sendTcSignal_local_b = true;
                    }


                    // when test signal is activated, overwrite trigger signal
                    if (sendAbsSignal)
                    {
                        sendAbsSignal_local_b = true;
                        sendTcSignal_local_b = true;
                    }



                    //fill the RPM value
                    if (Settings.RPM_effect_type == 0)
                    {
                        if (data.NewData.CarSettings_MaxRPM == 0)
                        {
                            RPM_MAX = 10000;
                        }
                        else
                        {
                            RPM_MAX = data.NewData.CarSettings_MaxRPM;
                        }

                        RPM_value = (data.NewData.Rpms / RPM_MAX * 100);
                    }
                    else
                    {
                        if (data.NewData.MaxSpeedKmh == 0)
                        {
                            RPM_MAX = 300;
                        }
                        else
                        { 
                            RPM_MAX= data.NewData.MaxSpeedKmh;
                        }
                        RPM_value = (data.NewData.SpeedKmh / RPM_MAX * 100);
                    }

                    
                    if (data.NewData.GlobalAccelerationG != 0)
                    {
                        _G_force = -1 * data.NewData.GlobalAccelerationG + 128;
                    }
                    else
                    {
                        _G_force = 128;
                    }

                    game_running_index = 1;
                    

                }
                else
                {
                    RPM_value = 0;
                    _G_force = 128;
                    


                }
            }
            else
            {
                RPM_value = 0;
                _G_force = 128;
                
            }
			




            absTrigger_currentTime = DateTime.Now;
            TimeSpan diff = absTrigger_currentTime - absTrigger_lastTime;
            int millisceonds = (int)diff.TotalMilliseconds;
            if (millisceonds <= 10)
            {
                sendAbsSignal_local_b = false;
                sendTcSignal_local_b = false;
                

            }
            else
            {
                absTrigger_lastTime = DateTime.Now;
            }




            bool update_flag = false;

            if (data.GameRunning)
            {
                UpdateFFBData(data);
                EvaluateActiveGraph(data);
                // Send ABS trigger signal via serial
                //for (uint function_idx = 0; function_idx < Settings.function_settings.Length; function_idx++)
                //{
                //    FunctionID function_id = (FunctionID)function_idx + 1;
                //    Message tmp = new Message();
                //    tmp.FfbAction = new FFBAction();
                //    switch ((FunctionID)function_idx + 1)
                //    {
                //        case FunctionID.Brake:
                //            tmp.FfbAction.AutomotivePedal = new AutomotivePedalFFBAction();
                //            break;
                //        case FunctionID.Accelerator:
                //            tmp.FfbAction.AutomotivePedal = new AutomotivePedalFFBAction();
                //            break;
                //        case FunctionID.Clutch:
                //            tmp.FfbAction.AutomotivePedal = new AutomotivePedalFFBAction();
                //            break;
                //    }
                //    tmp.FfbAction.AutomotivePedal = new AutomotivePedalFFBAction();

                //        if (Settings.function_settings[function_idx].G_force_enabled)
                //        {
                //            tmp.FfbAction.AutomotivePedal.G = (Byte)g_force_last_value;
                //        }
                //        else
                //        {
                //            tmp.FfbAction.AutomotivePedal.G = 128;
                //        }


                //        if (Settings.function_settings[function_idx].RPM_enabled)
                //        {

                //            if (Math.Abs(RPM_value - rpm_last_value) > 3)
                //            {
                //            tmp.FfbAction.AutomotivePedal.Rpm = (Byte)RPM_value;
                //                update_flag = true;
                //                rpm_last_value = (Byte)RPM_value;
                //            }

                //        }
                //        else
                //        {
                //        tmp.FfbAction.AutomotivePedal.Rpm = 0;
                //        }

                //        //G force effect only effect on brake
                //        if (function_idx == 1)
                //        {

                //            GTrigger_currentTime = DateTime.Now;
                //            TimeSpan diff_G = GTrigger_currentTime - GTrigger_lastTime;
                //            int millisceonds_G = (int)diff_G.TotalMilliseconds;
                //            if (millisceonds <= 10)
                //            {
                //                _G_force = g_force_last_value;
                //            }
                //            else
                //            {
                //                GTrigger_lastTime = DateTime.Now;
                //            }
                //            if (Settings.function_settings[function_idx].G_force_enabled)
                //            {
                //                //double value_check_g = 1 - _G_force / ((double)g_force_last_value);
                //                double value_check_g = (_G_force - (double)g_force_last_value);
                //                if (Math.Abs(value_check_g) > 2)
                //                {
                //                    tmp.FfbAction.AutomotivePedal.G = (Byte)_G_force;
                //                    update_flag = true;
                //                    g_force_last_value = (Byte)_G_force;
                //                }

                //            }
                //        }

                //        //Wheel slip

                //        if (Settings.function_settings[function_idx].WS_enabled)
                //        {
                //            if (pluginManager.GetPropertyValue(Settings.WSeffect_bind) != null)
                //            {
                //                /*object tmp_ws = (pluginManager.GetPropertyValue(Settings.WSeffect_bind));
                //                int tmp_ws_number = Int32.Parse(tmp_ws.ToString());
                //                WS_value = (byte)tmp_ws_number;
                //                */
                //                WS_value = Convert.ToByte(pluginManager.GetPropertyValue(Settings.WSeffect_bind));
                //                //pluginManager.SetPropertyValue("Wheelslip-test", this.GetType(), WS_value);
                //                if (WS_value >= (Settings.WS_trigger + 50))
                //                {
                //                    tmp.FfbAction.AutomotivePedal.TriggerWs = true;
                //                    update_flag = true;
                //                }
                //            }
                //        }
                //        //Road impact
                //        if (Settings.function_settings[function_idx].Road_impact_enabled)
                //        {
                //            if (pluginManager.GetPropertyValue(Settings.Road_impact_bind) != null)
                //            {
                //                Road_impact_value = Convert.ToByte(pluginManager.GetPropertyValue(Settings.Road_impact_bind));

                //                RoadTrigger_currentTime = DateTime.Now;
                //                TimeSpan diff_Road = RoadTrigger_currentTime - RoadTrigger_lastTime;
                //                int millisceonds_G = (int)diff_Road.TotalMilliseconds;
                //                if (millisceonds <= 10)
                //                {
                //                    Road_impact_value = Road_impact_last;
                //                }
                //                else
                //                {
                //                    RoadTrigger_lastTime = DateTime.Now;
                //                }
                //                if (true)
                //                {
                //                    //double value_check_g = 1 - _G_force / ((double)g_force_last_value);
                //                    double value_check_road = Road_impact_value - Road_impact_last;
                //                    if (Math.Abs(value_check_road) > 2)
                //                    {
                //                        tmp.FfbAction.AutomotivePedal.ImpactValue = Road_impact_value;
                //                        update_flag = true;
                //                        Road_impact_last = Road_impact_value;
                //                        debug_value = Road_impact_value;
                //                    }

                //                }
                //            }
                //        }
                //     //custom effcts
                //     if (Settings.function_settings[function_idx].CV1_enabled == true)
                //     {
                //        //CV1_value = Convert.ToByte(pluginManager.GetPropertyValue(Settings.CV1_bindings[pedalIdx]));
                //        string temp_string = Ncalc_reading(Settings.function_settings[function_idx].CV1_binding);
                //        if (temp_string != "Error")
                //        {
                //            CV1_value = Convert.ToByte(temp_string);
                //        }
                //        else
                //        {
                //            CV1_value = 0;
                //            SimHub.Logging.Current.Error("CV1 Reading error");
                //        }


                //        if (CV1_value > (Settings.function_settings[function_idx].CV1_trigger_level))
                //        {
                //            tmp.FfbAction.AutomotivePedal.TriggerCv1 = true;
                //            update_flag = true;
                //        }
                //    }
                //     if (Settings.function_settings[function_idx].CV2_enabled == true)
                //     {

                //        //CV2_value = Convert.ToByte(pluginManager.GetPropertyValue(Settings.CV2_bindings[pedalIdx]));
                //        string temp_string = Ncalc_reading(Settings.function_settings[function_idx].CV2_binding);
                //        if (temp_string != "Error")
                //        {
                //            CV2_value = Convert.ToByte(temp_string);
                //        }
                //        else
                //        {
                //            CV2_value = 0;
                //            SimHub.Logging.Current.Error("CV2 Reading error");
                //        }
                //        if (CV2_value > (Settings.function_settings[function_idx].CV2_trigger_level))
                //        {
                //            tmp.FfbAction.AutomotivePedal.TriggerCv2 = true;
                //            update_flag = true;
                //        }

                //    }




                //        if (function_idx == 1)
                //        {
                //            if (sendAbsSignal_local_b && Settings.function_settings[function_idx].ABS_enabled)
                //            {
                //            //_serialPort[1].Write("2");

                //            // compute checksum
                //                tmp.FfbAction.AutomotivePedal.TriggerAbs = true;
                //                update_flag = true;

                //            }
                //        }
                //        if (function_idx == 2)
                //        {
                //            if (sendTcSignal_local_b && Settings.function_settings[function_idx].ABS_enabled)
                //            {
                //            // compute checksum

                //                tmp.FfbAction.AutomotivePedal.TriggerAbs = true;
                //                update_flag = true;

                //            }
                //        }
                //    // check the update interval
                //    if (update_flag)
                //    {
                //        Action_currentTime[function_idx] = DateTime.Now;
                //        TimeSpan diff_action = Action_currentTime[function_idx] - Action_lastTime[function_idx];
                //        int millisceonds_action = (int)diff_action.TotalMilliseconds;
                //        if (millisceonds_action <= Settings.function_settings[function_idx].action_interval)
                //        {
                //            update_flag = false;
                //        }
                //        else
                //        {
                //            Action_lastTime[function_idx] = DateTime.Now;

                //        }


                //    }


                //    if (update_flag)
                //    {

                //            if (Settings.axis_settings[function_idx].via_gateway)
                //            {
                //                if (ESPsync_serialPort.IsOpen)
                //                {
                //                    tmp.FfbAction.FunctionId = FunctionID.Brake; // TODO: set correctly
                //                    ESPsync_serialPort.WriteMessage(tmp);
                //                    //ESPsync_serialPort.DiscardInBuffer();
                //                    //ESPsync_serialPort.Write(newBuffer, 0, newBuffer.Length);
                //                    System.Threading.Thread.Sleep(7);
                //                }

                //            }
                //            else
                //            {
                //                if (_serialPort[function_idx].IsOpen)
                //                {
                //                    // clear inbuffer 
                //                    _serialPort[function_idx].DiscardInBuffer();

                //                    // send query command
                //                    //_serialPort[pedalIdx].Write(newBuffer, 0, newBuffer.Length);
                //                }

                //            }



                //    }

                //}

            }
            else
            {
                if (game_running_index == 1)
                {
                    game_running_index = 0;
                    clear_action = true;   
                }
            }



            // Send ABS test signal if requested
            if (sendAbsSignal)
            {
                Message tmp = new Message();
                tmp.FfbAction = new FFBAction();
                tmp.FfbAction.AutomotivePedal = new AutomotivePedalFFBAction();
                tmp.FfbAction.AutomotivePedal.TriggerAbs = true;

                if (ESPsync_serialPort.IsOpen)
                {
                    ESPsync_serialPort.WriteMessage(tmp);
                }
            }
            if (clear_action)
            {
                Message tmp = new Message();
                tmp.FfbAction = new FFBAction();
                tmp.FfbAction.AutomotivePedal = new AutomotivePedalFFBAction();
                tmp.FfbAction.AutomotivePedal.G = 128;
                tmp.FfbAction.AutomotivePedal.TriggerAbs = true;

                for (uint pedalIdx = 0; pedalIdx < 3; pedalIdx++)
                {
                    tmp.FfbAction.FunctionId = FunctionID.BrakePedal; // TODO: set correctly
                    if (Settings.axis_settings[pedalIdx].via_gateway)
                    {
                        if (ESPsync_serialPort.IsOpen)
                        {
                            ESPsync_serialPort.WriteMessage(tmp);
                            //ESPsync_serialPort.DiscardInBuffer();
                            //ESPsync_serialPort.Write(newBuffer, 0, newBuffer.Length);
                            System.Threading.Thread.Sleep(10);
                        }
                    }
                    else
                    {
                        if (_serialPort[pedalIdx].IsOpen)
                        {
                            // clear inbuffer 
                            _serialPort[pedalIdx].DiscardInBuffer();

                            // send query command
                            //_serialPort[pedalIdx].Write(newBuffer, 0, newBuffer.Length);
                        }

                    }
                }
                clear_action = false;
            }


            ProcessGraphFfb();

            this.AttachDelegate("CurrentProfile", () => current_profile);
            pluginManager.SetPropertyValue("SelectedPedal", this.GetType(), current_pedal);
            pluginManager.SetPropertyValue("Action", this.GetType(), current_action);
            pluginManager.SetPropertyValue("ABS_effect_status", this.GetType(), Settings.function_settings[Settings.function_tab_selected].ABS_enabled);
            pluginManager.SetPropertyValue("RPM_effect_status", this.GetType(), Settings.function_settings[Settings.function_tab_selected].RPM_enabled);
            pluginManager.SetPropertyValue("Gforce_effect_status", this.GetType(), Settings.function_settings[Settings.function_tab_selected].G_force_enabled);
            pluginManager.SetPropertyValue("WheelSlip_effect_status", this.GetType(), Settings.function_settings[Settings.function_tab_selected].WS_enabled);
            pluginManager.SetPropertyValue("RoadImpact_effect_status", this.GetType(), Settings.function_settings[Settings.function_tab_selected].Road_impact_enabled);
            pluginManager.SetPropertyValue("Overlay_display", this.GetType(), overlay_display);
            pluginManager.SetPropertyValue("Theme_color", this.GetType(), simhub_theme_color);
            pluginManager.SetPropertyValue("ProfileIndex", this.GetType(), profile_index);
            pluginManager.SetPropertyValue("debugvalue", this.GetType(), debug_value);
            pluginManager.SetPropertyValue("pedal_position", this.GetType(), pedal_state_in_ratio);
            pluginManager.SetPropertyValue("PedalErrorIndex", this.GetType(), PedalErrorIndex);
            pluginManager.SetPropertyValue("PedalErrorCode", this.GetType(), PedalErrorCode);
        }




        /// <summary>
        /// Returns the settings control, return null if no settings control is required
        /// </summary>
        /// <param name="pluginManager"></param>
        /// <returns></returns>
        public System.Windows.Controls.Control GetWPFSettingsControl(PluginManager pluginManager)
        {
            ui = new DiyFfbPluginUI(this);
            return ui;
        }


        /// <summary>
        /// Called at plugin manager stop, close/dispose anything needed here !
        /// Plugins are rebuilt at game change
        /// </summary>
        /// <param name="pluginManager"></param>
        public void End(PluginManager pluginManager)
        {           
            if (!string.IsNullOrWhiteSpace(activeCarId) && HasUnsavedProfileChanges(activeGameId, activeCarId))
            {
                bool saveCurrent = false;
                if (ui != null)
                {
                    saveCurrent = (bool)ui.Dispatcher.Invoke(new Func<bool>(() =>
                        ui.ConfirmSaveCurrentProfile(activeCarName, activeCarId)));
                }

                if (saveCurrent)
                {
                    SaveCurrentAircraftProfile(activeGameId, activeCarId);
                }
                else
                {
                    // User chose to discard - restore profile to its original state
                    DiscardProfileChanges();
                }
            }

            // Save graph state for current vehicle before shutdown
            if (!string.IsNullOrWhiteSpace(activeCarId))
            {
                SaveGraphState(BuildProfileKey(activeGameId, activeCarId));
            }

            // Save settings
            this.SaveCommonSettings("GeneralSettings", Settings);

            StopGatewayAutoReconnect();
            StopXPlaneUdpReceiver();
            // Plan 19: stop the in-process MSFS client (no-op if never started).
            StopMsfsClient();

            // close serial communication
            if (ui != null)
            {

                try
                {
                    //wpfHandle.joystick.Release();
                    //wpfHandle.joystick.Dispose();
                    if (ui.joystick != null)
                    {
                        ui.joystick.RelinquishVJD(Settings.vjoy_order);
                    }
                    
                    
                }
                catch (Exception caughtEx)
                { 
                }
                
                ui.CloseSerialPorts();
            }
            else if (ESPsync_serialPort != null && ESPsync_serialPort.IsOpen)
            {
                ESPsync_serialPort.Close();
            }
            
            if (ToastNotificationManager.History.GetHistory("Pedal_notification").Count != 0)
            {
                ToastNotificationManager.History.Remove("Pedal_notification");
            }
            

        }



        public bool PortExists(string portName)
        {
            return UsbDeviceScanner.IsPresent(portName);
        }

        /// <summary>
        /// The COM port the gateway is on right now, or null if it isn't present.
        /// A gateway saved by USB identity is looked up by PID, so a renumbered
        /// port is followed (and ESPNow_port updated); a stale COM name is never
        /// opened in its place.
        /// </summary>
        public string ResolveGatewayPort()
        {
            if (Settings == null)
            {
                return null;
            }

            if (Settings.GatewayUsbIndex >= 0)
            {
                UsbPort gateway = UsbDeviceScanner.FindGateway(Settings.GatewayUsbIndex);
                if (gateway == null)
                {
                    return null;
                }
                Settings.ESPNow_port = gateway.Com;
                return gateway.Com;
            }

            string portName = Settings.ESPNow_port;
            if (string.IsNullOrWhiteSpace(portName) || portName == "NA")
            {
                return null;
            }

            UsbPort port = UsbDeviceScanner.GetPorts()
                .FirstOrDefault(p => string.Equals(p.Com, portName, StringComparison.OrdinalIgnoreCase));
            if (port == null)
            {
                return null;
            }

            // A COM name saved before identity tracking that turns out to be a
            // DIY-FFB gateway: remember the gateway from now on.
            if (port.Role == UsbPortRole.Gateway)
            {
                Settings.GatewayUsbIndex = port.Index;
            }
            return portName;
        }

        private void StartGatewayAutoReconnect()
        {
            if (gatewayReconnectTimer != null)
            {
                return;
            }

            gatewayReconnectTimer = new Timer(_ => AutoReconnectGateway(), null, 0, GatewayReconnectIntervalMs);
        }

        private void StopGatewayAutoReconnect()
        {
            if (gatewayReconnectTimer != null)
            {
                gatewayReconnectTimer.Dispose();
                gatewayReconnectTimer = null;
            }
        }

        private void StartXPlaneUdpReceiver()
        {
            if (xplaneUdpThread != null || Settings == null || !Settings.XPlaneUdpEnabled)
            {
                return;
            }

            xplaneUdpCts = new CancellationTokenSource();
            try
            {
                xplaneUdpClient = new UdpClient(Settings.XPlaneUdpPort);
                xplaneUdpClient.Client.ReceiveTimeout = 500;
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error($"XPlane UDP receiver failed to start: {ex.Message}");
                StopXPlaneUdpReceiver();
                return;
            }

            xplaneUdpThread = new Thread(XPlaneUdpLoop)
            {
                IsBackground = true,
                Name = "XPlaneUdpReceiver"
            };
            xplaneUdpThread.Start();
        }

        private void StopXPlaneUdpReceiver()
        {
            if (xplaneUdpCts != null)
            {
                xplaneUdpCts.Cancel();
            }

            if (xplaneUdpClient != null)
            {
                try
                {
                    xplaneUdpClient.Close();
                }
                catch
                {
                }
            }

            xplaneUdpThread = null;
            xplaneUdpClient = null;
            xplaneUdpCts = null;
        }

        private void XPlaneUdpLoop()
        {
            // Snapshot refs so we survive StopXPlaneUdpReceiver nulling fields
            // mid-loop (race between Cancel + Close and the next while-check).
            var client = xplaneUdpClient;
            var cts = xplaneUdpCts;
            if (client == null || cts == null)
            {
                return;
            }

            var endpoint = new IPEndPoint(IPAddress.Any, 0);
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    byte[] data = client.Receive(ref endpoint);
                    if (data != null && data.Length >= XPlanePacketSizeBytes)
                    {
                        ParseXPlanePacket(data);
                    }
                }
                catch (SocketException ex)
                {
                    if (ex.SocketErrorCode != SocketError.TimedOut)
                    {
                        Thread.Sleep(50);
                    }
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception)
                {
                }
            }
        }

        private void ParseXPlanePacket(byte[] data)
        {
            int offset = 0;
            uint magic = ReadUInt32(data, ref offset);
            if (magic != XPlanePacketMagic)
            {
                return;
            }

            ushort version = ReadUInt16(data, ref offset);
            if (version != XPlanePacketVersion)
            {
                return;
            }

            ushort size = ReadUInt16(data, ref offset);
            if (size > data.Length || size < XPlanePacketSizeBytes)
            {
                return;
            }

            uint sequence = ReadUInt32(data, ref offset);

            var packet = new XPlaneUdpPacket
            {
                Sequence = sequence,
                IasKts = ReadSingle(data, ref offset),
                TasMps = ReadSingle(data, ref offset),
                AlphaDeg = ReadSingle(data, ref offset),
                BetaDeg = ReadSingle(data, ref offset),
                PRate = ReadSingle(data, ref offset),
                QRate = ReadSingle(data, ref offset),
                RRate = ReadSingle(data, ref offset),
                ElevDefDeg = ReadSingle(data, ref offset),
                AilDefDeg = ReadSingle(data, ref offset),
                RudDefDeg = ReadSingle(data, ref offset),
                ElevTrimNorm = ReadSingle(data, ref offset),
                AilTrimNorm = ReadSingle(data, ref offset),
                RudTrimNorm = ReadSingle(data, ref offset),
                GNrml = ReadSingle(data, ref offset),
                ReceivedUtc = DateTime.UtcNow
            };
            for (int idx = 0; idx < XPlaneMaxRotors; idx++)
            {
                packet.TorqueNm[idx] = ReadSingle(data, ref offset);
            }
            for (int idx = 0; idx < XPlaneMaxRotors; idx++)
            {
                packet.OmegaRad[idx] = ReadSingle(data, ref offset);
            }
            for (int idx = 0; idx < XPlaneMaxRotors; idx++)
            {
                packet.PropRatio[idx] = ReadSingle(data, ref offset);
            }
            packet.LAero = ReadSingle(data, ref offset);
            packet.MAero = ReadSingle(data, ref offset);
            packet.NAero = ReadSingle(data, ref offset);
            packet.OnGround = ReadByte(data, ref offset) != 0;
            offset += 3;
            // v4: rotor vibration signals
            for (int idx = 0; idx < XPlaneMaxRotors; idx++)
            {
                packet.CyclicElevBladAlph[idx] = ReadSingle(data, ref offset);
            }
            for (int idx = 0; idx < XPlaneMaxRotors; idx++)
            {
                packet.CyclicAilnBladAlph[idx] = ReadSingle(data, ref offset);
            }
            for (int idx = 0; idx < XPlaneMaxRotors; idx++)
            {
                packet.RotorBladeSlapRat[idx] = ReadSingle(data, ref offset);
            }
            for (int idx = 0; idx < XPlaneMaxRotors; idx++)
            {
                packet.VortexRingState[idx] = ReadSingle(data, ref offset);
            }
            for (int idx = 0; idx < XPlaneMaxRotors; idx++)
            {
                packet.PropwashMtrSec[idx] = ReadSingle(data, ref offset);
            }

            lock (xplaneLock)
            {
                if (xplaneLastSequence != 0 && sequence > xplaneLastSequence + 1)
                {
                    xplaneDropouts += (int)(sequence - xplaneLastSequence - 1);
                }
                xplaneLastSequence = sequence;
                latestXPlanePacket = packet;
                xplaneLastReceivedUtc = packet.ReceivedUtc;
                UpdateRotorRpmHistory(packet);
            }
        }

        // Plan 19: in-process SimConnect client lifecycle. Mutually exclusive
        // with the bridge EXE path — caller chooses based on Settings.
        private void StartMsfsClient()
        {
            if (_msfsClient != null) return;
            _msfsClient = new MsfsSimConnectClient(
                onSample: ApplyMsfsSimConnectSample,
                log: msg => SimHub.Logging.Current?.Info(msg));
            _msfsClient.Start();
            // Feed any already-loaded graph's custom vars (graph may load before
            // the client starts during Init).
            UpdateMsfsCustomVars();
            UpdateMsfsWritableVars();
            SimHub.Logging.Current?.Info("[MsfsSimConnect] in-process client started.");
        }

        // Plan 23: scan the active graph's MsfsVarDef nodes and hand the
        // SimConnect client the custom-var registration list. Called on
        // graph/vehicle change and editor Apply. Dedups aliases (first wins);
        // empties are dropped. No-op if the client isn't running. Always feeds
        // the list (SetCustomVars re-registers) so per-aircraft LVAR availability
        // is re-evaluated even when two aircraft share one graph (plan §9.2).
        private void UpdateMsfsCustomVars()
        {
            var client = _msfsClient;
            if (client == null) return;

            var list = new List<DiyFfb.Msfs.MsfsCustomVar>();
            var inMaps = new Dictionary<string, MsfsVarInMap>(StringComparer.Ordinal);
            var graph = activeVehicleGraph;
            // Plan 34: overlay the active profile's per-alias read-binding overrides
            // onto the graph node defaults (overlay only — the graph stays untouched).
            var readOverrides = GetCurrentAircraftProfile()?.MsfsReadVarOverrides;
            if (graph != null && graph.Nodes != null)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var node in graph.Nodes)
                {
                    if (node == null || node.Kind != GraphNodeKind.MsfsVarDef || node.Ports == null)
                        continue;
                    foreach (var port in node.Ports)
                    {
                        if (port == null || port.Kind != GraphPortKind.Output) continue;
                        string alias = port.SignalSuffix?.Trim() ?? "";
                        if (alias.Length == 0) continue;
                        MsfsVarBindingOverride ov = null;
                        readOverrides?.TryGetValue(alias, out ov);
                        var r = MsfsBindingResolver.Resolve(
                            port.SimVar, port.Unit, port.InMin, port.InMax, port.OutMin, port.OutMax, ov);
                        string name = r.SimVar?.Trim() ?? "";
                        if (name.Length == 0) continue;
                        if (!seen.Add(alias)) continue; // duplicate alias — first wins
                        list.Add(new DiyFfb.Msfs.MsfsCustomVar(alias, name, r.Unit?.Trim() ?? ""));
                        inMaps[alias] = new MsfsVarInMap
                        {
                            InMin = r.InMin,
                            InMax = r.InMax,
                            OutMin = r.OutMin,
                            OutMax = r.OutMax,
                        };
                    }
                }
            }
            _msfsVarInMaps = inMaps; // atomic publish
            client.SetCustomVars(list);
        }

        // Plan 24: scan the active graph's MsfsVarOut nodes → the write-target
        // registration list + the alias→range-map/prefix lookup. Sibling of
        // UpdateMsfsCustomVars; called from the same sites. The lookup is
        // published as a NEW immutable dict via a single volatile assignment
        // (built here on the scan thread, read by CheckMsfsVarOutChanges on the
        // eval thread) — never mutated in place. No-op if the client isn't running.
        private void UpdateMsfsWritableVars()
        {
            var client = _msfsClient;
            if (client == null) return;

            var list = new List<DiyFfb.Msfs.MsfsCustomVar>();
            var maps = new Dictionary<string, MsfsVarOutMap>(StringComparer.Ordinal);
            var graph = activeVehicleGraph;
            // Plan 34: overlay the active profile's per-alias write-binding overrides
            // (SimVar + Unit + range map) onto the graph node defaults.
            var writeOverrides = GetCurrentAircraftProfile()?.MsfsWriteVarOverrides;
            if (graph != null && graph.Nodes != null)
            {
                foreach (var node in graph.Nodes)
                {
                    if (node == null || node.Kind != GraphNodeKind.MsfsVarOut || node.Ports == null)
                        continue;
                    foreach (var port in node.Ports)
                    {
                        if (port == null || port.Kind != GraphPortKind.Input) continue;
                        string alias = port.Name?.Trim() ?? "";
                        if (alias.Length == 0) continue;
                        MsfsVarBindingOverride ov = null;
                        writeOverrides?.TryGetValue(alias, out ov);
                        var r = MsfsBindingResolver.Resolve(
                            port.SimVar, port.Unit, port.InMin, port.InMax, port.OutMin, port.OutMax, ov);
                        string name = r.SimVar?.Trim() ?? "";
                        if (name.Length == 0) continue;
                        if (maps.ContainsKey(alias)) continue; // duplicate alias — first wins
                        list.Add(new DiyFfb.Msfs.MsfsCustomVar(alias, name, r.Unit?.Trim() ?? ""));
                        maps[alias] = new MsfsVarOutMap
                        {
                            InMin = r.InMin,
                            InMax = r.InMax,
                            OutMin = r.OutMin,
                            OutMax = r.OutMax,
                            IsActuation = name.StartsWith("B:", StringComparison.Ordinal)
                                       || name.StartsWith("K:", StringComparison.Ordinal)
                        };
                    }
                }
            }
            _msfsVarOutMaps = maps; // atomic publish
            client.SetWritableVars(list);
        }

        // Plan 24: dispatch changed MsfsVarOut values to the sim. Mirror of
        // CheckConfigOutChanges — apply the per-port linear range map + clamp,
        // change-detect on the MAPPED value (1e-6), and WriteValue only on change.
        // On a write-set (re)registration / reconnect (WriteGeneration bumped) the
        // A:/L: cache is cleared to force a full re-push of held values; B: aliases
        // are exempt (re-firing a toggle/preset on reconnect is a spurious
        // actuation), so they keep their last-sent value across the re-push.
        private void CheckMsfsVarOutChanges()
        {
            var client = _msfsClient;
            if (client == null) return;

            var maps = _msfsVarOutMaps; // snapshot the atomic reference

            int gen = client.WriteGeneration;
            if (gen != _lastSeenWriteGeneration)
            {
                _lastSeenWriteGeneration = gen;
                // Force re-push of A:/L: state; keep B: last-sent values so they
                // don't re-fire. Remove only non-B: aliases from the cache.
                var toClear = new List<string>();
                foreach (var kvp in _lastMsfsVarOutValues)
                {
                    if (!(maps.TryGetValue(kvp.Key, out var m) && m.IsActuation))
                        toClear.Add(kvp.Key);
                }
                foreach (var k in toClear) _lastMsfsVarOutValues.Remove(k);
            }

            var outputs = lastGraphEvaluation?.MsfsVarOutputs;
            if (outputs == null || outputs.Count == 0) return;

            foreach (var kvp in outputs)
            {
                string alias = kvp.Key;
                double raw = kvp.Value;

                double mapped;
                if (maps.TryGetValue(alias, out var map))
                {
                    mapped = MsfsBindingResolver.MapLinearClamped(raw, map.InMin, map.InMax, map.OutMin, map.OutMax);
                }
                else
                {
                    // No map (port removed between scan and eval) — passthrough.
                    mapped = raw;
                }

                if (_lastMsfsVarOutValues.TryGetValue(alias, out double last) &&
                    Math.Abs(mapped - last) < 1e-6)
                {
                    continue;
                }
                _lastMsfsVarOutValues[alias] = mapped;
                client.WriteValue(alias, mapped);
            }
        }

        // Plan 34: re-run both MSFS var scans so per-profile binding overrides are
        // re-overlaid and re-registered. UpdateMsfsWritableVars → SetWritableVars
        // flags the client's write set dirty, so ConfigureWritables re-runs and bumps
        // WriteGeneration — that forces a full re-push of A:/L: held state (B:/K:
        // actuations stay exempt in CheckMsfsVarOutChanges), and handles a transport
        // prefix change on a write override (e.g. L: → B:) re-registering correctly.
        // No-op when the client isn't running (each scan checks internally).
        public void ApplyMsfsVarOverrides()
        {
            UpdateMsfsCustomVars();
            UpdateMsfsWritableVars();
        }

        /// <summary>
        /// Plan 34: enumerate the MSFS var bindings declared by the active graph,
        /// grouped Read (MsfsVarDef) / Write (MsfsVarOut), each with the graph-default
        /// SimVar/Unit/range for the UI to show as placeholder baseline. Dedups aliases
        /// (first wins), matching the scan behaviour in UpdateMsfsCustomVars/WritableVars.
        /// </summary>
        public MsfsBindingCatalog EnumerateMsfsBindings()
        {
            var cat = new MsfsBindingCatalog();
            var graph = activeVehicleGraph;
            if (graph?.Nodes == null) return cat;

            var seenRead = new HashSet<string>(StringComparer.Ordinal);
            var seenWrite = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in graph.Nodes)
            {
                if (node?.Ports == null) continue;
                if (node.Kind == GraphNodeKind.MsfsVarDef)
                {
                    foreach (var port in node.Ports)
                    {
                        if (port == null || port.Kind != GraphPortKind.Output) continue;
                        string alias = port.SignalSuffix?.Trim() ?? "";
                        if (alias.Length == 0 || !seenRead.Add(alias)) continue;
                        cat.Reads.Add(new MsfsBindingInfo
                        {
                            Alias = alias,
                            IsWrite = false,
                            DefaultSimVar = port.SimVar?.Trim() ?? "",
                            DefaultUnit = port.Unit?.Trim() ?? "",
                            DefaultInMin = port.InMin,
                            DefaultInMax = port.InMax,
                            DefaultOutMin = port.OutMin,
                            DefaultOutMax = port.OutMax,
                        });
                    }
                }
                else if (node.Kind == GraphNodeKind.MsfsVarOut)
                {
                    foreach (var port in node.Ports)
                    {
                        if (port == null || port.Kind != GraphPortKind.Input) continue;
                        string alias = port.Name?.Trim() ?? "";
                        if (alias.Length == 0 || !seenWrite.Add(alias)) continue;
                        cat.Writes.Add(new MsfsBindingInfo
                        {
                            Alias = alias,
                            IsWrite = true,
                            DefaultSimVar = port.SimVar?.Trim() ?? "",
                            DefaultUnit = port.Unit?.Trim() ?? "",
                            DefaultInMin = port.InMin,
                            DefaultInMax = port.InMax,
                            DefaultOutMin = port.OutMin,
                            DefaultOutMax = port.OutMax,
                        });
                    }
                }
            }
            return cat;
        }

        /// <summary>True if the active graph declares any MSFS read/write var binding.</summary>
        public bool HasMsfsBindings()
        {
            var cat = EnumerateMsfsBindings();
            return cat.Reads.Count > 0 || cat.Writes.Count > 0;
        }

        /// <summary>Current profile's read-binding override for an alias, or null.</summary>
        public MsfsVarBindingOverride GetMsfsReadOverride(string alias)
        {
            var d = GetCurrentAircraftProfile()?.MsfsReadVarOverrides;
            if (d != null && alias != null && d.TryGetValue(alias, out var ov)) return ov;
            return null;
        }

        /// <summary>Current profile's write-binding override for an alias, or null.</summary>
        public MsfsVarBindingOverride GetMsfsWriteOverride(string alias)
        {
            var d = GetCurrentAircraftProfile()?.MsfsWriteVarOverrides;
            if (d != null && alias != null && d.TryGetValue(alias, out var ov)) return ov;
            return null;
        }

        // The range map's sim side is per-aircraft (overridable); the graph side is
        // graph-intrinsic (inherited, never overridden). For READS the sim value is
        // the INPUT, so In* is the overridable pair; for WRITES the sim value is the
        // OUTPUT, so Out* is the overridable pair. The other pair is left null so the
        // resolver always inherits the graph default.
        public void SetMsfsReadBinding(string alias, string simVar, string unit,
            double? inMin, double? inMax)
        {
            EditMsfsOverride(isWrite: false, alias: alias, mutate: ov =>
            {
                ov.SimVar = NullIfBlank(simVar);
                ov.Unit = NullIfBlank(unit);
                ov.InMin = inMin;
                ov.InMax = inMax;
            });
        }

        public void SetMsfsWriteBinding(string alias, string simVar, string unit,
            double? outMin, double? outMax)
        {
            EditMsfsOverride(isWrite: true, alias: alias, mutate: ov =>
            {
                ov.SimVar = NullIfBlank(simVar);
                ov.Unit = NullIfBlank(unit);
                ov.OutMin = outMin;
                ov.OutMax = outMax;
            });
        }

        public void ResetMsfsReadBinding(string alias)
            => EditMsfsOverride(isWrite: false, alias: alias, mutate: ov =>
            {
                ov.SimVar = null; ov.Unit = null;
                ov.InMin = null; ov.InMax = null;
            });

        public void ResetMsfsWriteBinding(string alias)
            => EditMsfsOverride(isWrite: true, alias: alias, mutate: ov =>
            {
                ov.SimVar = null; ov.Unit = null;
                ov.OutMin = null; ov.OutMax = null;
            });

        private static string NullIfBlank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        // Read-modify-write one alias's override in the active profile, prune the
        // entry if it becomes empty, mark dirty, then re-apply so the change reaches
        // the sim immediately. No-op when there is no active profile.
        private void EditMsfsOverride(bool isWrite, string alias, Action<MsfsVarBindingOverride> mutate)
        {
            if (string.IsNullOrWhiteSpace(alias)) return;
            var profile = GetCurrentAircraftProfile();
            if (profile == null) return;
            alias = alias.Trim();

            Dictionary<string, MsfsVarBindingOverride> dict;
            if (isWrite)
                dict = profile.MsfsWriteVarOverrides ?? (profile.MsfsWriteVarOverrides = new Dictionary<string, MsfsVarBindingOverride>(StringComparer.Ordinal));
            else
                dict = profile.MsfsReadVarOverrides ?? (profile.MsfsReadVarOverrides = new Dictionary<string, MsfsVarBindingOverride>(StringComparer.Ordinal));

            MarkProfileDirty();

            if (!dict.TryGetValue(alias, out var ov) || ov == null)
                ov = new MsfsVarBindingOverride();
            mutate(ov);

            if (ov.IsEmpty) dict.Remove(alias);
            else dict[alias] = ov;

            ApplyMsfsVarOverrides();
        }

        private void StopMsfsClient()
        {
            var c = _msfsClient;
            _msfsClient = null;
            if (c == null) return;
            try { c.Stop(); } catch { }
            try { c.Dispose(); } catch { }
        }

        // Worker-thread callback. The double[] is owned by the client and
        // reused per sample, so we must finish reading from it before
        // returning. Publishes under msfsLock so downstream sees a single
        // consistent latestMsfsPacket. `customs` (alias -> value) is populated
        // from graph-declared MsfsVarDef vars (plan 23 phase 1); ignored here.
        private void ApplyMsfsSimConnectSample(double[] s, IReadOnlyDictionary<string, double> customs)
        {
            var packet = new MsfsUdpPacket
            {
                Sequence                       = unchecked((uint)Interlocked.Increment(ref _msfsClientSeq)),
                IasKts                         = (float)s[(int)MsfsSampleIndex.IasKts],
                TasKts                         = (float)s[(int)MsfsSampleIndex.TasKts],
                AlphaDeg                       = (float)s[(int)MsfsSampleIndex.AlphaDeg],
                BetaDeg                        = (float)s[(int)MsfsSampleIndex.BetaDeg],
                PRateRadS                      = (float)s[(int)MsfsSampleIndex.PRateRadS],
                QRateRadS                      = (float)s[(int)MsfsSampleIndex.QRateRadS],
                RRateRadS                      = (float)s[(int)MsfsSampleIndex.RRateRadS],
                GForce                         = (float)s[(int)MsfsSampleIndex.GForce],
                VviWorldFps                    = (float)s[(int)MsfsSampleIndex.VviWorldFps],
                VelocityBodyXFps               = (float)s[(int)MsfsSampleIndex.VelocityBodyXFps],
                VelocityBodyYFps               = (float)s[(int)MsfsSampleIndex.VelocityBodyYFps],
                VelocityBodyZFps               = (float)s[(int)MsfsSampleIndex.VelocityBodyZFps],
                GroundVelocityKts              = (float)s[(int)MsfsSampleIndex.GroundVelocityKts],
                PitchRad                       = (float)s[(int)MsfsSampleIndex.PitchRad],
                BankRad                        = (float)s[(int)MsfsSampleIndex.BankRad],
                TotalWeightLb                  = (float)s[(int)MsfsSampleIndex.TotalWeightLb],
                AmbientDensitySlugsFt3         = (float)s[(int)MsfsSampleIndex.AmbientDensitySlugsFt3],
                MainRotorRpm                   = (float)s[(int)MsfsSampleIndex.MainRotorRpm],
                TailRotorRpm                   = (float)s[(int)MsfsSampleIndex.TailRotorRpm],
                EngTorquePct                   = (float)s[(int)MsfsSampleIndex.EngTorquePct],
                CollectivePosPct               = (float)s[(int)MsfsSampleIndex.CollectivePosPct],
                TailRotorPedalPct              = (float)s[(int)MsfsSampleIndex.TailRotorPedalPct],
                TailRotorBladePitchPct         = (float)s[(int)MsfsSampleIndex.TailRotorBladePitchPct],
                RotorCollectiveBladePitchPct   = (float)s[(int)MsfsSampleIndex.RotorCollectiveBladePitchPct],
                RotorCyclicBladePitchPct       = (float)s[(int)MsfsSampleIndex.RotorCyclicBladePitchPct],
                RotorCyclicBladeMaxPitchPosRad = (float)s[(int)MsfsSampleIndex.RotorCyclicBladeMaxPitchPosRad],
                DiskPitchAngleRad              = (float)s[(int)MsfsSampleIndex.DiskPitchAngleRad],
                DiskBankAngleRad               = (float)s[(int)MsfsSampleIndex.DiskBankAngleRad],
                DiskConingPct                  = (float)s[(int)MsfsSampleIndex.DiskConingPct],
                RotorLateralTrimPct            = (float)s[(int)MsfsSampleIndex.RotorLateralTrimPct],
                RotorLongitudinalTrimPct       = (float)s[(int)MsfsSampleIndex.RotorLongitudinalTrimPct],
                RotorRotationAngleRad          = (float)s[(int)MsfsSampleIndex.RotorRotationAngleRad],
                ElevTrimPct                    = (float)s[(int)MsfsSampleIndex.ElevTrimPct],
                AilTrimPct                     = (float)s[(int)MsfsSampleIndex.AilTrimPct],
                RudTrimPct                     = (float)s[(int)MsfsSampleIndex.RudTrimPct],
                OnGround                       = s[(int)MsfsSampleIndex.SimOnGround] != 0.0,
                Custom                         = customs,
                ReceivedUtc                    = DateTime.UtcNow,
            };
            lock (msfsLock)
            {
                latestMsfsPacket = packet;
            }
        }

        private int _msfsClientSeq;

        internal MsfsUdpPacket GetLatestMsfsPacket()
        {
            lock (msfsLock)
            {
                return latestMsfsPacket;
            }
        }

        internal static bool IsMsfsTelemetryFresh(DateTime utc)
        {
            return (DateTime.UtcNow - utc).TotalMilliseconds <= MsfsTelemetryFreshnessMs;
        }

        // Plan 17: graph-param lookup for signal-source derivations.
        // BuildMsfsInputs uses this to read per-aircraft tuning constants
        // (rotor tip speed, k_speed_pitch, etc.) from the active graph
        // so per-aircraft persistence falls out of GraphParamValues for free.
        // Returns the default if the param isn't defined on the active graph.
        internal double GetGraphParamValue(string name, double defaultValue)
        {
            if (string.IsNullOrEmpty(name)) return defaultValue;
            if (graphParams != null && graphParams.TryGetValue(name, out double v))
            {
                return v;
            }
            return defaultValue;
        }

        // Transient per-session "muted" param set. When a param is muted AND
        // its GraphParamUi has MuteValue set, BuildGraphParams substitutes
        // MuteValue for the resolved value. Used for the "solo a cue" tuning
        // workflow — not saved to profile.
        private readonly HashSet<string> _mutedParams = new HashSet<string>(StringComparer.Ordinal);

        public event EventHandler ParamMuteChanged;
        // Fired on any bulk mute change (profile/vehicle/user switch, or the
        // mute-checkbox context menu's mute/unmute all/others). Subscribers
        // should rebuild any UI that displays mute state — checkbox values
        // may have changed without an originating user click.
        public event EventHandler ParamMutesCleared;

        public bool IsParamMuted(string name)
        {
            return !string.IsNullOrEmpty(name) && _mutedParams.Contains(name);
        }

        public void SetParamMuted(string name, bool muted)
        {
            if (string.IsNullOrEmpty(name)) return;
            bool changed = muted ? _mutedParams.Add(name) : _mutedParams.Remove(name);
            if (changed)
            {
                ParamMuteChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public void ClearAllParamMutes()
        {
            if (_mutedParams.Count == 0) return;
            _mutedParams.Clear();
            ParamMutesCleared?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Bulk mute/unmute for the mute-checkbox context menu. Operates over
        /// every active-graph param that exposes a MuteValue. When
        /// <paramref name="exceptName"/> is non-null that param is left untouched
        /// ("mute/unmute others"). Fires <see cref="ParamMutesCleared"/> so all
        /// mute checkboxes rebuild to reflect the new state.
        /// </summary>
        public void SetAllParamMutes(bool muted, string exceptName = null)
        {
            var allParams = GetActiveGraphParams();
            if (allParams == null) return;

            bool changed = false;
            foreach (var kv in allParams)
            {
                if (kv.Value?.Ui?.MuteValue.HasValue != true) continue;
                if (exceptName != null && string.Equals(kv.Key, exceptName, StringComparison.Ordinal)) continue;
                changed |= muted ? _mutedParams.Add(kv.Key) : _mutedParams.Remove(kv.Key);
            }

            if (changed)
            {
                ParamMutesCleared?.Invoke(this, EventArgs.Empty);
            }
        }

        private static float Clamp(float value, float min, float max)
        {
            return Math.Min(max, Math.Max(min, value));
        }

        private static float Clamp01(float value)
        {
            return Clamp(value, 0.0f, 1.0f);
        }

        private static float ClampLoad(float value, float limit)
        {
            if (limit <= 0.0f)
            {
                return value;
            }

            return Clamp(value, -limit, limit);
        }

        private static float Lerp(float start, float end, float t)
        {
            return start + (end - start) * t;
        }

        internal static float ToRpm(float omegaRad)
        {
            return omegaRad * 60.0f / (float)(2.0 * Math.PI);
        }

        private static bool IsTelemetryFresh(DateTime utc)
        {
            return (DateTime.UtcNow - utc).TotalMilliseconds <= XPlaneTelemetryFreshnessMs;
        }

        private void UpdateRotorRpmHistory(XPlaneUdpPacket packet)
        {
            if (packet.OnGround || !IsTelemetryFresh(packet.ReceivedUtc))
            {
                return;
            }

            DateTime cutoff = packet.ReceivedUtc - TimeSpan.FromSeconds(XPlaneRotorWindowSeconds);
            for (int idx = 0; idx < XPlaneMaxRotors; idx++)
            {
                Queue<RotorRpmSample> history = rotorRpmHistory[idx];
                if (history == null)
                {
                    history = new Queue<RotorRpmSample>();
                    rotorRpmHistory[idx] = history;
                }

                history.Enqueue(new RotorRpmSample
                {
                    Utc = packet.ReceivedUtc,
                    Rpm = ToRpm(packet.OmegaRad[idx])
                });

                while (history.Count > 0 && history.Peek().Utc < cutoff)
                {
                    history.Dequeue();
                }
            }
        }

        private static ushort ReadUInt16(byte[] data, ref int offset)
        {
            ushort value = BitConverter.ToUInt16(data, offset);
            offset += 2;
            return value;
        }

        private static uint ReadUInt32(byte[] data, ref int offset)
        {
            uint value = BitConverter.ToUInt32(data, offset);
            offset += 4;
            return value;
        }

        private static float ReadSingle(byte[] data, ref int offset)
        {
            float value = BitConverter.ToSingle(data, offset);
            offset += 4;
            return value;
        }

        private static byte ReadByte(byte[] data, ref int offset)
        {
            byte value = data[offset];
            offset += 1;
            return value;
        }

        private void ProcessGraphFfb()
        {
            if (ESPsync_serialPort == null || !ESPsync_serialPort.IsOpen)
            {
                return;
            }

            // Plan 17: gate on ANY sim having fresh telemetry, not just X-Plane.
            // Graphs read whichever prefix (XPlane.* or MSFS.*) they were authored
            // against; the unused prefix sits at zero — it's the graph's job to
            // pick the right inputs, not this gate's.
            bool xplaneFresh;
            XPlaneUdpPacket xpacket;
            lock (xplaneLock)
            {
                xpacket = latestXPlanePacket;
                xplaneFresh = xpacket != null && IsTelemetryFresh(xpacket.ReceivedUtc);
            }
            bool msfsFresh;
            MsfsUdpPacket mpacket;
            lock (msfsLock)
            {
                mpacket = latestMsfsPacket;
                msfsFresh = mpacket != null && IsMsfsTelemetryFresh(mpacket.ReceivedUtc);
            }
            if (!xplaneFresh && !msfsFresh)
            {
                return;
            }

            if ((DateTime.UtcNow - xplaneLastSendUtc).TotalMilliseconds < 20)
            {
                return;
            }
            xplaneLastSendUtc = DateTime.UtcNow;

            if (lastGraphEvaluation?.Outputs == null)
            {
                return;
            }

            SendGraphFfbForFunction(FunctionID.FlightStickPitch);
            SendGraphFfbForFunction(FunctionID.FlightStickRoll);
            SendGraphFfbForFunction(FunctionID.FlightPedals);
            SendGraphFfbForFunction(FunctionID.FlightStickCollective);

            SendDdsFundamentals();
        }

        private void SendDdsFundamentals()
        {
            // Shared scope: read once, send to gateway. Gateway snoops and
            // broadcasts via 0x0F0 sync frame to all axes.
            float dds1Hz = TryGetGraphOutput("Shared.Vib1Fund", out float v1) ? v1 : 0.0f;
            float dds2Hz = TryGetGraphOutput("Shared.Vib2Fund", out float v2) ? v2 : 0.0f;
            Message msg = new Message
            {
                DdsFundamentals = new DdsFundamentals
                {
                    Dds1FundamentalHz = dds1Hz,
                    Dds2FundamentalHz = dds2Hz
                }
            };
            ESPsync_serialPort.WriteMessage(msg);
        }

        private int ResolveXPlaneRotorIndex(XPlaneUdpPacket packet)
        {
            if (Settings == null)
            {
                return 0;
            }
            if (Settings.XPlaneRotorIndex >= 0 && Settings.XPlaneRotorIndex < XPlaneMaxRotors)
            {
                return Settings.XPlaneRotorIndex;
            }

            if (packet.OnGround || !IsTelemetryFresh(packet.ReceivedUtc))
            {
                return hasAutoRotorIndex ? lastAutoRotorIndex : 0;
            }

            DateTime cutoff = packet.ReceivedUtc - TimeSpan.FromSeconds(XPlaneRotorWindowSeconds);
            int bestIndex = -1;
            float bestRpm = float.MaxValue;
            for (int idx = 0; idx < XPlaneMaxRotors; idx++)
            {
                Queue<RotorRpmSample> history = rotorRpmHistory[idx];
                if (history == null || history.Count == 0)
                {
                    continue;
                }

                float sum = 0.0f;
                int count = 0;
                foreach (var sample in history)
                {
                    if (sample.Utc < cutoff)
                    {
                        continue;
                    }
                    sum += sample.Rpm;
                    count++;
                }

                if (count == 0)
                {
                    continue;
                }

                float avgRpm = sum / count;
                if (avgRpm > 0.0f && avgRpm < bestRpm)
                {
                    bestRpm = avgRpm;
                    bestIndex = idx;
                }
            }

            if (bestIndex >= 0)
            {
                lastAutoRotorIndex = bestIndex;
                hasAutoRotorIndex = true;
                return bestIndex;
            }

            return hasAutoRotorIndex ? lastAutoRotorIndex : 0;
        }

        internal XPlaneUdpPacket GetLatestXPlanePacket()
        {
            lock (xplaneLock)
            {
                return latestXPlanePacket;
            }
        }

        internal int ResolveXPlaneRotorIndexForGraph(XPlaneUdpPacket packet)
        {
            return ResolveXPlaneRotorIndex(packet);
        }

        // Vib amplitudes go on the wire as uint8 at 0.01 mm/LSB (range 0..2.55 mm).
        // Plugin pre-scales here; firmware reads raw and multiplies by 0.01.
        // SyncVib output is now a position delta on the servo command path
        // (plan 12) — feel is decoupled from damping, but profiles tuned in
        // the previous "N" units must be retuned for the new mm scale.
        private static uint PackVibAmp(float amp)
        {
            int scaled = (int)Math.Round(amp * 100f);
            if (scaled < 0) return 0;
            if (scaled > 255) return 255;
            return (uint)scaled;
        }

        private void SendFlightFfb(FunctionID functionId, float kSpring, float kDamper, float kFriction, float trimOffset, float buffetAmp, float loadForce,
            float[] vibSlots, float[] vib2Slots)
        {
            Message msg = new Message
            {
                FfbAction = new FFBAction
                {
                    FunctionId = functionId,
                    FlightFfb = new FlightFfbAction
                    {
                        KSpring = kSpring,
                        KDamper = kDamper,
                        KFriction = kFriction,
                        TrimOffset = trimOffset,
                        BuffetAmp = buffetAmp,
                        LoadForce = loadForce,
                        VibAmpSlot1 = PackVibAmp(vibSlots[0]),
                        VibAmpSlot2 = PackVibAmp(vibSlots[1]),
                        VibAmpSlot3 = PackVibAmp(vibSlots[2]),
                        VibAmpSlot4 = PackVibAmp(vibSlots[3]),
                        VibAmpSlot5 = PackVibAmp(vibSlots[4]),
                        Vib2AmpSlot1 = PackVibAmp(vib2Slots[0]),
                        Vib2AmpSlot2 = PackVibAmp(vib2Slots[1])
                    }
                }
            };
            ESPsync_serialPort.WriteMessage(msg);
        }

        private void SendGraphFfbForFunction(FunctionID functionId)
        {
            if (IsFunctionOutputDisabled(functionId))
            {
                return;
            }

            if (!TryGetGraphFlightOutputs(functionId, out float spring, out float damper, out float friction, out float trim, out float buffet, out float load,
                    out float[] vibSlots, out float[] vib2Slots))
            {
                return;
            }

            SendFlightFfb(functionId, spring, damper, friction, trim, buffet, load, vibSlots, vib2Slots);
        }

        /// <summary>
        /// Checks ConfigOut values from the graph evaluation and triggers config uploads
        /// when any value changes. ConfigOut keys come in two shapes:
        ///   - Scoped (inside a FunctionScope Include): "FunctionScope:FieldPath"
        ///     applies the value to the named function only.
        ///   - Top-level (no FunctionScope): bare "FieldPath" — fans out to every
        ///     function whose group matches the field (e.g. a top-level
        ///     "FlightStick.Vib1HarmRatio1" writes Pitch + Roll + Collective).
        /// </summary>
        private void CheckConfigOutChanges()
        {
            var configOutputs = lastGraphEvaluation?.ConfigOutputs;
            if (configOutputs == null || configOutputs.Count == 0)
            {
                return;
            }

            // Collect all functions touched in this pass; schedule a single merge per
            // function once every value has been written to the tier. Otherwise the
            // first write per function fires the throttled leading-edge merge against
            // a partially-populated tier, sending zeros for slots not yet written.
            HashSet<FunctionID> functionsTouched = null;

            foreach (var kvp in configOutputs)
            {
                string key = kvp.Key;
                double newValue = kvp.Value;

                // Check if value changed (with tolerance for floating point)
                if (_lastConfigOutValues.TryGetValue(key, out double oldValue) &&
                    Math.Abs(newValue - oldValue) < 1e-6)
                {
                    continue;
                }

                bool firstSeen = !_lastConfigOutValues.ContainsKey(key);
                _lastConfigOutValues[key] = newValue;

                int sepIndex = key.IndexOf(':');
                string scopeName;
                string fieldPath;
                if (sepIndex > 0 && sepIndex < key.Length - 1)
                {
                    // Scoped: "FunctionScope:FieldPath"
                    scopeName = key.Substring(0, sepIndex);
                    fieldPath = key.Substring(sepIndex + 1);
                }
                else
                {
                    // Top-level: bare field path. Fan out by ConfigType below.
                    scopeName = null;
                    fieldPath = key;
                }

                var field = TieredConfig.OverrideFieldRegistry.GetField(fieldPath);
                if (field == null)
                {
                    if (firstSeen)
                    {
                        SimHub.Logging.Current.Warn(
                            $"[ConfigOut] '{key}' — unknown field path '{fieldPath}'; ignored");
                    }
                    continue;
                }

                // Determine target functions: explicit scope or fan out by group.
                var targets = ResolveConfigOutTargets(scopeName, field);
                if (targets.Count == 0)
                {
                    if (firstSeen)
                    {
                        SimHub.Logging.Current.Warn(
                            $"[ConfigOut] '{key}' — no matching function for field group '{field.Group}'; ignored");
                    }
                    continue;
                }

                if (firstSeen)
                {
                    string canonical = field.FieldPath;
                    string targetList = string.Join(",", targets.Select(f => f.ToString()));
                    SimHub.Logging.Current.Info(
                        $"[ConfigOut] '{key}' = {newValue:F3} → field '{canonical}', targets [{targetList}]");
                }

                // ConfigOut writes go to the in-memory ConfigOut tier, never to the
                // persisted profile/user overrides. The orchestrator clears this tier
                // on graph reload, so phantom defaults can't leak across sessions.
                if (ConfigOrchestrator != null)
                {
                    foreach (var fn in targets)
                    {
                        ConfigOrchestrator.StoreConfigOutField(
                            (int)fn,
                            overrides => OverrideFieldRegistry.SetValue(overrides, field.FieldPath, (float)newValue));
                        (functionsTouched ?? (functionsTouched = new HashSet<FunctionID>())).Add(fn);
                    }
                }
            }

            // Now that every ConfigOut value has been written, schedule one throttled
            // merge per affected function so the leading edge sees the full tier.
            if (functionsTouched != null && ConfigOrchestrator != null)
            {
                foreach (var fn in functionsTouched)
                {
                    ConfigOrchestrator.ScheduleConfigOutMerge((int)fn);
                }
            }
        }

        // Map a (scopeName, field) pair to the function IDs that should receive
        // the override. scopeName == null means top-level — fan out to all
        // functions whose ConfigType matches the field's group.
        private static IReadOnlyList<FunctionID> ResolveConfigOutTargets(
            string scopeName,
            TieredConfig.OverrideFieldDefinition field)
        {
            if (!string.IsNullOrEmpty(scopeName))
            {
                FunctionID? functionId = ResolveFunctionIdFromScope(scopeName);
                if (functionId == null) return Array.Empty<FunctionID>();

                string expectedConfigType = GraphSignalCatalogData.GetConfigTypeForScope(scopeName);
                if (!string.IsNullOrEmpty(expectedConfigType) && !IsSharedFieldGroup(field.Group))
                {
                    if (!string.Equals(field.Group.ToString(), expectedConfigType, StringComparison.OrdinalIgnoreCase))
                    {
                        return Array.Empty<FunctionID>();
                    }
                }
                return new[] { functionId.Value };
            }

            // Top-level fan-out: every function in the catalog whose ConfigType
            // matches the field's group, OR all flight functions for shared groups.
            var result = new List<FunctionID>();
            foreach (var scope in GraphSignalCatalogData.OutputGroups)
            {
                var fn = ResolveFunctionIdFromScope(scope);
                if (fn == null) continue;
                if (IsSharedFieldGroup(field.Group))
                {
                    result.Add(fn.Value);
                    continue;
                }
                string scopeType = GraphSignalCatalogData.GetConfigTypeForScope(scope);
                if (!string.IsNullOrEmpty(scopeType) &&
                    string.Equals(field.Group.ToString(), scopeType, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(fn.Value);
                }
            }
            return result;
        }

        private static bool IsSharedFieldGroup(TieredConfig.OverrideFieldGroup group)
        {
            switch (group)
            {
                case TieredConfig.OverrideFieldGroup.OutputScaling:
                case TieredConfig.OverrideFieldGroup.Physics:
                case TieredConfig.OverrideFieldGroup.StaticBalanceTuning:
                case TieredConfig.OverrideFieldGroup.ForceFeedback:
                    return true;
                default:
                    return false;
            }
        }

        private static FunctionID? ResolveFunctionIdFromScope(string scopeName)
        {
            switch (scopeName)
            {
                case "FlightStickPitch": return FunctionID.FlightStickPitch;
                case "FlightStickRoll": return FunctionID.FlightStickRoll;
                case "FlightPedals": return FunctionID.FlightPedals;
                case "FlightStickCollective": return FunctionID.FlightStickCollective;
                default: return null;
            }
        }

        private bool TryGetGraphFlightOutputs(FunctionID functionId, out float spring, out float damper, out float friction,
            out float trimOffset, out float buffetAmp, out float loadForce, out float[] vibSlots, out float[] vib2Slots)
        {
            spring = 0.0f;
            damper = 0.0f;
            friction = 0.0f;
            trimOffset = 0.0f;
            buffetAmp = 0.0f;
            loadForce = 0.0f;
            vibSlots = new float[5];
            vib2Slots = new float[2];

            string prefix = GetGraphFunctionPrefix(functionId);
            if (string.IsNullOrWhiteSpace(prefix))
            {
                return false;
            }

            bool hasOutput = false;
            if (TryGetGraphOutput($"{prefix}.SpringGain", out float value))
            {
                spring = value;
                hasOutput = true;
            }
            if (TryGetGraphOutput($"{prefix}.DamperGain", out value))
            {
                damper = value;
                hasOutput = true;
            }
            if (TryGetGraphOutput($"{prefix}.Friction", out value))
            {
                friction = value;
                hasOutput = true;
            }
            if (TryGetGraphOutput($"{prefix}.TrimOffset", out value))
            {
                trimOffset = value;
                hasOutput = true;
            }
            if (TryGetGraphOutput($"{prefix}.LoadForce", out value))
            {
                loadForce = value;
                hasOutput = true;
            }
            if (TryGetGraphOutput($"{prefix}.BuffetAmplitude", out value))
            {
                buffetAmp = value;
                hasOutput = true;
            }
            for (int i = 0; i < 5; i++)
            {
                if (TryGetGraphOutput($"{prefix}.Vib1Ampl{i + 1}", out value))
                {
                    vibSlots[i] = value;
                    hasOutput = true;
                }
            }
            for (int i = 0; i < 2; i++)
            {
                if (TryGetGraphOutput($"{prefix}.Vib2Ampl{i + 1}", out value))
                {
                    vib2Slots[i] = value;
                    hasOutput = true;
                }
            }

            return hasOutput;
        }

        public void SetFunctionOutputDisabled(FunctionID functionId, bool disabled)
        {
            lock (outputDisableLock)
            {
                if (disabled)
                {
                    disabledOutputFunctions.Add(functionId);
                }
                else
                {
                    disabledOutputFunctions.Remove(functionId);
                }
            }
        }

        public bool IsFunctionOutputDisabled(FunctionID functionId)
        {
            lock (outputDisableLock)
            {
                return disabledOutputFunctions.Contains(functionId);
            }
        }

        private static string GetGraphFunctionPrefix(FunctionID functionId)
        {
            switch (functionId)
            {
                case FunctionID.FlightStickPitch:
                    return "FlightStickPitch";
                case FunctionID.FlightStickRoll:
                    return "FlightStickRoll";
                case FunctionID.FlightPedals:
                    return "FlightPedals";
                case FunctionID.FlightStickCollective:
                    return "FlightStickCollective";
                default:
                    return null;
            }
        }

        private bool TryGetGraphOutput(string key, out float value)
        {
            value = 0.0f;
            if (lastGraphEvaluation?.Outputs == null)
            {
                return false;
            }

            if (lastGraphEvaluation.Outputs.TryGetValue(key, out var raw))
            {
                value = (float)raw;
                return true;
            }

            return false;
        }

        public bool TryGetGraphTrimOffset(FunctionID functionId, out float trimMm)
        {
            trimMm = 0.0f;
            string prefix = GetGraphFunctionPrefix(functionId);
            if (string.IsNullOrWhiteSpace(prefix))
            {
                return false;
            }

            return TryGetGraphOutput($"{prefix}.TrimOffset", out trimMm);
        }

        public void ApplyXPlaneUdpSettings(bool enabled, int port)
        {
            if (Settings == null)
            {
                return;
            }

            Settings.XPlaneUdpEnabled = enabled;
            Settings.XPlaneUdpPort = port;
            StopXPlaneUdpReceiver();
            StartXPlaneUdpReceiver();
        }

        private DiyFfbPluginSettings.FunctionSettings GetFunctionSettings(FunctionID functionId)
        {
            if (Settings?.function_settings == null)
            {
                return null;
            }

            int index = (int)functionId - 1;
            if (index < 0 || index >= Settings.function_settings.Length)
            {
                return null;
            }

            return Settings.function_settings[index];
        }

        private static bool NearlyEqual(float a, float b)
        {
            return Math.Abs(a - b) < 0.000001f;
        }

        private void HandleGameChange(string gameId)
        {
            if (string.IsNullOrWhiteSpace(gameId))
            {
                return;
            }

            if (string.Equals(gameId, activeGameId, StringComparison.Ordinal))
            {
                return;
            }

            activeGameId = gameId;
            if (!string.IsNullOrWhiteSpace(activeCarId))
            {
                ResolveActiveGraph(activeGameId, activeCarId);
                BuildGraphParams();
                RestoreGraphState(BuildProfileKey(activeGameId, activeCarId));
            }
        }

        private static string GetGameIdSafe(GameData data)
        {
            if (data == null)
            {
                return "";
            }

            if (gameIdGetter == null)
            {
                var type = data.GetType();
                var property = type.GetProperty("GameName") ?? type.GetProperty("GameId") ?? type.GetProperty("Game");
                if (property != null && property.PropertyType == typeof(string))
                {
                    gameIdGetter = d => (string)property.GetValue(d, null);
                }
                else
                {
                    gameIdGetter = d => "";
                }
            }

            return (gameIdGetter(data) ?? "").Trim();
        }

        private static string BuildVehicleGraphKey(string gameId, string carId)
        {
            if (string.IsNullOrWhiteSpace(carId))
            {
                return "";
            }

            string trimmedGame = (gameId ?? "").Trim();
            return string.IsNullOrWhiteSpace(trimmedGame) ? carId : $"{trimmedGame}::{carId}";
        }

        /// <summary>
        /// Builds the key used for AircraftFfbProfiles dictionary.
        /// Uses same format as vehicle graph keys (gameId::carId) for consistency.
        /// </summary>
        private static string BuildProfileKey(string gameId, string carId)
        {
            return BuildVehicleGraphKey(gameId, carId);
        }

        /// <summary>
        /// Migrates an old-style profile key (carId only) to new format (gameId::carId).
        /// Call this when accessing a profile to ensure backward compatibility.
        /// </summary>
        private void MigrateProfileKeyIfNeeded(string gameId, string carId)
        {
            if (Settings?.AircraftFfbProfiles == null || string.IsNullOrWhiteSpace(carId))
            {
                return;
            }

            string newKey = BuildProfileKey(gameId, carId);
            if (string.IsNullOrWhiteSpace(newKey) || !newKey.Contains("::"))
            {
                // No game context, can't migrate
                return;
            }

            // If new key already exists, no migration needed
            if (Settings.AircraftFfbProfiles.ContainsKey(newKey))
            {
                return;
            }

            // Check if old-style key (carId only) exists
            if (Settings.AircraftFfbProfiles.TryGetValue(carId, out var oldProfile))
            {
                // Migrate: copy to new key and remove old key
                Settings.AircraftFfbProfiles[newKey] = oldProfile;
                Settings.AircraftFfbProfiles.Remove(carId);
                SimHub.Logging.Current.Info($"[DIY-FFB] Migrated profile key '{carId}' -> '{newKey}'");
            }
        }

        /// <summary>
        /// Migrates legacy VehicleGraphPaths entries into AircraftFfbProfiles.GraphPath.
        /// Called once at startup.
        /// </summary>
        private void MigrateVehicleGraphPaths()
        {
            if (Settings?.VehicleGraphPaths == null || Settings.VehicleGraphPaths.Count == 0)
            {
                return;
            }

            if (Settings.AircraftFfbProfiles == null)
            {
                Settings.AircraftFfbProfiles = new Dictionary<string, DiyFfbPluginSettings.AircraftFfbProfile>();
            }

            int migratedCount = 0;
            foreach (var kvp in Settings.VehicleGraphPaths)
            {
                string profileKey = kvp.Key;
                string graphPath = kvp.Value;

                if (string.IsNullOrWhiteSpace(profileKey) || string.IsNullOrWhiteSpace(graphPath))
                {
                    continue;
                }

                // Create or update profile with graph path
                if (!Settings.AircraftFfbProfiles.TryGetValue(profileKey, out var profile))
                {
                    profile = new DiyFfbPluginSettings.AircraftFfbProfile();
                    Settings.AircraftFfbProfiles[profileKey] = profile;
                }

                // Only set if not already set (don't overwrite existing)
                if (string.IsNullOrWhiteSpace(profile.GraphPath))
                {
                    profile.GraphPath = graphPath;
                    migratedCount++;
                }
            }

            if (migratedCount > 0)
            {
                SimHub.Logging.Current.Info($"[DIY-FFB] Migrated {migratedCount} vehicle graph paths to profiles");
                // Clear old data after migration
                Settings.VehicleGraphPaths.Clear();
            }
        }

        private string ResolveGraphPath(string gameId, string carId)
        {
            if (Settings == null)
            {
                return "";
            }

            // Check vehicle profile
            string profileKey = BuildProfileKey(gameId, carId);
            if (!string.IsNullOrWhiteSpace(profileKey) &&
                Settings.AircraftFfbProfiles != null &&
                Settings.AircraftFfbProfiles.TryGetValue(profileKey, out var profile) &&
                !string.IsNullOrWhiteSpace(profile?.GraphPath))
            {
                return profile.GraphPath;
            }

            return "";
        }

        public static string ResolveGraphFilePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return "";
            }

            return Path.IsPathRooted(path)
                ? path
                : Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path));
        }

        /// <summary>
        /// Gets the currently loaded vehicle graph definition.
        /// </summary>
        public GraphEditor.GraphDefinition GetActiveVehicleGraph()
        {
            return activeVehicleGraph;
        }

        private string PromptForGraphTemplate(string gameId, string carId)
        {
            if (ui == null)
            {
                return null;
            }

            // Show ProfileBrowserDialog in NewVehicle mode on UI thread
            var result = ui.Dispatcher.Invoke(new Func<(string graphPath, DiyFfbPluginSettings.AircraftFfbProfile profile, bool useTuning)?>(() =>
            {
                var dialog = new ProfileBrowser.ProfileBrowserDialog(this, ProfileBrowser.ProfileBrowserMode.NewVehicle, gameId, carId);

                var parentWindow = System.Windows.Window.GetWindow(ui);
                if (parentWindow != null)
                {
                    dialog.Owner = parentWindow;
                }

                if (dialog.ShowDialog() == true && dialog.SelectedEntry != null)
                {
                    var entry = dialog.SelectedEntry;
                    string selectedPath = entry.GraphPath;

                    // Resolve template path if needed
                    if (entry.Source == ProfileBrowser.ProfileEntrySource.Template && entry.TemplateEntry != null)
                    {
                        selectedPath = GraphEditor.GraphTemplateRegistry.ResolveTemplatePath(
                            entry.TemplateEntry.TemplatePath, AppDomain.CurrentDomain.BaseDirectory);
                    }

                    return (selectedPath, entry.Profile, dialog.UseTuning);
                }

                return null;
            }));

            if (result == null)
            {
                return null;
            }

            var (graphPath, profile, useTuning) = result.Value;

            if (string.IsNullOrWhiteSpace(graphPath))
            {
                return null;
            }

            // Store graph path in profile (no copying - reference directly)
            SetVehicleGraphPath(gameId, carId, graphPath);

            // Copy tuning parameters if requested
            if (useTuning && profile?.GraphParamValues != null)
            {
                string profileKey = BuildProfileKey(gameId, carId);
                if (Settings.AircraftFfbProfiles.TryGetValue(profileKey, out var currentProfile))
                {
                    foreach (var kvp in profile.GraphParamValues)
                    {
                        currentProfile.GraphParamValues[kvp.Key] = kvp.Value;
                    }

                    // Rebuild graph params to reflect copied tuning values
                    BuildGraphParams();
                }
            }

            return graphPath;
        }

        private void ResolveActiveGraph(string gameId, string carId)
        {
            activeGraphKey = BuildVehicleGraphKey(gameId, carId);
            activeGraphPath = ResolveGraphPath(gameId, carId);
            activeVehicleGraph = null;
            activeGraphValidation = null;
            activeGraphRuntime = null;
            activeGraphEvaluator = null;
            activeGraphResolver = null;
            activeIncludeContextCache = null;
            lastGraphEvaluation = null;
            _lastConfigOutValues.Clear();
            _lastMsfsVarOutValues.Clear(); // plan 24: don't leak writes across graph switches
            ConfigOrchestrator?.ClearConfigOutOverrides();

            bool autoAssigned = false;
            if (string.IsNullOrWhiteSpace(activeGraphPath))
            {
                // Check if exactly one template matches this game — auto-assign without dialog
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var templates = GraphEditor.GraphTemplateRegistry.GetTemplates(gameId, baseDir).ToList();
                if (templates.Count == 1)
                {
                    string templatePath = GraphEditor.GraphTemplateRegistry.ResolveTemplatePath(
                        templates[0].TemplatePath, baseDir);
                    if (!string.IsNullOrWhiteSpace(templatePath))
                    {
                        string key = BuildProfileKey(gameId, carId);
                        if (!string.IsNullOrWhiteSpace(key))
                        {
                            if (Settings.AircraftFfbProfiles == null)
                                Settings.AircraftFfbProfiles = new Dictionary<string, DiyFfbPluginSettings.AircraftFfbProfile>();
                            if (!Settings.AircraftFfbProfiles.TryGetValue(key, out var profile))
                            {
                                profile = new DiyFfbPluginSettings.AircraftFfbProfile();
                                Settings.AircraftFfbProfiles[key] = profile;
                            }
                            profile.GraphPath = templatePath;
                        }
                        activeGraphPath = templatePath;
                        autoAssigned = true;
                        SimHub.Logging.Current.Info($"[Graph] Auto-assigned template '{templates[0].Name}' for {gameId}/{carId}");
                    }
                }

                if (string.IsNullOrWhiteSpace(activeGraphPath))
                {
                    // Multiple templates or auto-assign failed — prompt user
                    string templatePath = PromptForGraphTemplate(gameId, carId);
                    if (!string.IsNullOrWhiteSpace(templatePath))
                        activeGraphPath = templatePath;
                    else
                        return;
                }
            }

            string resolvedPath = ResolveGraphFilePath(activeGraphPath);
            if (!File.Exists(resolvedPath))
            {
                activeGraphValidation = new GraphValidationResult();
                activeGraphValidation.Errors.Add($"Graph file not found: {resolvedPath}");
                return;
            }

            try
            {
                string json = File.ReadAllText(resolvedPath);
                activeVehicleGraph = GraphSerializer.Deserialize(json, out activeGraphValidation);

                if (activeVehicleGraph != null && activeGraphValidation != null && activeGraphValidation.IsValid)
                {
                    string baseDir = Path.GetDirectoryName(resolvedPath) ?? AppDomain.CurrentDomain.BaseDirectory;

                    // Populate Include node ports from their included graphs (v3 schema doesn't serialize them)
                    GraphSerializer.PopulateIncludePorts(activeVehicleGraph, baseDir);

                    activeGraphRuntime = GraphRuntimeConverter.Convert(activeVehicleGraph);
                    activeGraphResolver = GraphRuntimeConverter.CreateResolver(baseDir);
                    activeIncludeContextCache = new DiyFfb.GraphTest.IncludeContextCache();
                    activeGraphEvaluator = new DiyFfb.GraphTest.GraphCompiledEvaluator(
                        activeGraphRuntime, activeGraphResolver, activeIncludeContextCache, baseDir);

                    // Notify UI that graph has changed
                    ActiveGraphChanged?.Invoke(this, EventArgs.Empty);

                    // Plan 23/24: re-register MSFS custom vars + write targets.
                    UpdateMsfsCustomVars();
                    UpdateMsfsWritableVars();

                    // Check for param migration needs
                    CheckParamMigration(resolvedPath, gameId, carId);

                    // Seed default active functions for auto-assigned templates
                    if (autoAssigned)
                        _configOrchestrator.SeedDefaultActiveFunctionIds(gameId, carId);
                }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error($"[Graph] Graph load exception: {ex.Message}", ex);
                activeGraphValidation = new GraphValidationResult();
                activeGraphValidation.Errors.Add($"Graph load failed: {ex.Message}");
            }
        }

        private void CheckParamMigration(string resolvedPath, string gameId, string carId)
        {
            if (activeVehicleGraph == null)
            {
                return;
            }

            var profile = GetCurrentAircraftProfile();
            if (profile == null)
            {
                return;
            }

            // Compute current hash
            string currentHash = GraphHashComputer.ComputeGraphTreeHash(resolvedPath, activeVehicleGraph);
            if (string.IsNullOrWhiteSpace(currentHash))
            {
                return;
            }

            // First-time setup: initialize hash without triggering notification
            if (string.IsNullOrWhiteSpace(profile.LastReviewedGraphHash))
            {
                InitializeParamSnapshots(profile, currentHash);
                return;
            }

            // Compare to stored hash
            if (currentHash == profile.LastReviewedGraphHash)
            {
                return;
            }

            // Hash changed - run migration
            var result = MigrateParamOverrides(profile, currentHash);
            if (result.HasChangesToReview)
            {
                ParamMigrationDetected?.Invoke(this, result);
            }
            else
            {
                // No notable changes, just update hash silently
                profile.LastReviewedGraphHash = currentHash;
                profile.LastReviewedParamSnapshots = result.CurrentSnapshots;
            }
        }

        private void InitializeParamSnapshots(DiyFfbPluginSettings.AircraftFfbProfile profile, string hash)
        {
            var allParams = CollectAllGraphParams(activeVehicleGraph, activeGraphResolver);
            var paramInfos = allParams.Select(p => new ParamMigrationHelper.ParamInfo
            {
                Name = p.Name,
                DefaultValue = p.DefaultValue,
                Min = p.Min,
                Max = p.Max
            });

            profile.LastReviewedGraphHash = hash;
            profile.LastReviewedParamSnapshots = ParamMigrationHelper.CreateSnapshots(paramInfos);
        }

        private ParamMigrationResult MigrateParamOverrides(
            DiyFfbPluginSettings.AircraftFfbProfile profile,
            string newHash)
        {
            var allParams = CollectAllGraphParams(activeVehicleGraph, activeGraphResolver);
            var paramInfos = allParams.Select(p => new ParamMigrationHelper.ParamInfo
            {
                Name = p.Name,
                DefaultValue = p.DefaultValue,
                Min = p.Min,
                Max = p.Max
            });

            return ParamMigrationHelper.Migrate(
                paramInfos,
                profile.LastReviewedParamSnapshots,
                profile.GraphParamValues,
                newHash);
        }

        private void EvaluateActiveGraph(GameData data)
        {
            if (activeGraphEvaluator == null)
            {
                return;
            }

            try
            {
                BuildGraphInputs(data);
                BuildGraphParams();
                // Clear context cache before top-level evaluation so include contexts are fresh
                activeIncludeContextCache?.Clear();
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                double dt = _lastGraphEvalTicks > 0
                    ? (double)(now - _lastGraphEvalTicks) / System.Diagnostics.Stopwatch.Frequency
                    : 0.0;
                _lastGraphEvalTicks = now;
                lastGraphEvaluation = activeGraphEvaluator.EvaluateWithTrace(graphInputs, graphParams, dt);
                ApplyPendingStateRestore();

                // Propagate ConfigOut values to the in-memory ConfigOut tier on every eval.
                // These are graph-derived static config (harm ratios, phase, etc.) computed
                // from params + math nodes — they don't depend on telemetry freshness.
                CheckConfigOutChanges();

                // Plan 24: dispatch changed MsfsVarOut values to the sim (write path).
                CheckMsfsVarOutChanges();
            }
            catch
            {
                // Ignore evaluation errors to keep runtime stable; outputs remain from the last successful evaluation.
            }
        }

        /// <summary>
        /// Resets persistent state in the active graph evaluator (accumulators, sample-holds, etc.).
        /// Call on user profile switch.
        /// </summary>
        internal void ResetGraphState()
        {
            activeGraphEvaluator?.ResetState();
        }

        /// <summary>
        /// Saves the current graph state (trim accumulators etc.) for the given vehicle key.
        /// </summary>
        private void SaveGraphState(string vehicleKey)
        {
            if (string.IsNullOrWhiteSpace(vehicleKey) || activeGraphEvaluator == null) return;
            if (Settings.GraphStateSnapshots == null)
                Settings.GraphStateSnapshots = new Dictionary<string, Dictionary<string, double[]>>();
            Settings.GraphStateSnapshots[vehicleKey] = activeGraphEvaluator.GetStateSnapshot();
        }

        /// <summary>
        /// Queues a graph state restore for the given vehicle key.
        /// The actual restore is deferred until after the first evaluation cycle,
        /// because sub-graph evaluators (Include cache) are lazily created and
        /// don't exist yet when the evaluator is first constructed.
        /// </summary>
        private Dictionary<string, double[]> _pendingStateRestore;

        private void RestoreGraphState(string vehicleKey)
        {
            _pendingStateRestore = null;
            if (string.IsNullOrWhiteSpace(vehicleKey)) return;
            if (Settings.GraphStateSnapshots != null
                && Settings.GraphStateSnapshots.TryGetValue(vehicleKey, out var snapshot))
            {
                _pendingStateRestore = snapshot;
            }
        }

        private void ApplyPendingStateRestore()
        {
            if (_pendingStateRestore == null || activeGraphEvaluator == null) return;
            activeGraphEvaluator.RestoreStateSnapshot(_pendingStateRestore);
            _pendingStateRestore = null;
        }

        // --- Axis position/force tracking for graph inputs ---
        // Keyed by the PHYSICAL AxisID (AxisState.AxisId, no direction flags).
        private readonly Dictionary<AxisID, float> _lastAxisPositions = new Dictionary<AxisID, float>();
        private readonly Dictionary<AxisID, float> _lastAxisForces = new Dictionary<AxisID, float>();

        /// <summary>
        /// Called from UI layer when an AxisState message is received.
        /// Caches position (mm) + force (N) for use as graph inputs.
        /// </summary>
        internal void UpdateAxisState(AxisID axisId, float position, float force)
        {
            _lastAxisPositions[axisId] = position;
            _lastAxisForces[axisId] = force;
        }

        /// <summary>
        /// Returns the last known position (mm) for the given axis, or 0 if unknown.
        /// </summary>
        internal double GetLastAxisPosition(AxisID axisId)
        {
            return _lastAxisPositions.TryGetValue(axisId, out var pos) ? pos : 0.0;
        }

        /// <summary>
        /// Returns the last known force (N) for the given axis, or 0 if unknown.
        /// </summary>
        internal double GetLastAxisForce(AxisID axisId)
        {
            return _lastAxisForces.TryGetValue(axisId, out var f) ? f : 0.0;
        }

        /// <summary>
        /// Returns the last known position (mm) for the axis linked to the given function.
        /// Resolves function → primary linked axis → cached AxisState position.
        /// </summary>
        internal double GetFunctionPosition(FunctionID functionId)
        {
            var config = _functionConfigManager.GetCurrentConfig((int)functionId);
            if (config?.Base == null || config.Base.LinkedAxes.Count == 0)
                return 0.0;
            var axisId = config.Base.LinkedAxes[0];
            if (axisId == AxisID.AxisUndefined)
                return 0.0;
            return GetLastAxisPosition(axisId);
        }

        /// <summary>
        /// Sums the cached force (N) of every assigned linked axis whose raw
        /// LinkedAxes entry satisfies <paramref name="match"/> (null = all). The
        /// physical AxisID (the force-cache key) is recovered by masking off the
        /// direction flags; unassigned (AxisUndefined) slots are skipped.
        /// When <paramref name="applyDirection"/> is true, subtractive axes
        /// contribute with a negated sign (per the AxisSubtractive convention),
        /// giving the NET directional force; when false, raw forces are summed.
        /// </summary>
        private double SumLinkedAxisForce(FunctionID functionId, Func<AxisID, bool> match,
                                          bool applyDirection = false)
        {
            var config = _functionConfigManager.GetCurrentConfig((int)functionId);
            if (config?.Base == null) return 0.0;
            double sum = 0.0;
            foreach (var linked in config.Base.LinkedAxes)
            {
                var physical = linked & AxisID.Mask;
                if (physical == AxisID.AxisUndefined) continue;
                if (match != null && !match(linked)) continue;
                double f = GetLastAxisForce(physical);
                if (applyDirection && (linked & AxisID.AxisSubtractive) != 0) f = -f;
                sum += f;
            }
            return sum;
        }

        /// <summary>
        /// Returns the NET force (N) for the given function, combined across ALL
        /// its assigned linked axes with the AxisSubtractive direction convention
        /// (subtractive axes are negated). Single-axis functions (stick
        /// pitch/roll, collective) reduce to that one axis; multi-axis setups
        /// (e.g. a differential/dual-actuator function) combine directionally.
        /// </summary>
        internal double GetFunctionForce(FunctionID functionId) =>
            SumLinkedAxisForce(functionId, null, applyDirection: true);

        /// <summary>
        /// Returns the summed force (N) of the flight-pedals axes on one side.
        /// Direction is encoded by the AxisSubtractive flag — subtractive = LEFT,
        /// additive = RIGHT. When more than one axis matches the side (e.g. pilot +
        /// copilot left pedals), their forces are ADDED. Positions are mirrored, so
        /// only force needs the per-side split.
        /// </summary>
        internal double GetPedalForce(FunctionID functionId, bool left) =>
            SumLinkedAxisForce(functionId, linked => ((linked & AxisID.AxisSubtractive) != 0) == left);

        /// <summary>
        /// Returns the center position (mm) for the given function, computed from its
        /// FlightControl config's (pos_min + pos_max) / 2.
        /// </summary>
        internal double GetFunctionCenter(FunctionID functionId)
        {
            var config = _functionConfigManager.GetCurrentConfig((int)functionId);
            if (config == null) return 0.0;

            if (config.FlightControl != null)
                return (config.FlightControl.PosMin + config.FlightControl.PosMax) / 2.0;
            return 0.0;
        }

        private void BuildGraphInputs(GameData data)
        {
            graphInputs.Clear();
            GraphSignalCatalog.BuildXPlaneInputs(this, data, graphInputs);
            GraphSignalCatalog.BuildMsfsInputs(this, data, graphInputs);
            GraphSignalCatalog.BuildGripInputs(_gripHeld, graphInputs);
            GraphSignalCatalog.BuildAxisInputs(this, graphInputs);
            BuildConfigInInputs(graphInputs);
        }

        /// <summary>
        /// Supplies ConfigIn node values: for each "Scope:FieldPath" key the active
        /// graph reads, resolve the scoped function's MERGED config and read the
        /// effective field value (e.g. the actual motion PosMin/PosMax). Mirror of
        /// the ConfigOut write path — ConfigIn is feed-forward (reads the current
        /// merged value; a same-frame ConfigOut write to the same field is seen next frame).
        /// </summary>
        private void BuildConfigInInputs(Dictionary<string, double> inputs)
        {
            var keys = activeGraphEvaluator?.ConfigInputKeys;
            if (keys == null || keys.Count == 0) return;

            foreach (var key in keys)
            {
                int sep = key.IndexOf(':');
                if (sep <= 0 || sep >= key.Length - 1) continue;  // need both scope and field
                string scope = key.Substring(0, sep);
                string fieldPath = key.Substring(sep + 1);

                FunctionID? fn = ResolveFunctionIdFromScope(scope);
                if (fn == null) continue;

                var field = ConfigInFieldCatalog.Get(fieldPath);
                if (field?.GetMergedValue == null) continue;

                var config = _functionConfigManager.GetCurrentConfig((int)fn.Value);
                double? value = field.GetMergedValue(config);
                if (value.HasValue) inputs[key] = value.Value;
            }
        }

        // Editor-preview support: resolve the active graph's ConfigIn values
        // (Scope:FieldPath -> merged config value, e.g. real PosMin/PosMax) so the
        // preview evaluator can feed ConfigIn nodes actual values instead of 0.
        // Without this, config-dependent Expr math (e.g. /(max-min)) read 0/0 in
        // preview -> ±Infinity, even though runtime was correct.
        internal IReadOnlyDictionary<string, double> GetConfigInInputs()
        {
            var d = new Dictionary<string, double>();
            try { BuildConfigInInputs(d); } catch { }
            return d;
        }

        internal Dictionary<string, double> GetLiveGraphInputs()
        {
            var inputs = new Dictionary<string, double>();
            GraphSignalCatalog.BuildXPlaneInputs(this, null, inputs);
            GraphSignalCatalog.BuildMsfsInputs(this, null, inputs);
            // Grip/Axis inputs: only include cached values, no COM calls.
            // These may be zero if no bindings are configured — that's fine.
            try
            {
                GraphSignalCatalog.BuildGripInputs(_gripHeld, inputs);
                GraphSignalCatalog.BuildAxisInputs(this, inputs);
            }
            catch { }
            return inputs;
        }

        // Plan 23: custom vars whose PERIOD_ONCE probe was rejected by MSFS
        // (bad A: name / absent on this aircraft), keyed by alias -> exception
        // code. Used by the graph editor to flag the offending ports. LVAR typos
        // don't appear here (unknown LVARs read 0 rather than raising).
        internal IReadOnlyDictionary<string, uint> GetMsfsFailedVars()
        {
            var map = new Dictionary<string, uint>(StringComparer.Ordinal);
            var client = _msfsClient;
            if (client != null)
            {
                foreach (var f in client.FailedVars)
                {
                    if (f != null && !string.IsNullOrEmpty(f.Alias))
                        map[f.Alias] = f.ExceptionCode;
                }
            }
            return map;
        }

        // Plan 24: MsfsVarOut write targets the client couldn't deliver — a
        // non-settable/unknown A: that raised, or a B: input event unresolved on
        // this aircraft (code 0). Keyed alias -> exception code; used by the editor
        // to flag the offending write ports.
        internal IReadOnlyDictionary<string, uint> GetMsfsWriteFailedVars()
        {
            var client = _msfsClient;
            return client != null
                ? client.WriteFailedVars
                : new Dictionary<string, uint>(StringComparer.Ordinal);
        }

        private void BuildGraphParams()
        {
            graphParams.Clear();
            if (activeVehicleGraph == null)
            {
                return;
            }

            // Collect all params from graph + includes (includes provide defaults)
            var allParams = CollectAllGraphParams(activeVehicleGraph, activeGraphResolver);

            foreach (var param in allParams)
            {
                double value = ResolveParamValue(param.Name, param.DefaultValue);
                if (param.Ui?.MuteValue.HasValue == true && _mutedParams.Contains(param.Name))
                {
                    value = param.Ui.MuteValue.Value;
                }
                graphParams[param.Name] = value;
            }
        }

        /// <summary>
        /// Resolves a param value using three-tier resolution:
        /// Tier 1: defaultValue (from param definition)
        /// Tier 2: Graph-level template defaults (from graph JSON ParamValues, read-only at runtime)
        /// Tier 3: Vehicle profile override (GraphParamValues, user edits go here)
        /// Higher tiers take precedence. User edits only write to Tier 3.
        /// </summary>
        private double ResolveParamValue(string paramName, double defaultValue)
        {
            double value = defaultValue;

            // Tier 2: Graph-level override
            if (activeVehicleGraph?.ParamValues != null
                && activeVehicleGraph.ParamValues.TryGetValue(paramName, out var graphOverride))
            {
                value = graphOverride;
            }

            // Tier 3: Vehicle profile override
            var profile = GetCurrentAircraftProfile();
            if (profile?.GraphParamValues != null
                && profile.GraphParamValues.TryGetValue(paramName, out var profileOverride))
            {
                value = profileOverride;
            }

            return value;
        }

        private List<GraphParam> CollectAllGraphParams(
            GraphEditor.GraphDefinition graph,
            DiyFfb.GraphTest.GraphIncludeResolver resolver)
        {
            string baseDir = GetActiveGraphBaseDirectory();
            return CollectAllGraphParams(graph, resolver, baseDir);
        }

        private List<GraphParam> CollectAllGraphParams(
            GraphEditor.GraphDefinition graph,
            DiyFfb.GraphTest.GraphIncludeResolver resolver,
            string baseDir)
        {
            var result = new Dictionary<string, GraphEditor.GraphParam>();

            if (graph == null)
            {
                return new List<GraphEditor.GraphParam>();
            }

            // First, recursively collect params from includes (these are defaults)
            if (graph.Nodes != null)
            {
                foreach (var node in graph.Nodes)
                {
                    if (node.Kind == GraphEditor.GraphNodeKind.Include
                        && !string.IsNullOrWhiteSpace(node.IncludePath))
                    {
                        string resolvedPath = ResolveIncludePath(baseDir, node.IncludePath);
                        var includedGraph = LoadIncludeGraphFromPath(resolvedPath);
                        if (includedGraph != null)
                        {
                            string includeDir = Path.GetDirectoryName(resolvedPath) ?? baseDir;
                            var includeParams = CollectAllGraphParams(includedGraph, resolver, includeDir);
                            foreach (var param in includeParams)
                            {
                                // Add if not already present (parent can override)
                                if (!result.ContainsKey(param.Name))
                                {
                                    result[param.Name] = param;
                                }
                            }
                        }
                    }
                }
            }

            // Then merge params from this graph. For a param that originates in an
            // include, the included (library) graph stays the authority for range
            // (Min/Max) and UI metadata — so editing the library's range propagates
            // even when the parent holds a copy (e.g. an auto-created value stub or a
            // stale range). The parent may still override the default value. Params
            // defined only in the parent are taken as-is.
            foreach (var param in graph.Params.Values)
            {
                if (result.TryGetValue(param.Name, out var includeParam))
                {
                    includeParam.DefaultValue = param.DefaultValue;
                    if (param.Ui != null)
                    {
                        includeParam.Ui = param.Ui;
                    }
                    // Keep includeParam.Min/Max: the library defines the range.
                }
                else
                {
                    result[param.Name] = param;
                }
            }

            return result.Values.ToList();
        }

        private GraphEditor.GraphDefinition LoadIncludeGraphFromPath(string resolvedPath)
        {
            if (string.IsNullOrWhiteSpace(resolvedPath) || !File.Exists(resolvedPath))
            {
                return null;
            }

            try
            {
                string json = File.ReadAllText(resolvedPath);
                return GraphEditor.GraphSerializer.Deserialize(json, out _);
            }
            catch
            {
                return null;
            }
        }

        public DiyFfbPluginSettings.AircraftFfbProfile GetCurrentAircraftProfile()
        {
            if (Settings?.AircraftFfbProfiles == null || string.IsNullOrWhiteSpace(activeCarId))
            {
                return null;
            }

            string profileKey = BuildProfileKey(activeGameId, activeCarId);
            if (string.IsNullOrWhiteSpace(profileKey))
            {
                return null;
            }

            if (Settings.AircraftFfbProfiles.TryGetValue(profileKey, out var profile))
            {
                return profile;
            }

            return null;
        }

        private void HandleAircraftChange(GameData data, string gameId)
        {
            string carId = data.NewData?.CarId;
            if (string.IsNullOrWhiteSpace(carId))
            {
                return;
            }

            if (string.Equals(carId, activeCarId, StringComparison.Ordinal))
            {
                return;
            }

            // Save graph state (trim accumulators etc.) for the outgoing vehicle
            if (!string.IsNullOrWhiteSpace(activeCarId))
            {
                string outgoingKey = BuildProfileKey(activeGameId, activeCarId);
                SaveGraphState(outgoingKey);
            }

            if (!string.IsNullOrWhiteSpace(activeCarId) && HasUnsavedProfileChanges(activeGameId, activeCarId))
            {
                bool saveCurrent = false;
                if (ui != null)
                {
                    saveCurrent = (bool)ui.Dispatcher.Invoke(new Func<bool>(() =>
                        ui.ConfirmSaveCurrentProfile(activeCarName, activeCarId)));
                }

                if (saveCurrent)
                {
                    SaveCurrentAircraftProfile(activeGameId, activeCarId);
                }
                else
                {
                    // User chose to discard - restore profile to its original state
                    DiscardProfileChanges();
                }
            }

            bool applyPending = false;
            if (hasPendingFfbProfile && pendingFfbProfile != null)
            {
                if (ui != null)
                {
                    applyPending = (bool)ui.Dispatcher.Invoke(new Func<bool>(() =>
                        ui.ConfirmApplyPendingProfile(activeCarName, carId)));
                }

                if (applyPending)
                {
                    string profileKey = BuildProfileKey(gameId, carId);
                    if (!string.IsNullOrWhiteSpace(profileKey))
                    {
                        if (Settings.AircraftFfbProfiles == null)
                        {
                            Settings.AircraftFfbProfiles = new System.Collections.Generic.Dictionary<string, DiyFfbPluginSettings.AircraftFfbProfile>();
                        }
                        Settings.AircraftFfbProfiles[profileKey] = pendingFfbProfile;
                    }
                }

                pendingFfbProfile = null;
                hasPendingFfbProfile = false;
            }

            // Set activeCarId before applying profile so IsFunctionActive()
            // checks the NEW profile during config-changed event handling.
            activeCarId = carId;
            activeCarName = !string.IsNullOrWhiteSpace(data.NewData?.CarModel) ? data.NewData.CarModel : carId;

            // Migrate old-style key before resolving graph so ResolveGraphPath
            // finds the profile under the new key format.
            MigrateProfileKeyIfNeeded(gameId, carId);

            // Mute state is per-vehicle: a different aircraft shouldn't inherit
            // tuning-session mutes. Clear before BuildGraphParams so the new
            // params dict isn't built with stale mute substitutions.
            ClearAllParamMutes();

            // Resolve graph first — auto-assign creates the profile and
            // SeedDefaultActiveFunctionIds populates ActiveFunctionIds.
            // ApplyAircraftProfile must run after so it sees populated IDs.
            ResolveActiveGraph(gameId, carId);
            ApplyAircraftProfile(gameId, carId);
            BuildGraphParams();

            // Restore graph state (trim accumulators etc.) for the incoming vehicle
            RestoreGraphState(BuildProfileKey(gameId, carId));

            // Fire ContextChanged event for badge/UI refresh
            _configOrchestrator.OnContextChanged();

            if (ui != null)
            {
                string carName = activeCarName;
                string carIdLabel = activeCarId;
                string gameIdCapture = gameId;
                ui.Dispatcher.BeginInvoke(new Action(() =>
                {
                    ui.RefreshGraphSelection();
                    ui.UpdateActiveAircraftLabel(carName, carIdLabel, gameIdCapture);
                    ui.RefreshVehicleParams();
                    ui.RefreshFunctionSelection();
                }));
            }
        }

        private void SaveCurrentAircraftProfile(string gameId, string carId)
        {
            if (Settings == null || string.IsNullOrWhiteSpace(carId))
            {
                return;
            }

            string profileKey = BuildProfileKey(gameId, carId);
            if (string.IsNullOrWhiteSpace(profileKey))
            {
                return;
            }

            if (Settings.AircraftFfbProfiles == null)
            {
                Settings.AircraftFfbProfiles = new System.Collections.Generic.Dictionary<string, DiyFfbPluginSettings.AircraftFfbProfile>();
            }

            Settings.AircraftFfbProfiles[profileKey] = BuildCurrentAircraftProfile();
            ClearDirtyState();
        }

        /// <summary>
        /// Explicitly stores the current vehicle's profile settings.
        /// </summary>
        /// <returns>True if profile was stored, false if no active vehicle.</returns>
        public bool StoreCurrentProfile()
        {
            if (string.IsNullOrWhiteSpace(activeCarId))
            {
                return false;
            }

            SaveCurrentAircraftProfile(activeGameId, activeCarId);
            return true;
        }

        private void ApplyAircraftProfile(string gameId, string carId)
        {
            if (Settings == null || string.IsNullOrWhiteSpace(carId))
            {
                return;
            }

            // Migrate old-style key if needed
            MigrateProfileKeyIfNeeded(gameId, carId);

            string profileKey = BuildProfileKey(gameId, carId);
            if (string.IsNullOrWhiteSpace(profileKey))
            {
                ApplyFfbProfileToCurrentSettings(new DiyFfbPluginSettings.AircraftFfbProfile());
                return;
            }

            if (Settings.AircraftFfbProfiles == null)
            {
                Settings.AircraftFfbProfiles = new System.Collections.Generic.Dictionary<string, DiyFfbPluginSettings.AircraftFfbProfile>();
            }

            if (Settings.AircraftFfbProfiles.TryGetValue(profileKey, out var profile))
            {
                Settings.XPlaneRotorIndex = profile.XPlaneRotorIndex;
                _configOrchestrator.ApplyProfileFunctionOverrides(profile);
            }
            else
            {
                // No profile - clear any active overrides
                _configOrchestrator.ApplyProfileFunctionOverrides(null);
            }
        }

        private DiyFfbPluginSettings.AircraftFfbProfile BuildCurrentAircraftProfile()
        {
            var profile = new DiyFfbPluginSettings.AircraftFfbProfile();
            profile.XPlaneRotorIndex = Settings.XPlaneRotorIndex;

            // Copy all fields from current stored profile
            var currentProfile = GetCurrentAircraftProfile();
            if (currentProfile != null)
            {
                profile.GraphPath = currentProfile.GraphPath;
                if (currentProfile.GraphParamValues != null)
                    profile.GraphParamValues = new Dictionary<string, double>(currentProfile.GraphParamValues);
                profile.LastReviewedGraphHash = currentProfile.LastReviewedGraphHash;
                if (currentProfile.LastReviewedParamSnapshots != null)
                    profile.LastReviewedParamSnapshots = new Dictionary<string, DiyFfbPluginSettings.ParamSnapshot>(currentProfile.LastReviewedParamSnapshots);
                if (currentProfile.FunctionOverrides != null)
                    profile.FunctionOverrides = new Dictionary<int, FunctionConfigOverrides>(currentProfile.FunctionOverrides);
                if (currentProfile.ActiveFunctionIds != null)
                    profile.ActiveFunctionIds = new HashSet<int>(currentProfile.ActiveFunctionIds);
                profile.MsfsReadVarOverrides = CloneMsfsOverrides(currentProfile.MsfsReadVarOverrides);
                profile.MsfsWriteVarOverrides = CloneMsfsOverrides(currentProfile.MsfsWriteVarOverrides);
            }

            return profile;
        }

        private bool HasUnsavedProfileChanges(string gameId, string carId)
        {
            if (Settings == null || string.IsNullOrWhiteSpace(carId))
            {
                return false;
            }

            // Graph param changes are tracked separately since they modify the profile in-place
            if (hasDirtyGraphParams)
            {
                return true;
            }

            string profileKey = BuildProfileKey(gameId, carId);
            if (string.IsNullOrWhiteSpace(profileKey))
            {
                return false;
            }

            var current = BuildCurrentAircraftProfile();
            if (Settings.AircraftFfbProfiles != null &&
                Settings.AircraftFfbProfiles.TryGetValue(profileKey, out var stored))
            {
                return !AreProfilesEqual(current, stored);
            }

            return !AreProfilesEqual(current, new DiyFfbPluginSettings.AircraftFfbProfile());
        }

        private bool AreProfilesEqual(DiyFfbPluginSettings.AircraftFfbProfile left, DiyFfbPluginSettings.AircraftFfbProfile right)
        {
            if (left == null || right == null)
            {
                return left == right;
            }

            return string.Equals(left.GraphPath, right.GraphPath, System.StringComparison.OrdinalIgnoreCase) &&
                   left.XPlaneRotorIndex == right.XPlaneRotorIndex &&
                   AreGraphParamValuesEqual(left.GraphParamValues, right.GraphParamValues) &&
                   AreFunctionOverridesEqual(left.FunctionOverrides, right.FunctionOverrides) &&
                   AreActiveFunctionIdsEqual(left.ActiveFunctionIds, right.ActiveFunctionIds) &&
                   AreMsfsOverridesEqual(left.MsfsReadVarOverrides, right.MsfsReadVarOverrides) &&
                   AreMsfsOverridesEqual(left.MsfsWriteVarOverrides, right.MsfsWriteVarOverrides);
        }

        // Plan 36: deep-clone the per-function override dict for a cross-profile copy.
        // FunctionConfigOverrides has nested objects/arrays and no Clone(), so round-trip
        // through JSON; skip null/empty entries so the destination stays clean.
        private static Dictionary<int, FunctionConfigOverrides> CloneFunctionOverrides(
            Dictionary<int, FunctionConfigOverrides> src)
        {
            var copy = new Dictionary<int, FunctionConfigOverrides>();
            if (src == null) return copy;
            foreach (var kvp in src)
            {
                if (kvp.Value == null || kvp.Value.IsEmpty) continue;
                string json = Newtonsoft.Json.JsonConvert.SerializeObject(kvp.Value);
                copy[kvp.Key] = Newtonsoft.Json.JsonConvert.DeserializeObject<FunctionConfigOverrides>(json);
            }
            return copy;
        }

        // Plan 34: deep-clone a per-alias override dict (never null; overrides are
        // mutated in place, so the profile snapshot must own its own copies).
        private static Dictionary<string, MsfsVarBindingOverride> CloneMsfsOverrides(
            Dictionary<string, MsfsVarBindingOverride> src)
        {
            var copy = new Dictionary<string, MsfsVarBindingOverride>(StringComparer.Ordinal);
            if (src != null)
            {
                foreach (var kvp in src)
                {
                    if (kvp.Value == null || kvp.Value.IsEmpty) continue;
                    copy[kvp.Key] = kvp.Value.Clone();
                }
            }
            return copy;
        }

        // Empty/null entries are treated as absent (like AreFunctionOverridesEqual).
        private static bool AreMsfsOverridesEqual(
            Dictionary<string, MsfsVarBindingOverride> left,
            Dictionary<string, MsfsVarBindingOverride> right)
        {
            var l = CloneMsfsOverrides(left);
            var r = CloneMsfsOverrides(right);
            if (l.Count != r.Count) return false;
            foreach (var kvp in l)
            {
                if (!r.TryGetValue(kvp.Key, out var ro)) return false;
                var lo = kvp.Value;
                if (!string.Equals(lo.SimVar, ro.SimVar, StringComparison.Ordinal)) return false;
                if (!string.Equals(lo.Unit, ro.Unit, StringComparison.Ordinal)) return false;
                if (lo.InMin != ro.InMin || lo.InMax != ro.InMax) return false;
                if (lo.OutMin != ro.OutMin || lo.OutMax != ro.OutMax) return false;
            }
            return true;
        }

        private bool AreGraphParamValuesEqual(Dictionary<string, double> left, Dictionary<string, double> right)
        {
            if (left == null && right == null) return true;
            if (left == null || right == null) return false;
            if (left.Count != right.Count) return false;

            foreach (var kvp in left)
            {
                if (!right.TryGetValue(kvp.Key, out var rightValue))
                {
                    return false;
                }
                if (!NearlyEqual((float)kvp.Value, (float)rightValue))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool AreFunctionOverridesEqual(
            Dictionary<int, FunctionConfigOverrides> left,
            Dictionary<int, FunctionConfigOverrides> right)
        {
            // Collect non-empty entries from each side (null/empty dict treated as equivalent)
            var leftEffective = new Dictionary<int, FunctionConfigOverrides>();
            var rightEffective = new Dictionary<int, FunctionConfigOverrides>();

            if (left != null)
            {
                foreach (var kvp in left)
                {
                    if (kvp.Value != null && !kvp.Value.IsEmpty)
                        leftEffective[kvp.Key] = kvp.Value;
                }
            }

            if (right != null)
            {
                foreach (var kvp in right)
                {
                    if (kvp.Value != null && !kvp.Value.IsEmpty)
                        rightEffective[kvp.Key] = kvp.Value;
                }
            }

            if (leftEffective.Count != rightEffective.Count) return false;

            foreach (var kvp in leftEffective)
            {
                if (!rightEffective.TryGetValue(kvp.Key, out var rightVal))
                    return false;
                if (!ConfigComparer.AreEqual(kvp.Value, rightVal))
                    return false;
            }

            return true;
        }

        private static bool AreActiveFunctionIdsEqual(HashSet<int> left, HashSet<int> right)
        {
            bool leftEmpty = left == null || left.Count == 0;
            bool rightEmpty = right == null || right.Count == 0;

            if (leftEmpty && rightEmpty) return true;
            if (leftEmpty || rightEmpty) return false;

            return left.SetEquals(right);
        }

        public string GetActiveCarId()
        {
            return activeCarId;
        }

        public string GetActiveCarName()
        {
            return activeCarName;
        }

        public string GetActiveGameId()
        {
            return activeGameId;
        }

        /// <summary>
        /// Resets the current vehicle's GraphParamValues to defaults (clears all overrides).
        /// Returns true if reset was performed, false if no profile exists.
        /// </summary>
        public bool ResetCurrentProfileToDefaults()
        {
            var profile = GetCurrentAircraftProfile();
            if (profile == null)
            {
                return false;
            }

            // Clear all param overrides
            profile.GraphParamValues?.Clear();

            // Clear dirty flag and snapshot since we're intentionally resetting
            ClearDirtyState();

            // Rebuild params from graph defaults (Tier 1) and graph template (Tier 2)
            BuildGraphParams();

            // Notify listeners so UI can refresh
            ActiveGraphChanged?.Invoke(this, EventArgs.Empty);

            return true;
        }

        /// <summary>
        /// Gets the profile key for the current active vehicle (gameId::carId format).
        /// </summary>
        public string GetActiveProfileKey()
        {
            return BuildProfileKey(activeGameId, activeCarId);
        }

        public string GetVehicleGraphPath(string gameId, string carId)
        {
            if (Settings?.AircraftFfbProfiles == null)
            {
                return "";
            }

            string key = BuildProfileKey(gameId, carId);
            if (string.IsNullOrWhiteSpace(key))
            {
                return "";
            }

            return Settings.AircraftFfbProfiles.TryGetValue(key, out var profile) ? profile?.GraphPath ?? "" : "";
        }

        public string GetActiveGraphStatus()
        {
            if (string.IsNullOrWhiteSpace(activeGraphPath))
            {
                return "(none)";
            }

            if (activeGraphValidation == null)
            {
                return $"{activeGraphPath}";
            }

            if (activeGraphValidation.IsValid)
            {
                return $"{activeGraphPath}";
            }

            string error = activeGraphValidation.Errors.Count > 0
                ? activeGraphValidation.Errors[0]
                : "Invalid graph.";
            return $"{activeGraphPath} ({error})";
        }

        public string GetActiveGraphPath()
        {
            if (string.IsNullOrWhiteSpace(activeGraphPath))
                return "";

            // Return resolved absolute path to avoid path resolution inconsistencies
            return ResolveGraphFilePath(activeGraphPath);
        }

        /// <summary>
        /// Gets the graph path for a profile key (gameId::carId format).
        /// </summary>
        public string GetGraphPathForProfileKey(string profileKey)
        {
            if (string.IsNullOrWhiteSpace(profileKey))
                return "";

            // Profile key format: "gameId::carId"
            int sep = profileKey.IndexOf("::");
            if (sep > 0 && sep < profileKey.Length - 2)
            {
                string gameId = profileKey.Substring(0, sep);
                string carId = profileKey.Substring(sep + 2);
                return GetVehicleGraphPath(gameId, carId);
            }

            // Legacy format: carId only
            return GetVehicleGraphPath("", profileKey);
        }

        /// <summary>
        /// Applies a profile selected from the Profile Manager to the current vehicle.
        /// Thin wrapper over <see cref="ApplyProfileToVehicle"/> for the active vehicle.
        /// </summary>
        public void ApplyProfileFromBrowser(string graphPath, DiyFfbPluginSettings.AircraftFfbProfile profile, bool useTuning,
            bool copyFunctionTuning = false, bool copySimVarBindings = false, bool copyActiveFunctions = false)
        {
            ApplyProfileToVehicle(activeGameId, activeCarId, graphPath, profile, useTuning,
                copyFunctionTuning, copySimVarBindings, copyActiveFunctions);
        }

        /// <summary>
        /// Applies a graph and/or tuning to a specific vehicle profile. When the target
        /// is the active vehicle the runtime graph is reloaded immediately; otherwise the
        /// change is persisted to that profile and takes effect when it next activates.
        /// </summary>
        /// <param name="gameId">Destination game id.</param>
        /// <param name="carId">Destination car/vehicle id.</param>
        /// <param name="graphPath">Graph path to assign, or null/empty to keep the destination's current graph (tuning-only).</param>
        /// <param name="sourceProfile">Profile to copy tuning from (null for templates / graph-only).</param>
        /// <param name="useTuning">If true, copies GraphParamValues (+ rotor index) from the source profile.</param>
        /// <param name="copyFunctionTuning">Plan 36: if true, copies per-function config overrides.</param>
        /// <param name="copySimVarBindings">Plan 36: if true, copies MSFS read/write var binding overrides.</param>
        /// <param name="copyActiveFunctions">Plan 36: if true, copies the active/used function set.</param>
        public void ApplyProfileToVehicle(string gameId, string carId, string graphPath,
            DiyFfbPluginSettings.AircraftFfbProfile sourceProfile, bool useTuning,
            bool copyFunctionTuning = false, bool copySimVarBindings = false, bool copyActiveFunctions = false)
        {
            if (Settings == null || string.IsNullOrWhiteSpace(carId))
                return;

            string destKey = BuildProfileKey(gameId, carId);
            if (string.IsNullOrWhiteSpace(destKey))
                return;

            bool isActive = !string.IsNullOrWhiteSpace(activeCarId)
                && destKey == BuildProfileKey(activeGameId, activeCarId);

            if (Settings.AircraftFfbProfiles == null)
                Settings.AircraftFfbProfiles = new Dictionary<string, DiyFfbPluginSettings.AircraftFfbProfile>();

            if (!Settings.AircraftFfbProfiles.TryGetValue(destKey, out var destProfile) || destProfile == null)
            {
                destProfile = new DiyFfbPluginSettings.AircraftFfbProfile();
                Settings.AircraftFfbProfiles[destKey] = destProfile;
            }

            // 1. Graph (skip when null/empty → keep the destination's current graph).
            if (!string.IsNullOrWhiteSpace(graphPath))
                destProfile.GraphPath = graphPath;

            // 2. Tuning params from the source profile.
            if (useTuning && sourceProfile?.GraphParamValues != null && sourceProfile.GraphParamValues.Count > 0)
            {
                if (destProfile.GraphParamValues == null)
                    destProfile.GraphParamValues = new Dictionary<string, double>();
                foreach (var kvp in sourceProfile.GraphParamValues)
                    destProfile.GraphParamValues[kvp.Key] = kvp.Value;
                destProfile.XPlaneRotorIndex = sourceProfile.XPlaneRotorIndex;
            }

            // Plan 36: transport the remaining per-profile state, each independently
            // gated. Overrides the destination wholesale (an override is atomic); any
            // function id / MSFS alias the destination graph does not use is inert at
            // scan time (like orphan GraphParamValues), so cross-graph copies are safe.
            if (copyFunctionTuning)
                destProfile.FunctionOverrides = CloneFunctionOverrides(sourceProfile?.FunctionOverrides);

            if (copySimVarBindings)
            {
                destProfile.MsfsReadVarOverrides = CloneMsfsOverrides(sourceProfile?.MsfsReadVarOverrides);
                destProfile.MsfsWriteVarOverrides = CloneMsfsOverrides(sourceProfile?.MsfsWriteVarOverrides);
            }

            if (copyActiveFunctions && sourceProfile?.ActiveFunctionIds != null)
                destProfile.ActiveFunctionIds = new HashSet<int>(sourceProfile.ActiveFunctionIds);

            // 3. Active target: reload the runtime graph now; non-active: persist only.
            if (isActive)
            {
                // Mute state is per-vehicle-profile: a fresh source resets session mutes.
                ClearAllParamMutes();
                ResolveActiveGraph(activeGameId, activeCarId);
                BuildGraphParams();
                ActiveGraphChanged?.Invoke(this, EventArgs.Empty);

                // Re-apply the copied config to the running graph. ApplyProfileFunctionOverrides
                // reads FunctionOverrides (null-derefs otherwise) and ActiveFunctionIds off the
                // profile, so ensure the dict exists before invoking.
                if (copyFunctionTuning || copyActiveFunctions)
                {
                    if (destProfile.FunctionOverrides == null)
                        destProfile.FunctionOverrides = new Dictionary<int, FunctionConfigOverrides>();
                    _configOrchestrator.ApplyProfileFunctionOverrides(destProfile);
                }

                if (copySimVarBindings)
                    ApplyMsfsVarOverrides();

                ui?.Dispatcher?.BeginInvoke(new Action(() =>
                {
                    ui.RefreshGraphSelection();
                }));
            }

            // Persist (important for non-active targets, which have no reload side effect).
            this.SaveCommonSettings("GeneralSettings", Settings);
        }

        /// <summary>
        /// Gets the include context cache for the active graph.
        /// Used by the graph editor to show sub-graph previews with parent context.
        /// </summary>
        public DiyFfb.GraphTest.IncludeContextCache ActiveIncludeContextCache => activeIncludeContextCache;

        /// <summary>
        /// Returns a state snapshot from the active runtime graph evaluator.
        /// Used to sync top-level preview stateful nodes with runtime values.
        /// </summary>
        public Dictionary<string, double[]> GetActiveGraphStateSnapshot()
        {
            return activeGraphEvaluator?.GetStateSnapshot();
        }

        public IReadOnlyDictionary<string, GraphParam> GetActiveGraphParams()
        {
            if (activeVehicleGraph == null)
            {
                return new Dictionary<string, GraphParam>();
            }

            // Collect all params from main graph and includes
            var allParams = CollectAllGraphParams(activeVehicleGraph, activeGraphResolver);
            var result = new Dictionary<string, GraphParam>();
            foreach (var param in allParams)
            {
                result[param.Name] = param;
            }
            return result;
        }

        public IReadOnlyList<string> GetActiveGraphParamOrder()
        {
            if (activeVehicleGraph == null)
            {
                return Array.Empty<string>();
            }

            string baseDir = GetActiveGraphBaseDirectory();
            var ordered = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            AppendOrderedGraphParams(activeVehicleGraph, baseDir, ordered, seen, visited);

            return ordered;
        }

        private void AppendOrderedGraphParams(GraphDefinition graph, string baseDir, List<string> ordered,
            HashSet<string> seen, HashSet<string> visited)
        {
            if (graph?.Nodes == null || graph.Nodes.Count == 0)
            {
                return;
            }

            var orderedNodes = graph.Nodes
                .OrderBy(n => n.Y)
                .ThenBy(n => n.X)
                .ThenBy(n => n.Id, StringComparer.OrdinalIgnoreCase);

            foreach (var node in orderedNodes)
            {
                if (node.Kind == GraphNodeKind.Param)
                {
                    foreach (var port in node.Ports.Where(p => p.Kind == GraphPortKind.Output))
                    {
                        string name = BuildParamName(node, port);
                        if (string.IsNullOrWhiteSpace(name) || seen.Contains(name))
                        {
                            continue;
                        }

                        seen.Add(name);
                        ordered.Add(name);
                    }
                }
                else if (node.Kind == GraphNodeKind.Include)
                {
                    string resolvedPath = ResolveIncludePath(baseDir, node.IncludePath);
                    if (string.IsNullOrEmpty(resolvedPath) || !File.Exists(resolvedPath))
                    {
                        continue;
                    }

                    if (!visited.Add(resolvedPath))
                    {
                        continue;
                    }

                    var includedGraph = TryLoadEditorGraph(resolvedPath);
                    if (includedGraph == null)
                    {
                        continue;
                    }

                    string includeDir = Path.GetDirectoryName(resolvedPath) ?? baseDir;
                    AppendOrderedGraphParams(includedGraph, includeDir, ordered, seen, visited);
                }
            }
        }

        private string GetActiveGraphBaseDirectory()
        {
            if (string.IsNullOrWhiteSpace(activeGraphPath))
            {
                return AppDomain.CurrentDomain.BaseDirectory;
            }

            string resolvedPath = ResolveGraphFilePath(activeGraphPath);
            string baseDir = Path.GetDirectoryName(resolvedPath);
            if (string.IsNullOrWhiteSpace(baseDir))
            {
                return AppDomain.CurrentDomain.BaseDirectory;
            }
            return baseDir;
        }

        private static string ResolveIncludePath(string baseDir, string includePath)
        {
            if (string.IsNullOrWhiteSpace(includePath))
            {
                return null;
            }

            if (Path.IsPathRooted(includePath))
            {
                return Path.GetFullPath(includePath);
            }

            if (!string.IsNullOrWhiteSpace(baseDir))
            {
                return Path.GetFullPath(Path.Combine(baseDir, includePath));
            }

            return includePath;
        }

        private static GraphDefinition TryLoadEditorGraph(string resolvedPath)
        {
            try
            {
                string json = File.ReadAllText(resolvedPath);
                var graph = GraphSerializer.Deserialize(json, out var validation);
                if (graph != null && validation != null && validation.IsValid)
                {
                    return graph;
                }
            }
            catch
            {
                return null;
            }

            return null;
        }

        private static string BuildParamName(GraphNode node, GraphPort port)
        {
            if (!string.IsNullOrEmpty(node.SignalGroup) && !string.IsNullOrEmpty(port.SignalSuffix))
            {
                return node.SignalGroup + "." + port.SignalSuffix;
            }
            return port.Name ?? "";
        }

        /// <summary>
        /// Detects the category of the active graph based on Output node SignalGroups.
        /// Priority: Collective (helicopter) > FlightStick/FlightPedals (aircraft) > Vehicle (default)
        /// </summary>
        public GraphCategory GetActiveGraphCategory()
        {
            if (activeVehicleGraph == null || activeVehicleGraph.Nodes == null)
            {
                return GraphCategory.Vehicle;
            }

            bool hasFlightOutput = false;

            foreach (var node in activeVehicleGraph.Nodes)
            {
                if (node.Kind != GraphNodeKind.Output || string.IsNullOrEmpty(node.SignalGroup))
                    continue;

                // Collective gets priority -> helicopter icon
                if (node.SignalGroup.StartsWith("FlightStickCollective", StringComparison.OrdinalIgnoreCase))
                {
                    return GraphCategory.Helicopter;
                }

                // Track if we have any flight-related outputs
                if (node.SignalGroup.StartsWith("FlightStick", StringComparison.OrdinalIgnoreCase) ||
                    node.SignalGroup.StartsWith("FlightPedals", StringComparison.OrdinalIgnoreCase))
                {
                    hasFlightOutput = true;
                }
            }

            return hasFlightOutput ? GraphCategory.Aircraft : GraphCategory.Vehicle;
        }

        public double GetGraphParamValue(string paramName)
        {
            if (graphParams.TryGetValue(paramName, out var value))
            {
                return value;
            }

            // Fallback: do three-tier resolution for params not yet in graphParams
            // Use GetActiveGraphParams() to find the default (reuses CollectAllGraphParams)
            var allParams = GetActiveGraphParams();
            double defaultValue = allParams.TryGetValue(paramName, out var param) ? param.DefaultValue : 0.0;

            return ResolveParamValue(paramName, defaultValue);
        }

        public void SetGraphParamValue(string paramName, double value)
        {
            // Update runtime param for immediate effect
            graphParams[paramName] = value;

            // Note: We intentionally do NOT write to activeVehicleGraph.ParamValues (Tier 2).
            // Tier 2 contains template defaults from the graph JSON file and is read-only at runtime.
            // All user edits go to Tier 3 (profile.GraphParamValues) which takes precedence.

            // Snapshot BEFORE modifying profile (so we can restore on discard)
            MarkProfileDirty();

            // Save to current aircraft profile (Tier 3, persists across sessions)
            var profile = GetCurrentAircraftProfile();
            if (profile != null)
            {
                if (profile.GraphParamValues == null)
                {
                    profile.GraphParamValues = new Dictionary<string, double>();
                }
                profile.GraphParamValues[paramName] = value;
            }

            // Notify listeners of parameter change. The next DataUpdate tick picks up
            // the new graphParams[paramName] and re-evaluates the graph; an explicit
            // re-eval here would race that tick (two threads writing the ConfigOut
            // tier within <1ms can fire the throttled merge with stale zero values
            // — see plan 12 follow-up). Without a connected game there is no active
            // profile, so deferring the eval costs nothing.
            GraphParamChanged?.Invoke(this, new GraphParamChangedEventArgs(paramName, value));
        }

        /// <summary>
        /// Resets a graph parameter to its default value by removing the vehicle override.
        /// </summary>
        /// <param name="paramName">Name of the parameter to reset.</param>
        public void ResetGraphParamValue(string paramName)
        {
            // Snapshot BEFORE modifying profile (so we can restore on discard)
            MarkProfileDirty();

            // Remove from profile (Tier 3)
            var profile = GetCurrentAircraftProfile();
            if (profile?.GraphParamValues != null)
            {
                profile.GraphParamValues.Remove(paramName);
            }

            // Get the default value (from graph Tier 2 or param definition)
            var allParams = GetActiveGraphParams();
            double defaultValue = allParams.TryGetValue(paramName, out var param) ? param.DefaultValue : 0.0;
            double effectiveValue = ResolveParamValue(paramName, defaultValue);

            // Update runtime param
            graphParams[paramName] = effectiveValue;

            // Notify listeners so UI updates
            GraphParamChanged?.Invoke(this, new GraphParamChangedEventArgs(paramName, effectiveValue));
        }

        /// <summary>
        /// Gets all graph parameters from the current active graph.
        /// </summary>
        /// <returns>List of graph parameters, or empty list if no graph is loaded.</returns>
        public List<GraphEditor.GraphParam> GetAllGraphParams()
        {
            if (activeVehicleGraph == null)
            {
                return new List<GraphEditor.GraphParam>();
            }
            return CollectAllGraphParams(activeVehicleGraph, activeGraphResolver);
        }

        private bool hasDirtyGraphParams = false;
        private Dictionary<string, double> _graphParamValuesSnapshot = null;
        // Plan 34: captured alongside the param snapshot so discarding on a vehicle
        // switch restores MSFS binding overrides too (edits mutate them in place).
        private Dictionary<string, MsfsVarBindingOverride> _msfsReadOverridesSnapshot = null;
        private Dictionary<string, MsfsVarBindingOverride> _msfsWriteOverridesSnapshot = null;

        private void MarkProfileDirty()
        {
            // On first dirty, snapshot the current profile's GraphParamValues
            if (!hasDirtyGraphParams)
            {
                var profile = GetCurrentAircraftProfile();
                if (profile?.GraphParamValues != null)
                {
                    _graphParamValuesSnapshot = new Dictionary<string, double>(profile.GraphParamValues);
                }
                else
                {
                    _graphParamValuesSnapshot = new Dictionary<string, double>();
                }
                _msfsReadOverridesSnapshot = CloneMsfsOverrides(profile?.MsfsReadVarOverrides);
                _msfsWriteOverridesSnapshot = CloneMsfsOverrides(profile?.MsfsWriteVarOverrides);
            }
            hasDirtyGraphParams = true;
        }

        /// <summary>
        /// Discards unsaved profile changes by restoring GraphParamValues from the snapshot.
        /// Called when user declines to save changes on vehicle switch.
        /// </summary>
        public void DiscardProfileChanges()
        {
            if (!hasDirtyGraphParams || _graphParamValuesSnapshot == null)
            {
                return;
            }

            var profile = GetCurrentAircraftProfile();
            if (profile != null)
            {
                // Restore from snapshot
                profile.GraphParamValues = new Dictionary<string, double>(_graphParamValuesSnapshot);
                profile.MsfsReadVarOverrides = CloneMsfsOverrides(_msfsReadOverridesSnapshot);
                profile.MsfsWriteVarOverrides = CloneMsfsOverrides(_msfsWriteOverridesSnapshot);
            }

            // Clear dirty state and snapshot
            hasDirtyGraphParams = false;
            _graphParamValuesSnapshot = null;
            _msfsReadOverridesSnapshot = null;
            _msfsWriteOverridesSnapshot = null;

            // Rebuild runtime params from restored profile; re-overlay MSFS bindings
            BuildGraphParams();
            ApplyMsfsVarOverrides();
        }

        private void ClearDirtyState()
        {
            hasDirtyGraphParams = false;
            _graphParamValuesSnapshot = null;
            _msfsReadOverridesSnapshot = null;
            _msfsWriteOverridesSnapshot = null;
        }

        public event EventHandler ActiveGraphChanged;
        public event EventHandler<GraphParamChangedEventArgs> GraphParamChanged;
        public event EventHandler<ParamMigrationResult> ParamMigrationDetected;

        /// <summary>
        /// Gets the current value of a graph output signal.
        /// </summary>
        /// <param name="outputName">Full output signal name (e.g., "FlightPedals.SpringGain")</param>
        /// <param name="defaultValue">Value to return if output is not available</param>
        /// <returns>The current output value or defaultValue if not found</returns>
        public double GetGraphOutputValue(string outputName, double defaultValue = 0.0)
        {
            if (lastGraphEvaluation?.Outputs?.TryGetValue(outputName, out var value) == true)
            {
                return value;
            }
            return defaultValue;
        }

        /// <summary>
        /// Gets all current graph output values.
        /// </summary>
        /// <returns>Dictionary of output name to value, or empty dictionary if no evaluation</returns>
        public IReadOnlyDictionary<string, double> GetAllGraphOutputs()
        {
            if (lastGraphEvaluation?.Outputs != null)
            {
                return lastGraphEvaluation.Outputs;
            }
            return new Dictionary<string, double>();
        }

        /// <summary>
        /// Called by graph editor when graph content changes (e.g., reload, edit).
        /// Fires ActiveGraphChanged event to refresh parameter UI in function tabs.
        /// </summary>
        public void OnGraphContentChanged()
        {
            ActiveGraphChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Applies the given graph definition to runtime evaluation.
        /// Called from graph editor Apply button to push in-memory changes.
        /// </summary>
        public void ApplyGraphToRuntime(GraphDefinition graph, string basePath)
        {
            if (graph == null)
            {
                return;
            }

            activeVehicleGraph = graph;
            string baseDir = string.IsNullOrEmpty(basePath) ? "" : Path.GetDirectoryName(basePath);

            activeGraphRuntime = GraphRuntimeConverter.Convert(graph);
            activeGraphResolver = GraphRuntimeConverter.CreateResolver(baseDir);
            activeIncludeContextCache = new DiyFfb.GraphTest.IncludeContextCache();
            activeGraphEvaluator = new DiyFfb.GraphTest.GraphCompiledEvaluator(
                activeGraphRuntime, activeGraphResolver, activeIncludeContextCache, baseDir);

            ActiveGraphChanged?.Invoke(this, EventArgs.Empty);

            // Plan 23/24: re-register MSFS custom vars + write targets.
            UpdateMsfsCustomVars();
            UpdateMsfsWritableVars();
        }

        public void SetVehicleGraphPath(string gameId, string carId, string path)
        {
            if (Settings == null)
            {
                return;
            }

            string key = BuildProfileKey(gameId, carId);
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            if (Settings.AircraftFfbProfiles == null)
            {
                Settings.AircraftFfbProfiles = new Dictionary<string, DiyFfbPluginSettings.AircraftFfbProfile>();
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                // Remove profile if clearing path
                Settings.AircraftFfbProfiles.Remove(key);
            }
            else
            {
                // Create or update profile with graph path
                if (!Settings.AircraftFfbProfiles.TryGetValue(key, out var profile))
                {
                    profile = new DiyFfbPluginSettings.AircraftFfbProfile();
                    Settings.AircraftFfbProfiles[key] = profile;
                }
                profile.GraphPath = path;
            }

            ResolveActiveGraph(gameId, carId);
            BuildGraphParams();
        }

        public void ApplyAircraftFfbProfile(string carId, DiyFfbPluginSettings.AircraftFfbProfile profile)
        {
            if (Settings == null || string.IsNullOrWhiteSpace(carId) || profile == null)
            {
                return;
            }

            string profileKey = BuildProfileKey(activeGameId, carId);
            if (string.IsNullOrWhiteSpace(profileKey))
            {
                return;
            }

            if (Settings.AircraftFfbProfiles == null)
            {
                Settings.AircraftFfbProfiles = new System.Collections.Generic.Dictionary<string, DiyFfbPluginSettings.AircraftFfbProfile>();
            }

            Settings.AircraftFfbProfiles[profileKey] = profile;
            ApplyAircraftProfile(activeGameId, carId);
            ApplyFfbProfileToCurrentSettings(profile);
            ui?.Dispatcher?.BeginInvoke(new Action(() =>
            {
                ui.RefreshGraphSelection();
            }));
        }

        public void ApplyFfbProfileToCurrentSettings(DiyFfbPluginSettings.AircraftFfbProfile profile)
        {
            if (Settings == null || profile == null)
            {
                return;
            }

            Settings.XPlaneRotorIndex = profile.XPlaneRotorIndex;
        }

        public void SetPendingFfbProfile(DiyFfbPluginSettings.AircraftFfbProfile profile)
        {
            pendingFfbProfile = profile;
            hasPendingFfbProfile = profile != null;
        }

        public void ReplaceAircraftFfbProfiles(System.Collections.Generic.Dictionary<string, DiyFfbPluginSettings.AircraftFfbProfile> profiles)
        {
            if (Settings == null)
            {
                return;
            }

            Settings.AircraftFfbProfiles = profiles ?? new System.Collections.Generic.Dictionary<string, DiyFfbPluginSettings.AircraftFfbProfile>();
            BackupAircraftProfiles();
            if (!string.IsNullOrWhiteSpace(activeCarId))
            {
                ApplyAircraftProfile(activeGameId, activeCarId);
            }
        }

        /// <summary>
        /// Safety net: backs up non-empty profiles, restores from backup if profiles are empty.
        /// Protects against JSON.NET silently dropping protobuf data on deserialize.
        /// </summary>
        private void BackupOrRestoreAircraftProfiles()
        {
            if (Settings.AircraftFfbProfiles != null && Settings.AircraftFfbProfiles.Count > 0)
            {
                BackupAircraftProfiles();
            }
            else
            {
                // Profiles empty — try to restore from backup
                var backup = this.ReadCommonSettings<Dictionary<string, DiyFfbPluginSettings.AircraftFfbProfile>>(
                    "AircraftProfilesBackup", () => null);
                if (backup != null && backup.Count > 0)
                {
                    SimHub.Logging.Current.Warn(
                        $"[Profiles] AircraftFfbProfiles empty — restoring {backup.Count} profiles from backup");
                    Settings.AircraftFfbProfiles = backup;
                }
            }
        }

        private void BackupAircraftProfiles()
        {
            if (Settings.AircraftFfbProfiles != null && Settings.AircraftFfbProfiles.Count > 0)
            {
                this.SaveCommonSettings("AircraftProfilesBackup", Settings.AircraftFfbProfiles);
            }
        }

        private void AutoReconnectGateway()
        {
            if (Settings == null || !Settings.Pedal_ESPNow_auto_connect_flag)
            {
                return;
            }

            // Connected to the saved port: nothing to do, and no WMI scan every tick.
            if (ESPsync_serialPort != null && ESPsync_serialPort.IsOpen
                && string.Equals(ESPsync_serialPort.PortName, Settings.ESPNow_port, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string portName = ResolveGatewayPort();
            if (portName == null)
            {
                return;
            }

            if (ESPsync_serialPort != null && ESPsync_serialPort.IsOpen)
            {
                if (string.Equals(ESPsync_serialPort.PortName, portName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                ESPsync_serialPort.Close();
            }

            if (Interlocked.Exchange(ref gatewayReconnectBusy, 1) == 1)
            {
                return;
            }

            try
            {
                if (ESPsync_serialPort == null)
                {
                    ESPsync_serialPort = new ProtobufSerial<Message>(portName, 3000000);
                }
                else if (!string.Equals(ESPsync_serialPort.PortName, portName, StringComparison.OrdinalIgnoreCase))
                {
                    ESPsync_serialPort.PortName = portName;
                }

                ESPsync_serialPort.Open();
                if (ui != null)
                {
                    ui.NotifyGatewayPortAutoConnected(portName);
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                Interlocked.Exchange(ref gatewayReconnectBusy, 0);
            }
        }




        /// <summary>
        /// Called once after plugins startup
        /// Plugins are rebuilt at game change
        /// </summary>
        /// <param name="pluginManager"></param>
        public void Init(PluginManager pluginManager)
        {
            SimHub.Logging.Current.Info("Starting DIY active pedal plugin");

            // Load settings
            Settings = this.ReadCommonSettings<DiyFfbPluginSettings>("GeneralSettings", () => new DiyFfbPluginSettings());

            // Migrate VehicleGraphPaths to AircraftFfbProfiles.GraphPath
            MigrateVehicleGraphPaths();

            // Safety net: backup non-empty profiles, restore if empty
            BackupOrRestoreAircraftProfiles();

            // Create tiered config orchestrator
            _configOrchestrator = new TieredConfigOrchestrator(
                _functionConfigManager,
                _axisConfigManager,
                Settings,
                () => this.SaveCommonSettings("GeneralSettings", Settings),
                GetCurrentAircraftProfile,
                GetActiveGraphCategory,
                BuildProfileKey,
                () => activeGameId,
                () => activeCarId);

            // Initialize manager with stored baselines and overrides
            _configOrchestrator.InitializeManagerFromSettings();

            // Plan 10 §3.7: safety damper defaults to ENGAGED on every SimHub start.
            // Force-apply by adding all four flight functions to the disabled-output set
            // (the engaged-state field already defaults to true; the set is empty so we
            // need to populate it explicitly here).
            foreach (var fid in _flightFunctionIds)
                SetFunctionOutputDisabled(fid, true);

            // Plan 11: warn once if a settings.json carries grip bindings from before the
            // SimHub-actions migration. The dict is no longer read at runtime; users must
            // rebind in SimHub Controls.
#pragma warning disable CS0618 // GripButtonBindings is intentionally read here for the migration warning.
            if (Settings?.GripButtonBindings != null && Settings.GripButtonBindings.Count > 0)
            {
                SimHub.Logging.Current.Info(
                    $"DiyFfb: {Settings.GripButtonBindings.Count} grip binding(s) from a previous version were found in settings.json. " +
                    "Grip controls now bind through SimHub Controls — search for actions starting with 'Grip.' and rebind. " +
                    "See the INPUT tab for details.");
            }
#pragma warning restore CS0618

            // Populate function and axis configs from manager
            if (ui != null)
            {
                ui.PopulateFunctionConfigsFromBaselines();
                ui.PopulateAxisConfigsFromBaselines();
            }

            Simhub_version = (String)pluginManager.GetPropertyValue("DataCorePlugin.SimHubVersion");
            // Declare a property available in the property list, this gets evaluated "on demand" (when shown or used in formulas)
            //this.AttachDelegate("CurrentDateTime", () => DateTime.Now);
            pluginManager.AddProperty("ProfileIndex", this.GetType(), profile_index);
            pluginManager.AddProperty("SelectedPedal", this.GetType(), current_pedal);
            pluginManager.AddProperty("Action", this.GetType(), current_action);
            pluginManager.AddProperty("ABS_effect_status", this.GetType(), Settings.function_settings[Settings.function_tab_selected].ABS_enabled);
            pluginManager.AddProperty("RPM_effect_status", this.GetType(), Settings.function_settings[Settings.function_tab_selected].RPM_enabled);
            pluginManager.AddProperty("Gforce_effect_status", this.GetType(), Settings.function_settings[Settings.function_tab_selected].G_force_enabled);
            pluginManager.AddProperty("WheelSlip_effect_status", this.GetType(), Settings.function_settings[Settings.function_tab_selected].WS_enabled);
            pluginManager.AddProperty("RoadImpact_effect_status", this.GetType(), Settings.function_settings[Settings.function_tab_selected].Road_impact_enabled);
            pluginManager.AddProperty("Overlay_display", this.GetType(), overlay_display);
            pluginManager.AddProperty("Theme_color", this.GetType(), simhub_theme_color);
            pluginManager.AddProperty("debugvalue", this.GetType(), debug_value);
            pluginManager.AddProperty("pedal_position", this.GetType(), pedal_state_in_ratio);
            pluginManager.AddProperty("PedalErrorIndex", this.GetType(), PedalErrorIndex);
            pluginManager.AddProperty("PedalErrorCode", this.GetType(), PedalErrorCode);
            for (uint pedali=0; pedali < 3; pedali++)
            {
                Action_currentTime[pedali] = new DateTime();
                Action_currentTime[pedali]=DateTime.Now;
                Action_lastTime[pedali] = new DateTime();
                Action_lastTime[pedali] = DateTime.Now;
            }

            Version inputVersion = new Version(Simhub_version);
            string MSFS_Version_Above = "9.5.99";
            Version versionThreshold = new Version(MSFS_Version_Above);
            if (inputVersion > versionThreshold)
            {
                Version_Check_Simhub_MSFS = true;
            }
            else
            {
                Version_Check_Simhub_MSFS = false;
            }

            // Declare an event
            //this.AddEvent("SpeedWarning");


            // Declare an action which can be called
            /*
            this.AddAction("IncrementSpeedWarning",(a, b) =>
            {
                Settings.SpeedWarningLevel++;
                SimHub.Logging.Current.Info("Speed warning changed");
            });

            // Declare an action which can be called
            this.AddAction("DecrementSpeedWarning", (a, b) =>
            {
                Settings.SpeedWarningLevel--;
            });

            */


            this.AddAction("ChangeSlotA", (a, b) =>
            {
                profile_index = 0;
                Page_update_flag = true;
                SimHub.Logging.Current.Info("SlotA");
                current_profile = "Slot A";
                current_action= "Slot A";
            });

            this.AddAction("ChangeSlotB", (a, b) =>
            {
                
                profile_index = 1;
                Page_update_flag = true;
                SimHub.Logging.Current.Info("SlotB");
                current_profile = "Slot B";
                current_action = "Slot B";
            });

            this.AddAction("ChangeSlotC", (a, b) =>
            {
                profile_index = 2;
                Page_update_flag = true;
                SimHub.Logging.Current.Info("SlotC");
                current_profile = "Slot C";
                current_action = "Slot C";
            });

            this.AddAction("ChangeSlotD", (a, b) =>
            {
                profile_index = 3;
                Page_update_flag = true;
                SimHub.Logging.Current.Info("SlotD");
                current_profile = "Slot D";
                current_action = "Slot D";
            });
            this.AddAction("ChangeSlotE", (a, b) =>
            {
                profile_index = 4;
                Page_update_flag = true;
                SimHub.Logging.Current.Info("SlotE");
                current_profile = "Slot E";
                current_action = "Slot E";
            });
            this.AddAction("ChangeSlotF", (a, b) =>
            {
                profile_index = 5;
                Page_update_flag = true;
                SimHub.Logging.Current.Info("SlotF");
                current_profile = "Slot F";
                current_action = "Slot F";
            });
            this.AddAction("SendConfigToPedal", (a, b) =>
            {
                sendconfig_flag =1;
                SimHub.Logging.Current.Info("SendConfig");
                current_action = "Send Config to Pedal";
            });

            this.AddAction("PreviousProfile", (a, b) =>
            {
                if (profile_index == 0)
                {
                    profile_index=5;
                }
                else
                {
                    profile_index--;
                }
                

                Page_update_flag = true;
                SimHub.Logging.Current.Info("PreviousProfile");
                current_action = "Previous Profile";
            });

            this.AddAction("NextProfile", (a, b) =>
            {
                profile_index++;
                if (profile_index > 5)
                {
                    profile_index = 0;
                }
                Page_update_flag = true;
                SimHub.Logging.Current.Info("NextProfile");
                current_action = "Next Profile";
            });
            this.AddAction("NextPedal", (a, b) =>
            {
                Settings.function_tab_selected++;
                if (Settings.function_tab_selected >= 4)
                {
                    Settings.function_tab_selected = 0;
                }
                Page_update_flag = true;
                SimHub.Logging.Current.Info("NextPedal");
                current_action = "Next Pedal";
            });
            this.AddAction("PreviousPedal", (a, b) =>
            {
                
                if (Settings.function_tab_selected == 0)
                {
                    Settings.function_tab_selected = 3;
                }
                else
                {
                    Settings.function_tab_selected--;
                }
                Page_update_flag = true;
                SimHub.Logging.Current.Info("PreviousPedal");
                current_action = "Previous Pedal";
            });
            // Plan 10 §3.7: safety damper toggle. Single global action that flips
            // frame-emission gating for all four flight functions together. Engaged
            // = frames gated, ESP32 falls back to FlightControlConfig.damping (rest
            // value). Disengaged = frames flow, per-frame k_damper authoritative.
            // Registered as an input mapping (not an action) so the user doesn't have
            // to pick the right Input mode in SimHub Controls — AddInputMapping enforces
            // press/release semantics. SimHub fires inputPressed repeatedly while the
            // bound control is held (same "during" cadence as AddAction's During mode),
            // so a naive toggle inside inputPressed flips rapidly during a hold. Track
            // held state explicitly so the toggle fires exactly once per press-down
            // edge regardless of how long the user holds the button.
            // SimHub treats a held control as a stream of release+press tick events.
            // The first press fires inputPressed only; ~500 ms later (typical key
            // auto-repeat delay) SimHub starts firing inputReleased+inputPressed
            // pairs every ~30 ms. To detect a real new user press without false-
            // toggling on the first auto-repeat, track the most recent event of
            // EITHER kind: inputReleased advances the timestamp so the auto-repeat
            // inputPressed that follows it sees a near-zero gap and is suppressed.
            // A real user re-press always has at least one tick of silence (>75 ms)
            // separating it from any prior event.
            DateTime lastSafetyDamperEventUtc = DateTime.MinValue;
            const int SafetyDamperGapMs = 75;
            this.AddInputMapping(
                "FlightControl.SafetyDamperToggle",
                inputPressed: (a, b) =>
                {
                    var now = DateTime.UtcNow;
                    bool isNewPress = (now - lastSafetyDamperEventUtc).TotalMilliseconds > SafetyDamperGapMs;
                    lastSafetyDamperEventUtc = now;
                    if (!isNewPress) return;
                    bool nowEngaged = !_flightSafetyDamperEngaged;
                    SetFlightSafetyDamperEngaged(nowEngaged);
                    SimHub.Logging.Current.Info("FlightControl Safety Damper: " + (nowEngaged ? "ENGAGED" : "Disengaged"));
                    current_action = "Safety Damper " + (nowEngaged ? "On" : "Off");
                },
                inputReleased: (a, b) =>
                {
                    lastSafetyDamperEventUtc = DateTime.UtcNow;
                });
            // Plan 11: grip-button signals are SimHub input mappings (replacing the
            // custom DirectInput path). AddInputMapping enforces held-state semantics
            // — inputPressed/inputReleased fire on the press and release edges of the
            // bound control, so the plugin keeps a clean held bool per signal. The
            // graph reads the same 0.0/1.0 booleans it did under the previous polling
            // path; downstream nodes (trim integration, edge detection on TrimReset)
            // are unchanged.
            foreach (var gripSignal in GraphSignalCatalog.GripSignalNames)
            {
                string captured = gripSignal;
                this.AddInputMapping(
                    captured,
                    inputPressed: (a, b) => { _gripHeld[captured] = true; },
                    inputReleased: (a, b) => { _gripHeld[captured] = false; });
            }
            this.AddAction("ABStoggle", (a, b) =>
            {
                if (!Settings.function_settings[Settings.function_tab_selected].ABS_enabled)
                {
                    Settings.function_settings[Settings.function_tab_selected].ABS_enabled = true;
                    SimHub.Logging.Current.Info("ABS on");
                    current_action = "ABS On";
                }
                else
                {
                    Settings.function_settings[Settings.function_tab_selected].ABS_enabled = false;
                    SimHub.Logging.Current.Info("ABS off");
                    current_action = "ABS Off";
                }
                Page_update_flag = true;
            });
            this.AddAction("RPMtoggle", (a, b) =>
            {
                if (!Settings.function_settings[Settings.function_tab_selected].RPM_enabled)
                {
                    Settings.function_settings[Settings.function_tab_selected].RPM_enabled = true;
                    SimHub.Logging.Current.Info("RPM on");
                    current_action = "RPM On";
                }
                else
                {
                    Settings.function_settings[Settings.function_tab_selected].RPM_enabled = false;
                    SimHub.Logging.Current.Info("RPM off");
                    current_action = "RPM Off";
                }
                Page_update_flag = true;
            });
            this.AddAction("Gforce_toggle", (a, b) =>
            {
                if (Settings.function_tab_selected == 1)
                {
                    if (!Settings.function_settings[Settings.function_tab_selected].G_force_enabled)
                    {
                        Settings.function_settings[Settings.function_tab_selected].G_force_enabled = true;
                        SimHub.Logging.Current.Info("Gforce on");
                        current_action = "Gforce On";
                    }
                    else
                    {
                        Settings.function_settings[Settings.function_tab_selected].G_force_enabled = false;
                        SimHub.Logging.Current.Info("Gforce off");
                        current_action = "Gforce Off";
                    }
                    Page_update_flag = true;
                }

            });
            this.AddAction("WheelSliptoggle", (a, b) =>
            {
                if (!Settings.function_settings[Settings.function_tab_selected].WS_enabled)
                {
                    Settings.function_settings[Settings.function_tab_selected].WS_enabled = true;
                    SimHub.Logging.Current.Info("WheelSlip on");
                    current_action = "Wheel Slip On";
                }
                else
                {
                    Settings.function_settings[Settings.function_tab_selected].WS_enabled = false;
                    SimHub.Logging.Current.Info("WheelSlip off");
                    current_action = "Wheel Slip Off";
                }
                Page_update_flag = true;
            });

            this.AddAction("RoadImpacttoggle", (a, b) =>
            {
                if (!Settings.function_settings[Settings.function_tab_selected].Road_impact_enabled)
                {
                    Settings.function_settings[Settings.function_tab_selected].Road_impact_enabled = true;
                    SimHub.Logging.Current.Info("RoadImpact on");
                    current_action = "Wheel Slip On";
                }
                else
                {
                    Settings.function_settings[Settings.function_tab_selected].Road_impact_enabled = false;
                    SimHub.Logging.Current.Info("RoadImpact off");
                    current_action = "RoadImpact Off";
                }
                Page_update_flag = true;
            });

            this.AddAction("OverlayToggle", (a, b) =>
            {
                Page_update_flag = true;
                SimHub.Logging.Current.Info("OverlayToggle");               
                current_action = "OverlayToggle";
                if (overlay_display == 1)
                {
                    overlay_display = 0;
                }
                else
                {
                    overlay_display = 1;
                }
            });
            //Settings.selectedJsonIndexLast[0]
            //SimHub.Logging.Current.Info("Diy active pedas plugin - Test 1");
            //SimHub.Logging.Current.Info("Diy active pedas plugin - COM port: " + Settings.selectedComPortNames[0]);




            // get WPF handler
            //wpfHandler = (DiyFfbPluginUI)GetWPFSettingsControl(pluginManager);

            //if (wpfHandler.)
            {
                // prepare serial port interfaces
                for (uint pedalIdx = 0; pedalIdx < 3; pedalIdx++)
                {

                    _serialPort[pedalIdx].Handshake = Handshake.None;
                    _serialPort[pedalIdx].RtsEnable = false;
                    _serialPort[pedalIdx].DtrEnable = false;


                    if (_serialPort[pedalIdx].IsOpen)
                    {
                        System.Threading.Thread.Sleep(300);
                    }


                    try
                    {
                        _serialPort[pedalIdx].PortName = Settings.axis_settings[pedalIdx].com_port_name; ;
                    }
                    catch (Exception caughtEx)
                    {
                    }
                    
                    ////try connect back to com port
                    //if (Settings.axis_settings[pedalIdx].auto_connect_flag == 1)
                    //{

                    //    if (Settings.connect_status[pedalIdx] == 1)
                    //    {
                    //        //_serialPort[pedalIdx].PortName = Settings.selectedComPortNames[pedalIdx];
                    //        //SerialPort.GetPortNames
                    //        if (PortExists(_serialPort[pedalIdx].PortName))
                    //        {
                    //            if (_serialPort[pedalIdx].IsOpen == false)
                    //            {
                    //                //if (wpfHandle != null)
                    //                //{
                    //                //    wpfHandle.openSerialAndAddReadCallback(pedalIdx);
                    //                //}

                    //                connectSerialPort[pedalIdx] = true;
                    //            }
                    //            else
                    //            {
                    //                //if (wpfHandle != null)
                    //                //{
                    //                //    wpfHandle.closeSerialAndStopReadCallback(pedalIdx);
                    //                //}
                    //                //ConnectToPedal.IsChecked = false;
                    //                //TextBox_debugOutput.Text = "Serialport already open, close it";
                    //                //Settings.connect_status[pedalIdx] = 0;
                    //                connectSerialPort[pedalIdx] = false;
                    //            }


                    //        }
                    //        else
                    //        {
                    //            //Settings.connect_status[pedalIdx] = 0;
                    //            connectSerialPort[pedalIdx] = false;
                    //        }
                    //    }
                    //    else
                    //    {
                    //        //Settings.connect_status[pedalIdx] = 0;
                    //        connectSerialPort[pedalIdx] = false;
                    //    }

                    //}
                    

                }

            }

            StartGatewayAutoReconnect();
            StartXPlaneUdpReceiver();
            // Plan 19/23: single MSFS transport — the pure-C# in-process
            // SimConnect client.
            StartMsfsClient();

        }
    }

    /// <summary>
    /// Event arguments for graph parameter changes
    /// </summary>
    public class GraphParamChangedEventArgs : EventArgs
    {
        public string ParamName { get; }
        public double Value { get; }

        public GraphParamChangedEventArgs(string paramName, double value)
        {
            ParamName = paramName;
            Value = value;
        }
    }

    /// <summary>
    /// Event args for override field changes.
    /// </summary>
    public class OverrideFieldChangedEventArgs : EventArgs
    {
        public int FunctionId { get; }
        public string FieldPath { get; }

        public OverrideFieldChangedEventArgs(int functionId, string fieldPath)
        {
            FunctionId = functionId;
            FieldPath = fieldPath;
        }
    }
}
