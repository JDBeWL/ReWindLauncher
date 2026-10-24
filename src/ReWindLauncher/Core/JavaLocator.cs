using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ReWindLauncher.Core
{
    public static class JavaLocator
    {
        private static readonly object CacheGate = new object();
        private static List<string> _cachedAll;
        private static readonly Dictionary<string, int> _majorCache =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public static string Find(string configured)
        {
            if (IsJavaExecutable(configured)) return configured;
            var all = FindAll();
            return all.OrderByDescending(GetMajorVersion).FirstOrDefault();
        }

        public static List<string> FindAll() => FindAll(false);

        // 缓存扫描结果，重复调用不再遍历注册表与 Program Files
        public static List<string> FindAll(bool refresh)
        {
            lock (CacheGate)
            {
                if (refresh || _cachedAll == null)
                {
                    _majorCache.Clear();
                    _cachedAll = DiscoverAll();
                }
                return new List<string>(_cachedAll);
            }
        }

        private static List<string> DiscoverAll()
        {
            var results = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string candidate)
            {
                if (string.IsNullOrEmpty(candidate) || !File.Exists(candidate)) return;
                if (!Path.GetFileName(candidate).Equals("javaw.exe", StringComparison.OrdinalIgnoreCase)) return;
                if (seen.Add(candidate)) results.Add(candidate);
            }

            foreach (string fromRegistry in FromRegistry()) Add(fromRegistry);

            string javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
            if (!string.IsNullOrEmpty(javaHome))
                Add(Path.Combine(javaHome, "bin", "javaw.exe"));

            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string part in path.Split(';'))
            {
                if (string.IsNullOrWhiteSpace(part)) continue;
                Add(Path.Combine(part.Trim(), "javaw.exe"));
            }

            foreach (string root in CommonRoots())
                Add(SearchUnder(root));

            return results;
        }

        public static string PickForMajor(int requiredMajor, out int actualMajor)
        {
            actualMajor = 0;
            var candidates = FindAll()
                .Select(path => new { Path = path, Major = GetMajorVersion(path) })
                .Where(item => item.Major > 0)
                .ToList();
            if (candidates.Count == 0) return null;

            var exact = candidates
                .Where(item => item.Major == requiredMajor)
                .OrderByDescending(item => item.Major)
                .FirstOrDefault();
            if (exact != null)
            {
                actualMajor = exact.Major;
                return exact.Path;
            }

            var newer = candidates
                .Where(item => item.Major >= requiredMajor)
                .OrderBy(item => item.Major)
                .FirstOrDefault();
            if (newer != null)
            {
                actualMajor = newer.Major;
                return newer.Path;
            }

            var fallback = candidates.OrderByDescending(item => item.Major).First();
            actualMajor = fallback.Major;
            return fallback.Path;
        }

        private static bool IsJavaExecutable(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                return File.Exists(path) &&
                       Path.GetFileName(path).Equals("javaw.exe", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static IEnumerable<string> CommonRoots()
        {
            var roots = new List<string>();
            AddRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java");
            AddRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Java");
            AddRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Eclipse Adoptium");
            AddRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft");
            AddRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Zulu");
            AddRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BellSoft");
            AddRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Amazon Corretto");
            AddRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AdoptOpenJDK");
            AddRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Eclipse Adoptium");
            AddRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
            return roots.Where(Directory.Exists);
        }

        private static void AddRoot(List<string> roots, string programFiles, string vendor)
        {
            if (string.IsNullOrEmpty(programFiles)) return;
            roots.Add(Path.Combine(programFiles, vendor));
        }

        private static string SearchUnder(string root)
        {
            try
            {
                foreach (string dir in Directory.GetDirectories(root))
                {
                    string direct = Path.Combine(dir, "bin", "javaw.exe");
                    if (File.Exists(direct)) return direct;
                    foreach (string sub in Directory.GetDirectories(dir))
                    {
                        string nested = Path.Combine(sub, "bin", "javaw.exe");
                        if (File.Exists(nested)) return nested;
                    }
                }
            }
            catch
            {
            }
            return null;
        }

        private static IEnumerable<string> FromRegistry()
        {
            var results = new List<string>();
            string[] keys =
            {
                @"SOFTWARE\JavaSoft\Java Runtime Environment",
                @"SOFTWARE\JavaSoft\Java Development Kit",
                @"SOFTWARE\JavaSoft\JRE",
                @"SOFTWARE\JavaSoft\JDK"
            };
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                foreach (string key in keys)
                {
                    try
                    {
                        using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                        using (RegistryKey javaKey = baseKey.OpenSubKey(key))
                        {
                            if (javaKey == null) continue;
                            string current = javaKey.GetValue("CurrentVersion") as string;
                            var versions = new List<string>();
                            if (!string.IsNullOrEmpty(current)) versions.Add(current);
                            versions.AddRange(javaKey.GetSubKeyNames());
                            foreach (string version in versions.Distinct())
                            {
                                using (RegistryKey home = javaKey.OpenSubKey(version))
                                {
                                    string javaHome = home?.GetValue("JavaHome") as string;
                                    if (string.IsNullOrEmpty(javaHome)) continue;
                                    string javaw = Path.Combine(javaHome, "bin", "javaw.exe");
                                    if (File.Exists(javaw)) results.Add(javaw);
                                }
                            }
                        }
                    }
                    catch
                    {
                    }
                }
            }
            return results;
        }

        public static bool Is64Bit(string javaExe)
        {
            try
            {
                using (var stream = new FileStream(javaExe, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new BinaryReader(stream))
                {
                    stream.Position = 0x3C;
                    int pe = reader.ReadInt32();
                    stream.Position = pe + 4;
                    ushort machine = reader.ReadUInt16();
                    return machine == 0x8664 || machine == 0xAA64;
                }
            }
            catch
            {
                return Environment.Is64BitOperatingSystem;
            }
        }

        public static int GetMajorVersion(string javaExe)
        {
            if (string.IsNullOrEmpty(javaExe)) return 0;
            lock (CacheGate)
            {
                if (_majorCache.TryGetValue(javaExe, out int cached)) return cached;
            }

            int major = ReadMajorVersion(javaExe);
            if (major > 0)
            {
                lock (CacheGate) _majorCache[javaExe] = major;
            }
            return major;
        }

        private static int ReadMajorVersion(string javaExe)
        {
            try
            {
                string home = Path.GetDirectoryName(Path.GetDirectoryName(javaExe));
                string release = Path.Combine(home, "release");
                if (!File.Exists(release)) return 0;
                foreach (string line in File.ReadAllLines(release))
                {
                    if (!line.StartsWith("JAVA_VERSION", StringComparison.OrdinalIgnoreCase)) continue;
                    Match match = Regex.Match(line, "\"([^\"]+)\"");
                    if (!match.Success) continue;
                    string version = match.Groups[1].Value;
                    if (version.StartsWith("1."))
                    {
                        string[] parts = version.Split('.');
                        if (parts.Length > 1 && int.TryParse(parts[1], out int legacy)) return legacy;
                    }
                    string[] modern = version.Split('.');
                    if (modern.Length > 0 && int.TryParse(modern[0], out int major)) return major;
                }
            }
            catch
            {
            }
            return 0;
        }

        public static string Describe(string javaExe)
        {
            if (string.IsNullOrEmpty(javaExe)) return "未找到";
            int major = GetMajorVersion(javaExe);
            string bits = Is64Bit(javaExe) ? "64 位" : "32 位";
            return (major > 0 ? "Java " + major + " · " : "") + bits;
        }

        public static int GetFreeMemoryMB()
        {
            try
            {
                var status = new NativeMethods.MEMORYSTATUSEX();
                status.dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf(status);
                if (NativeMethods.GlobalMemoryStatusEx(ref status))
                    return (int)(status.ullAvailPhys / (1024 * 1024));
            }
            catch
            {
            }
            return 0;
        }

        public static int AutoMemoryMB(string javaExe)
        {
            int free = GetFreeMemoryMB();
            int value = free > 0 ? free / 4 : 1024;
            value = Math.Max(500, Math.Min(1300, value));
            if (!Is64Bit(javaExe)) value = Math.Min(value, 1024);
            return value;
        }
    }
}
