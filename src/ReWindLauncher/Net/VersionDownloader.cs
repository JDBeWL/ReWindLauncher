using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using ReWindLauncher.Core;

namespace ReWindLauncher.Net
{
    public sealed class DownloadProgress
    {
        public string Phase;
        public int FilesDone;
        public int FilesTotal;
        public long BytesDone;
        public long BytesTotal;
        public double BytesPerSecond;
    }

    public enum DownloadContent
    {
        Game,
        Sounds
    }

    public sealed class VersionDownloader
    {
        private sealed class FileJob
        {
            public string Official;
            public string Mirror;
            public string Destination;
            public string Sha1;
            public long Size;
        }

        private readonly IProgress<DownloadProgress> _progress;
        private readonly Action<string> _status;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private DateTime _lastReport = DateTime.MinValue;
        private int _filesDone;
        private int _filesTotal;
        private long _bytesDone;
        private long _bytesTotal;

        public VersionDownloader(IProgress<DownloadProgress> progress, Action<string> status)
        {
            _progress = progress;
            _status = status;
        }

        public async Task DownloadAsync(RemoteVersion version, string gameRoot, DownloadContent content, CancellationToken token)
        {
            string id = version.Id;
            string versionDir = Path.Combine(gameRoot, "versions", id);
            string assetsDir = Path.Combine(gameRoot, "assets");
            string librariesDir = Path.Combine(gameRoot, "libraries");
            Directory.CreateDirectory(versionDir);

            _status?.Invoke("获取版本信息: " + id);
            string json = await MojangApi.GetStringAsync(
                version.Url,
                MojangApi.MirrorBase + "/version/" + id + "/json",
                token).ConfigureAwait(false);
            JObject root = JObject.Parse(json);
            string jsonPath = Path.Combine(versionDir, id + ".json");
            File.WriteAllText(jsonPath, json, new UTF8Encoding(false));

            var jobs = new List<FileJob>();
            if (content == DownloadContent.Game)
            {
                AddClientJob(root, id, versionDir, jobs);
                AddLibraryJobs(root, librariesDir, jobs);
            }

            _status?.Invoke("获取资源索引");
            await DownloadAssetIndexAsync(root, gameRoot, assetsDir, token).ConfigureAwait(false);
            AddAssetJobs(root, assetsDir, jobs, content == DownloadContent.Sounds);

            _filesTotal = jobs.Count;
            _bytesTotal = jobs.Sum(j => j.Size);
            _status?.Invoke((content == DownloadContent.Sounds ? "正在更新声音: " : "正在更新: ") + id +
                            "（0/" + _filesTotal + "）");
            Report();

            int concurrency = Math.Max(2, Math.Min(8, Environment.ProcessorCount));
            using (var semaphore = new SemaphoreSlim(concurrency))
            {
                var tasks = jobs.Select(async job =>
                {
                    await semaphore.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        await EnsureFileAsync(job, token).ConfigureAwait(false);
                        Interlocked.Increment(ref _filesDone);
                        Interlocked.Add(ref _bytesDone, job.Size > 0 ? job.Size : FileSize(job.Destination));
                        Report();
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }).ToList();

                await Task.WhenAll(tasks).ConfigureAwait(false);
            }

            if (content == DownloadContent.Game)
                await MaterializeLegacyAssetsAsync(root, gameRoot, assetsDir, token).ConfigureAwait(false);

            Report(true);
            _status?.Invoke((content == DownloadContent.Sounds ? "声音更新完成: " : "更新完成: ") + id);
            Log.Info("版本下载完成: " + id + " (" + content + ")");
        }

        private void AddClientJob(JObject root, string id, string versionDir, List<FileJob> jobs)
        {
            JToken client = root["downloads"]?["client"];
            if (client == null) return;
            jobs.Add(new FileJob
            {
                Official = (string)client["url"],
                Mirror = MojangApi.MirrorBase + "/version/" + id + "/client",
                Destination = Path.Combine(versionDir, id + ".jar"),
                Sha1 = (string)client["sha1"],
                Size = (long?)client["size"] ?? 0
            });
        }

        private void AddLibraryJobs(JObject root, string librariesDir, List<FileJob> jobs)
        {
            if (!(root["libraries"] is JArray libraries)) return;
            foreach (JToken token in libraries)
            {
                if (!(token is JObject lib) || !RulesAllow(lib)) continue;

                JObject artifact = lib["downloads"]?["artifact"] as JObject;
                if (artifact != null)
                {
                    string path = (string)artifact["path"];
                    if (!string.IsNullOrEmpty(path))
                    {
                        jobs.Add(new FileJob
                        {
                            Official = (string)artifact["url"],
                            Mirror = MojangApi.MirrorBase + "/maven/" + path,
                            Destination = Path.Combine(librariesDir, path.Replace('/', Path.DirectorySeparatorChar)),
                            Sha1 = (string)artifact["sha1"],
                            Size = (long?)artifact["size"] ?? 0
                        });
                    }
                }

                JObject natives = lib["natives"] as JObject;
                if (natives != null)
                {
                    string classifier = (string)natives["windows"];
                    if (!string.IsNullOrEmpty(classifier))
                    {
                        classifier = classifier.Replace("${arch}", Environment.Is64BitOperatingSystem ? "64" : "32");
                        JToken nativeArtifact = lib["downloads"]?["classifiers"]?[classifier];
                        string path = (string)nativeArtifact?["path"];
                        if (!string.IsNullOrEmpty(path))
                        {
                            jobs.Add(new FileJob
                            {
                                Official = (string)nativeArtifact["url"],
                                Mirror = MojangApi.MirrorBase + "/maven/" + path,
                                Destination = Path.Combine(librariesDir, path.Replace('/', Path.DirectorySeparatorChar)),
                                Sha1 = (string)nativeArtifact["sha1"],
                                Size = (long?)nativeArtifact["size"] ?? 0
                            });
                        }
                    }
                }
            }
        }

        private async Task DownloadAssetIndexAsync(JObject root, string gameRoot, string assetsDir, CancellationToken token)
        {
            JToken index = root["assetIndex"];
            string indexId = (string)index?["id"] ?? (string)root["assets"];
            if (string.IsNullOrEmpty(indexId)) return;

            string path = Path.Combine(assetsDir, "indexes", indexId + ".json");
            string official = (string)index?["url"] ?? MojangApi.MirrorBase + "/indexes/" + indexId + ".json";
            string mirror = MojangApi.MirrorBase + "/indexes/" + indexId + ".json";
            string sha1 = (string)index?["sha1"];
            long size = (long?)index?["size"] ?? 0;

            await EnsureFileAsync(new FileJob
            {
                Official = official,
                Mirror = mirror,
                Destination = path,
                Sha1 = sha1,
                Size = size
            }, token, false).ConfigureAwait(false);
        }

        private void AddAssetJobs(JObject root, string assetsDir, List<FileJob> jobs, bool soundsOnly)
        {
            string indexId = (string)root["assetIndex"]?["id"] ?? (string)root["assets"];
            if (string.IsNullOrEmpty(indexId)) return;

            string indexPath = Path.Combine(assetsDir, "indexes", indexId + ".json");
            if (!File.Exists(indexPath)) return;

            JObject index = JObject.Parse(File.ReadAllText(indexPath));
            if (!(index["objects"] is JObject objects)) return;

            foreach (var property in objects.Properties())
            {
                bool isSound = property.Name.StartsWith("minecraft/sounds/", StringComparison.OrdinalIgnoreCase);
                if (soundsOnly != isSound) continue;

                JToken entry = property.Value;
                string hash = (string)entry["hash"];
                if (string.IsNullOrEmpty(hash) || hash.Length < 2) continue;
                long size = (long?)entry["size"] ?? 0;
                string sub = hash.Substring(0, 2);
                string dest = Path.Combine(assetsDir, "objects", sub, hash);
                jobs.Add(new FileJob
                {
                    Official = "https://resources.download.minecraft.net/" + sub + "/" + hash,
                    Mirror = MojangApi.MirrorBase + "/assets/" + sub + "/" + hash,
                    Destination = dest,
                    Sha1 = hash,
                    Size = size
                });
            }
        }

        private async Task MaterializeLegacyAssetsAsync(JObject root, string gameRoot, string assetsDir, CancellationToken token)
        {
            string indexId = (string)root["assetIndex"]?["id"] ?? (string)root["assets"];
            if (string.IsNullOrEmpty(indexId)) return;
            string indexPath = Path.Combine(assetsDir, "indexes", indexId + ".json");
            if (!File.Exists(indexPath)) return;

            JObject index = JObject.Parse(File.ReadAllText(indexPath));
            bool isVirtual = index["virtual"] != null && index["virtual"].Type == JTokenType.Boolean &&
                             (bool)index["virtual"];
            if (!isVirtual) return;

            JObject objects = index["objects"] as JObject;
            if (objects == null) return;

            _status?.Invoke("整理旧版资源");
            string virtualDir = Path.Combine(assetsDir, "virtual", "legacy");
            foreach (var property in objects.Properties())
            {
                token.ThrowIfCancellationRequested();
                string hash = (string)property.Value["hash"];
                if (string.IsNullOrEmpty(hash) || hash.Length < 2) continue;
                string source = Path.Combine(assetsDir, "objects", hash.Substring(0, 2), hash);
                string destination = Path.Combine(virtualDir, property.Name.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(source) || File.Exists(destination)) continue;
                string parent = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                try { File.Copy(source, destination, false); } catch { }
            }
            await Task.Yield();
        }

        private async Task EnsureFileAsync(FileJob job, CancellationToken token, bool report = true)
        {
            if (string.IsNullOrEmpty(job.Destination)) return;
            string dir = Path.GetDirectoryName(job.Destination);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            if (File.Exists(job.Destination))
            {
                long length = FileSize(job.Destination);
                bool sizeOk = job.Size <= 0 || length == job.Size;
                if (sizeOk && (string.IsNullOrEmpty(job.Sha1) || await VerifyAsync(job.Destination, job.Sha1, token).ConfigureAwait(false)))
                    return;
            }

            string temp = job.Destination + ".tmp";
            Exception last = null;
            foreach (string url in new[] { job.Official, job.Mirror })
            {
                if (string.IsNullOrEmpty(url)) continue;
                try
                {
                    await DownloadToFileAsync(url, temp, token).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(job.Sha1) &&
                        !await VerifyAsync(temp, job.Sha1, token).ConfigureAwait(false))
                    {
                        try { File.Delete(temp); } catch { }
                        throw new InvalidDataException("文件校验失败: " + Path.GetFileName(job.Destination));
                    }
                    if (File.Exists(job.Destination)) File.Delete(job.Destination);
                    File.Move(temp, job.Destination);
                    return;
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
            throw last ?? new IOException("下载失败: " + job.Destination);
        }

        private static async Task DownloadToFileAsync(string url, string tempPath, CancellationToken token)
        {
            using (var response = await MojangApi.Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token)
                       .ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                           81920, true))
                {
                    await input.CopyToAsync(output, 81920, token).ConfigureAwait(false);
                }
            }
        }

        private static async Task<bool> VerifyAsync(string path, string expected, CancellationToken token)
        {
            try
            {
                byte[] hash = await Task.Run(() =>
                {
                    using (var sha = SHA1.Create())
                    using (var stream = File.OpenRead(path))
                        return sha.ComputeHash(stream);
                }, token).ConfigureAwait(false);
                string actual = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static long FileSize(string path)
        {
            try { return new FileInfo(path).Length; } catch { return 0; }
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
                if (rule["features"] != null) continue;
                allowed = (string)rule["action"] == "allow";
            }
            return allowed;
        }

        private void Report(bool force = false)
        {
            if (!force && (DateTime.Now - _lastReport).TotalMilliseconds < 300) return;
            _lastReport = DateTime.Now;

            double speed = _clock.Elapsed.TotalSeconds > 0.2 ? _bytesDone / _clock.Elapsed.TotalSeconds : 0;
            _progress?.Report(new DownloadProgress
            {
                Phase = "同步文件",
                FilesDone = Volatile.Read(ref _filesDone),
                FilesTotal = _filesTotal,
                BytesDone = Interlocked.Read(ref _bytesDone),
                BytesTotal = _bytesTotal,
                BytesPerSecond = speed
            });
        }
    }
}
