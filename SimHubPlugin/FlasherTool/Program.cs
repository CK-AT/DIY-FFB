using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using DiyFfb.Flasher;

namespace DiyFfb.FlasherTool
{
    /// <summary>
    /// Desk harness for the pure-C# ROM flasher (Plan 29 phase 2). Not shipped.
    /// </summary>
    internal static class Program
    {
        private const string Usage =
@"FlasherTool list
FlasherTool selftest  <package.ffbota>
FlasherTool flash     <COMx> <package.ffbota> [--erase-nvs] [--baud N]
FlasherTool erase-nvs <COMx> [--package package.ffbota]
FlasherTool cycle     <COMx> <a.ffbota> <b.ffbota> [count]   (alternate a/b, default 20)

<COMx> may be a running DIY-FFB device (1200 bps touch), the S3 bootloader
port (303A:1001), or a USB-UART bridge with auto-program wiring.";

        private static int Main(string[] args)
        {
            try
            {
                switch (args.FirstOrDefault())
                {
                    case "list": return List();
                    case "selftest" when args.Length >= 2: return SelfTest(args[1]);
                    case "flash" when args.Length >= 3: return Flash(args);
                    case "cycle" when args.Length >= 4: return Cycle(args);
                    case "erase-nvs" when args.Length >= 2: return EraseNvs(args);
                    default:
                        Console.Error.WriteLine(Usage);
                        return 2;
                }
            }
            catch (Exception ex) when (ex is EspRomException || ex is IOException || ex is TimeoutException
                                       || ex is InvalidDataException || ex is UnauthorizedAccessException)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine($"FAILED: {ex.Message}");
                return 1;
            }
        }

        private static int List()
        {
            foreach (UsbPort p in UsbDeviceScanner.GetPorts(forceRefresh: true))
            {
                string id = p.Vid >= 0 ? $"{p.Vid:X4}:{p.Pid:X4}" : "????:????";
                Console.WriteLine($"{p.Com,-7} {id}  {p.Role,-8} {p.Label}");
            }
            return 0;
        }

        private static FirmwareImage LoadPackage(string path)
        {
            FirmwarePackage package = FirmwarePackage.Load(path);
            Console.WriteLine($"Package {Path.GetFileName(path)}: {package.Board} v{package.Version} ({package.BuildEnv}, {package.Chip})");
            return package.Image;
        }

        private static int SelfTest(string packagePath)
        {
            FirmwareImage image = LoadPackage(packagePath);
            Console.WriteLine($"Image {image.Data.Length} bytes, flash {image.FlashSizeBytes / (1024 * 1024)} MB, " +
                              $"NVS 0x{image.NvsOffset:X}+0x{image.NvsSize:X}");
            foreach (FlashSegment s in image.GetSegments(eraseNvs: false))
            {
                Console.WriteLine($"  keep-config segment {s}");
            }
            foreach (FlashSegment s in image.GetSegments(eraseNvs: true))
            {
                Console.WriteLine($"  factory segment     {s}");
            }

            bool ok = true;
            foreach (FlashSegment s in image.GetSegments(eraseNvs: false))
            {
                ok &= CheckCompression(s.Data, $"segment {s}");
            }
            var blank = Enumerable.Repeat((byte)0xFF, (int)image.NvsSize).ToArray();
            ok &= CheckCompression(blank, "NVS blank");

            Console.WriteLine(ok ? "selftest OK" : "selftest FAILED");
            return ok ? 0 : 1;
        }

        /// <summary>zlib round trip, Adler-32, and the per-block inflate estimate summing to the input.</summary>
        private static bool CheckCompression(byte[] data, string what)
        {
            byte[] z = EspRomLoader.ZlibCompress(data);

            byte[] roundTrip;
            using (var src = new MemoryStream(z, 2, z.Length - 6))
            using (var inflate = new DeflateStream(src, CompressionMode.Decompress))
            using (var dst = new MemoryStream())
            {
                inflate.CopyTo(dst);
                roundTrip = dst.ToArray();
            }
            bool same = roundTrip.SequenceEqual(data);

            uint a = 1, b = 0;
            foreach (byte x in data) { a = (a + x) % 65521; b = (b + a) % 65521; }
            uint adler = (b << 16) | a;
            uint trailer = (uint)(z[z.Length - 4] << 24 | z[z.Length - 3] << 16 | z[z.Length - 2] << 8 | z[z.Length - 1]);

            long inflated = 0;
            using (var inflater = new EspRomLoader.BlockInflater())
            {
                for (int off = 0; off < z.Length; off += EspRomLoader.FlashWriteSize)
                {
                    int len = Math.Min(EspRomLoader.FlashWriteSize, z.Length - off);
                    var block = new byte[len];
                    Buffer.BlockCopy(z, off, block, 0, len);
                    inflated += inflater.Feed(block);
                }
            }

            bool ok = same && adler == trailer && inflated == data.Length;
            Console.WriteLine($"  {what}: {data.Length} -> {z.Length} bytes, round trip {(same ? "ok" : "MISMATCH")}, " +
                              $"adler {(adler == trailer ? "ok" : "MISMATCH")}, block inflate sum {inflated}" +
                              (inflated == data.Length ? "" : " MISMATCH"));
            return ok;
        }

        private static int Flash(string[] args)
        {
            FirmwareImage image = LoadPackage(args[2]);
            var options = new FlashOptions { EraseNvs = args.Contains("--erase-nvs") };
            int baudIdx = Array.IndexOf(args, "--baud");
            if (baudIdx > 0 && baudIdx + 1 < args.Length)
            {
                options.UartBaud = int.Parse(args[baudIdx + 1]);
            }

            var sw = Stopwatch.StartNew();
            var flasher = new Esp32S3Flasher(Log) { Progress = PrintProgress };
            string backOn = flasher.Flash(args[1], image, options);
            if (backOn != null)
            {
                Console.WriteLine($"Board is on {backOn}.");
            }
            Console.WriteLine($"Done in {sw.Elapsed.TotalSeconds:F1} s.");
            return 0;
        }

        /// <summary>
        /// Plan 29 phase 3 exit test: flash a, b, a, b, ... from the running app
        /// with no hands on the board, following it across COM renumbering.
        /// </summary>
        private static int Cycle(string[] args)
        {
            FirmwareImage[] images = { LoadPackage(args[2]), LoadPackage(args[3]) };
            int count = args.Length >= 5 ? int.Parse(args[4]) : 20;
            string port = args[1];
            var times = new List<double>();

            for (int i = 0; i < count; i++)
            {
                Console.WriteLine();
                Console.WriteLine($"=== Cycle {i + 1}/{count}: {(i % 2 == 0 ? "a" : "b")} via {port} ===");
                var sw = Stopwatch.StartNew();
                var flasher = new Esp32S3Flasher(Log) { Progress = PrintProgress };
                string backOn = flasher.Flash(port, images[i % 2], new FlashOptions());
                times.Add(sw.Elapsed.TotalSeconds);

                // Null over a UART bridge (stable COM) or if the board didn't
                // re-enumerate; then the next cycle fails loudly on the old port.
                if (backOn != null)
                {
                    port = backOn;
                }
            }

            Console.WriteLine();
            Console.WriteLine($"{count}/{count} cycles OK. Per flash: min {times.Min():F1} s, " +
                              $"avg {times.Average():F1} s, max {times.Max():F1} s.");
            return 0;
        }

        private static int EraseNvs(string[] args)
        {
            uint offset = Esp32S3Flasher.DefaultNvsOffset, size = Esp32S3Flasher.DefaultNvsSize;
            int packageIdx = Array.IndexOf(args, "--package");
            if (packageIdx > 0 && packageIdx + 1 < args.Length)
            {
                FirmwareImage image = LoadPackage(args[packageIdx + 1]);
                offset = image.NvsOffset;
                size = image.NvsSize;
            }
            new Esp32S3Flasher(Log).EraseNvs(args[1], offset, size);
            Console.WriteLine("Done.");
            return 0;
        }

        private static int lastPercent = -1;

        private static void PrintProgress(long done, long total)
        {
            int percent = (int)(100 * done / Math.Max(1, total));
            if (percent != lastPercent)
            {
                lastPercent = percent;
                Console.Write($"\r  {percent,3}%  {done}/{total} bytes");
                if (done >= total)
                {
                    Console.WriteLine();
                }
            }
        }

        private static void Log(string message)
        {
            if (lastPercent >= 0 && lastPercent < 100)
            {
                Console.WriteLine();
            }
            lastPercent = -1;
            Console.WriteLine(message);
        }
    }
}
