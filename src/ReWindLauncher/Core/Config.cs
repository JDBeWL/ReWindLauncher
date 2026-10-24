using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ReWindLauncher.Core
{
    public sealed class Config
    {
        private const string FileName = "rewindlauncher.cfg";

        public string UserName = "Player";
        public bool UseMicrosoft;
        public bool MemoryAuto = true;
        public int MemoryMB = 1024;
        public string JavaPath = "";
        public string GameRoot = "";
        public string SelectedVersion = "";
        public bool Isolate;
        public bool RandomSkin = true;
        public bool DebugMode;
        public string Mirror = "auto";
        public string ClientId = "";
        public bool FirstRun = true;
        public string DisplayVersion = "1.35";

        public string BaseDir { get; private set; }
        public string ConfigPath { get; private set; }

        public string ResolvedGameRoot
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(GameRoot)) return GameRoot;
                return Path.Combine(BaseDir, ".minecraft");
            }
        }

        public string VersionsDir => Path.Combine(ResolvedGameRoot, "versions");
        public string SkinDir => Path.Combine(BaseDir, "bgskin");

        public static Config Load()
        {
            var cfg = new Config();
            cfg.BaseDir = AppDomain.CurrentDomain.BaseDirectory;
            string primary = Path.Combine(cfg.BaseDir, FileName);
            string fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ReWindLauncher", FileName);

            string path = File.Exists(primary) ? primary : (File.Exists(fallback) ? fallback : primary);
            cfg.ConfigPath = path;
            if (!File.Exists(path)) return cfg;

            try
            {
                Dictionary<string, Dictionary<string, string>> data = Parse(File.ReadAllLines(path, Encoding.UTF8));
                cfg.UserName = Get(data, "User", "Name", cfg.UserName);
                cfg.UseMicrosoft = GetBool(data, "User", "UseMicrosoft", cfg.UseMicrosoft);
                cfg.MemoryAuto = GetBool(data, "Memory", "Auto", cfg.MemoryAuto);
                cfg.MemoryMB = GetInt(data, "Memory", "MB", cfg.MemoryMB);
                cfg.JavaPath = Get(data, "Java", "Path", cfg.JavaPath);
                cfg.GameRoot = Get(data, "Game", "Root", cfg.GameRoot);
                cfg.SelectedVersion = Get(data, "Game", "Version", cfg.SelectedVersion);
                cfg.Isolate = GetBool(data, "Game", "Isolate", cfg.Isolate);
                cfg.RandomSkin = GetBool(data, "UI", "RandomSkin", cfg.RandomSkin);
                cfg.DebugMode = GetBool(data, "Debug", "Enabled", cfg.DebugMode);
                cfg.Mirror = Get(data, "Update", "Mirror", cfg.Mirror);
                cfg.ClientId = Get(data, "Auth", "ClientId", cfg.ClientId);
                cfg.FirstRun = GetBool(data, "Launcher", "FirstRun", cfg.FirstRun);
            }
            catch (Exception ex)
            {
                Log.Error("读取配置失败", ex);
            }
            return cfg;
        }

        public void Save()
        {
            string target = ConfigPath;
            try
            {
                WriteTo(target);
                return;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
            {
                target = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "ReWindLauncher", FileName);
                ConfigPath = target;
                WriteTo(target);
            }
        }

        private void WriteTo(string path)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var sb = new StringBuilder();
            sb.AppendLine("; ReWindLauncher 复刻版配置");
            sb.AppendLine("[User]");
            sb.AppendLine("Name=" + UserName);
            sb.AppendLine("UseMicrosoft=" + B(UseMicrosoft));
            sb.AppendLine();
            sb.AppendLine("[Memory]");
            sb.AppendLine("Auto=" + B(MemoryAuto));
            sb.AppendLine("MB=" + MemoryMB.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine();
            sb.AppendLine("[Java]");
            sb.AppendLine("Path=" + JavaPath);
            sb.AppendLine();
            sb.AppendLine("[Game]");
            sb.AppendLine("Root=" + GameRoot);
            sb.AppendLine("Version=" + SelectedVersion);
            sb.AppendLine("Isolate=" + B(Isolate));
            sb.AppendLine();
            sb.AppendLine("[UI]");
            sb.AppendLine("RandomSkin=" + B(RandomSkin));
            sb.AppendLine();
            sb.AppendLine("[Debug]");
            sb.AppendLine("Enabled=" + B(DebugMode));
            sb.AppendLine();
            sb.AppendLine("[Update]");
            sb.AppendLine("Mirror=" + (string.IsNullOrEmpty(Mirror) ? "auto" : Mirror));
            sb.AppendLine();
            sb.AppendLine("[Auth]");
            sb.AppendLine("ClientId=" + ClientId);
            sb.AppendLine();
            sb.AppendLine("[Launcher]");
            sb.AppendLine("FirstRun=" + B(FirstRun));
            sb.AppendLine("Version=" + DisplayVersion);

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        private static string B(bool value) => value ? "1" : "0";

        private static Dictionary<string, Dictionary<string, string>> Parse(string[] lines)
        {
            var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            string section = "";
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    section = line.Substring(1, line.Length - 2).Trim();
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();
                if (!result.TryGetValue(section, out var map))
                {
                    map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    result[section] = map;
                }
                map[key] = value;
            }
            return result;
        }

        private static string Get(Dictionary<string, Dictionary<string, string>> data, string section, string key, string fallback)
        {
            if (data.TryGetValue(section, out var map) && map.TryGetValue(key, out string value)) return value;
            return fallback;
        }

        private static bool GetBool(Dictionary<string, Dictionary<string, string>> data, string section, string key, bool fallback)
        {
            string value = Get(data, section, key, null);
            if (value == null) return fallback;
            value = value.Trim().ToLowerInvariant();
            return value == "1" || value == "true" || value == "yes" || value == "真";
        }

        private static int GetInt(Dictionary<string, Dictionary<string, string>> data, string section, string key, int fallback)
        {
            string value = Get(data, section, key, null);
            return int.TryParse(value, out int parsed) ? parsed : fallback;
        }
    }
}
