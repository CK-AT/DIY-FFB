using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.IO.Ports;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace DiyFfb.Flasher
{
    public class EspRomException : Exception
    {
        public EspRomException(string message) : base(message) { }
    }

    /// <summary>
    /// Talks to the ESP32 ROM serial bootloader directly (no flasher stub).
    /// Wire format and constants follow esptool 5.4.0 loader.py; see
    /// docs/plans/29_usb_port_detection_and_flasher.md §4.1.
    /// </summary>
    public sealed class EspRomLoader : IDisposable
    {
        private const byte OpSync = 0x08;
        private const byte OpWriteReg = 0x09;
        private const byte OpReadReg = 0x0A;
        private const byte OpSpiSetParams = 0x0B;
        private const byte OpSpiAttach = 0x0D;
        private const byte OpChangeBaudrate = 0x0F;
        private const byte OpFlashDeflBegin = 0x10;
        private const byte OpFlashDeflData = 0x11;
        private const byte OpSpiFlashMd5 = 0x13;
        private const byte OpGetSecurityInfo = 0x14;

        private const byte RomInvalidRecvMsg = 0x05;
        private const byte ChecksumMagic = 0xEF;

        public const int FlashWriteSize = 0x400;
        public const int FlashSectorSize = 0x1000;

        private const int DefaultTimeoutMs = 3000;
        private const int SyncTimeoutMs = 100;
        private const double EraseWriteSecondsPerMb = 40;
        private const double Md5SecondsPerMb = 8;
        private const int WriteBlockAttempts = 3;

        private readonly SerialPort port;
        private readonly SlipReader reader;

        public Action<string> Log { get; set; }

        public string PortName => port.PortName;

        public EspRomLoader(string portName, int baud = 115200)
        {
            port = new SerialPort(portName, baud)
            {
                ReadTimeout = SyncTimeoutMs,
                WriteTimeout = DefaultTimeoutMs,
                DtrEnable = false,
                RtsEnable = false,
            };
            reader = new SlipReader(port);
        }

        public void Open()
        {
            if (!port.IsOpen)
            {
                port.Open();
            }
        }

        public void Dispose()
        {
            try
            {
                if (port.IsOpen)
                {
                    port.Close();
                }
            }
            catch (IOException)
            {
                // Device already gone (reset/re-enumeration).
            }
            port.Dispose();
        }

        // Line control for reset sequences; true = asserted (esp_pylib PIN_LOW).
        // Through the auto-program transistors DTR drives IO0 and RTS drives EN.

        public void SetDtr(bool asserted)
        {
            port.DtrEnable = asserted;
        }

        public void SetRts(bool asserted)
        {
            port.RtsEnable = asserted;
            // usbser.sys only sends SET_CONTROL_LINE_STATE for an RTS change when
            // DTR is written after it (esp_pylib set_rts workaround).
            port.DtrEnable = port.DtrEnable;
        }

        // ---------------------------------------------------------------- sync

        /// <summary>SYNC until the ROM answers. Throws after <paramref name="attempts"/> tries.</summary>
        public void Sync(int attempts = 7)
        {
            var payload = new byte[36];
            payload[0] = 0x07; payload[1] = 0x07; payload[2] = 0x12; payload[3] = 0x20;
            for (int i = 4; i < payload.Length; i++)
            {
                payload[i] = 0x55;
            }

            for (int attempt = 0; attempt < attempts; attempt++)
            {
                for (int tries = 0; tries < 5; tries++)
                {
                    port.DiscardInBuffer();
                    reader.Reset();
                    try
                    {
                        Command(OpSync, payload, 0, SyncTimeoutMs);
                        // The ROM answers SYNC eight times; drain the rest.
                        for (int i = 0; i < 7; i++)
                        {
                            TryReadResponse(OpSync, SyncTimeoutMs);
                        }
                        return;
                    }
                    catch (TimeoutException)
                    {
                        Thread.Sleep(50);
                    }
                }
            }
            throw new EspRomException($"No response from the ESP bootloader on {port.PortName}.");
        }

        // ----------------------------------------------------------- registers

        public uint ReadReg(uint address)
        {
            return CheckCommand("read register", OpReadReg, Pack(address), 0, 0, DefaultTimeoutMs).Value;
        }

        public void WriteReg(uint address, uint value, uint mask = 0xFFFFFFFF, uint delayUs = 0)
        {
            CheckCommand("write register", OpWriteReg, Pack(address, value, mask, delayUs), 0, 0, DefaultTimeoutMs);
        }

        /// <summary>Chip id from GET_SECURITY_INFO (ESP32-S3 = 9).</summary>
        public uint GetChipId(out uint securityFlags)
        {
            // flags u32, crypt_cnt u8, 7 key purposes, chip_id u32, api_version u32
            byte[] info = CheckCommand("get security info", OpGetSecurityInfo, new byte[0], 0, 20, DefaultTimeoutMs).Data;
            securityFlags = BitConverter.ToUInt32(info, 0);
            return BitConverter.ToUInt32(info, 12);
        }

        // ------------------------------------------------------------- SPI flash

        public void SpiAttach()
        {
            // hspi_arg = 0 (default pins) + ROM-only 'is_legacy' word.
            CheckCommand("attach SPI flash", OpSpiAttach, new byte[8], 0, 0, DefaultTimeoutMs);
        }

        public void SpiSetParams(uint totalSize)
        {
            CheckCommand("set SPI params", OpSpiSetParams,
                Pack(0, totalSize, 64 * 1024, 4 * 1024, 256, 0xFFFF), 0, 0, DefaultTimeoutMs);
        }

        public void ChangeBaud(int baud)
        {
            Command(OpChangeBaudrate, Pack((uint)baud, 0), 0, DefaultTimeoutMs);
            port.BaudRate = baud;
            Thread.Sleep(50);
            port.DiscardInBuffer();
            reader.Reset();
        }

        /// <summary>
        /// Writes <paramref name="data"/> at <paramref name="offset"/> (sector-aligned)
        /// using compressed transfer, then verifies the flash MD5.
        /// </summary>
        public void WriteFlash(uint offset, byte[] data, Action<int, int> progress = null)
        {
            if (offset % FlashSectorSize != 0)
            {
                throw new ArgumentException($"Offset 0x{offset:X} is not sector-aligned.");
            }

            byte[] compressed = ZlibCompress(data);
            int numBlocks = (compressed.Length + FlashWriteSize - 1) / FlashWriteSize;
            int eraseBlocks = (data.Length + FlashWriteSize - 1) / FlashWriteSize;
            uint writeSize = (uint)(eraseBlocks * FlashWriteSize);  // ROM wants it rounded up

            // ROM erases the whole region up front, so this one can take a while.
            CheckCommand("begin compressed flash write", OpFlashDeflBegin,
                Pack(writeSize, (uint)numBlocks, FlashWriteSize, offset, 0),
                0, 0, TimeoutPerMb(EraseWriteSecondsPerMb, writeSize));

            using (var inflater = new BlockInflater())
            {
                for (int seq = 0; seq < numBlocks; seq++)
                {
                    int start = seq * FlashWriteSize;
                    int len = Math.Min(FlashWriteSize, compressed.Length - start);
                    var block = new byte[len];
                    Buffer.BlockCopy(compressed, start, block, 0, len);

                    // Each block's timeout scales with how much flash it writes.
                    int uncompressed = inflater.Feed(block);
                    int timeout = TimeoutPerMb(EraseWriteSecondsPerMb, uncompressed);

                    var payload = new byte[16 + len];
                    Buffer.BlockCopy(Pack((uint)len, (uint)seq, 0, 0), 0, payload, 0, 16);
                    Buffer.BlockCopy(block, 0, payload, 16, len);
                    uint chk = Checksum(block);

                    for (int attempt = 1; ; attempt++)
                    {
                        try
                        {
                            CheckCommand($"write flash block {seq}", OpFlashDeflData, payload, chk, 0, timeout);
                            break;
                        }
                        catch (EspRomException) when (attempt < WriteBlockAttempts)
                        {
                        }
                    }
                    progress?.Invoke(seq + 1, numBlocks);
                }
            }
            // No FLASH_DEFL_END: on the ROM it leaves the loader (esptool skips it too).

            string expected = Md5Hex(data);
            string actual = FlashMd5(offset, (uint)data.Length);
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                throw new EspRomException($"Verify failed at 0x{offset:X}: flash MD5 {actual}, expected {expected}.");
            }
        }

        public string FlashMd5(uint offset, uint size)
        {
            // ROM returns the digest as 32 ASCII hex chars.
            byte[] hex = CheckCommand("calculate flash MD5", OpSpiFlashMd5, Pack(offset, size, 0, 0),
                0, 32, TimeoutPerMb(Md5SecondsPerMb, size)).Data;
            return Encoding.ASCII.GetString(hex, 0, 32).ToLowerInvariant();
        }

        // ------------------------------------------------------------ transport

        private struct Response
        {
            public uint Value;
            public byte[] Data;
        }

        private Response CheckCommand(string what, byte op, byte[] data, uint chk, int respDataLen, int timeoutMs)
        {
            Response r = Command(op, data, chk, timeoutMs);
            // Status = the 2 bytes right after the expected response data; the ROM
            // appends 2 reserved bytes after that, which are ignored.
            if (r.Data.Length < respDataLen + 2)
            {
                if (r.Data.Length >= 2 && r.Data[0] != 0)
                {
                    throw new EspRomException($"Failed to {what}: ROM error 0x{r.Data[1]:X2}.");
                }
                throw new EspRomException($"Failed to {what}: short response ({r.Data.Length} bytes).");
            }
            if (r.Data[respDataLen] != 0)
            {
                throw new EspRomException($"Failed to {what}: ROM error 0x{r.Data[respDataLen + 1]:X2}.");
            }
            if (respDataLen > 0)
            {
                var payload = new byte[respDataLen];
                Buffer.BlockCopy(r.Data, 0, payload, 0, respDataLen);
                r.Data = payload;
            }
            return r;
        }

        private Response Command(byte op, byte[] data, uint chk, int timeoutMs)
        {
            var packet = new byte[8 + data.Length];
            packet[0] = 0x00;
            packet[1] = op;
            packet[2] = (byte)data.Length;
            packet[3] = (byte)(data.Length >> 8);
            Buffer.BlockCopy(BitConverter.GetBytes(chk), 0, packet, 4, 4);
            Buffer.BlockCopy(data, 0, packet, 8, data.Length);
            WriteSlip(packet);

            Response? r = TryReadResponse(op, timeoutMs);
            if (r == null)
            {
                throw new TimeoutException($"No response to command 0x{op:X2}.");
            }
            return r.Value;
        }

        /// <summary>Reads until a response for <paramref name="op"/> arrives; null on timeout.</summary>
        private Response? TryReadResponse(byte op, int timeoutMs)
        {
            var deadline = Stopwatch.StartNew();
            for (int i = 0; i < 100; i++)
            {
                int remaining = timeoutMs - (int)deadline.ElapsedMilliseconds;
                if (remaining <= 0)
                {
                    return null;
                }

                byte[] p = reader.ReadPacket(remaining);
                if (p == null)
                {
                    return null;
                }
                if (p.Length < 8 || p[0] != 0x01)
                {
                    continue;
                }

                var data = new byte[p.Length - 8];
                Buffer.BlockCopy(p, 8, data, 0, data.Length);
                if (p[1] == op)
                {
                    return new Response { Value = BitConverter.ToUInt32(p, 4), Data = data };
                }
                if (data.Length >= 2 && data[0] != 0 && data[1] == RomInvalidRecvMsg)
                {
                    throw new EspRomException($"Command 0x{op:X2} is not supported by this bootloader.");
                }
            }
            throw new EspRomException($"No matching response to command 0x{op:X2}.");
        }

        private void WriteSlip(byte[] packet)
        {
            var buf = new List<byte>(packet.Length + 16) { 0xC0 };
            foreach (byte b in packet)
            {
                if (b == 0xC0) { buf.Add(0xDB); buf.Add(0xDC); }
                else if (b == 0xDB) { buf.Add(0xDB); buf.Add(0xDD); }
                else buf.Add(b);
            }
            buf.Add(0xC0);
            byte[] bytes = buf.ToArray();
            port.Write(bytes, 0, bytes.Length);
        }

        // -------------------------------------------------------------- helpers

        private static byte[] Pack(params uint[] words)
        {
            var bytes = new byte[words.Length * 4];
            for (int i = 0; i < words.Length; i++)
            {
                Buffer.BlockCopy(BitConverter.GetBytes(words[i]), 0, bytes, i * 4, 4);
            }
            return bytes;
        }

        private static uint Checksum(byte[] data)
        {
            byte state = ChecksumMagic;
            foreach (byte b in data)
            {
                state ^= b;
            }
            return state;
        }

        private static int TimeoutPerMb(double secondsPerMb, long sizeBytes)
        {
            double ms = secondsPerMb * (sizeBytes / 1e6) * 1000;
            return ms < DefaultTimeoutMs ? DefaultTimeoutMs : (int)ms;
        }

        public static string Md5Hex(byte[] data)
        {
            using (var md5 = MD5.Create())
            {
                return BitConverter.ToString(md5.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
            }
        }

        /// <summary>
        /// zlib stream (RFC 1950), which the ROM's FLASH_DEFL_* expects. .NET 4.8
        /// only has raw deflate, so the 2-byte header and Adler-32 are added here.
        /// </summary>
        public static byte[] ZlibCompress(byte[] data)
        {
            using (var ms = new MemoryStream())
            {
                ms.WriteByte(0x78);
                ms.WriteByte(0xDA);  // max compression, matching esptool's zlib level 9
                using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                {
                    deflate.Write(data, 0, data.Length);
                }
                uint adler = Adler32(data);
                ms.WriteByte((byte)(adler >> 24));
                ms.WriteByte((byte)(adler >> 16));
                ms.WriteByte((byte)(adler >> 8));
                ms.WriteByte((byte)adler);
                return ms.ToArray();
            }
        }

        private static uint Adler32(byte[] data)
        {
            const uint Mod = 65521;
            uint a = 1, b = 0;
            foreach (byte x in data)
            {
                a = (a + x) % Mod;
                b = (b + a) % Mod;
            }
            return (b << 16) | a;
        }

        /// <summary>
        /// Incrementally inflates the compressed stream to learn how many flash
        /// bytes each block produces (for per-block timeouts).
        /// </summary>
        internal sealed class BlockInflater : IDisposable
        {
            private readonly MemoryStream source = new MemoryStream();
            private DeflateStream inflate;
            private bool headerSkipped;
            private readonly byte[] scratch = new byte[64 * 1024];

            public int Feed(byte[] block)
            {
                long pos = source.Position;
                source.Seek(0, SeekOrigin.End);
                source.Write(block, 0, block.Length);
                source.Position = pos;

                if (!headerSkipped)
                {
                    if (source.Length - source.Position < 2)
                    {
                        return 0;
                    }
                    source.Position += 2;
                    headerSkipped = true;
                    inflate = new DeflateStream(source, CompressionMode.Decompress, leaveOpen: true);
                }

                // DeflateStream reads ahead into its own buffer; whatever it can
                // produce from the bytes so far is a fine estimate.
                int total = 0;
                try
                {
                    int n;
                    while ((n = inflate.Read(scratch, 0, scratch.Length)) > 0)
                    {
                        total += n;
                    }
                }
                catch (InvalidDataException)
                {
                    // Trailing Adler-32 bytes after the deflate end; nothing more to inflate.
                }
                return total;
            }

            public void Dispose()
            {
                inflate?.Dispose();
                source.Dispose();
            }
        }

        /// <summary>SLIP frame reader over a SerialPort with a per-call deadline.</summary>
        private sealed class SlipReader
        {
            private readonly SerialPort port;
            private readonly List<byte> frame = new List<byte>();
            private readonly byte[] buffer = new byte[4096];
            private int bufferPos;
            private int bufferLen;
            private bool inFrame;
            private bool escape;

            public SlipReader(SerialPort port)
            {
                this.port = port;
            }

            public void Reset()
            {
                frame.Clear();
                bufferPos = bufferLen = 0;
                inFrame = false;
                escape = false;
            }

            private int NextByte(int timeoutMs)
            {
                if (bufferPos == bufferLen)
                {
                    try
                    {
                        if (port.ReadTimeout != timeoutMs)
                        {
                            port.ReadTimeout = timeoutMs;
                        }
                        bufferLen = port.Read(buffer, 0, buffer.Length);
                        bufferPos = 0;
                    }
                    catch (TimeoutException)
                    {
                        return -1;
                    }
                    if (bufferLen <= 0)
                    {
                        bufferLen = 0;
                        return -1;
                    }
                }
                return buffer[bufferPos++];
            }

            /// <summary>Next complete frame, or null if none arrives within <paramref name="timeoutMs"/>.</summary>
            public byte[] ReadPacket(int timeoutMs)
            {
                var sw = Stopwatch.StartNew();
                while (true)
                {
                    int remaining = timeoutMs - (int)sw.ElapsedMilliseconds;
                    if (remaining <= 0)
                    {
                        return null;
                    }

                    int b = NextByte(remaining);
                    if (b < 0)
                    {
                        return null;
                    }

                    if (!inFrame)
                    {
                        // Anything outside a frame (boot log text) is ignored.
                        if (b == 0xC0)
                        {
                            inFrame = true;
                            frame.Clear();
                        }
                        continue;
                    }

                    if (escape)
                    {
                        escape = false;
                        if (b == 0xDC) frame.Add(0xC0);
                        else if (b == 0xDD) frame.Add(0xDB);
                        else { Reset(); }  // invalid escape: drop the frame
                        continue;
                    }

                    if (b == 0xDB)
                    {
                        escape = true;
                    }
                    else if (b == 0xC0)
                    {
                        if (frame.Count == 0)
                        {
                            continue;  // back-to-back delimiters: treat as a new start
                        }
                        byte[] result = frame.ToArray();
                        frame.Clear();
                        inFrame = false;
                        return result;
                    }
                    else
                    {
                        frame.Add((byte)b);
                    }
                }
            }
        }
    }
}
