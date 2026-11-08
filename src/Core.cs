using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexBar
{
    public static class AppPaths
    {
        public static string DataDirectory { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexBar"); } }
        public static string LegacyDataDirectory { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIAPIBar"); } }
        public static void MigrateLegacyData(string legacy, string current)
        {
            if (!Directory.Exists(legacy)) return;
            Directory.CreateDirectory(current);
            foreach (string name in new[] { "settings.json", "codex-cli.path" })
            {
                string source = Path.Combine(legacy, name), target = Path.Combine(current, name);
                if (File.Exists(source) && !File.Exists(target)) File.Copy(source, target, false);
            }
        }
        public static void Initialize()
        {
            try { MigrateLegacyData(LegacyDataDirectory, DataDirectory); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static class Json
    {
        public static Dictionary<string, object> Parse(string value) { return new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }.Deserialize<Dictionary<string, object>>(value); }
        public static object Get(Dictionary<string, object> map, string key) { object value; return map != null && map.TryGetValue(key, out value) ? value : null; }
        public static Dictionary<string, object> Map(object value) { return value as Dictionary<string, object>; }
        public static string Str(object value) { return value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture); }
        public static decimal? Number(object value)
        {
            decimal parsed;
            return value != null && decimal.TryParse(Str(value), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? (decimal?)parsed : null;
        }
    }

    public sealed class Settings
    {
        public string CodexHome { get; set; }
        public bool ShowCodexBall { get; set; }
        public bool StartWithWindows { get; set; }
        public int LowQuotaPercent { get; set; }
        public Settings()
        {
            CodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (string.IsNullOrWhiteSpace(CodexHome)) CodexHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            ShowCodexBall = true; LowQuotaPercent = 10;
        }
        public Settings Copy() { return (Settings)MemberwiseClone(); }
    }

    public sealed class SettingsStore
    {
        public readonly string DirectoryPath;
        private readonly string filePath;
        public SettingsStore(string directoryPath) { DirectoryPath = directoryPath; filePath = Path.Combine(directoryPath, "settings.json"); }
        public Settings Load()
        {
            if (!File.Exists(filePath)) return new Settings();
            string raw = File.ReadAllText(filePath, Encoding.UTF8);
            Settings settings = new JavaScriptSerializer().Deserialize<Settings>(raw) ?? new Settings();
            var legacy = Json.Parse(raw);
            if (legacy.ContainsKey("DeepSeekSecret") || legacy.ContainsKey("MiniMaxSecret") || legacy.ContainsKey("LowBalanceCny"))
            {
                try { Save(settings); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            return settings;
        }
        public void Save(Settings settings)
        {
            Directory.CreateDirectory(DirectoryPath);
            string temp = filePath + ".tmp";
            File.WriteAllText(temp, new JavaScriptSerializer().Serialize(settings), Encoding.UTF8);
            if (File.Exists(filePath)) File.Replace(temp, filePath, null); else File.Move(temp, filePath);
        }
    }

    public sealed class QuotaWindow
    {
        public double UsedPercent;
        public int Minutes;
        public DateTimeOffset? ResetsAt;
        public double Remaining { get { return Math.Max(0, Math.Min(100, 100 - UsedPercent)); } }
        public bool Expired(DateTimeOffset now) { return ResetsAt.HasValue && ResetsAt.Value <= now; }
    }
    public sealed class CodexSnapshot
    {
        public string Source = "local-log";
        public string Notice;
        public QuotaWindow Weekly;
        public QuotaWindow FiveHour;
        public DateTimeOffset RecordedAt;
        public bool Stale(DateTimeOffset now)
        { return now - RecordedAt > TimeSpan.FromMinutes(15) || (Weekly != null && Weekly.Expired(now)) || (FiveHour != null && FiveHour.Expired(now)); }
    }
    public static class CodexParser
    {
        public static CodexSnapshot CodexOnline(Dictionary<string, object> result, DateTimeOffset recordedAt)
        {
            Dictionary<string, object> buckets = Json.Map(Json.Get(result, "rateLimitsByLimitId"));
            Dictionary<string, object> rate = Json.Map(Json.Get(buckets, "codex")) ?? Json.Map(Json.Get(result, "rateLimits"));
            string id = Json.Str(Json.Get(rate, "limitId"));
            if (rate == null || (!string.IsNullOrEmpty(id) && id != "codex")) throw new InvalidDataException("服务未提供 Codex 主额度。");
            CodexSnapshot snapshot = new CodexSnapshot { Source = "app-server", RecordedAt = recordedAt };
            foreach (string name in new[] { "primary", "secondary" })
            {
                QuotaWindow window = ParseWindow(Json.Map(Json.Get(rate, name)), true);
                if (window == null) continue;
                if (window.Minutes == 300) snapshot.FiveHour = window;
                if (window.Minutes == 10080) snapshot.Weekly = window;
            }
            if (snapshot.FiveHour == null && snapshot.Weekly == null) throw new InvalidDataException("服务暂未提供 5 小时或周额度。");
            return snapshot;
        }
        private static QuotaWindow ParseWindow(Dictionary<string, object> window, bool online)
        {
            decimal? used = Json.Number(Json.Get(window, online ? "usedPercent" : "used_percent"));
            decimal? minutes = Json.Number(Json.Get(window, online ? "windowDurationMins" : "window_minutes"));
            if (!used.HasValue || used < 0 || used > 100 || !minutes.HasValue || minutes < 0 || minutes > int.MaxValue || decimal.Truncate(minutes.Value) != minutes.Value) return null;
            QuotaWindow parsed = new QuotaWindow { UsedPercent = (double)used.Value, Minutes = (int)minutes.Value };
            decimal? reset = Json.Number(Json.Get(window, online ? "resetsAt" : "resets_at"));
            if (reset.HasValue && reset >= 0 && reset <= 253402300799m)
                parsed.ResetsAt = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds((double)reset.Value);
            return parsed;
        }
        public static CodexSnapshot CodexEvent(string raw)
        {
            Dictionary<string, object> root = Json.Parse(raw);
            Dictionary<string, object> payload = Json.Map(Json.Get(root, "payload"));
            if (Json.Str(Json.Get(root, "type")) != "event_msg" || Json.Str(Json.Get(payload, "type")) != "token_count") return null;
            Dictionary<string, object> rate = Json.Map(Json.Get(payload, "rate_limits"));
            if (rate == null) return null;
            string limitId = Json.Str(Json.Get(rate, "limit_id"));
            if (!string.IsNullOrEmpty(limitId) && limitId != "codex") return null;
            DateTimeOffset stamp;
            if (!DateTimeOffset.TryParse(Json.Str(Json.Get(root, "timestamp")), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out stamp)) return null;
            CodexSnapshot snapshot = new CodexSnapshot { RecordedAt = stamp };
            foreach (string key in new[] { "primary", "secondary" })
            {
                QuotaWindow parsed = ParseWindow(Json.Map(Json.Get(rate, key)), false);
                if (parsed == null) continue;
                if (parsed.Minutes == 300) snapshot.FiveHour = parsed;
                if (parsed.Minutes == 10080) snapshot.Weekly = parsed;
            }
            return snapshot.Weekly != null || snapshot.FiveHour != null ? snapshot : null;
        }
    }

    public static class CodexReader
    {
        public static CodexSnapshot Read(string codexHome)
        {
            string root = Path.Combine(codexHome, "sessions");
            if (!Directory.Exists(root)) throw new InvalidOperationException("尚未找到本机 Codex 会话。请检查设置中的数据目录。");
            List<FileInfo> files = new DirectoryInfo(root).EnumerateFiles("*.jsonl", SearchOption.AllDirectories).OrderByDescending(f => f.LastWriteTimeUtc).Take(32).ToList();
            CodexSnapshot latest = null;
            foreach (FileInfo file in files)
            {
                try
                {
                    using (FileStream stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        long offset = Math.Max(0, stream.Length - 2 * 1024 * 1024);
                        stream.Seek(offset, SeekOrigin.Begin);
                        using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                        {
                            if (offset > 0) reader.ReadLine();
                            string line;
                            while ((line = reader.ReadLine()) != null)
                            {
                                if (!line.Contains("\"rate_limits\"") || !line.Contains("\"token_count\"")) continue;
                                try
                                {
                                    CodexSnapshot snapshot = CodexParser.CodexEvent(line);
                                    if (snapshot != null && (latest == null || snapshot.RecordedAt > latest.RecordedAt)) latest = snapshot;
                                }
                                catch (ArgumentException) { }
                                catch (InvalidOperationException) { }
                            }
                        }
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            if (latest == null) throw new InvalidOperationException("记录中未找到 5 小时或周额度。完成一次 Codex 交互后可重试。");
            return latest;
        }
    }
}
