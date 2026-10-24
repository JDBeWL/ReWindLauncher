using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ReWindLauncher.Core
{
    public sealed class VersionEntry
    {
        public string Id;
        public string Type;
        public DateTime ReleaseTime;
        public string JsonPath;
        public string JarPath;
        public string InheritsFrom;

        public string DisplayName =>
            string.IsNullOrEmpty(InheritsFrom) ? Id : Id;

        public override string ToString() => Id;
    }

    public static class VersionStore
    {
        public static List<VersionEntry> Scan(string gameRoot)
        {
            var list = new List<VersionEntry>();
            string versionsDir = Path.Combine(gameRoot, "versions");
            if (!Directory.Exists(versionsDir)) return list;

            foreach (string dir in Directory.GetDirectories(versionsDir))
            {
                try
                {
                    string id = Path.GetFileName(dir);
                    string json = Path.Combine(dir, id + ".json");
                    if (!File.Exists(json))
                    {
                        string[] jsons = Directory.GetFiles(dir, "*.json");
                        json = jsons.FirstOrDefault();
                    }
                    if (json == null && !Directory.GetFiles(dir, "*.jar").Any()) continue;

                    var entry = new VersionEntry
                    {
                        Id = id,
                        JsonPath = json,
                        JarPath = Path.Combine(dir, id + ".jar"),
                        Type = "release"
                    };
                    if (!File.Exists(entry.JarPath))
                    {
                        string[] jars = Directory.GetFiles(dir, "*.jar");
                        if (jars.Length > 0) entry.JarPath = jars[0];
                    }

                    if (json != null)
                    {
                        try
                        {
                            JObject obj = JObject.Parse(File.ReadAllText(json));
                            entry.Id = (string)obj["id"] ?? id;
                            entry.Type = (string)obj["type"] ?? entry.Type;
                            entry.InheritsFrom = (string)obj["inheritsFrom"];
                            string time = (string)obj["releaseTime"] ?? (string)obj["time"];
                            if (DateTime.TryParse(time, CultureInfo.InvariantCulture,
                                DateTimeStyles.AdjustToUniversal, out DateTime parsed))
                                entry.ReleaseTime = parsed;
                        }
                        catch (Exception ex)
                        {
                            Log.Error("解析版本 JSON 失败: " + json, ex);
                        }
                    }
                    list.Add(entry);
                }
                catch (Exception ex)
                {
                    Log.Error("扫描版本目录失败: " + dir, ex);
                }
            }

            return list
                .OrderByDescending(v => v.ReleaseTime)
                .ThenByDescending(v => v.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static JObject LoadMerged(VersionEntry entry, string gameRoot)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return LoadMerged(entry, gameRoot, visited);
        }

        private static JObject LoadMerged(VersionEntry entry, string gameRoot, HashSet<string> visited)
        {
            if (entry == null || string.IsNullOrEmpty(entry.JsonPath) || !File.Exists(entry.JsonPath))
                return null;
            if (!visited.Add(entry.Id)) return null;

            JObject child = JObject.Parse(File.ReadAllText(entry.JsonPath));
            string parentId = (string)child["inheritsFrom"];
            if (string.IsNullOrEmpty(parentId))
            {
                child.Remove("inheritsFrom");
                return child;
            }

            string parentJson = Path.Combine(gameRoot, "versions", parentId, parentId + ".json");
            if (!File.Exists(parentJson)) return child;

            var parentEntry = new VersionEntry { Id = parentId, JsonPath = parentJson };
            JObject parent = LoadMerged(parentEntry, gameRoot, visited);
            if (parent == null) return child;
            return Merge(parent, child);
        }

        private static JObject Merge(JObject parent, JObject child)
        {
            var merged = (JObject)parent.DeepClone();
            foreach (var property in child.Properties())
            {
                string name = property.Name;
                if (name == "libraries")
                {
                    var parentLibs = merged["libraries"] as JArray;
                    var childLibs = property.Value as JArray;
                    var combined = new JArray();
                    if (parentLibs != null) foreach (var item in parentLibs) combined.Add(item.DeepClone());
                    if (childLibs != null) foreach (var item in childLibs) combined.Add(item.DeepClone());
                    merged["libraries"] = combined;
                }
                else if (name == "arguments")
                {
                    var parentArgs = merged["arguments"] as JObject;
                    var childArgs = property.Value as JObject;
                    if (parentArgs != null && childArgs != null)
                    {
                        var args = (JObject)parentArgs.DeepClone();
                        foreach (var section in childArgs.Properties())
                        {
                            var existing = args[section.Name] as JArray;
                            var incoming = section.Value as JArray;
                            if (existing != null && incoming != null)
                            {
                                foreach (var item in incoming) existing.Add(item.DeepClone());
                            }
                            else
                            {
                                args[section.Name] = section.Value.DeepClone();
                            }
                        }
                        merged["arguments"] = args;
                    }
                    else
                    {
                        merged[name] = property.Value.DeepClone();
                    }
                }
                else
                {
                    merged[name] = property.Value.DeepClone();
                }
            }
            merged.Remove("inheritsFrom");
            return merged;
        }
    }
}
