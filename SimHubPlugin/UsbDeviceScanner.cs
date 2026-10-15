using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;

namespace DiyFfb
{
    public enum UsbPortRole
    {
        Unknown,
        Gateway,
        Axis,
        Grip,
        EspRom,
        UsbUart,
    }

    /// <summary>
    /// A COM port that is present right now, classified by its USB VID/PID.
    /// Identities are assigned by the firmware at runtime
    /// (CommManager::setup_joystick): gateway N = 303B:8210+N, axis N = 303B:8220+N.
    /// </summary>
    public sealed class UsbPort
    {
        public string Com { get; }
        public int Vid { get; }
        public int Pid { get; }
        public UsbPortRole Role { get; }

        /// <summary>Gateway/axis index taken from the PID; -1 for other roles.</summary>
        public int Index { get; }

        public string Label { get; }

        public int ComNumber
        {
            get
            {
                int n;
                return Com.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(Com.Substring(3), out n) ? n : int.MaxValue;
            }
        }

        public UsbPort(string com, int vid = -1, int pid = -1)
        {
            Com = com;
            Vid = vid;
            Pid = pid;

            int index;
            Role = Classify(vid, pid, out index);
            Index = index;
            Label = BuildLabel(com, Role, index, vid);
        }

        public override string ToString() => Label;

        private static UsbPortRole Classify(int vid, int pid, out int index)
        {
            index = -1;
            if (vid == 0x303B)
            {
                if (pid > 0x8210 && pid < 0x8220) { index = pid - 0x8210; return UsbPortRole.Gateway; }
                if (pid > 0x8220 && pid < 0x8230) { index = pid - 0x8220; return UsbPortRole.Axis; }
                if (pid == 0x8230) return UsbPortRole.Grip;
                return UsbPortRole.Unknown;
            }
            if (vid == 0x303A)
            {
                // 1001 = S3 USB-Serial/JTAG (ROM download mode), 0009 = S3 ROM over USB-OTG.
                // Any other 303A PID is a stock Espressif app, not ours.
                return pid == 0x1001 || pid == 0x0009 ? UsbPortRole.EspRom : UsbPortRole.Unknown;
            }
            // WCH (CH34x), Silicon Labs (CP210x), FTDI. On PCB 13 the CH343 is the
            // UART0 console; on older boards a bridge like this is the protocol port.
            if (vid == 0x1A86 || vid == 0x10C4 || vid == 0x0403)
            {
                return UsbPortRole.UsbUart;
            }
            return UsbPortRole.Unknown;
        }

        private static string BuildLabel(string com, UsbPortRole role, int index, int vid)
        {
            switch (role)
            {
                case UsbPortRole.Gateway: return $"{com}  Gateway {index}";
                case UsbPortRole.Axis: return $"{com}  Axis {index}";
                case UsbPortRole.Grip: return $"{com}  Grip";
                case UsbPortRole.EspRom: return $"{com}  ESP bootloader";
                case UsbPortRole.UsbUart:
                    return vid == 0x1A86 ? $"{com}  CH34x" : vid == 0x10C4 ? $"{com}  CP210x" : $"{com}  FTDI";
                default: return com;
            }
        }
    }

    /// <summary>
    /// Enumerates present COM ports via WMI (Win32_PnPEntity). Unlike
    /// SerialPort.GetPortNames(), which reads the registry, this does not
    /// return ghost ports of unplugged devices.
    /// </summary>
    public static class UsbDeviceScanner
    {
        private const int CacheMs = 1000;

        private static readonly Regex ComRegex = new Regex(@"\((COM\d+)\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex VidRegex = new Regex(@"VID_([0-9A-F]{4})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex PidRegex = new Regex(@"PID_([0-9A-F]{4})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly object CacheLock = new object();
        private static List<UsbPort> cachedPorts = new List<UsbPort>();
        private static DateTime cachedAt = DateTime.MinValue;

        /// <summary>Present ports, gateways first, then by COM number.</summary>
        public static IReadOnlyList<UsbPort> GetPorts(bool forceRefresh = false)
        {
            lock (CacheLock)
            {
                if (!forceRefresh && (DateTime.UtcNow - cachedAt).TotalMilliseconds < CacheMs)
                {
                    return cachedPorts;
                }

                cachedPorts = Scan()
                    .OrderBy(p => p.Role == UsbPortRole.Gateway ? 0 : 1)
                    .ThenBy(p => p.ComNumber)
                    .ToList();
                cachedAt = DateTime.UtcNow;
                return cachedPorts;
            }
        }

        public static bool IsPresent(string com)
        {
            return GetPorts().Any(p => string.Equals(p.Com, com, StringComparison.OrdinalIgnoreCase));
        }

        public static UsbPort FindGateway(int index)
        {
            return GetPorts().FirstOrDefault(p => p.Role == UsbPortRole.Gateway && p.Index == index);
        }

        private static List<UsbPort> Scan()
        {
            try
            {
                var ports = new Dictionary<string, UsbPort>(StringComparer.OrdinalIgnoreCase);
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT Name, DeviceID FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'"))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementBaseObject device in results)
                    {
                        using (device)
                        {
                            Match com = ComRegex.Match(device["Name"] as string ?? string.Empty);
                            if (!com.Success)
                            {
                                continue;
                            }

                            string deviceId = device["DeviceID"] as string ?? string.Empty;
                            string name = com.Groups[1].Value.ToUpperInvariant();
                            ports[name] = new UsbPort(name, ParseHex(VidRegex, deviceId), ParseHex(PidRegex, deviceId));
                        }
                    }
                }
                return ports.Values.ToList();
            }
            catch (Exception)
            {
                // WMI unavailable (service stopped, broken repository): fall back
                // to the registry list, unclassified and possibly with ghosts.
                return SerialPort.GetPortNames()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(name => new UsbPort(name))
                    .ToList();
            }
        }

        private static int ParseHex(Regex regex, string text)
        {
            Match m = regex.Match(text);
            return m.Success ? Convert.ToInt32(m.Groups[1].Value, 16) : -1;
        }
    }
}
