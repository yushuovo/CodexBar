using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexBar
{
    public static class CodexPackageRegistration
    {
        private const string Family = "OpenAI.Codex_2p2nqsd0c76g0";
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int GetPackagesByPackageFamily(string family, ref uint count, IntPtr names, ref uint bufferLength, IntPtr buffer);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int GetPackagePathByFullName(string fullName, ref uint pathLength, StringBuilder path);

        // Query per-user registration. Never enumerate the protected WindowsApps directory.
        public static IEnumerable<string> InstallLocations()
        {
            List<KeyValuePair<Version, string>> roots = new List<KeyValuePair<Version, string>>();
            for (int attempt = 0; attempt < 3; attempt++)
            {
                uint count = 0, length = 0;
                int result = GetPackagesByPackageFamily(Family, ref count, IntPtr.Zero, ref length, IntPtr.Zero);
                if (result == 0 && count == 0) return new string[0];
                if (result != 122) throw new System.ComponentModel.Win32Exception(result);
                IntPtr names = Marshal.AllocHGlobal(checked((int)count * IntPtr.Size));
                IntPtr buffer = IntPtr.Zero;
                try
                {
                    buffer = Marshal.AllocHGlobal(checked((int)length * 2));
                    result = GetPackagesByPackageFamily(Family, ref count, names, ref length, buffer);
                    if (result == 122) continue; // Registration can change during a Store update.
                    if (result != 0) throw new System.ComponentModel.Win32Exception(result);
                    for (int index = 0; index < count; index++)
                    {
                        string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(names, index * IntPtr.Size));
                        uint pathLength = 0;
                        if (GetPackagePathByFullName(name, ref pathLength, null) != 122) continue;
                        StringBuilder path = new StringBuilder(checked((int)pathLength));
                        if (GetPackagePathByFullName(name, ref pathLength, path) != 0) continue;
                        Version version;
                        string[] parts = name.Split('_');
                        if (parts.Length < 2 || !Version.TryParse(parts[1], out version)) version = new Version(0, 0);
                        roots.Add(new KeyValuePair<Version, string>(version, path.ToString()));
                    }
                    return roots.OrderByDescending(item => item.Key).Select(item => item.Value).ToArray();
                }
                finally
                {
                    if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                    Marshal.FreeHGlobal(names);
                }
            }
            throw new IOException("Codex 安装登记正在变化，请稍后重试。");
        }
    }

    public static class CodexLocator
    {
        private static string CacheFile { get { return Path.Combine(AppPaths.DataDirectory, "codex-cli.path"); } }
        public static string DiscoverySource { get; private set; }
        public static string RegistrationStatus { get; private set; }
        public static void Remember(string path)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)); File.WriteAllText(CacheFile, path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        public static string Find()
        {
            AppPaths.Initialize();
            string configured = Environment.GetEnvironmentVariable("CODEXBAR_CODEX_EXE");
            if (string.IsNullOrWhiteSpace(configured)) configured = Environment.GetEnvironmentVariable("AIAPIBAR_CODEX_EXE");
            string cached = null;
            try
            {
                if (File.Exists(CacheFile)) cached = File.ReadAllText(CacheFile).Trim();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            List<string> roots = new List<string>();
            RegistrationStatus = "not-queried";
            try { roots.AddRange(CodexPackageRegistration.InstallLocations()); RegistrationStatus = "registered:" + roots.Count; }
            catch (System.ComponentModel.Win32Exception error) { RegistrationStatus = "error:" + error.NativeErrorCode; }
            catch (EntryPointNotFoundException) { RegistrationStatus = "unsupported"; }
            catch (DllNotFoundException) { RegistrationStatus = "unsupported"; }
            catch (IOException) { RegistrationStatus = "registration-changing"; }
            return Resolve(configured, cached, roots, RunningCandidates, (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator), File.Exists);
        }
        // Dependency-injected discovery allows tests to model a removed package and an inaccessible directory.
        public static string Resolve(string configured, string cached, IEnumerable<string> registeredRoots, Func<IEnumerable<string>> runningCandidates, IEnumerable<string> pathEntries, Func<string, bool> exists)
        {
            DiscoverySource = "not-found";
            if (!string.IsNullOrWhiteSpace(configured) && exists(configured)) { DiscoverySource = "configured"; return configured; }
            // Registration wins over cache, including an old package that remains staged on disk.
            foreach (string root in registeredRoots)
            {
                string candidate = Path.Combine(root, "app", "resources", "codex.exe");
                if (exists(candidate)) { DiscoverySource = "registered-package"; return candidate; }
            }
            if (!string.IsNullOrWhiteSpace(cached) && exists(cached)) { DiscoverySource = "cache"; return cached; }
            foreach (string candidate in runningCandidates())
                if (exists(candidate)) { DiscoverySource = "running-desktop"; return candidate; }
            foreach (string entry in pathEntries)
            {
                try
                {
                    string folder = entry.Trim().Trim('"');
                    if (string.IsNullOrEmpty(folder) || folder.IndexOf("Microsoft\\WindowsApps", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    string resource = Path.Combine(folder, "resources", "codex.exe");
                    if (exists(resource)) { DiscoverySource = "path-resources"; return resource; }
                    string candidate = Path.Combine(folder, "codex.exe");
                    if (exists(candidate)) { DiscoverySource = "path"; return candidate; }
                }
                catch (ArgumentException) { }
            }
            throw new InvalidOperationException("未找到 Codex CLI；Windows 安装登记中未发现可用程序。");
        }
        private static IEnumerable<string> RunningCandidates()
        {
            List<string> candidates = new List<string>();
            foreach (Process process in Process.GetProcessesByName("Codex"))
            {
                using (process)
                {
                    try
                    {
                        string folder = Path.GetDirectoryName(process.MainModule.FileName);
                        string candidate = Path.Combine(folder, "resources", "codex.exe");
                        candidates.Add(candidate);
                    }
                    catch (System.ComponentModel.Win32Exception) { }
                    catch (InvalidOperationException) { }
                }
            }
            return candidates;
        }
    }

    public sealed class CodexRpcException : InvalidOperationException
    {
        public readonly bool RequiresAuthRefresh;
        public CodexRpcException(bool requiresAuthRefresh)
            : base(requiresAuthRefresh ? "Codex 登录状态需要刷新。" : "Codex 额度服务暂时拒绝查询。")
        { RequiresAuthRefresh = requiresAuthRefresh; }
    }

    // Owns only the child process it starts. Never starts a thread, a turn, or a model request.
    public sealed class CodexQuotaClient : IDisposable
    {
        private static readonly object StartEncodingLock = new object();
        private Process server;
        private string currentHome;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private readonly string executable;
        private readonly int timeoutMs;
        private int nextId;
        private bool disposed;
        public string Stage { get; private set; }
        public string ResolvedExecutable { get; private set; }
        public CodexQuotaClient() : this(null, 15000) { }
        public CodexQuotaClient(string executablePath, int requestTimeoutMs) { executable = executablePath; timeoutMs = requestTimeoutMs; }
        public int? ChildProcessId { get { return server == null ? (int?)null : server.Id; } }

        public async Task<CodexSnapshot> Read(string codexHome)
        {
            await gate.WaitAsync();
            try
            {
                if (disposed) throw new ObjectDisposedException("CodexQuotaClient");
                if (server == null || server.HasExited || currentHome != codexHome)
                {
                    StopServer();
                    Stage = "locate";
                    ResolvedExecutable = executable ?? CodexLocator.Find();
                    Stage = "start";
                    ProcessStartInfo start = new ProcessStartInfo(ResolvedExecutable, "app-server")
                    {
                        UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                        StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
                        WorkingDirectory = Environment.SystemDirectory
                    };
                    start.EnvironmentVariables["CODEX_HOME"] = codexHome;
                    server = new Process { StartInfo = start };
                    // Drain diagnostics to avoid pipe deadlock; do not persist raw service output.
                    server.ErrorDataReceived += delegate { };
                    bool started;
                    // .NET Framework chooses redirected stdin from Console.InputEncoding at Start().
                    // A UTF-8 preamble corrupts the first JSON line. Restore the console setting immediately.
                    lock (StartEncodingLock)
                    {
                        Encoding previous = Console.InputEncoding;
                        bool changed = false;
                        try
                        {
                            if (previous.GetPreamble().Length > 0)
                            {
                                // A windowed application may have no console handle to reconfigure.
                                try { Console.InputEncoding = new UTF8Encoding(false); changed = true; }
                                catch (IOException) { }
                            }
                            started = server.Start();
                        }
                        finally { if (changed) Console.InputEncoding = previous; }
                    }
                    if (!started) throw new InvalidOperationException("Codex 额度服务未能启动。");
                    server.BeginErrorReadLine(); currentHome = codexHome;
                    int init = ++nextId;
                    Stage = "initialize";
                    Send(new { id = init, method = "initialize", @params = new { clientInfo = new { name = "codexbar", title = "CodexBar", version = "0.6.0" } } });
                    await Response(init);
                    Send(new { method = "initialized" });
                }
                int request = ++nextId;
                Stage = "query";
                Send(new { id = request, method = "account/rateLimits/read" });
                Dictionary<string, object> result = null;
                bool refreshAuth = false;
                try { result = await Response(request); }
                catch (CodexRpcException error)
                {
                    if (!error.RequiresAuthRefresh) throw;
                    refreshAuth = true;
                }
                if (refreshAuth)
                {
                    // Query first with existing managed login. Do not force an OAuth refresh on every launch.
                    Stage = "auth-refresh";
                    int auth = ++nextId;
                    Send(new { id = auth, method = "account/read", @params = new { refreshToken = true } });
                    await Response(auth);
                    Stage = "query";
                    request = ++nextId;
                    Send(new { id = request, method = "account/rateLimits/read" });
                    result = await Response(request);
                }
                Stage = "parse";
                CodexSnapshot snapshot = CodexParser.CodexOnline(result, DateTimeOffset.Now);
                Stage = "ready";
                if (executable == null) CodexLocator.Remember(ResolvedExecutable);
                return snapshot;
            }
            catch
            {
                StopServer();
                throw;
            }
            finally { gate.Release(); }
        }
        private void Send(object message)
        {
            server.StandardInput.WriteLine(new JavaScriptSerializer().Serialize(message));
            server.StandardInput.Flush();
        }
        private async Task<Dictionary<string, object>> Response(int requestId)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < timeoutMs)
            {
                Task<string> read = server.StandardOutput.ReadLineAsync();
                int remainingMs = Math.Max(1, timeoutMs - (int)clock.ElapsedMilliseconds);
                using (CancellationTokenSource timeout = new CancellationTokenSource())
                {
                    Task timer = Task.Delay(remainingMs, timeout.Token);
                    if (await Task.WhenAny(read, timer) != read) throw new TimeoutException("Codex 在线额度查询超时。");
                    timeout.Cancel();
                }
                string line = await read;
                if (line == null) throw new IOException("Codex 额度服务已退出。");
                Dictionary<string, object> envelope;
                try { envelope = Json.Parse(line); }
                catch (ArgumentException) { continue; }
                if (Json.Number(Json.Get(envelope, "id")) != requestId) continue;
                Dictionary<string, object> error = Json.Map(Json.Get(envelope, "error"));
                if (error != null)
                {
                    string message = (Json.Str(Json.Get(error, "message")) ?? "").ToLowerInvariant();
                    decimal? code = Json.Number(Json.Get(error, "code"));
                    bool authentication = code == 401 || code == -32001 || message.Contains("401") || message.Contains("unauthorized") || message.Contains("not logged") || message.Contains("authentication") || message.Contains("access token") || message.Contains("refresh token") || message.Contains("sign in");
                    throw new CodexRpcException(authentication);
                }
                Dictionary<string, object> result = Json.Map(Json.Get(envelope, "result"));
                if (result == null) throw new InvalidDataException("Codex 额度服务响应格式无法识别。");
                return result;
            }
            throw new TimeoutException("Codex 在线额度查询超时。");
        }
        private void StopServer()
        {
            Process child = server; server = null; currentHome = null;
            if (child == null) return;
            try
            {
                if (!child.HasExited)
                {
                    try { child.StandardInput.Close(); } catch (IOException) { }
                    if (!child.WaitForExit(1000)) { child.Kill(); child.WaitForExit(2000); }
                }
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            finally { child.Dispose(); }
        }
        public void Dispose()
        {
            disposed = true;
            StopServer();
        }
    }
}
