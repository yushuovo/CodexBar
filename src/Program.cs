using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyVersion("0.6.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("0.6.0.0")]
[assembly: System.Reflection.AssemblyTitle("CodexBar")]
[assembly: System.Reflection.AssemblyProduct("CodexBar")]
[assembly: System.Reflection.AssemblyDescription("Windows tray monitor for Codex usage limits")]
[assembly: System.Reflection.AssemblyCopyright("Copyright (c) 2026 yushuovo")]

namespace CodexBar
{
    internal static class Program
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string title);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
        [STAThread]
        public static void Main(string[] args)
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs error) { ReportError(error.Exception); };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs error) { ReportError(error.ExceptionObject as Exception); };
            try { Run(args); } catch (Exception error) { ReportError(error); }
        }
        private static void Run(string[] args)
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length == 2 && args[0] == "--render-panel")
            {
                using (CodexPanel panel = new CodexPanel(true)) panel.RenderPreview(args[1]);
                return;
            }
            bool created;
            using (Mutex mutex = new Mutex(true, "Local\\CodexBar.Desktop", out created))
            {
                if (!created)
                {
                    IntPtr window = FindWindow(null, "CodexBar");
                    int message = args.Contains("--quit") ? 0x8002 : args.Contains("--refresh") ? 0x8003 : 0x8001;
                    if (window != IntPtr.Zero) PostMessage(window, message, IntPtr.Zero, IntPtr.Zero);
                    return;
                }
                if (args.Contains("--quit") || args.Contains("--refresh")) return;
                AppPaths.Initialize();
                // An ApplicationContext keeps the message loop alive without showing a startup form.
                using (CodexTrayContext context = new CodexTrayContext()) Application.Run(context);
            }
        }
        private static void ReportError(Exception error)
        {
            try
            {
                File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CodexBar-error.log"),
                    DateTimeOffset.Now.ToString("o") + " " + (error == null ? "Unknown" : error.GetType().FullName + Environment.NewLine + error.StackTrace) + Environment.NewLine);
            }
            catch { }
            MessageBox.Show("程序遇到错误，已在程序目录记录错误类型与调用位置。", "CodexBar", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    internal sealed class CodexTrayContext : ApplicationContext
    {
        private readonly CodexPanel panel;
        public CodexTrayContext()
        {
            panel = new CodexPanel(false);
            panel.ExitRequested += delegate { ExitThread(); };
            // The hidden handle receives single-instance commands; it is never a visible startup window.
            IntPtr handle = panel.Handle;
            panel.StartMonitoring();
        }
        protected override void ExitThreadCore()
        {
            panel.Dispose();
            base.ExitThreadCore();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && panel != null) panel.Dispose();
            base.Dispose(disposing);
        }
    }
}
