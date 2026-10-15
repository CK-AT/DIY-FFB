using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace DiyFfb.Flasher
{
    /// <summary>
    /// A board's published firmware, found through the same update_info.json the
    /// ESP32 pulls during WiFi OTA. That file stays ESP32-facing: its URL points at
    /// firmware_&lt;env&gt;.bin, and the packager publishes firmware_&lt;env&gt;.ffbota
    /// next to it, so the package URL is the .bin URL with the extension swapped.
    /// </summary>
    public sealed class PublishedFirmware
    {
        public string Board { get; }
        public string Version { get; }
        public string FirmwareMd5 { get; }
        public string BinUrl { get; }

        /// <summary>Null when the entry's URL isn't a .bin (no package by convention).</summary>
        public string PackageUrl { get; }

        private PublishedFirmware(Entry e)
        {
            Board = e.Board ?? string.Empty;
            Version = e.Version ?? string.Empty;
            FirmwareMd5 = e.Md5 ?? string.Empty;
            BinUrl = e.Url ?? string.Empty;
            PackageUrl = BinUrl.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
                ? BinUrl.Substring(0, BinUrl.Length - 4) + ".ffbota"
                : null;
        }

        public override string ToString() => $"{Board} v{Version}";

        /// <summary>All boards listed in update_info.json at <paramref name="infoUrl"/>.</summary>
        public static IReadOnlyList<PublishedFirmware> List(string infoUrl)
        {
            byte[] json;
            using (var client = new WebClient())
            {
                json = client.DownloadData(infoUrl);
            }

            Info info;
            using (var ms = new MemoryStream(json))
            {
                info = (Info)new DataContractJsonSerializer(typeof(Info)).ReadObject(ms);
            }
            return (info.Configurations ?? new List<Entry>())
                .Where(e => !string.IsNullOrWhiteSpace(e.Board))
                .Select(e => new PublishedFirmware(e))
                .ToList();
        }

        /// <summary>
        /// Downloads the package into <paramref name="cacheDir"/> (default: the
        /// user's local app data) and opens it. Checks that it is the build the
        /// entry advertises: same board, same version, same firmware.bin MD5.
        /// </summary>
        public FirmwarePackage Download(string cacheDir = null)
        {
            if (PackageUrl == null)
            {
                throw new InvalidDataException($"{this}: the published URL is not a .bin, so there's no package next to it.");
            }

            cacheDir = cacheDir ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiyFfbPlugin", "firmware");
            Directory.CreateDirectory(cacheDir);
            string path = Path.Combine(cacheDir, Path.GetFileNameWithoutExtension(new Uri(PackageUrl).LocalPath)
                                                 + "_v" + Version + ".ffbota");

            try
            {
                using (var client = new WebClient())
                {
                    client.DownloadFile(PackageUrl, path + ".part");
                }
            }
            catch (WebException ex) when ((ex.Response as HttpWebResponse)?.StatusCode == HttpStatusCode.NotFound)
            {
                throw new InvalidDataException($"{this} has no USB flash package published ({PackageUrl}).");
            }
            File.Copy(path + ".part", path, overwrite: true);
            File.Delete(path + ".part");

            try
            {
                return Verify(FirmwarePackage.Load(path));
            }
            catch (InvalidDataException ex)
            {
                // Don't keep a package we refused.
                File.Delete(path);
                throw new InvalidDataException($"{this}: {ex.Message}");
            }
        }

        private FirmwarePackage Verify(FirmwarePackage package)
        {
            var mismatch = new StringBuilder();
            if (!string.Equals(package.Board, Board, StringComparison.Ordinal))
            {
                mismatch.Append($" board {package.Board} vs {Board};");
            }
            if (!string.Equals(package.Version, Version, StringComparison.Ordinal))
            {
                mismatch.Append($" version {package.Version} vs {Version};");
            }
            if (!string.Equals(package.FirmwareMd5, FirmwareMd5, StringComparison.OrdinalIgnoreCase))
            {
                mismatch.Append($" firmware MD5 {package.FirmwareMd5} vs {FirmwareMd5};");
            }
            if (mismatch.Length > 0)
            {
                throw new InvalidDataException(
                    $"Published package doesn't match update_info.json ({mismatch.ToString().Trim(' ', ';')}). " +
                    "The two may be mid-update; try again later.");
            }
            return package;
        }

        [DataContract]
        private sealed class Info
        {
            [DataMember(Name = "Configurations")] public List<Entry> Configurations { get; set; }
        }

        [DataContract]
        private sealed class Entry
        {
            [DataMember(Name = "Board")] public string Board { get; set; }
            [DataMember(Name = "Version")] public string Version { get; set; }
            [DataMember(Name = "MD5")] public string Md5 { get; set; }
            [DataMember(Name = "URL")] public string Url { get; set; }
        }
    }
}
