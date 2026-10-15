using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Threading;

namespace DiyFfb.Flasher
{
    public sealed class FlashOptions
    {
        /// <summary>Also wipe NVS (the stored configuration).</summary>
        public bool EraseNvs { get; set; }

        /// <summary>Baud rate after SYNC on a USB-UART bridge. Irrelevant over native USB.</summary>
        public int UartBaud { get; set; } = 921600;
    }

    /// <summary>
    /// Flashes an ESP32-S3 through its ROM bootloader. Plan 29 §4.2:
    /// enter ROM -> SYNC -> check chip -> disable watchdogs (USB-Serial/JTAG)
    /// -> SPI attach/params -> write+verify segments -> clear forced download
    /// boot -> hard reset.
    /// </summary>
    public sealed class Esp32S3Flasher
    {
        private const uint ChipIdEsp32S3 = 9;

        // esptool targets/esp32s3.py
        private const uint RtcCntlWdtConfig0Reg = 0x60008098;
        private const uint RtcCntlWdtWprotectReg = 0x600080B0;
        private const uint RtcCntlWdtWkey = 0x50D83AA1;
        private const uint RtcCntlSwdConfReg = 0x600080B4;
        private const uint RtcCntlSwdWprotectReg = 0x600080B8;
        private const uint RtcCntlSwdWkey = 0x8F1D312A;
        private const uint RtcCntlSwdAutoFeedEn = 1u << 31;
        private const uint RtcCntlOption1Reg = 0x6000812C;
        private const uint RtcCntlForceDownloadBootMask = 0x1;

        // Default NVS location, used only when erasing without an image at hand.
        public const uint DefaultNvsOffset = 0x9000;
        public const uint DefaultNvsSize = 0x5000;

        private enum Transport { UsbJtag, UsbOtg, Uart }

        private readonly Action<string> log;

        /// <summary>(bytes done, bytes total) across all segments.</summary>
        public Action<long, long> Progress { get; set; }

        public Esp32S3Flasher(Action<string> log)
        {
            this.log = log ?? (_ => { });
        }

        /// <summary>
        /// Flashes <paramref name="image"/> through <paramref name="portName"/>.
        /// Returns the COM port the board came back on as the same DIY-FFB device,
        /// or null if it was flashed through a bootloader/UART port (identity
        /// unknown) or didn't re-enumerate in time.
        /// </summary>
        public string Flash(string portName, FirmwareImage image, FlashOptions options)
        {
            IReadOnlyList<FlashSegment> segments = image.GetSegments(options.EraseNvs);
            log(options.EraseNvs
                ? $"Writing full image ({image.Data.Length} bytes); stored configuration will be erased."
                : $"Writing {segments.Count} segments, keeping NVS 0x{image.NvsOffset:X}+0x{image.NvsSize:X}.");
            // Keep-config mode proves NVS survived: MD5 before the first write and after the last.
            FlashSegment preserve = options.EraseNvs ? null : new FlashSegment(image.NvsOffset, new byte[image.NvsSize]);
            return Run(portName, image.FlashSizeBytes, options, segments, preserve);
        }

        /// <summary>Wipes NVS only (the stored configuration), leaving the firmware intact.</summary>
        public string EraseNvs(string portName, uint nvsOffset = DefaultNvsOffset, uint nvsSize = DefaultNvsSize)
        {
            var blank = new byte[nvsSize];
            for (int i = 0; i < blank.Length; i++)
            {
                blank[i] = 0xFF;
            }
            log($"Erasing NVS 0x{nvsOffset:X}+0x{nvsSize:X}.");
            // Flash size only bounds the ROM's address checks; NVS is far below 1 MB.
            return Run(portName, 0, new FlashOptions(), new[] { new FlashSegment(nvsOffset, blank) }, null);
        }

        private const int Attempts = 5;
        private const int RetryBackoffMs = 3000;
        private const int ReappearTimeoutMs = 15000;

        private string Run(string portName, uint flashSize, FlashOptions options, IReadOnlyList<FlashSegment> segments,
                           FlashSegment preserve)
        {
            UsbPort target = FindPort(p => string.Equals(p.Com, portName, StringComparison.OrdinalIgnoreCase));
            if (target == null)
            {
                throw new EspRomException($"{portName} is not present.");
            }

            string nvsBaseline = null;
            for (int attempt = 1; ; attempt++)
            {
                bool lastAttempt = attempt == Attempts;
                try
                {
                    Session(target, attempt > 1, lastAttempt, flashSize, options, segments, preserve, ref nvsBaseline);
                    break;
                }
                catch (Exception ex) when (IsConnectionError(ex))
                {
                    if (lastAttempt)
                    {
                        throw new EspRomConnectionException(
                            $"{ex.Message} Gave up after {Attempts} attempts. Hold BOOT, tap RST or replug the board, and flash again.");
                    }
                    log($"Attempt {attempt} failed: {ex.Message} Retrying...");
                    Thread.Sleep(RetryBackoffMs);
                }
            }

            return WaitForAppPort(target);
        }

        private static bool IsConnectionError(Exception ex)
        {
            // InvalidOperationException: SerialPort reports "port is closed" when
            // the device vanished under an open handle.
            return ex is EspRomConnectionException || ex is TimeoutException || ex is IOException
                || ex is UnauthorizedAccessException || ex is InvalidOperationException;
        }

        private void Session(UsbPort target, bool retry, bool lastAttempt, uint flashSize, FlashOptions options,
                             IReadOnlyList<FlashSegment> segments, FlashSegment preserve, ref string nvsBaseline)
        {
            Transport transport;
            bool resetFirst;
            string romPort = EnterBootloader(target, retry, out transport, out resetFirst);

            using (var rom = new EspRomLoader(romPort) { Log = log })
            {
                rom.Open();
                if (resetFirst)
                {
                    if (transport == Transport.Uart)
                    {
                        ClassicReset(rom);
                    }
                    else
                    {
                        UsbJtagReset(rom);
                    }
                }

                log($"Connecting to bootloader on {romPort}...");
                rom.Sync();

                uint securityFlags;
                uint chipId = rom.GetChipId(out securityFlags);
                if (chipId != ChipIdEsp32S3)
                {
                    throw new EspRomException($"Connected chip has id {chipId}, expected ESP32-S3 ({ChipIdEsp32S3}).");
                }
                if ((securityFlags & 0x4) != 0)
                {
                    throw new EspRomException("Secure download mode is enabled on this chip; flashing is not supported.");
                }
                log("ESP32-S3 bootloader connected.");

                if (transport == Transport.UsbJtag)
                {
                    // ROM download mode over USB-Serial/JTAG doesn't reset the RTC
                    // WDT and SWD; left running they reset the chip mid-flash.
                    DisableWatchdogs(rom);
                }

                rom.SpiAttach();
                if (flashSize > 0)
                {
                    rom.SpiSetParams(flashSize);
                }

                // The last attempt stays at the ROM's 115200 in case the bridge
                // or cabling can't hold the higher rate.
                if (transport == Transport.Uart && options.UartBaud > 115200 && !lastAttempt)
                {
                    rom.ChangeBaud(options.UartBaud);
                    log($"Baud rate {options.UartBaud}.");
                }

                if (preserve != null && nvsBaseline == null)
                {
                    nvsBaseline = rom.FlashMd5(preserve.Offset, (uint)preserve.Data.Length);
                    log($"NVS 0x{preserve.Offset:X}+0x{preserve.Data.Length:X} MD5 before: {nvsBaseline}");
                }

                long total = segments.Sum(s => (long)s.Data.Length);
                long done = 0;
                foreach (FlashSegment segment in segments)
                {
                    log($"Writing {segment}...");
                    long before = done;
                    int length = segment.Data.Length;
                    rom.WriteFlash(segment.Offset, segment.Data,
                        (block, blocks) => Progress?.Invoke(before + (long)length * block / blocks, total));
                    done += length;
                    log($"Verified {segment}.");
                }

                if (preserve != null)
                {
                    // Compared against the first session's baseline, so a failed
                    // attempt in between can't hide a change.
                    string after = rom.FlashMd5(preserve.Offset, (uint)preserve.Data.Length);
                    if (after != nvsBaseline)
                    {
                        throw new EspRomException($"NVS changed during flashing (MD5 {nvsBaseline} -> {after}); stored configuration may be lost.");
                    }
                    log($"NVS unchanged ({after}): stored configuration preserved.");
                }

                // Our 1200 bps handler enters the ROM via usb_persist_restart(),
                // which forces download boot; clear it or the reset lands back
                // in the bootloader (arduino-esp32#6762).
                try
                {
                    rom.WriteReg(RtcCntlOption1Reg, 0, RtcCntlForceDownloadBootMask);
                }
                catch (Exception ex) when (ex is EspRomException || ex is TimeoutException)
                {
                    log($"Could not clear forced download boot: {ex.Message}");
                }

                log("Resetting into the new firmware.");
                HardReset(rom);
            }
        }

        // ------------------------------------------------------- ROM entry

        private static UsbPort FindPort(Func<UsbPort, bool> match)
        {
            return UsbDeviceScanner.GetPorts(forceRefresh: true).FirstOrDefault(match);
        }

        private static bool IsAppRole(UsbPortRole role)
        {
            return role == UsbPortRole.Gateway || role == UsbPortRole.Axis || role == UsbPortRole.Grip;
        }

        private static bool SameDevice(UsbPort a, UsbPort b)
        {
            return a.Role == b.Role && a.Index == b.Index;
        }

        /// <summary>
        /// Gets <paramref name="target"/> into the ROM and returns the port to talk to.
        /// On a retry the board may be anywhere: back in the app (touch again),
        /// already in the ROM from the failed attempt (reset it there), or gone.
        /// </summary>
        private string EnterBootloader(UsbPort target, bool retry, out Transport transport, out bool resetFirst)
        {
            if (IsAppRole(target.Role))
            {
                UsbPort app = FindPort(p => SameDevice(p, target));
                if (app != null)
                {
                    // Our app CDC: DTR/RTS do nothing by design (enableReboot(false));
                    // only a 1200 bps open reboots into the ROM.
                    log($"Rebooting {app.Label} into the bootloader...");
                    transport = Transport.UsbJtag;
                    resetFirst = false;
                    return TouchAndWaitForRom(app);
                }

                UsbPort rom = retry ? FindPort(p => p.Role == UsbPortRole.EspRom) : null;
                if (rom == null)
                {
                    throw new EspRomConnectionException("The board is not connected.");
                }
                log($"Board is still in the bootloader on {rom.Com}.");
                return RomPort(rom, out transport, out resetFirst);
            }

            if (target.Role == UsbPortRole.EspRom)
            {
                // Same COM usually, but re-scan in case it re-enumerated elsewhere.
                UsbPort rom = FindPort(p => string.Equals(p.Com, target.Com, StringComparison.OrdinalIgnoreCase))
                    ?? FindPort(p => p.Role == UsbPortRole.EspRom);
                if (rom == null)
                {
                    throw new EspRomConnectionException("The bootloader port is gone.");
                }
                return RomPort(rom, out transport, out resetFirst);
            }

            // USB-UART bridge with auto-program wiring: stable COM, classic reset every time.
            if (FindPort(p => string.Equals(p.Com, target.Com, StringComparison.OrdinalIgnoreCase)) == null)
            {
                throw new EspRomConnectionException($"{target.Com} is gone.");
            }
            transport = Transport.Uart;
            resetFirst = true;
            return target.Com;
        }

        private static string RomPort(UsbPort rom, out Transport transport, out bool resetFirst)
        {
            transport = rom.Pid == 0x1001 ? Transport.UsbJtag : Transport.UsbOtg;
            resetFirst = transport == Transport.UsbJtag;
            WaitUntilOpenable(rom.Com);
            return rom.Com;
        }

        /// <summary>
        /// After the reset, wait for the board to re-enumerate as the same device
        /// (its COM number may change). Null if its identity isn't known.
        /// </summary>
        private string WaitForAppPort(UsbPort target)
        {
            if (!IsAppRole(target.Role))
            {
                return null;
            }

            string candidate = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ReappearTimeoutMs)
            {
                Thread.Sleep(250);
                UsbPort app = FindPort(p => SameDevice(p, target));
                if (app == null)
                {
                    candidate = null;
                    continue;
                }
                if (app.Com == candidate)
                {
                    log($"Board is back as {app.Label}.");
                    return app.Com;
                }
                candidate = app.Com;
            }
            log("Board flashed, but it hasn't re-enumerated yet; replug it if it doesn't come back.");
            return null;
        }

        private string TouchAndWaitForRom(UsbPort appPort)
        {
            HashSet<string> before = new HashSet<string>(
                UsbDeviceScanner.GetPorts(forceRefresh: true).Select(p => p.Com), StringComparer.OrdinalIgnoreCase);

            try
            {
                using (var touch = new SerialPort(appPort.Com, 1200))
                {
                    touch.Open();
                    Thread.Sleep(100);
                    touch.Close();
                }
            }
            catch (IOException)
            {
                // The device dropped off the bus as we closed: that's the reboot.
            }
            catch (UnauthorizedAccessException)
            {
                throw new EspRomConnectionException($"{appPort.Com} is in use by another program.");
            }

            // Wait for a ROM port that wasn't there before, seen on two polls in a row.
            string candidate = null;
            for (int i = 0; i < 30; i++)
            {
                Thread.Sleep(200);
                UsbPort rom = UsbDeviceScanner.GetPorts(forceRefresh: true)
                    .FirstOrDefault(p => p.Role == UsbPortRole.EspRom && !before.Contains(p.Com));
                if (rom == null)
                {
                    candidate = null;
                    continue;
                }
                if (rom.Com == candidate)
                {
                    WaitUntilOpenable(rom.Com);
                    return rom.Com;
                }
                candidate = rom.Com;
            }
            throw new EspRomConnectionException("The board didn't come back in bootloader mode.");
        }

        /// <summary>
        /// Windows lists a new port before the driver accepts an open; wait for
        /// two clean open/close cycles in a row, then let it settle.
        /// </summary>
        private static void WaitUntilOpenable(string com)
        {
            int ok = 0;
            for (int i = 0; i < 20 && ok < 2; i++)
            {
                try
                {
                    using (var probe = new SerialPort(com, 115200) { DtrEnable = false, RtsEnable = false })
                    {
                        probe.Open();
                        probe.Close();
                    }
                    ok++;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    ok = 0;
                }
                Thread.Sleep(250);
            }
            if (ok < 2)
            {
                throw new EspRomConnectionException($"{com} appeared but can't be opened.");
            }
            Thread.Sleep(300);
        }

        // --------------------------------------------- reset sequences (esp_pylib)

        private static void ClassicReset(EspRomLoader rom)
        {
            rom.SetDtr(false);  // IO0 high
            rom.SetRts(true);   // EN low: chip in reset
            Thread.Sleep(100);
            rom.SetDtr(true);   // IO0 low
            rom.SetRts(false);  // EN high: chip out of reset, samples IO0
            Thread.Sleep(50);
            rom.SetDtr(false);  // IO0 high
        }

        private static void UsbJtagReset(EspRomLoader rom)
        {
            rom.SetRts(false);
            rom.SetDtr(false);  // idle
            Thread.Sleep(100);
            rom.SetDtr(true);   // set IO0
            rom.SetRts(false);
            Thread.Sleep(100);
            rom.SetRts(true);   // reset; ordered to pass through (1,1), not (0,0)
            rom.SetDtr(false);
            rom.SetRts(true);   // re-write so Windows propagates the DTR change
            Thread.Sleep(100);
            rom.SetDtr(false);
            rom.SetRts(false);  // chip out of reset
        }

        private static void HardReset(EspRomLoader rom)
        {
            try
            {
                rom.SetRts(true);   // EN low
                Thread.Sleep(100);
                rom.SetRts(false);  // EN high
            }
            catch (IOException)
            {
                // USB device left the bus on reset.
            }
        }

        private void DisableWatchdogs(EspRomLoader rom)
        {
            rom.WriteReg(RtcCntlWdtWprotectReg, RtcCntlWdtWkey);
            rom.WriteReg(RtcCntlWdtConfig0Reg, 0);
            rom.WriteReg(RtcCntlWdtWprotectReg, 0);

            rom.WriteReg(RtcCntlSwdWprotectReg, RtcCntlSwdWkey);
            rom.WriteReg(RtcCntlSwdConfReg, rom.ReadReg(RtcCntlSwdConfReg) | RtcCntlSwdAutoFeedEn);
            rom.WriteReg(RtcCntlSwdWprotectReg, 0);
            log("Watchdogs disabled for USB-Serial/JTAG.");
        }
    }
}
