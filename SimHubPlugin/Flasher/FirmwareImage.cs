using System;
using System.Collections.Generic;
using System.IO;

namespace DiyFfb.Flasher
{
    public sealed class FlashSegment
    {
        public uint Offset { get; }
        public byte[] Data { get; }

        public FlashSegment(uint offset, byte[] data)
        {
            Offset = offset;
            Data = data;
        }

        public override string ToString() => $"0x{Offset:X6} +0x{Data.Length:X}";
    }

    /// <summary>
    /// A merged image to be written at flash offset 0 (PlatformIO's
    /// firmware.factory.bin: bootloader, partition table, boot_app0/otadata, app).
    /// Its gaps are 0xFF padding, so writing it whole erases NVS -- the stored
    /// configuration. <see cref="GetSegments"/> skips the NVS partition unless
    /// asked not to. The NVS range comes from the image's own partition table.
    /// </summary>
    public sealed class FirmwareImage
    {
        private const uint PartitionTableOffset = 0x8000;
        private const int PartitionEntrySize = 32;
        private const int MaxPartitions = 95;  // 0xC00 table / 32 bytes, minus the MD5 entry

        public byte[] Data { get; }
        public uint FlashSizeBytes { get; }
        public uint NvsOffset { get; }
        public uint NvsSize { get; }

        public FirmwareImage(byte[] data)
        {
            if (data.Length < PartitionTableOffset + 0x1000 || data[0] != 0xE9)
            {
                throw new InvalidDataException("Not a merged factory image (no bootloader header at offset 0).");
            }
            Data = data;
            FlashSizeBytes = DecodeFlashSize(data[3]);

            uint nvsOffset, nvsSize;
            if (!FindNvs(data, out nvsOffset, out nvsSize))
            {
                throw new InvalidDataException("Partition table at 0x8000 has no NVS partition.");
            }
            // The ROM erases whole 4 KB sectors from each write's start, so a
            // segment ending inside a sector shared with NVS would wipe part of it.
            if (nvsOffset % EspRomLoader.FlashSectorSize != 0 || nvsSize % EspRomLoader.FlashSectorSize != 0)
            {
                throw new InvalidDataException($"NVS 0x{nvsOffset:X}+0x{nvsSize:X} is not sector-aligned; can't flash around it safely.");
            }
            NvsOffset = nvsOffset;
            NvsSize = nvsSize;
        }

        /// <summary>
        /// The image split around NVS. With <paramref name="eraseNvs"/> the whole
        /// image is one segment and its 0xFF padding wipes the stored config.
        /// </summary>
        public IReadOnlyList<FlashSegment> GetSegments(bool eraseNvs)
        {
            var segments = new List<FlashSegment>();
            uint end = (uint)Data.Length;
            uint nvsEnd = NvsOffset + NvsSize;

            if (eraseNvs || NvsOffset >= end)
            {
                segments.Add(new FlashSegment(0, Data));
                return segments;
            }

            if (NvsOffset > 0)
            {
                segments.Add(Slice(0, NvsOffset));
            }
            if (nvsEnd < end)
            {
                segments.Add(Slice(nvsEnd, end - nvsEnd));
            }
            return segments;
        }

        private FlashSegment Slice(uint offset, uint length)
        {
            var bytes = new byte[length];
            Buffer.BlockCopy(Data, (int)offset, bytes, 0, (int)length);
            return new FlashSegment(offset, bytes);
        }

        /// <summary>Image header byte 3, high nibble (esptool ESP32ROM.FLASH_SIZES, inherited by the S3).</summary>
        private static uint DecodeFlashSize(byte sizeFreq)
        {
            int code = sizeFreq >> 4;
            if (code > 7)
            {
                throw new InvalidDataException($"Unknown flash size code {code} in image header.");
            }
            return (1u << code) * 1024 * 1024;  // 0 = 1 MB … 7 = 128 MB
        }

        private static bool FindNvs(byte[] data, out uint offset, out uint size)
        {
            offset = size = 0;
            for (int i = 0; i < MaxPartitions; i++)
            {
                int e = (int)PartitionTableOffset + i * PartitionEntrySize;
                if (e + PartitionEntrySize > data.Length)
                {
                    return false;
                }
                // Entry magic AA 50; EB EB = MD5 entry; FF FF = end of table.
                if (data[e] != 0xAA || data[e + 1] != 0x50)
                {
                    return false;
                }
                byte type = data[e + 2];
                byte subtype = data[e + 3];
                if (type == 0x01 && subtype == 0x02)  // data / nvs
                {
                    offset = BitConverter.ToUInt32(data, e + 4);
                    size = BitConverter.ToUInt32(data, e + 8);
                    return true;
                }
            }
            return false;
        }
    }
}
