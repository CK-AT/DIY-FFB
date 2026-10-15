using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace DiyFfb.Flasher
{
    /// <summary>
    /// A .ffbota package (ESP32/scripts/ota_package_post.py) opened for USB
    /// flashing. OTA uses its firmware.bin; the USB flasher uses the merged
    /// firmware.factory.bin that newer packages also carry.
    /// </summary>
    public sealed class FirmwarePackage
    {
        public const string FactoryEntryName = "firmware.factory.bin";

        public string Version { get; }
        public string Board { get; }
        public string BuildEnv { get; }
        public string Chip { get; }
        public FirmwareImage Image { get; }

        private FirmwarePackage(Manifest manifest, FirmwareImage image)
        {
            Version = manifest.Version ?? string.Empty;
            Board = manifest.Board ?? string.Empty;
            BuildEnv = manifest.BuildEnv ?? string.Empty;
            Chip = manifest.Chip ?? string.Empty;
            Image = image;
        }

        public static FirmwarePackage Load(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                ZipArchiveEntry manifestEntry = archive.GetEntry("manifest.json");
                if (manifestEntry == null)
                {
                    throw new InvalidDataException("Package has no manifest.json.");
                }

                Manifest manifest;
                using (Stream s = manifestEntry.Open())
                {
                    manifest = (Manifest)new DataContractJsonSerializer(typeof(Manifest)).ReadObject(s);
                }

                ZipArchiveEntry factoryEntry = archive.GetEntry(FactoryEntryName);
                if (factoryEntry == null || string.IsNullOrEmpty(manifest.FactoryMd5))
                {
                    throw new InvalidDataException(
                        "Package has no USB flash image. It was built by an older packager or for a board without one; rebuild it with pio run.");
                }
                if (!string.Equals(manifest.Chip, "esp32s3", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Package is for chip '{manifest.Chip}'; only ESP32-S3 is supported.");
                }

                byte[] data;
                using (Stream s = factoryEntry.Open())
                using (var ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    data = ms.ToArray();
                }

                string md5 = EspRomLoader.Md5Hex(data);
                if (!string.Equals(md5, manifest.FactoryMd5, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"USB flash image MD5 mismatch (manifest {manifest.FactoryMd5}, computed {md5}).");
                }

                return new FirmwarePackage(manifest, new FirmwareImage(data));
            }
        }

        [DataContract]
        private sealed class Manifest
        {
            [DataMember(Name = "version")] public string Version { get; set; }
            [DataMember(Name = "board")] public string Board { get; set; }
            [DataMember(Name = "build_env")] public string BuildEnv { get; set; }
            [DataMember(Name = "chip")] public string Chip { get; set; }
            [DataMember(Name = "factory_md5")] public string FactoryMd5 { get; set; }
        }
    }
}
