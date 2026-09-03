using System;
using System.IO;
using System.Text.Json;

namespace Choibin.Core
{
    public class AppSettings
    {
        public const int DiscoveryPort = 53317;
        public const int TransferPort = 53318;
        public const int WebPort = 53319;
        public const string MulticastGroup = "239.77.77.77";

        public string DeviceName { get; set; }
        public string SaveFolder { get; set; }
        public bool AutoAccept { get; set; }
        public bool PhoneAccessEnabled { get; set; }

        /// <summary>スマホ向けページをHTTPSで配ります（自己署名のため初回に警告が出ます）。</summary>
        public bool PhoneUseHttps { get; set; }

        /// <summary>送信前に確認コードを表示して、相手の画面と見比べてから送ります。</summary>
        public bool VerifyCode { get; set; }

        private static string ConfigPath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Choibin");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "settings.json");
            }
        }

        public static AppSettings Load()
        {
            AppSettings s = null;
            try
            {
                if (File.Exists(ConfigPath))
                    s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(ConfigPath));
            }
            catch
            {
                s = null;
            }

            if (s == null) s = new AppSettings();

            if (string.IsNullOrWhiteSpace(s.DeviceName))
                s.DeviceName = Environment.MachineName;

            if (string.IsNullOrWhiteSpace(s.SaveFolder) || !IsUsableFolder(s.SaveFolder))
                s.SaveFolder = DefaultSaveFolder();

            if (!File.Exists(ConfigPath))
            {
                s.PhoneAccessEnabled = true;
                s.PhoneUseHttps = true;
            }

            return s;
        }

        public void Save()
        {
            try
            {
                var opts = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, opts));
            }
            catch
            {
                // 設定の保存に失敗しても動作は続けます
            }
        }

        private static bool IsUsableFolder(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                return Directory.Exists(path);
            }
            catch
            {
                return false;
            }
        }

        public static string DefaultSaveFolder()
        {
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string path = Path.Combine(baseDir, "Downloads", "Choibin");
            try
            {
                Directory.CreateDirectory(path);
            }
            catch
            {
                path = Path.Combine(Path.GetTempPath(), "Choibin");
                Directory.CreateDirectory(path);
            }
            return path;
        }
    }
}
