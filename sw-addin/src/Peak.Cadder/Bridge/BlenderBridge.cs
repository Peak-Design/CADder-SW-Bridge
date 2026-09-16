using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using Peak.Cadder.Core;

namespace Peak.Cadder.Bridge
{
    /// <summary>One running Blender with the CADder bridge listening.</summary>
    public sealed class BlenderInstance
    {
        public int Pid;
        public int Port;
        public string Token;
        public string BlenderVersion;
        public string AddonVersion;
        public string BlendFile;
        public string RegistryFile;

        public string Describe()
        {
            string file = string.IsNullOrEmpty(BlendFile)
                ? "unsaved file" : Path.GetFileName(BlendFile);
            return "Blender " + (BlenderVersion ?? "?") + " at " + file
                + " (pid " + Pid + ")";
        }
    }

    /// <summary>
    /// The SolidWorks side of the SW ⇄ Blender bridge. Discovery is a
    /// directory of registry files (%LOCALAPPDATA%\PeakDesign\CADder\
    /// bridge\&lt;pid&gt;.json) that each listening Blender writes; every
    /// entry is pinged and corpses are deleted. All requests carry the
    /// instance's token: possession of the user-private file is the auth.
    /// </summary>
    public static class BlenderBridge
    {
        public static string RegistryDir
            => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PeakDesign", "CADder", "bridge");

        // ── Discovery ───────────────────────────────────────────────────────

        private static DateTime _lookedAt;
        private static bool _sawOne;

        /// <summary>
        /// Whether a Blender with the bridge looks like it is running.
        ///
        /// For the RIBBON, which asks on every idle: a directory listing,
        /// cached for a few seconds, and no ping. Discover is the honest
        /// answer and costs an HTTP round trip per instance, which is far
        /// too much to pay for greying a button. A stale registry file
        /// therefore makes this say yes when the answer is no, and the
        /// command then reports that nothing is listening, which is the
        /// harmless way round.
        /// </summary>
        public static bool AnyListening()
        {
            if ((DateTime.UtcNow - _lookedAt) < TimeSpan.FromSeconds(3))
                return _sawOne;
            _lookedAt = DateTime.UtcNow;
            try
            {
                _sawOne = Directory.Exists(RegistryDir)
                    && Directory.GetFiles(RegistryDir, "*.json").Length > 0;
            }
            catch (IOException) { _sawOne = false; }
            catch (UnauthorizedAccessException) { _sawOne = false; }
            return _sawOne;
        }

        public static List<BlenderInstance> Discover(Action<string> log)
        {
            var result = new List<BlenderInstance>();
            if (!Directory.Exists(RegistryDir)) return result;
            foreach (var file in Directory.GetFiles(RegistryDir, "*.json"))
            {
                BlenderInstance inst = null;
                try { inst = ReadRegistryFile(file); }
                catch (Exception ex)
                {
                    if (log != null) log("bridge registry " + file + ": " + ex.Message);
                }
                if (inst != null && Ping(inst, log))
                {
                    result.Add(inst);
                    continue;
                }
                // A dead entry: the Blender behind it is gone. Deleting keeps
                // discovery fast for the next call.
                try { File.Delete(file); } catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            // Newest Blender first when several are up.
            result.Sort((a, b) => string.CompareOrdinal(
                b.BlenderVersion ?? "", a.BlenderVersion ?? ""));
            return result;
        }

        private static BlenderInstance ReadRegistryFile(string path)
        {
            var obj = MiniJson.ParseObject(File.ReadAllText(path));
            var inst = new BlenderInstance
            {
                Pid = MiniJson.Int(obj, "pid"),
                Port = MiniJson.Int(obj, "port"),
                Token = MiniJson.Str(obj, "token"),
                BlenderVersion = MiniJson.Str(obj, "blender_version"),
                AddonVersion = MiniJson.Str(obj, "addon_version"),
                BlendFile = MiniJson.Str(obj, "blend_file"),
                RegistryFile = path,
            };
            if (inst.Port <= 0 || string.IsNullOrEmpty(inst.Token)) return null;
            return inst;
        }

        public static bool Ping(BlenderInstance inst, Action<string> log)
        {
            try
            {
                var obj = Request(inst, "GET", "/cadlink/ping", null, 2000);
                if (!MiniJson.Flag(obj, "ok")) return false;
                // The live answer beats the registry file: the blend file
                // changes as the user works.
                inst.BlendFile = MiniJson.Str(obj, "blend_file", inst.BlendFile);
                inst.BlenderVersion = MiniJson.Str(obj, "blender_version", inst.BlenderVersion);
                inst.AddonVersion = MiniJson.Str(obj, "addon_version", inst.AddonVersion);
                return true;
            }
            catch (Exception ex)
            {
                if (log != null) log("ping 127.0.0.1:" + inst.Port + " failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>Runs the import pipeline in the given Blender. Blocks
        /// until Blender finishes (big assemblies take minutes: call from a
        /// worker thread). Returns the parsed response, ok or not.</summary>
        public static Dictionary<string, object> PostImport(
            BlenderInstance inst, Dictionary<string, object> payload,
            int timeoutMs, Action<string> log)
        {
            return Request(inst, "POST", "/cadlink/import", payload, timeoutMs);
        }

        private static Dictionary<string, object> Request(
            BlenderInstance inst, string method, string path,
            Dictionary<string, object> payload, int timeoutMs)
        {
            var url = "http://127.0.0.1:" + inst.Port + path;
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.Headers["X-CADLink-Token"] = inst.Token ?? "";
            req.Proxy = null;   // a system proxy must never sit in a localhost call

            if (payload != null)
            {
                var bytes = Encoding.UTF8.GetBytes(MiniJson.Write(payload));
                req.ContentType = "application/json; charset=utf-8";
                req.ContentLength = bytes.Length;
                using (var stream = req.GetRequestStream())
                    stream.Write(bytes, 0, bytes.Length);
            }

            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                    return ReadBody(resp);
            }
            catch (WebException ex)
            {
                // A 500 still carries the bridge's JSON error report.
                var resp = ex.Response as HttpWebResponse;
                if (resp != null)
                    using (resp) return ReadBody(resp);
                throw;
            }
        }

        private static Dictionary<string, object> ReadBody(HttpWebResponse resp)
        {
            using (var reader = new StreamReader(
                resp.GetResponseStream(), Encoding.UTF8))
            {
                return MiniJson.ParseObject(reader.ReadToEnd());
            }
        }

        // ── Installed Blenders ──────────────────────────────────────────────

        /// <summary>blender.exe paths found on this machine, newest first.</summary>
        public static List<string> FindInstalledBlenders()
        {
            var found = new List<KeyValuePair<double, string>>();
            foreach (var programFiles in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            })
            {
                if (string.IsNullOrEmpty(programFiles)) continue;
                string root = Path.Combine(programFiles, "Blender Foundation");
                if (!Directory.Exists(root)) continue;
                foreach (var dir in Directory.GetDirectories(root, "Blender*"))
                {
                    string exe = Path.Combine(dir, "blender.exe");
                    if (!File.Exists(exe)) continue;
                    double version = 0;
                    var name = Path.GetFileName(dir);
                    int space = name.LastIndexOf(' ');
                    if (space >= 0)
                        double.TryParse(name.Substring(space + 1),
                            NumberStyles.Float, CultureInfo.InvariantCulture, out version);
                    found.Add(new KeyValuePair<double, string>(version, exe));
                }
            }
            return found.OrderByDescending(kv => kv.Key)
                        .Select(kv => kv.Value).Distinct().ToList();
        }

        /// <summary>The exe to launch: the explicit setting when it exists,
        /// else the newest install, else null.</summary>
        public static string ResolveExe(AppSettings settings)
        {
            if (!string.IsNullOrEmpty(settings.BlenderExe)
                && File.Exists(settings.BlenderExe))
                return settings.BlenderExe;
            return FindInstalledBlenders().FirstOrDefault();
        }

        // ── Launching ───────────────────────────────────────────────────────

        /// <summary>Starts Blender and waits for its bridge to come up: the
        /// addon registers the server during startup, so the new registry
        /// entry appearing (and answering a ping) IS the ready signal.</summary>
        public static BlenderInstance Launch(
            string exe, Action<string> log, int timeoutMs = 120000)
        {
            var before = new HashSet<string>(
                Directory.Exists(RegistryDir)
                    ? Directory.GetFiles(RegistryDir, "*.json")
                    : new string[0],
                StringComparer.OrdinalIgnoreCase);

            if (log != null) log("launching " + exe);
            Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true });

            var deadline = Environment.TickCount + timeoutMs;
            while (Environment.TickCount < deadline)
            {
                System.Threading.Thread.Sleep(500);
                if (!Directory.Exists(RegistryDir)) continue;
                foreach (var file in Directory.GetFiles(RegistryDir, "*.json"))
                {
                    if (before.Contains(file)) continue;
                    BlenderInstance inst = null;
                    try { inst = ReadRegistryFile(file); } catch { }
                    if (inst != null && Ping(inst, log)) return inst;
                }
            }
            throw new TimeoutException(
                "Blender started but its CADder bridge did not come up "
                + "within " + (timeoutMs / 1000) + "s. Is CADder installed "
                + "and its SolidWorks-bridge option enabled in that Blender?");
        }

        // ── Focus ───────────────────────────────────────────────────────────

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);
        private const int SW_RESTORE = 9;

        public static void Focus(BlenderInstance inst, Action<string> log)
        {
            try
            {
                var proc = Process.GetProcessById(inst.Pid);
                var hwnd = proc.MainWindowHandle;
                if (hwnd == IntPtr.Zero) return;
                // SW_RESTORE un-maximizes a maximized window (it restores the
                // previous floating bounds), so it runs ONLY for a minimized
                // one: focusing must never change the window's size or state.
                if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
            }
            catch (Exception ex)
            {
                if (log != null) log("focus Blender: " + ex.Message);
            }
        }
    }
}
