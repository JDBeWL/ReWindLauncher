using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ReWindLauncher.Core
{
    /// <summary>`AccessToken` 是 Minecraft token，不是微软的 `access_token`。</summary>
    public sealed class AuthSession
    {
        public string Name = "";
        public string Uuid = "";
        public string AccessToken = "";
        public string RefreshToken = "";
        public string Xuid = "";
        public long ExpiresAt;

        public bool IsExpiring =>
            DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= ExpiresAt - 300;
    }

    public sealed class DeviceCodeInfo
    {
        public string UserCode = "";
        public string DeviceCode = "";
        public string VerificationUri = "";
        public int Interval = 5;
        public int ExpiresIn = 900;
    }

    public sealed class AuthException : Exception
    {
        public readonly bool Canceled;
        public readonly bool PortBusy;

        public AuthException(string message, bool canceled = false, bool portBusy = false) : base(message)
        {
            Canceled = canceled;
            PortBusy = portBusy;
        }
    }

    /// <summary>微软 → Xbox Live → XSTS → Minecraft 四段链，设备代码与浏览器回环共用后三段。</summary>
    public static class MicrosoftAuth
    {
        public const string DefaultClientId = "907a248d-3eb5-4d01-99d2-ff72d79c5eb1";

        private const string DeviceCodeEndpoint =
            "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";
        private const string TokenEndpoint =
            "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";
        private const string AuthorizeEndpoint =
            "https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize";
        private const string XboxLiveEndpoint = "https://user.auth.xboxlive.com/user/authenticate";
        private const string XstsEndpoint = "https://xsts.auth.xboxlive.com/xsts/authorize";
        private const string MinecraftLoginEndpoint =
            "https://api.minecraftservices.com/authentication/login_with_xbox";
        private const string MinecraftProfileEndpoint =
            "https://api.minecraftservices.com/minecraft/profile";

        private const string Scope = "XboxLive.signin offline_access";
        private const string RedirectUri = "http://localhost:26669/relogin";
        private const int CallbackPort = 26669;
        private const int CallbackTimeoutSeconds = 300;

        private static readonly HttpClient Http = CreateHttp();

        private static HttpClient CreateHttp()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "ReWindLauncher/1.35");
            return client;
        }

        public static string ClientIdFor(Config cfg)
        {
            string id = cfg?.ClientId;
            return string.IsNullOrWhiteSpace(id) ? DefaultClientId : id.Trim();
        }

        public static async Task<DeviceCodeInfo> StartDeviceCodeAsync(string clientId, CancellationToken token)
        {
            HttpResult result = await PostFormAsync(DeviceCodeEndpoint, new Dictionary<string, string>
            {
                { "client_id", clientId },
                { "scope", Scope }
            }, token);

            if (result.Status != HttpStatusCode.OK) throw new AuthException(DescribeOAuthError(result.Body));

            JObject json = ParseJson(result.Body);
            return new DeviceCodeInfo
            {
                UserCode = (string)json["user_code"] ?? "",
                DeviceCode = (string)json["device_code"] ?? "",
                VerificationUri = (string)json["verification_uri"] ?? "https://www.microsoft.com/link",
                Interval = (int?)json["interval"] ?? 5,
                ExpiresIn = (int?)json["expires_in"] ?? 900
            };
        }

        public static async Task<AuthSession> LoginWithDeviceCodeAsync(string clientId, DeviceCodeInfo info,
            Action<string> status, CancellationToken token)
        {
            int interval = Math.Max(1, info.Interval);
            DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(60, info.ExpiresIn));

            status?.Invoke("等待你在网页中完成授权…");
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(interval), token);
                }
                catch (OperationCanceledException)
                {
                    throw new AuthException("登录已取消", true);
                }

                HttpResult result = await PostFormAsync(TokenEndpoint, new Dictionary<string, string>
                {
                    { "client_id", clientId },
                    { "grant_type", "urn:ietf:params:oauth:grant-type:device_code" },
                    { "device_code", info.DeviceCode }
                }, token);

                if (result.Status == HttpStatusCode.OK)
                    return await CompleteAsync(result.Body, null, token);

                string error = ReadErrorCode(result.Body);
                switch (error)
                {
                    case "authorization_pending":
                        continue;
                    case "slow_down":
                        interval += 5;
                        continue;
                    case "expired_token":
                        throw new AuthException("设备代码已过期，请重新发起登录。");
                    case "authorization_declined":
                    case "access_denied":
                        throw new AuthException("你拒绝了本次授权。");
                    default:
                        throw new AuthException(DescribeOAuthError(result.Body));
                }
            }

            throw new AuthException("设备代码已过期，请重新发起登录。");
        }

        public static string BuildAuthorizeUrl(string clientId, out string codeVerifier, out string state)
        {
            codeVerifier = RandomToken();
            state = RandomToken();
            string challenge = Base64Url(Sha256(Encoding.ASCII.GetBytes(codeVerifier)));

            string query = "client_id=" + Uri.EscapeDataString(clientId) +
                           "&response_type=code" +
                           "&redirect_uri=" + Uri.EscapeDataString(RedirectUri) +
                           "&scope=" + Uri.EscapeDataString(Scope) +
                           "&code_challenge=" + Uri.EscapeDataString(challenge) +
                           "&code_challenge_method=S256" +
                           "&state=" + Uri.EscapeDataString(state) +
                           "&prompt=select_account";
            return AuthorizeEndpoint + "?" + query;
        }

        public static async Task<AuthSession> LoginWithBrowserAsync(string clientId, string authorizeUrl,
            string codeVerifier, string state, Action<string> status, CancellationToken token)
        {
            status?.Invoke("正在打开浏览器…");
            OpenUrl(authorizeUrl);
            status?.Invoke("已在浏览器中打开登录页，完成授权后会自动返回。");

            string code = await WaitForCallbackAsync(state, token);
            status?.Invoke("正在验证微软账号…");

            HttpResult result = await PostFormAsync(TokenEndpoint, new Dictionary<string, string>
            {
                { "client_id", clientId },
                { "grant_type", "authorization_code" },
                { "code", code },
                { "redirect_uri", RedirectUri },
                { "code_verifier", codeVerifier },
                { "scope", Scope }
            }, token);

            if (result.Status != HttpStatusCode.OK) throw new AuthException(DescribeOAuthError(result.Body));
            return await CompleteAsync(result.Body, null, token);
        }

        public static bool IsCallbackPortAvailable()
        {
            var listener = new TcpListener(IPAddress.Loopback, CallbackPort);
            try
            {
                listener.Start();
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
            finally
            {
                try { listener.Stop(); } catch { }
            }
        }

        public static async Task<string> WaitForCallbackAsync(string expectedState, CancellationToken token)
        {
            var listener = new TcpListener(IPAddress.Loopback, CallbackPort);
            try
            {
                listener.Start();
            }
            catch (SocketException)
            {
                throw new AuthException(
                    "本地回调端口 " + CallbackPort + " 被占用，已自动改用设备代码登录。", false, true);
            }

            try
            {
                Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync();
                _ = acceptTask.ContinueWith(t => { var ignored = t.Exception; },
                    TaskContinuationOptions.OnlyOnFaulted);

                var cancelSignal = new TaskCompletionSource<bool>();
                using (token.Register(() => cancelSignal.TrySetResult(true)))
                {
                    Task timeoutTask = Task.Delay(TimeSpan.FromSeconds(CallbackTimeoutSeconds));
                    Task finished = await Task.WhenAny(acceptTask, cancelSignal.Task, timeoutTask);
                    if (finished == cancelSignal.Task)
                        throw new AuthException("登录已取消", true);
                    if (finished != acceptTask)
                        throw new AuthException("等待浏览器回调超时，请重新发起登录。");
                }

                using (TcpClient client = await acceptTask)
                using (NetworkStream stream = client.GetStream())
                {
                    string target = await ReadRequestTargetAsync(stream);
                    Dictionary<string, string> query = ParseQuery(target);

                    bool stateOk = query.TryGetValue("state", out string state) &&
                                   state == expectedState;
                    bool hasCode = query.TryGetValue("code", out string code);

                    await WriteResponseAsync(stream, hasCode && stateOk);

                    if (query.TryGetValue("error", out string error))
                    {
                        query.TryGetValue("error_description", out string description);
                        throw new AuthException("登录被拒绝：" + error +
                                                (string.IsNullOrEmpty(description) ? "" : " - " + description));
                    }
                    if (!stateOk)
                        throw new AuthException("回调校验失败，请重新发起登录。");
                    if (string.IsNullOrEmpty(code))
                        throw new AuthException("没有收到授权码，请重新发起登录。");
                    return code;
                }
            }
            finally
            {
                try { listener.Stop(); } catch { }
            }
        }

        private static async Task<string> ReadRequestTargetAsync(NetworkStream stream)
        {
            // 读完整请求头再回写，否则关闭连接时残留数据会让浏览器收到 RST
            var reader = new StreamReader(stream, Encoding.ASCII, false, 256, true);
            string requestLine = await reader.ReadLineAsync() ?? "";

            int guard = 0;
            while (guard++ < 100)
            {
                string line = await reader.ReadLineAsync();
                if (string.IsNullOrEmpty(line)) break;
            }

            string[] parts = requestLine.Split(' ');
            return parts.Length >= 2 ? parts[1] : "";
        }

        private static async Task WriteResponseAsync(NetworkStream stream, bool success)
        {
            string body = success ? SuccessPage : FailurePage;
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
            byte[] headBytes = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: text/html; charset=utf-8\r\n" +
                "Content-Length: " + bodyBytes.Length + "\r\n" +
                "Connection: close\r\n\r\n");

            await stream.WriteAsync(headBytes, 0, headBytes.Length);
            await stream.WriteAsync(bodyBytes, 0, bodyBytes.Length);
            await stream.FlushAsync();
        }

        private static Dictionary<string, string> ParseQuery(string target)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int mark = target.IndexOf('?');
            if (mark < 0 || mark + 1 >= target.Length) return result;

            foreach (string pair in target.Substring(mark + 1).Split('&'))
            {
                if (pair.Length == 0) continue;
                int eq = pair.IndexOf('=');
                string key = eq < 0 ? pair : pair.Substring(0, eq);
                string value = eq < 0 ? "" : pair.Substring(eq + 1);
                result[Uri.UnescapeDataString(key.Replace("+", " "))] =
                    Uri.UnescapeDataString(value.Replace("+", " "));
            }
            return result;
        }

        /// <summary>刷新令牌只换得到微软 token，后面整条链要重走一遍。</summary>
        public static async Task<AuthSession> RefreshAsync(AuthSession session, string clientId,
            CancellationToken token)
        {
            if (session == null || string.IsNullOrEmpty(session.RefreshToken))
                throw new AuthException("没有可用的正版登录信息，请重新登录。");

            HttpResult result = await PostFormAsync(TokenEndpoint, new Dictionary<string, string>
            {
                { "client_id", clientId },
                { "grant_type", "refresh_token" },
                { "refresh_token", session.RefreshToken },
                { "scope", Scope }
            }, token);

            if (result.Status != HttpStatusCode.OK)
            {
                string error = ReadErrorCode(result.Body);
                if (error == "invalid_grant")
                    throw new AuthException("正版登录已失效，请在设置中重新登录。");
                throw new AuthException("刷新正版登录失败：" + DescribeOAuthError(result.Body));
            }

            return await CompleteAsync(result.Body, session.RefreshToken, token);
        }

        private static async Task<AuthSession> CompleteAsync(string microsoftTokenJson, string fallbackRefresh,
            CancellationToken token)
        {
            JObject tokenJson = ParseJson(microsoftTokenJson);
            string msAccess = (string)tokenJson["access_token"];
            string refresh = (string)tokenJson["refresh_token"];
            if (string.IsNullOrEmpty(refresh)) refresh = fallbackRefresh ?? "";
            if (string.IsNullOrEmpty(msAccess))
                throw new AuthException("微软登录没有返回 access_token。");

            string xblToken = await XboxLiveAsync(msAccess, token);
            (string xstsToken, string uhs, string xuid) = await XstsAsync(xblToken, token);
            (string mcToken, int mcExpires) = await MinecraftLoginAsync(xstsToken, uhs, token);
            (string name, string uuid) = await MinecraftProfileAsync(mcToken, token);

            return new AuthSession
            {
                Name = name,
                Uuid = uuid,
                AccessToken = mcToken,
                RefreshToken = refresh,
                Xuid = xuid ?? "",
                ExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + Math.Max(3600, mcExpires)
            };
        }

        private static async Task<string> XboxLiveAsync(string microsoftAccessToken, CancellationToken token)
        {
            var payload = new JObject
            {
                ["Properties"] = new JObject
                {
                    ["AuthMethod"] = "RPS",
                    ["SiteName"] = "user.auth.xboxlive.com",
                    ["RpsTicket"] = "d=" + microsoftAccessToken
                },
                ["RelyingParty"] = "http://auth.xboxlive.com",
                ["TokenType"] = "JWT"
            };

            HttpResult result = await PostJsonAsync(XboxLiveEndpoint, payload, token);
            if (result.Status != HttpStatusCode.OK)
                throw new AuthException("Xbox Live 认证失败：" + DescribeXboxError(result));

            JObject json = ParseJson(result.Body);
            string tokenValue = (string)json["Token"];
            if (string.IsNullOrEmpty(tokenValue))
                throw new AuthException("Xbox Live 认证结果中缺少 Token。");
            return tokenValue;
        }

        private static async Task<(string Token, string Uhs, string Xuid)> XstsAsync(string xboxLiveToken,
            CancellationToken token)
        {
            var payload = new JObject
            {
                ["Properties"] = new JObject
                {
                    ["SandboxId"] = "RETAIL",
                    ["UserTokens"] = new JArray(xboxLiveToken)
                },
                ["RelyingParty"] = "rp://api.minecraftservices.com/",
                ["TokenType"] = "JWT"
            };

            HttpResult result = await PostJsonAsync(XstsEndpoint, payload, token);
            if (result.Status != HttpStatusCode.OK)
            {
                long xerr = ReadXErr(result.Body);
                switch (xerr)
                {
                    case 2148916233:
                        throw new AuthException(
                            "该微软账号还没有 Xbox 档案。请先用同一账号打开 xbox.com 登录一次，再回来重试。");
                    case 2148916235:
                        throw new AuthException("该账号所在地区不支持 Xbox Live。");
                    case 2148916238:
                        throw new AuthException("该账号是儿童账号，需要先由家庭组织者加入 Microsoft 家庭组。");
                    case 2148916227:
                        throw new AuthException("该账号已被 Xbox Live 封禁。");
                    default:
                        throw new AuthException("XSTS 认证失败：" + DescribeXboxError(result));
                }
            }

            JObject json = ParseJson(result.Body);
            JArray xui = json["DisplayClaims"]?["xui"] as JArray;
            if (xui == null || xui.Count == 0)
                throw new AuthException("XSTS 认证结果中缺少用户声明。");

            string uhs = (string)xui[0]["uhs"];
            string xuid = (string)xui[0]["xid"];
            string xsts = (string)json["Token"];
            if (string.IsNullOrEmpty(uhs) || string.IsNullOrEmpty(xsts))
                throw new AuthException("XSTS 认证结果不完整。");
            return (xsts, uhs, xuid);
        }

        private static async Task<(string Token, int ExpiresIn)> MinecraftLoginAsync(string xstsToken,
            string uhs, CancellationToken token)
        {
            var payload = new JObject
            {
                ["identityToken"] = "XBL3.0 x=" + uhs + ";" + xstsToken
            };

            HttpResult result = await PostJsonAsync(MinecraftLoginEndpoint, payload, token);
            if (result.Status == (HttpStatusCode)429)
                throw new AuthException("登录请求过于频繁，请稍后再试。");
            if (result.Status != HttpStatusCode.OK)
                throw new AuthException("Minecraft 认证失败：" + DescribeXboxError(result));

            JObject json = ParseJson(result.Body);
            string accessToken = (string)json["access_token"];
            if (string.IsNullOrEmpty(accessToken))
                throw new AuthException("Minecraft 认证结果中缺少 access_token。");
            return (accessToken, (int?)json["expires_in"] ?? 86400);
        }

        private static async Task<(string Name, string Uuid)> MinecraftProfileAsync(string accessToken,
            CancellationToken token)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, MinecraftProfileEndpoint))
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
                using (HttpResponseMessage response = await Http.SendAsync(request, token))
                {
                    string body = await response.Content.ReadAsStringAsync();
                    if (response.StatusCode == HttpStatusCode.NotFound)
                        throw new AuthException("该微软账号没有购买 Java 版 Minecraft，无法正版登录。");
                    if (!response.IsSuccessStatusCode)
                        throw new AuthException("获取 Minecraft 档案失败（" + (int)response.StatusCode + "）。");

                    JObject json = ParseJson(body);
                    string name = (string)json["name"];
                    string uuid = (string)json["id"];
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(uuid))
                        throw new AuthException("Minecraft 档案缺少名字或 UUID。");
                    return (name, uuid);
                }
            }
        }

        private sealed class HttpResult
        {
            public HttpStatusCode Status;
            public string Body;
        }

        private static async Task<HttpResult> PostFormAsync(string url, Dictionary<string, string> form,
            CancellationToken token)
        {
            using (var content = new FormUrlEncodedContent(form))
            using (HttpResponseMessage response = await Http.PostAsync(url, content, token))
            {
                return new HttpResult
                {
                    Status = response.StatusCode,
                    Body = await response.Content.ReadAsStringAsync()
                };
            }
        }

        private static async Task<HttpResult> PostJsonAsync(string url, JObject payload, CancellationToken token)
        {
            using (var content = new StringContent(payload.ToString(Newtonsoft.Json.Formatting.None),
                       Encoding.UTF8, "application/json"))
            using (HttpResponseMessage response = await Http.PostAsync(url, content, token))
            {
                return new HttpResult
                {
                    Status = response.StatusCode,
                    Body = await response.Content.ReadAsStringAsync()
                };
            }
        }

        private static JObject ParseJson(string body)
        {
            try
            {
                return JObject.Parse(body);
            }
            catch (Exception)
            {
                throw new AuthException("服务器返回了无法解析的数据。");
            }
        }

        private static string ReadErrorCode(string body)
        {
            try
            {
                return (string)JObject.Parse(body)["error"] ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static long ReadXErr(string body)
        {
            try
            {
                return (long?)JObject.Parse(body)["XErr"] ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        private static string DescribeXboxError(HttpResult result)
        {
            try
            {
                JObject json = JObject.Parse(result.Body);
                string message = (string)json["Message"];
                if (!string.IsNullOrEmpty(message)) return message;
            }
            catch
            {
            }
            return "HTTP " + (int)result.Status + "。";
        }

        private static string DescribeOAuthError(string body)
        {
            string error = "";
            string description = "";
            try
            {
                JObject json = JObject.Parse(body);
                error = (string)json["error"] ?? "";
                description = (string)json["error_description"] ?? "";
            }
            catch
            {
            }

            if (description.Contains("AADSTS700016"))
                return "Client ID 无效，请在 rewindlauncher.cfg 的 [Auth] ClientId 中填写正确的应用 ID。";
            if (description.Contains("AADSTS7000218"))
                return "该应用未启用“允许公共客户端流”，请在 Azure 应用注册中开启后重试。";
            if (error == "invalid_grant")
                return "登录授权已失效，请重新登录。";
            if (error == "invalid_client")
                return "微软不认可这个 Client ID，请检查应用注册。";

            if (!string.IsNullOrEmpty(description))
            {
                int cut = description.IndexOf("Trace ID", StringComparison.OrdinalIgnoreCase);
                if (cut > 0) description = description.Substring(0, cut);
                description = description.Replace("\r", " ").Replace("\n", " ").Trim();
                if (description.Length > 300) description = description.Substring(0, 300);
                if (!string.IsNullOrEmpty(description)) return description;
            }
            if (!string.IsNullOrEmpty(error)) return "微软登录失败：" + error;
            return "微软登录服务返回了未知错误。";
        }

        private static void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                throw new AuthException("无法打开浏览器：" + ex.Message);
            }
        }

        private static string RandomToken()
        {
            byte[] bytes = new byte[32];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            return Base64Url(bytes);
        }

        private static byte[] Sha256(byte[] data)
        {
            using (var sha = SHA256.Create())
                return sha.ComputeHash(data);
        }

        private static string Base64Url(byte[] data)
        {
            return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private const string SuccessPage =
            "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>登录成功</title>" +
            "<style>body{margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;" +
            "font-family:'Segoe UI','Microsoft YaHei',sans-serif;background:#eaf3f8;color:#333}" +
            ".card{background:#fff;border:1px solid #c6d9e6;padding:36px 44px;text-align:center}" +
            "h1{font-size:20px;margin:0 0 10px;font-weight:600}p{margin:0;color:#555;font-size:13px}" +
            "</style></head><body><div class=\"card\"><h1>登录成功</h1>" +
            "<p>可以关闭此页面并返回启动器</p></div></body></html>";

        private const string FailurePage =
            "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>登录未完成</title>" +
            "<style>body{margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;" +
            "font-family:'Segoe UI','Microsoft YaHei',sans-serif;background:#eaf3f8;color:#333}" +
            ".card{background:#fff;border:1px solid #c6d9e6;padding:36px 44px;text-align:center}" +
            "h1{font-size:20px;margin:0 0 10px;font-weight:600;color:#b3261e}" +
            "p{margin:0;color:#555;font-size:13px}" +
            "</style></head><body><div class=\"card\"><h1>登录未完成</h1>" +
            "<p>请返回启动器重试</p></div></body></html>";
    }
}
