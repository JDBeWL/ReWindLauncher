using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ReWindLauncher.Net
{
    public sealed class RemoteVersion
    {
        public string Id;
        public string Type;
        public string Url;
        public DateTime ReleaseTime;

        public override string ToString() => Id;
    }

    internal static class MojangApi
    {
        public const string OfficialManifest = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
        public const string MirrorManifest = "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json";
        public const string MirrorBase = "https://bmclapi2.bangbang93.com";

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                AllowAutoRedirect = true
            };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(100) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ReWindLauncher/1.35");
            return client;
        }

        public static async Task<string> GetStringAsync(string official, string mirror, CancellationToken token)
        {
            Exception last = null;
            foreach (string url in new[] { official, mirror })
            {
                if (string.IsNullOrEmpty(url)) continue;
                try
                {
                    using (var response = await Http.GetAsync(url, token).ConfigureAwait(false))
                    {
                        response.EnsureSuccessStatusCode();
                        return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    last = ex;
                }
            }
            throw last ?? new InvalidOperationException("下载失败");
        }

        public static async Task<List<RemoteVersion>> GetVersionListAsync(bool includeSnapshots, CancellationToken token)
        {
            string json = await GetStringAsync(OfficialManifest, MirrorManifest, token).ConfigureAwait(false);
            JObject root = JObject.Parse(json);
            var versions = new List<RemoteVersion>();
            if (root["versions"] is JArray array)
            {
                foreach (JToken tokenVersion in array)
                {
                    string type = (string)tokenVersion["type"];
                    bool allowed = type == "release" ||
                                   (includeSnapshots && (type == "snapshot" || type == "old_beta" || type == "old_alpha"));
                    if (!allowed) continue;
                    DateTime.TryParse((string)tokenVersion["releaseTime"], CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal, out DateTime release);
                    versions.Add(new RemoteVersion
                    {
                        Id = (string)tokenVersion["id"],
                        Type = type,
                        Url = (string)tokenVersion["url"],
                        ReleaseTime = release
                    });
                }
            }
            return versions;
        }

        public static HttpClient Client => Http;
    }
}
