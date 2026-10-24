using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace ReWindLauncher.Core
{
    /// <summary>DPAPI 按当前用户加密，拷到别的电脑解不出明文。</summary>
    public static class AuthStore
    {
        private const string FileName = "auth.dat";
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ReWindLauncher.Auth.v1");

        private static IEnumerable<string> CandidatePaths()
        {
            yield return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName);
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ReWindLauncher", FileName);
        }

        public static AuthSession Load()
        {
            foreach (string path in CandidatePaths())
            {
                if (!File.Exists(path)) continue;
                try
                {
                    byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy,
                        DataProtectionScope.CurrentUser);
                    JObject json = JObject.Parse(Encoding.UTF8.GetString(plain));

                    var session = new AuthSession
                    {
                        Name = (string)json["name"] ?? "",
                        Uuid = (string)json["uuid"] ?? "",
                        AccessToken = (string)json["accessToken"] ?? "",
                        RefreshToken = (string)json["refreshToken"] ?? "",
                        Xuid = (string)json["xuid"] ?? "",
                        ExpiresAt = (long?)json["expiresAt"] ?? 0
                    };
                    if (session.Name.Length > 0 && session.RefreshToken.Length > 0) return session;
                }
                catch (Exception ex)
                {
                    Log.Error("读取正版登录信息失败: " + path, ex);
                }
            }
            return null;
        }

        public static bool Save(AuthSession session)
        {
            if (session == null || string.IsNullOrEmpty(session.RefreshToken)) return false;

            var json = new JObject
            {
                ["name"] = session.Name,
                ["uuid"] = session.Uuid,
                ["accessToken"] = session.AccessToken,
                ["refreshToken"] = session.RefreshToken,
                ["xuid"] = session.Xuid,
                ["expiresAt"] = session.ExpiresAt
            };

            byte[] blob;
            try
            {
                blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(json.ToString()),
                    Entropy, DataProtectionScope.CurrentUser);
            }
            catch (Exception ex)
            {
                Log.Error("加密正版登录信息失败", ex);
                return false;
            }

            foreach (string path in CandidatePaths())
            {
                try
                {
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllBytes(path, blob);
                    RemoveOtherCopies(path);
                    return true;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Log.Error("保存正版登录信息失败: " + path, ex);
                }
            }
            return false;
        }

        public static void Delete()
        {
            foreach (string path in CandidatePaths())
            {
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                }
                catch (Exception ex)
                {
                    Log.Error("删除正版登录信息失败: " + path, ex);
                }
            }
        }

        private static void RemoveOtherCopies(string savedPath)
        {
            foreach (string path in CandidatePaths())
            {
                if (string.Equals(path, savedPath, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                }
                catch
                {
                }
            }
        }
    }
}
