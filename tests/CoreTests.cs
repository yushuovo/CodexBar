using System;
using System.Drawing;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Windows.Forms;
using CodexBar;

namespace CodexBar
{
    public static class CoreTests
    {
        private static int checks;
        private static void Assert(bool valid, string label) { checks++; if (!valid) throw new Exception(label); }
        private static void Reject(Action action, string label)
        { bool threw = false; try { action(); } catch { threw = true; } Assert(threw, label); }
        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "app-server") return FakeServer();
            if (args.Length > 0 && args[0] == "--probe-discovery")
            {
                try
                {
                    string[] roots = new System.Collections.Generic.List<string>(CodexPackageRegistration.InstallLocations()).ToArray();
                    string path = CodexLocator.Resolve(null, null, roots,
                        delegate { throw new Exception("Registration-only discovery must not inspect running processes."); }, new string[0], File.Exists);
                    Console.WriteLine("Registered packages: " + roots.Length);
                    Console.WriteLine("CLI without cache, PATH or desktop processes: " + path);
                    Console.WriteLine("Discovery source: " + CodexLocator.DiscoverySource);
                    return 0;
                }
                catch (Exception ex) { Console.WriteLine("Discovery failed: " + ex.GetType().Name + ": " + ex.Message); return 1; }
            }
            if (args.Length > 0 && args[0] == "--probe-codex")
            {
                try
                {
                    string home = new Settings().CodexHome;
                    using (CodexQuotaClient service = new CodexQuotaClient())
                    {
                        Console.WriteLine("CLI: " + CodexLocator.Find());
                        CodexSnapshot value = service.Read(home).GetAwaiter().GetResult();
                        Console.WriteLine("Source: " + value.Source + "; observed: " + value.RecordedAt.ToString("o"));
                        Console.WriteLine("Weekly remaining: " + (value.Weekly == null ? "unknown" : value.Weekly.Remaining.ToString()) + "; five-hour remaining: " + (value.FiveHour == null ? "unknown" : value.FiveHour.Remaining.ToString()));
                    }
                    Console.WriteLine("Owned server disposed."); return 0;
                }
                catch (Exception ex) { Console.WriteLine("Probe failed: " + ex.GetType().Name + ": " + ex.Message); return 1; }
            }
            // CI consoles commonly use UTF-8 with a preamble. The child protocol must stay BOM-free.
            Console.InputEncoding = new System.Text.UTF8Encoding(true);
            try
            {
                string package = @"C:\Packages\OpenAI.Codex_new";
                string freshCli = Path.Combine(package, "app", "resources", "codex.exe");
                string staleCli = @"C:\Packages\OpenAI.Codex_removed\app\resources\codex.exe";
                var candidates = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase) { freshCli };
                Func<System.Collections.Generic.IEnumerable<string>> noDesktop = delegate { throw new UnauthorizedAccessException("The desktop is closed and WindowsApps cannot be enumerated."); };
                string resolved = CodexLocator.Resolve(null, staleCli, new[] { package }, noDesktop, new string[0], candidates.Contains);
                Assert(resolved == freshCli && CodexLocator.DiscoverySource == "registered-package", "Removed old cache recovers from per-user registration without desktop, PATH or WindowsApps enumeration");
                candidates.Add(staleCli);
                Assert(CodexLocator.Resolve(null, staleCli, new[] { package }, noDesktop, new string[0], candidates.Contains) == freshCli, "Current registration wins even when old cached package is still staged");
                Assert(CodexLocator.Resolve(staleCli, null, new[] { package }, noDesktop, new string[0], candidates.Contains) == staleCli, "An explicit configured CLI retains precedence");
                Assert(CodexLocator.Resolve(null, staleCli, new string[0], noDesktop, new string[0], candidates.Contains) == staleCli, "Portable installations can still use a valid cache");
                string portable = @"C:\Tools\Codex";
                string portableCli = Path.Combine(portable, "codex.exe");
                candidates.Clear(); candidates.Add(portableCli);
                Assert(CodexLocator.Resolve(null, staleCli, new string[0], () => new string[0], new[] { portable }, candidates.Contains) == portableCli, "Missing package and invalid cache fall back to portable CLI on PATH");
                string alias = @"C:\Users\Test\AppData\Local\Microsoft\WindowsApps";
                candidates.Add(Path.Combine(alias, "codex.exe"));
                Assert(CodexLocator.Resolve(null, null, new string[0], () => new string[0], new[] { alias, portable }, candidates.Contains) == portableCli, "Desktop execution aliases are not mistaken for a CLI");
                Reject(() => CodexLocator.Resolve(null, staleCli, new string[0], () => new string[0], new string[0], path => false), "Unavailable locations produce a clear locate failure");
                string prefix = "{\"type\":\"event_msg\",\"timestamp\":\"2026-11-06T10:00:00Z\",\"payload\":{\"type\":\"token_count\",\"rate_limits\":";
                CodexSnapshot codex = CodexParser.CodexEvent(prefix + "{\"primary\":{\"used_percent\":75,\"window_minutes\":10080},\"secondary\":{\"used_percent\":0,\"window_minutes\":300}}}}");
                Assert(codex.Weekly.Remaining == 25 && codex.FiveHour.Remaining == 100, "Windows mapped by duration, not primary/secondary position");
                Assert(CodexParser.CodexEvent(prefix + "{\"primary\":{\"window_minutes\":300}}}}") == null, "Missing usage cannot become 100 percent remaining");
                Assert(CodexParser.CodexEvent(prefix + "{\"primary\":{\"used_percent\":101,\"window_minutes\":300}}}}") == null, "Invalid usage rejected");
                Assert(CodexParser.CodexEvent(prefix + "{\"limit_id\":\"other-model\",\"primary\":{\"used_percent\":20,\"window_minutes\":300}}}}") == null, "Unrelated buckets excluded");
                CodexSnapshot online = CodexParser.CodexOnline(Json.Parse("{\"rateLimits\":{\"limitId\":\"codex\",\"primary\":{\"usedPercent\":0,\"windowDurationMins\":300,\"resetsAt\":2000000000},\"secondary\":{\"usedPercent\":11,\"windowDurationMins\":10080}}}"), DateTimeOffset.Now);
                Assert(online.FiveHour.Remaining == 100 && online.Weekly.Remaining == 89 && online.Source == "app-server", "Online response maps new reset window and correct source");
                Reject(() => CodexParser.CodexOnline(Json.Parse("{\"rateLimits\":{\"limitId\":\"codex\",\"primary\":{\"windowDurationMins\":300}}}"), DateTimeOffset.Now), "Missing online usage remains unknown");
                CodexSnapshot multi = CodexParser.CodexOnline(Json.Parse("{\"rateLimits\":{\"limitId\":\"other\"},\"rateLimitsByLimitId\":{\"codex\":{\"primary\":{\"usedPercent\":80,\"windowDurationMins\":10080}}}}"), DateTimeOffset.Now);
                Assert(multi.Weekly.Remaining == 20 && multi.FiveHour == null, "Multi-bucket response selects Codex rather than unrelated legacy bucket");
                Assert(codex.Stale(new DateTimeOffset(2026, 11, 6, 10, 16, 0, TimeSpan.Zero)), "Old records are stale");
                CodexSnapshot partial = CodexParser.CodexEvent(prefix + "{\"primary\":{\"used_percent\":50,\"window_minutes\":300,\"resets_at\":1}}}}");
                Assert(partial.Weekly == null && partial.Stale(DateTimeOffset.Now), "Missing window remains missing and reset timestamps expire");
                Assert(Math.Abs(BallRenderer.FillTop(50) - 0.5) < 0.0001 && BallRenderer.FillTop(100) < 0.0001 && BallRenderer.FillTop(0) > 0.9999, "Ball endpoints and midpoint are accurate");
                using (Icon icon = BallRenderer.IconFor(codex, true)) Assert(icon.Width == 32, "Tray icon rendering and native handle lifetime");
                int? child;
                using (CodexQuotaClient fake = new CodexQuotaClient(System.Reflection.Assembly.GetExecutingAssembly().Location, 3000))
                {
                    CodexSnapshot first = fake.Read(new Settings().CodexHome).GetAwaiter().GetResult();
                    child = fake.ChildProcessId;
                    Assert(first.FiveHour.Remaining == 100 && first.Weekly.Remaining == 89, "App Server initialization and read-only quota request roundtrip");
                    Assert(Console.InputEncoding.GetPreamble().Length > 0, "Protocol startup restores the caller's console encoding");
                    fake.Read(new Settings().CodexHome).GetAwaiter().GetResult();
                    Assert(fake.ChildProcessId == child, "Repeated queries reuse a single owned process");
                    using (Process stale = Process.GetProcessById(child.Value)) { stale.Kill(); stale.WaitForExit(2000); }
                    fake.Read(new Settings().CodexHome).GetAwaiter().GetResult();
                    Assert(fake.ChildProcessId != child, "An exited quota service reconnects and refreshes its managed login");
                    child = fake.ChildProcessId;
                }
                bool exited = false;
                try { using (Process old = Process.GetProcessById(child.Value)) exited = old.HasExited; } catch (ArgumentException) { exited = true; }
                Assert(exited, "Disposing service releases child process and redirected pipes");
                string folder = Path.Combine(Path.GetTempPath(), "CodexBar-tests-" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(Path.Combine(folder, "sessions"));
                    File.WriteAllText(Path.Combine(folder, "simulate-expired-auth"), "test");
                    using (CodexQuotaClient expired = new CodexQuotaClient(System.Reflection.Assembly.GetExecutingAssembly().Location, 3000))
                    {
                        Assert(expired.Read(folder).GetAwaiter().GetResult().Source == "app-server", "Only a confirmed authentication failure triggers managed login refresh and retries quota");
                    }
                    File.WriteAllText(Path.Combine(folder, "simulate-auth-failure-once"), "test");
                    using (CodexQuotaClient recovery = new CodexQuotaClient(System.Reflection.Assembly.GetExecutingAssembly().Location, 3000))
                    {
                        Reject(() => recovery.Read(folder).GetAwaiter().GetResult(), "An initial login refresh failure is reported");
                        Assert(!recovery.ChildProcessId.HasValue, "A failed login refresh releases the failed connection");
                        CodexSnapshot recovered = recovery.Read(folder).GetAwaiter().GetResult();
                        Assert(recovered.Source == "app-server" && recovery.Stage == "ready", "A later manual refresh reconnects after an initial login failure");
                    }
                    File.WriteAllText(Path.Combine(folder, "sessions", "test.jsonl"), "{malformed}\n" + prefix + "{\"primary\":{\"used_percent\":50,\"window_minutes\":300}}}}\n" + prefix + "{\"primary\":{\"window_minutes\":300}}}}\n");
                    Assert(CodexReader.Read(folder).FiveHour.Remaining == 50, "Reader tolerates invalid records and preserves latest valid event");
                    SettingsStore store = new SettingsStore(folder);
                    Settings config = new Settings { CodexHome = Path.Combine(folder, "custom-codex"), LowQuotaPercent = 17, StartWithWindows = true };
                    store.Save(config); config.ShowCodexBall = false; store.Save(config);
                    Assert(!store.Load().ShowCodexBall && store.Load().LowQuotaPercent == 17 && store.Load().StartWithWindows, "Atomic save retains Codex-only configuration");
                    string legacyJson = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new { CodexHome = config.CodexHome, ShowCodexBall = false, StartWithWindows = true, LowQuotaPercent = 23, DeepSeekSecret = "retired", MiniMaxSecret = "retired", LowBalanceCny = 10 });
                    File.WriteAllText(Path.Combine(folder, "settings.json"), legacyJson);
                    Assert(store.Load().LowQuotaPercent == 23 && !store.Load().ShowCodexBall, "Retired vendor configuration migration preserves Codex preferences");
                    Assert(Json.Parse(File.ReadAllText(Path.Combine(folder, "settings.json"))).Count == 4, "Only Codex configuration fields survive legacy migration");
                    string legacyFolder = Path.Combine(folder, "legacy"), newFolder = Path.Combine(folder, "renamed");
                    Directory.CreateDirectory(legacyFolder);
                    File.WriteAllText(Path.Combine(legacyFolder, "settings.json"), legacyJson);
                    File.WriteAllText(Path.Combine(legacyFolder, "codex-cli.path"), "old-cli");
                    File.WriteAllText(Path.Combine(legacyFolder, "runtime-status.json"), "private-runtime-data");
                    AppPaths.MigrateLegacyData(legacyFolder, newFolder);
                    Assert(new SettingsStore(newFolder).Load().LowQuotaPercent == 23 && File.ReadAllText(Path.Combine(newFolder, "codex-cli.path")) == "old-cli", "Product rename preserves Codex settings and CLI cache");
                    Assert(!File.Exists(Path.Combine(newFolder, "runtime-status.json")), "Product rename does not copy runtime diagnostics");
                    File.WriteAllText(Path.Combine(newFolder, "codex-cli.path"), "new-cli");
                    AppPaths.MigrateLegacyData(legacyFolder, newFolder);
                    Assert(File.ReadAllText(Path.Combine(newFolder, "codex-cli.path")) == "new-cli", "Legacy migration never overwrites current preferences or cache");
                }
                finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
                using (CodexPanel panel = new CodexPanel(true))
                {
                    Assert(!panel.Visible && !panel.ShowInTaskbar, "Codex panel is hidden until explicitly opened from the tray menu");
                    Assert(panel.Controls.Find("codexHome", true).Length == 1 && panel.Controls.Find("lowQuotaPercent", true).Length == 1 && panel.Controls.Find("showCodexBall", true).Length == 1 && panel.Controls.Find("startWithWindows", true).Length == 1, "Single panel contains all retained Codex settings");
                    Assert(panel.Controls[0].Controls.OfType<CodexCard>().Count() == 1, "Single panel contains the Codex quota card");
                    panel.Opacity = 0;
                    ((ToolStripMenuItem)panel.TrayMenu.Items["codexPanel"]).PerformClick();
                    Assert(panel.Visible && panel.Controls.Find("codexHome", true)[0].Visible, "Merged right-click menu item opens the quota and settings panel");
                    ((Button)panel.Controls.Find("closePanel", true)[0]).PerformClick();
                    Assert(!panel.Visible && !panel.IsDisposed, "Closing the merged panel hides it without ending monitoring");
                }
                using (ContextMenuStrip menu = TrayMenus.Create(delegate { }, delegate { }, delegate { }, delegate { }, true))
                {
                    Assert(menu.Items.OfType<ToolStripMenuItem>().Select(item => item.Name).SequenceEqual(new[] { "codexPanel", "refresh", "codexBall", "quit" }), "Right-click menu has one merged panel entry plus refresh, ball toggle and quit");
                }
                Console.WriteLine("PASS: " + checks + " core checks");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine("FAIL after " + checks + " checks: " + ex.ToString()); return 1; }
        }
        private static int FakeServer()
        {
            bool initialized = false;
            bool authRefreshed = false;
            string line;
            while ((line = Console.ReadLine()) != null)
            {
                System.Collections.Generic.Dictionary<string, object> request = Json.Parse(line);
                string method = Json.Str(Json.Get(request, "method"));
                object id = Json.Get(request, "id");
                if (method == "initialize") Console.WriteLine("{\"id\":" + id + ",\"result\":{}}");
                else if (method == "initialized") initialized = true;
                else if (method == "account/read" && initialized)
                {
                    System.Collections.Generic.Dictionary<string, object> options = Json.Map(Json.Get(request, "params"));
                    if (!object.Equals(Json.Get(options, "refreshToken"), true)) return 3;
                    string home = Environment.GetEnvironmentVariable("CODEX_HOME");
                    if (string.IsNullOrEmpty(home) || !File.Exists(Path.Combine(home, "simulate-expired-auth"))) return 4;
                    if (!string.IsNullOrEmpty(home) && File.Exists(Path.Combine(home, "simulate-auth-failure-once")) && !File.Exists(Path.Combine(home, "failure-observed")))
                    {
                        File.WriteAllText(Path.Combine(home, "failure-observed"), "test");
                        Console.WriteLine("{\"id\":" + id + ",\"error\":{\"code\":-32001,\"message\":\"test authentication temporarily failed\"}}");
                        Console.Out.Flush(); continue;
                    }
                    authRefreshed = true;
                    Console.WriteLine("{\"id\":" + id + ",\"result\":{\"account\":{\"type\":\"chatgpt\"}}}");
                }
                else if (method == "account/rateLimits/read" && initialized)
                {
                    string home = Environment.GetEnvironmentVariable("CODEX_HOME");
                    if (!authRefreshed && !string.IsNullOrEmpty(home) && File.Exists(Path.Combine(home, "simulate-expired-auth")))
                        Console.WriteLine("{\"id\":" + id + ",\"error\":{\"code\":401,\"message\":\"authentication required\"}}");
                    else Console.WriteLine("{\"id\":" + id + ",\"result\":{\"rateLimits\":{\"limitId\":\"codex\",\"primary\":{\"usedPercent\":0,\"windowDurationMins\":300,\"resetsAt\":2000000000},\"secondary\":{\"usedPercent\":11,\"windowDurationMins\":10080,\"resetsAt\":2000000000}}}}");
                }
                else return 2;
                Console.Out.Flush();
            }
            return 0;
        }
    }
}
