using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace ReWindLauncher.Core
{
    public sealed class LaunchPlan
    {
        public string JavaPath;
        public string Arguments;
        public string WorkingDirectory;
        public string GameDirectory;
        public string NativesDirectory;
        public string LogPath;
        public string VersionId;
        public string AssetIndex;
        public string AssetsIndexPath;
        public int JavaMajor;
        public readonly List<string> MissingFiles = new List<string>();
    }

    public sealed class LaunchException : Exception
    {
        public LaunchException(string message) : base(message)
        {
        }
    }

    public static class LaunchBuilder
    {
        public static LaunchPlan Build(Config cfg, VersionEntry entry, string userName, AuthSession auth = null)
        {
            if (entry == null) throw new LaunchException("没有选择游戏,请选择或点击更新按钮更新游戏");
            if (string.IsNullOrWhiteSpace(userName)) throw new LaunchException("没有填写游戏名字");

            string root = cfg.ResolvedGameRoot;
            JObject version = VersionStore.LoadMerged(entry, root);
            bool legacy = version == null;

            int requiredMajor = 8;
            if (!legacy)
                requiredMajor = (int?)version["javaVersion"]?["majorVersion"] ?? 8;

            string java;
            int actualMajor;
            if (!string.IsNullOrWhiteSpace(cfg.JavaPath) && File.Exists(cfg.JavaPath))
            {
                java = cfg.JavaPath;
                actualMajor = JavaLocator.GetMajorVersion(java);
            }
            else
            {
                java = JavaLocator.PickForMajor(requiredMajor, out actualMajor);
            }

            if (java == null) throw new LaunchException("没有找到java路径,请手动指定javaw.exe");
            if (actualMajor > 0 && actualMajor < requiredMajor)
                throw new LaunchException("此版本需要 Java " + requiredMajor + "，当前找到的是 Java " +
                                          actualMajor + "，请在设置中指定 javaw.exe。");
            if (legacy && actualMajor > 8)
                Log.Info("提示: 旧版本可能不兼容 Java " + actualMajor);

            string gameDir = root;
            if (cfg.Isolate && !legacy)
            {
                gameDir = Path.Combine(root, "versions", entry.Id);
                Directory.CreateDirectory(gameDir);
                string marker = Path.Combine(gameDir, "游戏独立");
                if (!File.Exists(marker)) File.WriteAllBytes(marker, new byte[0]);
            }

            string nativesDir = Path.Combine(root, "natives");
            string assetsDir = Path.Combine(root, "assets");
            string librariesDir = Path.Combine(root, "libraries");
            PrepareNativesDirectory(nativesDir);

            var plan = new LaunchPlan
            {
                JavaPath = java,
                VersionId = entry.Id,
                GameDirectory = gameDir,
                NativesDirectory = nativesDir,
                WorkingDirectory = gameDir,
                JavaMajor = actualMajor,
                LogPath = Path.Combine(cfg.BaseDir, "输出日志.txt")
            };

            int xmx = cfg.MemoryAuto
                ? JavaLocator.AutoMemoryMB(java)
                : Math.Max(256, cfg.MemoryMB);
            int xms = Math.Min(xmx, 500);

            var jvm = new List<string>
            {
                "-Xms" + xms.ToString(CultureInfo.InvariantCulture) + "M",
                "-Xmx" + xmx.ToString(CultureInfo.InvariantCulture) + "M"
            };

            string classpath;
            string mainClass;
            string assetIndex;
            var gameArgs = new List<string>();

            if (legacy)
            {
                if (auth != null)
                    Log.Info("旧版本不支持微软正版会话，将以离线身份启动");
                classpath = BuildLegacyClasspath(root, entry, plan.MissingFiles);
                mainClass = "net.minecraft.client.Minecraft";
                assetIndex = "legacy";
                gameArgs.AddRange(new[]
                {
                    "--username", userName,
                    "--session", "0",
                    "--version", entry.Id,
                    "--gameDir", gameDir,
                    "--assetsDir", assetsDir
                });
            }
            else
            {
                assetIndex = (string)version["assetIndex"]?["id"] ?? (string)version["assets"] ?? "legacy";
                classpath = BuildClasspath(version, root, entry, plan.MissingFiles);
                mainClass = (string)version["mainClass"] ?? "net.minecraft.client.Minecraft";
                ExtractNatives(version, root, nativesDir, plan.MissingFiles, entry, JavaLocator.Is64Bit(java));
                gameArgs.AddRange(BuildGameArguments(version, root, entry, userName, gameDir, assetsDir, assetIndex, nativesDir, classpath, auth));
            }

            bool jvmHasClasspath = false;
            if (!legacy && version["arguments"]?["jvm"] is JArray jvmArgs)
            {
                var replaced = new List<string>();
                foreach (JToken token in jvmArgs)
                {
                    if (token.Type == JTokenType.String)
                    {
                        string arg = Replace((string)token, root, entry, userName, gameDir, assetsDir, assetIndex, nativesDir, classpath, auth);
                        if (arg.Contains(classpath)) jvmHasClasspath = true;
                        replaced.Add(arg);
                    }
                    else if (token is JObject obj)
                    {
                        if (!RulesAllow(obj)) continue;
                        string value = (string)obj["value"];
                        if (string.IsNullOrEmpty(value)) continue;
                        string arg = Replace(value, root, entry, userName, gameDir, assetsDir, assetIndex, nativesDir, classpath, auth);
                        if (arg.Contains(classpath)) jvmHasClasspath = true;
                        replaced.Add(arg);
                    }
                }
                jvm.AddRange(replaced);
            }

            jvm.Add("-Djava.library.path=" + nativesDir);
            jvm.Add("-Dfml.ignoreInvalidMinecraftCertificates=true");
            jvm.Add("-Dfml.ignorePatchDiscrepancies=true");
            jvm.Add("-Dlog4j2.formatMsgNoLookups=true");

            if (!jvmHasClasspath)
            {
                jvm.Add("-cp");
                jvm.Add(classpath);
            }

            if (!legacy)
            {
                string clientJar = ResolveClientJar(root, entry);
                if (string.IsNullOrEmpty(clientJar) || !File.Exists(clientJar))
                    plan.MissingFiles.Add(Path.Combine(root, "versions", entry.Id, entry.Id + ".jar"));
            }

            plan.AssetIndex = assetIndex;
            plan.AssetsIndexPath = Path.Combine(assetsDir, "indexes", assetIndex + ".json");

            var allArgs = new List<string>(jvm);
            allArgs.Add(mainClass);
            allArgs.AddRange(gameArgs);

            plan.Arguments = string.Join(" ", allArgs.Select(QuoteArgument));
            Log.Debug("启动命令: " + plan.JavaPath + " " + plan.Arguments);
            return plan;
        }

        private static void PrepareNativesDirectory(string nativesDir)
        {
            if (!Directory.Exists(nativesDir))
            {
                Directory.CreateDirectory(nativesDir);
                return;
            }
            try
            {
                foreach (string file in Directory.GetFiles(nativesDir))
                {
                    try { File.Delete(file); } catch { }
                }
            }
            catch
            {
            }
        }

        private static string BuildLegacyClasspath(string root, VersionEntry entry, List<string> missing)
        {
            var parts = new List<string>();
            string[] candidates =
            {
                Path.Combine(root, "bin", "minecraft.jar"),
                Path.Combine(root, "bin", "lwjgl.jar"),
                Path.Combine(root, "bin", "jinput.jar"),
                Path.Combine(root, "bin", "lwjgl_util.jar")
            };
            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate)) parts.Add(candidate);
                else missing.Add(candidate);
            }
            if (parts.Count == 0 && File.Exists(entry.JarPath)) parts.Add(entry.JarPath);
            return string.Join(";", parts);
        }

        private static string BuildClasspath(JObject version, string root, VersionEntry entry, List<string> missing)
        {
            var parts = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (version["libraries"] is JArray libraries)
            {
                foreach (JToken token in libraries)
                {
                    if (!(token is JObject lib) || !RulesAllow(lib)) continue;
                    string path = ArtifactPath(lib);
                    if (string.IsNullOrEmpty(path)) continue;
                    string full = Path.Combine(root, "libraries", path.Replace('/', Path.DirectorySeparatorChar));
                    if (seen.Add(full) && File.Exists(full)) parts.Add(full);
                    else if (!File.Exists(full)) missing.Add(full);
                }
            }

            string clientJar = ResolveClientJar(root, entry);
            if (!string.IsNullOrEmpty(clientJar) && File.Exists(clientJar) && seen.Add(clientJar))
                parts.Add(clientJar);

            return string.Join(";", parts);
        }

        /// <summary>Forge/OptiFine 自身没有客户端 jar，回退到 `inheritsFrom` 的基础版本。</summary>
        private static string ResolveClientJar(string root, VersionEntry entry)
        {
            string own = Path.Combine(root, "versions", entry.Id, entry.Id + ".jar");
            if (File.Exists(own)) return own;

            if (!string.IsNullOrEmpty(entry.InheritsFrom))
            {
                string parent = Path.Combine(root, "versions", entry.InheritsFrom,
                    entry.InheritsFrom + ".jar");
                if (File.Exists(parent)) return parent;
            }

            return entry.JarPath;
        }

        private static string ArtifactPath(JObject lib)
        {
            string path = (string)lib["downloads"]?["artifact"]?["path"];
            if (!string.IsNullOrEmpty(path)) return path;

            string name = (string)lib["name"];
            if (string.IsNullOrEmpty(name)) return null;
            string[] segments = name.Split(':');
            if (segments.Length < 3) return null;
            string group = segments[0].Replace('.', '/');
            string artifact = segments[1];
            string version = segments[2];
            string classifier = segments.Length > 3 ? "-" + segments[3] : "";
            return group + "/" + artifact + "/" + version + "/" + artifact + "-" + version + classifier + ".jar";
        }

        private static void ExtractNatives(JObject version, string root, string nativesDir, List<string> missing, VersionEntry entry, bool is64)
        {
            if (!(version["libraries"] is JArray libraries)) return;
            string arch = is64 ? "64" : "32";

            foreach (JToken token in libraries)
            {
                if (!(token is JObject lib) || !RulesAllow(lib)) continue;
                string name = (string)lib["name"] ?? "";
                string classifier = null;

                JToken natives = lib["natives"];
                if (natives is JObject nativesMap)
                {
                    classifier = (string)nativesMap["windows"];
                    if (classifier != null) classifier = classifier.Replace("${arch}", arch);
                }
                if (classifier == null && name.IndexOf("natives-windows", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    int lastColon = name.LastIndexOf(':');
                    if (lastColon >= 0) classifier = name.Substring(lastColon + 1);
                }
                if (classifier == null) continue;

                string path = (string)lib["downloads"]?["classifiers"]?[classifier]?["path"];
                if (string.IsNullOrEmpty(path))
                {
                    string[] segments = name.Split(':');
                    if (segments.Length >= 3)
                    {
                        string group = segments[0].Replace('.', '/');
                        string artifact = segments[1];
                        string ver = segments[2];
                        path = group + "/" + artifact + "/" + ver + "/" + artifact + "-" + ver + "-" + classifier + ".jar";
                    }
                }
                if (string.IsNullOrEmpty(path)) continue;
                string jar = Path.Combine(root, "libraries", path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(jar))
                {
                    missing.Add(jar);
                    continue;
                }
                try
                {
                    using (ZipArchive archive = ZipFile.OpenRead(jar))
                    {
                        foreach (ZipArchiveEntry zipEntry in archive.Entries)
                        {
                            if (string.IsNullOrEmpty(zipEntry.Name)) continue;
                            string entryName = zipEntry.FullName.Replace('/', Path.DirectorySeparatorChar);
                            if (entryName.StartsWith("META-INF", StringComparison.OrdinalIgnoreCase)) continue;
                            if (entryName.Contains("..")) continue;
                            string destination = Path.Combine(nativesDir, entryName);
                            string parent = Path.GetDirectoryName(destination);
                            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                            zipEntry.ExtractToFile(destination, true);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("解压 natives 失败: " + jar, ex);
                }
            }
        }

        private static IEnumerable<string> BuildGameArguments(JObject version, string root, VersionEntry entry,
            string userName, string gameDir, string assetsDir, string assetIndex, string nativesDir, string classpath,
            AuthSession auth)
        {
            var result = new List<string>();
            JToken game = version["arguments"]?["game"];
            if (game is JArray array)
            {
                foreach (JToken token in array)
                {
                    if (token.Type == JTokenType.String)
                    {
                        result.Add(Replace((string)token, root, entry, userName, gameDir, assetsDir, assetIndex, nativesDir, classpath, auth));
                    }
                    else if (token is JObject obj)
                    {
                        if (!RulesAllow(obj)) continue;
                        if (obj["value"] is JArray values)
                        {
                            foreach (JToken value in values)
                                if (value.Type == JTokenType.String)
                                    result.Add(Replace((string)value, root, entry, userName, gameDir, assetsDir, assetIndex, nativesDir, classpath, auth));
                        }
                        else
                        {
                            string value = (string)obj["value"];
                            if (!string.IsNullOrEmpty(value))
                                result.Add(Replace(value, root, entry, userName, gameDir, assetsDir, assetIndex, nativesDir, classpath, auth));
                        }
                    }
                }
            }
            else
            {
                string minecraftArguments = (string)version["minecraftArguments"];
                if (!string.IsNullOrEmpty(minecraftArguments))
                {
                    foreach (string value in minecraftArguments.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                        result.Add(Replace(value, root, entry, userName, gameDir, assetsDir, assetIndex, nativesDir, classpath, auth));
                }
            }
            return result;
        }

        private static string Replace(string input, string root, VersionEntry entry, string userName,
            string gameDir, string assetsDir, string assetIndex, string nativesDir, string classpath,
            AuthSession auth)
        {
            if (string.IsNullOrEmpty(input)) return input;

            string uuid = auth != null && !string.IsNullOrEmpty(auth.Uuid) ? auth.Uuid : OfflineUuid(userName);
            string accessToken = auth != null && !string.IsNullOrEmpty(auth.AccessToken) ? auth.AccessToken : "0";
            string userType = auth != null ? "msa" : "legacy";
            string xuid = auth != null ? auth.Xuid : "";

            return input
                .Replace("${auth_player_name}", userName)
                .Replace("${version_name}", entry.Id)
                .Replace("${game_directory}", gameDir)
                .Replace("${assets_root}", assetsDir)
                .Replace("${game_assets}", Path.Combine(assetsDir, "virtual", "legacy"))
                .Replace("${assets_index_name}", assetIndex)
                .Replace("${auth_uuid}", uuid)
                .Replace("${auth_access_token}", accessToken)
                .Replace("${auth_session}", "0")
                .Replace("${user_type}", userType)
                .Replace("${version_type}", "release")
                .Replace("${user_properties}", "{}")
                .Replace("${clientid}", "")
                .Replace("${auth_xuid}", xuid)
                .Replace("${natives_directory}", nativesDir)
                .Replace("${library_directory}", Path.Combine(root, "libraries"))
                .Replace("${classpath_separator}", ";")
                .Replace("${classpath}", classpath)
                .Replace("${launcher_name}", "ReWindLauncher")
                .Replace("${launcher_version}", "1.35");
        }

        private static bool RulesAllow(JObject item)
        {
            if (!(item["rules"] is JArray rules) || rules.Count == 0) return true;
            bool allowed = false;
            bool is64 = Environment.Is64BitOperatingSystem;
            foreach (JToken token in rules)
            {
                if (!(token is JObject rule)) continue;
                JObject os = rule["os"] as JObject;
                if (os != null)
                {
                    string name = (string)os["name"];
                    if (!string.IsNullOrEmpty(name) && !name.Equals("windows", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string arch = (string)os["arch"];
                    if (!string.IsNullOrEmpty(arch))
                    {
                        bool want64 = arch.Contains("64");
                        if (want64 != is64) continue;
                    }
                }
                JObject features = rule["features"] as JObject;
                if (features != null)
                {
                    // 复刻版不启用 demo/自定义分辨率，带 `features` 的规则一律跳过
                    continue;
                }
                string action = (string)rule["action"];
                allowed = action == "allow";
            }
            return allowed;
        }

        public static string OfflineUuid(string name)
        {
            using (MD5 md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
                hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
                hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
                string hex = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                return hex.Substring(0, 8) + "-" + hex.Substring(8, 4) + "-" + hex.Substring(12, 4) +
                       "-" + hex.Substring(16, 4) + "-" + hex.Substring(20, 12);
            }
        }

        public static string QuoteArgument(string value)
        {
            if (value.Length == 0) return "\"\"";
            if (value.IndexOfAny(new[] { ' ', '\t', '"', '\n', '\v' }) < 0)
                return value;

            var sb = new StringBuilder();
            sb.Append('"');
            int backslashes = 0;
            foreach (char c in value)
            {
                if (c == '\\')
                {
                    backslashes++;
                }
                else if (c == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                    backslashes = 0;
                }
                else
                {
                    sb.Append('\\', backslashes);
                    sb.Append(c);
                    backslashes = 0;
                }
            }
            sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }
    }
}
