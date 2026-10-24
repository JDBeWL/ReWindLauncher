using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using ReWindLauncher.Core;

namespace ReWindLauncher.Net
{
    public sealed class ForgeBuild
    {
        public string Minecraft;
        public string Version;
        public int Build;
        public DateTime Released;
        public string InstallerSha1;

        public string Id => Minecraft + "-forge-" + Version;

        public override string ToString() => Version;
    }

    public static class ForgeService
    {
        private const string BmclApi = "https://bmclapi2.bangbang93.com/forge";
        private const string Maven = "https://maven.minecraftforge.net/net/minecraftforge/forge";
        private const string AltMaven = "https://files.minecraftforge.net/maven/net/minecraftforge/forge";
        private const string Promotions = "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json";

        // 1.12.2 之前的安装器没有 --installClient，无法静默安装
        private const string MinHeadlessMinecraft = "1.12.2";

        public static async Task<List<string>> GetMinecraftVersionsAsync(CancellationToken token)
        {
            var versions = new List<string>();
            try
            {
                string json = await GetStringAsync(BmclApi + "/minecraft", token)
                    .ConfigureAwait(false);
                if (JArray.Parse(json) is JArray array)
                {
                    foreach (JToken item in array)
                    {
                        string id = (string)item;
                        if (!string.IsNullOrEmpty(id)) versions.Add(id);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error("获取 Forge 版本列表失败", ex);
            }

            if (versions.Count == 0)
            {
                foreach (string id in await GetPromotionMinecraftVersionsAsync(token).ConfigureAwait(false))
                    versions.Add(id);
            }

            return versions
                .Where(id => IsHeadlessSupported(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(id => id, Comparer<string>.Create(CompareVersion))
                .ToList();
        }

        public static async Task<List<ForgeBuild>> GetBuildsAsync(string minecraft, CancellationToken token)
        {
            var builds = new List<ForgeBuild>();
            if (string.IsNullOrEmpty(minecraft)) return builds;

            try
            {
                string json = await GetStringAsync(BmclApi + "/minecraft/" + Uri.EscapeDataString(minecraft), token)
                    .ConfigureAwait(false);
                if (JArray.Parse(json) is JArray array)
                {
                    foreach (JToken item in array)
                    {
                        if (!(item is JObject obj)) continue;
                        var build = new ForgeBuild
                        {
                            Minecraft = (string)obj["mcversion"] ?? minecraft,
                            Version = (string)obj["version"],
                            Build = (int?)obj["build"] ?? 0
                        };
                        if (string.IsNullOrEmpty(build.Version)) continue;
                        build.Released = ParseTime((string)obj["modified"]);

                        if (obj["files"] is JArray files)
                        {
                            foreach (JToken file in files)
                            {
                                if ((string)file["category"] != "installer") continue;
                                if ((string)file["format"] != "jar") continue;
                                build.InstallerSha1 = (string)file["hash"];
                                break;
                            }
                        }
                        builds.Add(build);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error("获取 Forge 构建列表失败: " + minecraft, ex);
            }

            if (builds.Count == 0)
                builds.AddRange(await GetPromotionBuildsAsync(minecraft, token).ConfigureAwait(false));

            return builds
                .GroupBy(b => b.Version, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderByDescending(b => b.Build)
                .ThenByDescending(b => b.Version, Comparer<string>.Create(CompareVersion))
                .ToList();
        }

        public static IEnumerable<string> InstallerUrls(ForgeBuild build)
        {
            // 1.12.2+ 的安装包在 Maven 上按 {mc}-{forge} 命名
            string artifact = build.Minecraft + "-" + build.Version;
            string file = "forge-" + artifact + "-installer.jar";
            yield return Maven + "/" + artifact + "/" + file;
            yield return AltMaven + "/" + artifact + "/" + file;
        }

        public static async Task<string> DownloadInstallerAsync(ForgeBuild build, string preferredTempDir,
            Action<string> status, CancellationToken token)
        {
            string tempDir = ResolveTempDir(preferredTempDir);
            string fileName = "forge-" + build.Minecraft + "-" + build.Version + "-installer.jar";
            string destination = Path.Combine(tempDir, fileName);

            if (File.Exists(destination) &&
                (string.IsNullOrEmpty(build.InstallerSha1) ||
                 await VerifyAsync(destination, build.InstallerSha1, token).ConfigureAwait(false)))
                return destination;

            string temp = destination + ".tmp";
            Exception last = null;
            foreach (string url in InstallerUrls(build))
            {
                try
                {
                    status?.Invoke("正在下载 Forge 安装器 " + build.Version + "...");
                    using (var response = await MojangApi.Client
                               .GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, token)
                               .ConfigureAwait(false))
                    {
                        response.EnsureSuccessStatusCode();
                        using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None,
                                   81920, true))
                        {
                            await input.CopyToAsync(output, 81920, token).ConfigureAwait(false);
                        }
                    }

                    if (!string.IsNullOrEmpty(build.InstallerSha1) &&
                        !await VerifyAsync(temp, build.InstallerSha1, token).ConfigureAwait(false))
                    {
                        try { File.Delete(temp); } catch { }
                        throw new InvalidDataException("Forge 安装器校验失败");
                    }

                    if (File.Exists(destination)) File.Delete(destination);
                    File.Move(temp, destination);
                    return destination;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    last = ex;
                    try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                }
            }
            throw last ?? new IOException("无法下载 Forge 安装器");
        }

        public static async Task InstallAsync(string javaExe, string installerPath, string gameRoot,
            Action<string> status, CancellationToken token)
        {
            EnsureLauncherProfiles(gameRoot);

            var info = new ProcessStartInfo
            {
                FileName = javaExe,
                Arguments = "-jar " + LaunchBuilder.QuoteArgument(installerPath) +
                            " --installClient " + LaunchBuilder.QuoteArgument(gameRoot),
                WorkingDirectory = Path.GetDirectoryName(installerPath) ?? gameRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            var lines = new Queue<string>();
            var lastReport = DateTime.MinValue;

            void OnLine(string line)
            {
                if (string.IsNullOrWhiteSpace(line)) return;
                string clean = line.Trim();
                Log.Debug("[Forge] " + clean);
                lock (lines)
                {
                    lines.Enqueue(clean);
                    while (lines.Count > 30) lines.Dequeue();
                }
                if (!IsInteresting(clean)) return;
                if ((DateTime.Now - lastReport).TotalMilliseconds < 250) return;
                lastReport = DateTime.Now;
                status?.Invoke("正在安装 Forge：" + Shorten(clean, 96));
            }

            using (var process = new Process { StartInfo = info, EnableRaisingEvents = true })
            {
                process.OutputDataReceived += (sender, args) => OnLine(args.Data);
                process.ErrorDataReceived += (sender, args) => OnLine(args.Data);

                var completed = new TaskCompletionSource<int>();
                process.Exited += (sender, args) =>
                {
                    int code = 0;
                    try { code = process.ExitCode; } catch { }
                    completed.TrySetResult(code);
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                using (token.Register(() =>
                       {
                           try
                           {
                               if (!process.HasExited) process.Kill();
                           }
                           catch
                           {
                           }
                           completed.TrySetCanceled();
                       }))
                {
                    int exitCode;
                    try
                    {
                        exitCode = await completed.Task.ConfigureAwait(false);
                    }
                    catch (TaskCanceledException)
                    {
                        throw new OperationCanceledException(token);
                    }

                    // 进程退出后异步输出还在泵里，等它收尾再取日志
                    try { await Task.Run(() => process.WaitForExit(5000)).ConfigureAwait(false); } catch { }

                    if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                    if (exitCode != 0)
                    {
                        string tail = Tail(lines);
                        throw new InvalidOperationException(
                            "Forge 安装器返回错误码 " + exitCode + "。" +
                            (string.IsNullOrEmpty(tail) ? "" : "\r\n" + tail));
                    }
                }
            }
        }

        public static VersionEntry FindInstalled(string gameRoot, string minecraft, string forgeVersion)
        {
            string expected = minecraft + "-forge-" + forgeVersion;
            List<VersionEntry> versions = VersionStore.Scan(gameRoot);

            VersionEntry exact = versions.FirstOrDefault(v =>
                string.Equals(v.Id, expected, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;

            return versions.FirstOrDefault(v =>
                string.Equals(v.InheritsFrom, minecraft, StringComparison.OrdinalIgnoreCase) &&
                v.Id.IndexOf("forge", StringComparison.OrdinalIgnoreCase) >= 0 &&
                v.Id.IndexOf(forgeVersion, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public static string CopyOptiFine(string gameRoot, VersionEntry entry, bool isolate, string jarPath)
        {
            string modsDir = isolate
                ? Path.Combine(gameRoot, "versions", entry.Id, "mods")
                : Path.Combine(gameRoot, "mods");
            Directory.CreateDirectory(modsDir);

            string destination = Path.Combine(modsDir, Path.GetFileName(jarPath));
            File.Copy(jarPath, destination, true);
            return destination;
        }

        public static bool IsHeadlessSupported(string minecraft)
        {
            return CompareVersion(minecraft, MinHeadlessMinecraft) >= 0;
        }

        public static int CompareVersion(string left, string right)
        {
            string[] a = (left ?? "").Split('.');
            string[] b = (right ?? "").Split('.');
            int count = Math.Max(a.Length, b.Length);
            for (int i = 0; i < count; i++)
            {
                int x = i < a.Length ? ParseVersionPart(a[i]) : 0;
                int y = i < b.Length ? ParseVersionPart(b[i]) : 0;
                if (x != y) return x.CompareTo(y);
            }
            return string.Compare(left ?? "", right ?? "", StringComparison.OrdinalIgnoreCase);
        }

        private static int ParseVersionPart(string part)
        {
            string clean = part.Split('_')[0];
            return int.TryParse(clean, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                ? value
                : 0;
        }

        private static DateTime ParseTime(string value)
        {
            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal,
                    out DateTime parsed))
                return parsed;
            return DateTime.MinValue;
        }

        private static async Task<List<string>> GetPromotionMinecraftVersionsAsync(CancellationToken token)
        {
            var result = new List<string>();
            try
            {
                JObject root = JObject.Parse(await GetStringAsync(Promotions, token)
                    .ConfigureAwait(false));
                if (root["promos"] is JObject promos)
                {
                    foreach (var property in promos.Properties())
                    {
                        int dash = property.Name.LastIndexOf('-');
                        if (dash <= 0) continue;
                        result.Add(property.Name.Substring(0, dash));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error("获取 Forge promotions 失败", ex);
            }
            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static async Task<List<ForgeBuild>> GetPromotionBuildsAsync(string minecraft, CancellationToken token)
        {
            var result = new List<ForgeBuild>();
            try
            {
                JObject root = JObject.Parse(await GetStringAsync(Promotions, token)
                    .ConfigureAwait(false));
                if (!(root["promos"] is JObject promos)) return result;

                foreach (var property in promos.Properties())
                {
                    string prefix = minecraft + "-";
                    if (!property.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    string version = (string)property.Value;
                    if (string.IsNullOrEmpty(version)) continue;

                    result.Add(new ForgeBuild
                    {
                        Minecraft = minecraft,
                        Version = version,
                        Build = property.Name.EndsWith("-recommended", StringComparison.OrdinalIgnoreCase) ? 2 : 1
                    });
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error("获取 Forge promotions 构建失败", ex);
            }
            return result;
        }

        private static async Task<string> GetStringAsync(string url, CancellationToken token)
        {
            using (var response = await MojangApi.Client.GetAsync(url, token).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
        }

        private static string ResolveTempDir(string preferred)
        {
            try
            {
                Directory.CreateDirectory(preferred);
                string probe = Path.Combine(preferred, ".write-test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return preferred;
            }
            catch
            {
                string fallback = Path.Combine(Path.GetTempPath(), "ReWindLauncher", "forge");
                Directory.CreateDirectory(fallback);
                return fallback;
            }
        }

        private static void EnsureLauncherProfiles(string gameRoot)
        {
            Directory.CreateDirectory(gameRoot);
            string path = Path.Combine(gameRoot, "launcher_profiles.json");
            if (File.Exists(path)) return;
            File.WriteAllText(path, "{\"profiles\":{},\"settings\":{},\"version\":3}",
                new UTF8Encoding(false));
            Log.Info("已创建 launcher_profiles.json");
        }

        private static bool IsInteresting(string line)
        {
            return line.StartsWith("Downloading", StringComparison.OrdinalIgnoreCase) ||
                   line.StartsWith("Installing", StringComparison.OrdinalIgnoreCase) ||
                   line.StartsWith("Extracting", StringComparison.OrdinalIgnoreCase) ||
                   line.StartsWith("Building", StringComparison.OrdinalIgnoreCase) ||
                   line.StartsWith("Injecting", StringComparison.OrdinalIgnoreCase) ||
                   line.StartsWith("Validating", StringComparison.OrdinalIgnoreCase) ||
                   line.StartsWith("Successfully", StringComparison.OrdinalIgnoreCase);
        }

        private static string Shorten(string value, int max)
        {
            if (string.IsNullOrEmpty(value)) return "";
            value = value.Replace("\r", " ").Replace("\n", " ").Trim();
            if (value.Length <= max) return value;
            return value.Substring(0, max - 1) + "…";
        }

        private static string Tail(Queue<string> lines)
        {
            lock (lines)
            {
                return string.Join("\r\n", lines.ToArray());
            }
        }

        private static async Task<bool> VerifyAsync(string path, string expected, CancellationToken token)
        {
            try
            {
                string actual = await Task.Run(() =>
                {
                    using (var sha = SHA1.Create())
                    using (var stream = File.OpenRead(path))
                        return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                }, token).ConfigureAwait(false);
                return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return false;
            }
        }
    }
}
