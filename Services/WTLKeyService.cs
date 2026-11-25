using System.Globalization;

namespace wow.tools.local.Services
{
    public class WTLKeyService
    {
        public static readonly List<ulong> KnownKeys = new();
        private const long MaxKeyFileBytes = 16L * 1024 * 1024;

        public static bool TryGetKey(ulong keyName, out byte[] key)
        {
            if (CASC.IsTACTSharpInit)
            {
                return TACTSharp.KeyService.TryGetKey(keyName, out key);
            }
            else
            {
                throw new Exception("No TACT/CASC library initialized");
            }
        }

        public static byte[] GetKey(ulong keyName)
        {
            if (CASC.IsTACTSharpInit)
            {
                if (TACTSharp.KeyService.TryGetKey(keyName, out var key))
                    return key;

                return Array.Empty<byte>();
            }
            else
                throw new Exception("No TACT/CASC library initialized");
        }

        public static bool HasKey(ulong keyName)
        {
            if (CASC.IsTACTSharpInit)
                return TACTSharp.KeyService.TryGetKey(keyName, out _);
            else
                throw new Exception("No TACT/CASC library initialized");
        }

        public static void SetKey(ulong keyName, byte[] key)
        {
            if (!KnownKeys.Contains(keyName))
                KnownKeys.Add(keyName);

            if (CASC.IsTACTSharpInit)
                TACTSharp.KeyService.SetKey(keyName, key);
            else
                throw new Exception("No TACT/CASC library initialized");
        }

        public static async Task<bool> LoadKeys()
        {
            if (!File.Exists("WoW.txt")) return false;

            foreach (var line in File.ReadAllLines("WoW.txt"))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (!TryParseKeyLine(line, out var lookup, out var key))
                    throw new InvalidDataException("TACT key file contains an invalid record.");

                if (!KnownKeys.Contains(lookup))
                    KnownKeys.Add(lookup);

                SetKey(lookup, key);
            }

            return true;
        }

        public static async Task<bool> LoadKeys(bool forceRedownload = false)
        {
            if (KnownKeys.Count > 0 || forceRedownload)
                KnownKeys.Clear();

            var download = forceRedownload;
            if (File.Exists("WoW.txt"))
            {
                var info = new FileInfo("WoW.txt");
                if (info.Length == 0 || DateTime.Now.Subtract(TimeSpan.FromHours(12)) > info.LastWriteTime)
                {
                    Console.WriteLine("TACT Keys outdated, redownloading..");
                    download = true;
                }
            }
            else
            {
                download = true;
            }

            if (download)
            {
                Console.WriteLine("Downloading TACT keys");

                var keyURL = new UriBuilder(SettingsManager.TACTKeyURL);
                var cacheBuster = "v=" + (long)DateTime.UtcNow.Subtract(DateTime.UnixEpoch).TotalSeconds;
                keyURL.Query = string.IsNullOrEmpty(keyURL.Query) ? cacheBuster : keyURL.Query.TrimStart('?') + "&" + cacheBuster;

                var keyFileContents = await SafeRemoteFile.DownloadAsync(keyURL.Uri.AbsoluteUri, MaxKeyFileBytes);
                if (!IsValidKeyFile(keyFileContents))
                    throw new InvalidDataException("Downloaded TACT key file does not contain valid key records.");

                File.WriteAllBytes("WoW.txt", keyFileContents);
            }

            if (CASC.IsTACTSharpInit)
                await LoadKeys();

            // If there are known statuses, make sure to reload.
            if (CASC.EncryptionStatuses.Count > 0)
                CASC.RefreshEncryptionStatus();

            Console.WriteLine("Loaded " + KnownKeys.Count + " known TACTkeys");

            return true;
        }

        private static bool IsValidKeyFile(byte[] contents)
        {
            using var reader = new StreamReader(new MemoryStream(contents));
            var hasKey = false;

            while (reader.ReadLine() is string line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (!TryParseKeyLine(line, out _, out _))
                    return false;

                hasKey = true;
            }

            return hasKey;
        }

        private static bool TryParseKeyLine(string line, out ulong lookup, out byte[] key)
        {
            lookup = 0;
            key = [];
            var splitLine = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (splitLine.Length != 2 || !ulong.TryParse(splitLine[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out lookup))
                return false;

            try
            {
                key = Convert.FromHexString(splitLine[1]);
                return key.Length == 16;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        public static bool ExportTACTKeys()
        {
            KnownKeys.Sort();

            var tactKeyLines = new List<string>();
            foreach (var key in KnownKeys)
            {
                if (!HasKey(key))
                    continue;

                tactKeyLines.Add(key.ToString("X16") + " " + Convert.ToHexString(GetKey(key)));
            }

            File.WriteAllLines("WoW.txt", tactKeyLines.ToArray());
            return true;
        }
    }
}
