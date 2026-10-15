using System.Collections.Generic;
using System.Linq.Expressions;
using System.Windows.Media.Converters;
using DiyFfb.TieredConfig;

namespace DiyFfb
{
    /// <summary>
    /// Settings class, make sure it can be correctly serialized using JSON.net
    /// </summary>

    /// <summary>
    /// Plan 34: per-profile override of a single MSFS SimConnect variable binding.
    /// Each field is nullable/optional; null means "inherit the graph node's value".
    /// Read bindings only use SimVar/Unit; write bindings also use the range map.
    /// Keyed by alias in AircraftFfbProfile.MsfsReadVarOverrides / MsfsWriteVarOverrides.
    /// </summary>
    public sealed class MsfsVarBindingOverride
    {
        public string SimVar;          // null = inherit graph node's SimVar
        public string Unit;            // null = inherit
        public double? InMin, InMax;   // write-only; null = inherit
        public double? OutMin, OutMax; // write-only; null = inherit

        public bool IsEmpty => SimVar == null && Unit == null
            && !InMin.HasValue && !InMax.HasValue && !OutMin.HasValue && !OutMax.HasValue;

        public MsfsVarBindingOverride Clone() => new MsfsVarBindingOverride
        {
            SimVar = SimVar,
            Unit = Unit,
            InMin = InMin,
            InMax = InMax,
            OutMin = OutMin,
            OutMax = OutMax,
        };
    }

    /// <summary>
    /// Plan 34: the resolved MSFS binding after overlaying a profile override on the
    /// graph node defaults (first non-null wins). Pure value type — no SimConnect.
    /// </summary>
    public struct ResolvedMsfsBinding
    {
        public string SimVar;
        public string Unit;
        public double InMin, InMax, OutMin, OutMax;
    }

    /// <summary>
    /// Plan 34: pure overlay resolver (Baseline = graph node value, Profile = override).
    /// Factored out so it is unit-testable without SimConnect (see GraphTestRunner).
    /// </summary>
    public static class MsfsBindingResolver
    {
        public static ResolvedMsfsBinding Resolve(
            string defSimVar, string defUnit,
            double defInMin, double defInMax, double defOutMin, double defOutMax,
            MsfsVarBindingOverride ov)
        {
            return new ResolvedMsfsBinding
            {
                SimVar = ov?.SimVar ?? defSimVar,
                Unit = ov?.Unit ?? defUnit,
                InMin = ov?.InMin ?? defInMin,
                InMax = ov?.InMax ?? defInMax,
                OutMin = ov?.OutMin ?? defOutMin,
                OutMax = ov?.OutMax ?? defOutMax,
            };
        }

        /// <summary>Linear map In*→Out* with clamp to the output range.</summary>
        public static double MapLinearClamped(double raw, double inMin, double inMax, double outMin, double outMax)
        {
            double span = inMax - inMin;
            double t = System.Math.Abs(span) < 1e-12 ? 0.0 : (raw - inMin) / span;
            double mapped = outMin + t * (outMax - outMin);
            double lo = System.Math.Min(outMin, outMax);
            double hi = System.Math.Max(outMin, outMax);
            if (mapped < lo) mapped = lo;
            else if (mapped > hi) mapped = hi;
            return mapped;
        }

        /// <summary>The default 0..1 → 0..1 map, treated as passthrough on reads.</summary>
        public static bool IsIdentityRange(double inMin, double inMax, double outMin, double outMax)
            => inMin == 0.0 && inMax == 1.0 && outMin == 0.0 && outMax == 1.0;

        /// <summary>
        /// Normalize a raw read value by its range map. Passthrough when the map is the
        /// identity default so raw-unit read ports (RPM, altitude, …) are never clamped.
        /// </summary>
        public static double NormalizeRead(double raw, double inMin, double inMax, double outMin, double outMax)
            => IsIdentityRange(inMin, inMax, outMin, outMax)
                ? raw
                : MapLinearClamped(raw, inMin, inMax, outMin, outMax);
    }

    /// <summary>
    /// Plan 34: one enumerated MSFS var binding as declared by the active graph,
    /// with the graph-default values the UI shows as greyed placeholder baseline.
    /// </summary>
    public sealed class MsfsBindingInfo
    {
        public string Alias;          // read: port SignalSuffix; write: port Name
        public bool IsWrite;
        public string DefaultSimVar;
        public string DefaultUnit;
        public double DefaultInMin, DefaultInMax, DefaultOutMin, DefaultOutMax;
    }

    /// <summary>Plan 34: enumerated MSFS bindings grouped by direction.</summary>
    public sealed class MsfsBindingCatalog
    {
        public List<MsfsBindingInfo> Reads = new List<MsfsBindingInfo>();
        public List<MsfsBindingInfo> Writes = new List<MsfsBindingInfo>();
    }

    public class DiyFfbPluginSettings
    {
        /// <summary>
        /// Snapshot of a graph parameter's definition at time of last review.
        /// Used to detect when defaults or ranges change.
        /// </summary>
        public class ParamSnapshot
        {
            public double DefaultValue;
            public double Min;
            public double Max;
        }

        public class AircraftFfbProfile
        {
            public string GraphPath;
            public int XPlaneRotorIndex = -1;
            public Dictionary<string, double> GraphParamValues = new Dictionary<string, double>();

            /// <summary>
            /// Hash of graph + includes content at last review.
            /// Null means profile predates hash tracking (will initialize on next load).
            /// </summary>
            public string LastReviewedGraphHash;

            /// <summary>
            /// Snapshot of param definitions at last review.
            /// Key: param name, Value: default/min/max at that time.
            /// </summary>
            public Dictionary<string, ParamSnapshot> LastReviewedParamSnapshots = new Dictionary<string, ParamSnapshot>();

            /// <summary>
            /// Vehicle-specific function config overrides.
            /// Key: function ID, Value: delta overlay for that function.
            /// </summary>
            public Dictionary<int, FunctionConfigOverrides> FunctionOverrides = new Dictionary<int, FunctionConfigOverrides>();

            /// <summary>
            /// Which functions are active for this profile.
            /// On profile load, only these functions have their overrides applied.
            /// </summary>
            public HashSet<int> ActiveFunctionIds = new HashSet<int>();

            /// <summary>
            /// Plan 34: per-profile MSFS read-variable (MsfsVarDef) binding overrides.
            /// Keyed by alias (port SignalSuffix). Null/absent entry = inherit graph node.
            /// Kept separate from writes so a read alias and a write alias that share a
            /// string never collide.
            /// </summary>
            public Dictionary<string, MsfsVarBindingOverride> MsfsReadVarOverrides = new Dictionary<string, MsfsVarBindingOverride>();

            /// <summary>
            /// Plan 34: per-profile MSFS write-variable (MsfsVarOut) binding overrides.
            /// Keyed by alias (port Name). Overrides SimVar, Unit, and the In/Out range map.
            /// </summary>
            public Dictionary<string, MsfsVarBindingOverride> MsfsWriteVarOverrides = new Dictionary<string, MsfsVarBindingOverride>();
        }

        /// <summary>
        /// Wrapper for exporting/importing aircraft FFB profiles.
        /// Includes the profile data plus metadata about which graph it was created for.
        /// </summary>
        public class ExportedProfile
        {
            public int Version = 1;
            public string ProfileKey;
            public string GraphPath;
            public string ExportedAt;
            public AircraftFfbProfile Profile;
        }

        public class AxisSettings
        {
            public string com_port_name = "COM1";
            public bool auto_connect = false;
            public bool RTSDTR_False = true;
            public bool USING_ESP32S3 = true;
            public bool via_gateway = false;
        }

        public class FunctionSettings
        {
            public bool ABS_enabled = false;
            public bool RPM_enabled = false;
            public bool WS_enabled = false;
            public bool G_force_enabled = false;
            public bool Road_impact_enabled = false;
            public bool CV1_enabled = false;
            public bool CV2_enabled = false;
            public int CV1_trigger_level = 0;
            public string CV1_binding = "";
            public int CV2_trigger_level = 0;
            public string CV2_binding = "";
            public int action_interval = 30;
            public bool[,] effect_status_profiles = new bool[6, 8] { { false, false, false, false, false, false, false, false }, { false, false, false, false, false, false, false, false }, { false, false, false, false, false, false, false, false } , { false, false, false, false, false, false, false, false }, { false, false, false, false, false, false, false, false }, { false, false, false, false, false, false, false, false } };
        }

        public DiyFfbPluginSettings()
        {
            for (int i = 0; i < 8; i++)
            {
                axis_settings[i] = new AxisSettings();
                function_settings[i] = new FunctionSettings();
            }
        }

        //should change the variable name after array size change to updtae the config setting
        public AxisSettings[] axis_settings = new AxisSettings[8];
        public FunctionSettings[] function_settings = new FunctionSettings[8];
        public uint axis_tab_selected = 0;
        public uint function_tab_selected = 0;
        public int XPlaneRotorIndex = -1;
        // Obsolete: kept for migration. Use AircraftFfbProfiles[key].GraphPath instead.
        public Dictionary<string, string> VehicleGraphPaths = new Dictionary<string, string>();
        // Obsolete: game-level graph fallback removed. Profile Browser handles new vehicles.
        public Dictionary<string, string> GameGraphPaths = new Dictionary<string, string>();

        public string[] selectedJsonFileNames = { "1", "2", "3" };
        public int reading_config = 0;
        public uint RPM_effect_type = 0;
        public int vjoy_output_flag = 0;
        public uint vjoy_order = 1;
        public string[,] Pedal_file_string = new string[6, 3] { { "NA", "NA", "NA" }, { "NA", "NA", "NA" }, { "NA", "NA", "NA" }, { "NA", "NA", "NA" }, { "NA", "NA", "NA" }, { "NA", "NA", "NA" } };
        public int[,] file_enable_check = new int[6, 3] { { 0, 0, 0 }, { 0, 0, 0 }, { 0, 0, 0 }, { 0, 0, 0 }, { 0, 0, 0 }, { 0, 0, 0 } };
        public string WSeffect_bind = "";
        public string Road_impact_bind = "";
        public int WS_trigger = 30;
        public string[] Profile_name = new string[6] { "", "", "", "", "", "" };
        public double kinematicDiagram_zeroPos_OX = 100;
        public double kinematicDiagram_zeroPos_OY = 20;
        public double kinematicDiagram_zeroPos_scale = 1.5;
        public string ESPNow_port = "";
        // Gateway identity from its USB PID (0x8210 + N). When >= 0, reconnect
        // follows this gateway across COM renumbering; ESPNow_port is kept in sync.
        // -1 = plain COM name (legacy boards / non-DIY-FFB ports).
        public int GatewayUsbIndex = -1;
        public bool Pedal_ESPNow_auto_connect_flag = false;
        public bool Serial_auto_clean = false; //clean serial monitor
        public bool Serial_auto_clean_bridge = false; //clean serial monitor bridge
        public bool Using_CDC_bridge = false;
        public bool Rudder_RPM_effect_b = false;
        public bool Rudder_ACC_effect_b = false;
        public bool Rudder_ACC_WindForce = false;
        public bool advanced_b = false;
        public string SSID_string = "";
        public string PASS_string = "";
        public string OtaCustomUrl = "";
        public bool OtaUseCustomUrl = false;
        public bool OtaUseLocalSource = false;
        public string OtaLocalFfbotaPath = "";
        public int OtaLocalPort = 8000;
        public bool XPlaneUdpEnabled = true;
        public int XPlaneUdpPort = 27015;
        public Dictionary<string, AircraftFfbProfile> AircraftFfbProfiles = new Dictionary<string, AircraftFfbProfile>();

        // Tiered Config Override System
        /// <summary>
        /// Current user profile name. Defaults to Windows username.
        /// User preferences are stored per-profile to support multiple users on shared rigs.
        /// </summary>
        public string CurrentUserProfile = System.Environment.UserName;

        /// <summary>
        /// Per-user preferences keyed by user profile name.
        /// Contains function config overrides that follow the user across vehicles.
        /// </summary>
        public Dictionary<string, UserPreferences> UserPreferencesProfiles = new Dictionary<string, UserPreferences>();

        /// <summary>
        /// Per-function axis parameter overrides (function_id → axis_id → overrides).
        /// Defined globally; only applied when function is active for current profile.
        /// </summary>
        public Dictionary<int, Dictionary<int, AxisParameterOverrides>> FunctionAxisOverrides = new Dictionary<int, Dictionary<int, AxisParameterOverrides>>();

        /// <summary>
        /// Function baselines (complete FunctionConfig snapshots) stored in SimHub.
        /// These serve as the "Baseline layer" for tiered config merge operations.
        /// Updated only by: importing compound configs, or explicit "Save to Baseline" action.
        /// Key: function ID, Value: complete FunctionConfig snapshot.
        /// </summary>
        // Store as JSON strings because FunctionConfig (protobuf) doesn't serialize correctly with JSON.NET
        public Dictionary<int, string> FunctionBaselines = new Dictionary<int, string>();

        /// <summary>
        /// Axis baselines (complete AxisConfig snapshots) stored in SimHub.
        /// Prevents ESP32 reconnect from overwriting axis geometry with overridden values.
        /// Key: axis ID, Value: complete AxisConfig snapshot as JSON.
        /// </summary>
        public Dictionary<int, string> AxisBaselines = new Dictionary<int, string>();

        /// <summary>
        /// DEPRECATED (plan 11): grip signals now bind through SimHub's standard
        /// control panel as named actions (Grip.TrimHat.Up, etc.). Retained only so
        /// existing settings.json files from older plugin versions still load. The
        /// runtime ignores this dict; users must rebind once via SimHub Controls.
        /// Slated for removal one minor release after plan 11 ships.
        /// </summary>
        [System.Obsolete("Replaced by SimHub control bindings — see plan 11. Kept for one release for settings deserialization.")]
        public Dictionary<string, ButtonBinding> GripButtonBindings = new Dictionary<string, ButtonBinding>();

        /// <summary>
        /// Persisted graph state (trim accumulators, sample-holds, etc.) per vehicle.
        /// Key: vehicle profile key (gameId/carId), Value: flattened state snapshot.
        /// Survives SimHub restarts so trim is preserved across sessions.
        /// </summary>
        public Dictionary<string, Dictionary<string, double[]>> GraphStateSnapshots
            = new Dictionary<string, Dictionary<string, double[]>>();
    }


}
