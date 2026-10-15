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

        public void Flash(string portName, FirmwareImage image, FlashOptions options)
        {
            IReadOnlyList<FlashSegment> segments = image.GetSegments(options.EraseNvs);
            log(options.EraseNvs
                ? $"Writing full image ({image.Data.Length} bytes); stored configuration will be erased."
                : $"Writing {segments.Count} segments, keeping NVS 0x{image.NvsOffset:X}+0x{image.NvsSize:X}.");
            // Keep-config mode proves NVS survived: MD5 before and after, same ROM session.
            FlashSegment preserve = options.EraseNvs ? null : new FlashSegment(image.NvsOffset, new byte[image.NvsSize]);
            Run(portName, image.FlashSizeBytes, options, segments, preserve);
        }

        /// <summary>Wipes NVS only (the stored configuration), leaving the firmware intact.</summary>
        public void EraseNvs(string portName, uint nvsOffset = DefaultNvsOffset, uint nvsSize = DefaultNvsSize)
        {
            var blank = new byte[nvsSize];
            for (int i = 0; i < blank.Length; i++)
            {
                blank[i] = 0xFF;
            }
            log($"Erasing NVS 0x{nvsOffset:X}+0x{nvsSize:X}.");
            // Flash size only bounds the ROM's address checks; NVS is far below 1 MB.
            Run(portName, 0, new FlashOptions(), new[] { new FlashSegment(nvsOffset, blank) }, null);
        }

        private void Run(string portName, uint flashSize, FlashOptions options, IReadOnlyList<FlashSegment> segments,
                         FlashSegment preserve)
        {
            Transport transport;
            bool resetFirst;
            string romPort = EnterBootloader(portName, out transport, out resetFirst);

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

                if (transport == Transport.Uart && options.UartBaud > 115200)
                {
                    rom.ChangeBaud(options.UartBaud);
                    log($"Baud rate {options.UartBaud}.");
                }

                string preservedMd5 = null;
                if (preserve != null)
                {
                    preservedMd5 = rom.FlashMd5(preserve.Offset, (uint)preserve.Data.Length);
                    log($"NVS 0x{preserve.Offset:X}+0x{preserve.Data.Length:X} MD5 before: {preservedMd5}");
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
                    string after = rom.FlashMd5(preserve.Offset, (uint)preserve.Data.Length);
                    if (after != preservedMd5)
                    {
                        throw new EspRomException($"NVS changed during flashing (MD5 {preservedMd5} -> {after}); stored configuration may be lost.");
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

        private string EnterBootloader(string portName, out Transport transport, out bool resetFirst)
        {
            UsbPort port = UsbDeviceScanner.GetPorts(forceRefresh: true)
                .FirstOrDefault(p => string.Equals(p.Com, portName, StringComparison.OrdinalIgnoreCase));
            if (port == null)
            {
                throw new EspRomException($"{portName} is not present.");
            }

            switch (port.Role)
            {
                case UsbPortRole.Gateway:
                case UsbPortRole.Axis:
                case UsbPortRole.Grip:
                    // Our app CDC: DTR/RTS do nothing by design (enableReboot(false));
                    // only a 1200 bps open reboots into the ROM.
                    log($"Rebooting {port.Label} into the bootloader...");
                    string romPort = TouchAndWaitForRom(port);
                    transport = Transport.UsbJtag;
                    resetFirst = false;
                    return romPort;

                case UsbPortRole.EspRom:
                    transport = port.Pid == 0x1001 ? Transport.UsbJtag : Transport.UsbOtg;
                    resetFirst = transport == Transport.UsbJtag;
                    return port.Com;

                default:
                    transport = Transport.Uart;
                    resetFirst = true;
                    return port.Com;
            }
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
                throw new EspRomException($"{appPort.Com} is in use by another program.");
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
            throw new EspRomException("The board didn't come back in bootloader mode. Hold BOOT, replug it, and flash the bootloader port.");
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
                throw new EspRomException($"{com} appeared but can't be opened.");
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
