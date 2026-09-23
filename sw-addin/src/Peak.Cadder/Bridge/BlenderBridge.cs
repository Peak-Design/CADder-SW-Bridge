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
        /// <summary>"CADder" or "CADder Pro", as the bridge says. Null for
        /// a bridge before 1.1.1, which is CADder.</summary>
        public string AddonName;
        public string BlendFile;
        public string RegistryFile;

        /// <summary>The CAD documents this Blender's scenes hold, as its
        /// bridge says. Null for an older bridge that does not say.</summary>
        public List<string> Documents;

        /// <summary>Whether this Blender says it holds a scene of the
        /// document. False when it does not say.</summary>
        public bool Holds(string documentPath)
        {
            if (Documents == null || string.IsNullOrEmpty(documentPath)) return false;
            foreach (var d in Documents)
                if (SamePath(d, documentPath)) return true;
            return false;
        }

        internal static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return string.Equals(Full(a), Full(b), StringComparison.OrdinalIgnoreCase);
        }

        private static string Full(string path)
        {
            try { return Path.GetFullPath(path); }
            catch (Exception) { return path; }
        }

        /// <summary>The "documents" list of a registry file or a ping
        /// answer, or null when there is none.</summary>
        internal static List<string> DocumentsOf(Dictionary<string, object> obj)
        {
            var raw = MiniJson.Arr(obj, "documents");
            if (raw == null) return null;
            var list = new List<string>();
            foreach (var o in raw)
            {
                var s = o as string;
                if (!string.IsNullOrEmpty(s)) list.Add(s);
            }
            return list;
        }

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

        private static DateTime _readAt;
        private static List<BlenderInstance> _running = new List<BlenderInstance>();

        /// <summary>
        /// Whether a running Blender with the bridge holds a scene of this
        /// document: what Refresh Model needs, so its button is grey
        /// otherwise.
        ///
        /// For the RIBBON, which asks on every idle: the registry files and
        /// the process list, read at most every three seconds, and no ping.
        /// Before, any registry file enabled the button. A Blender that
        /// crashed or was ended leaves its file, and a new Blender that never
        /// got the send holds no scene of the document, so a refresh went to
        /// a scene without the model. Each bridge now lists the documents its
        /// scenes hold (the document tag of a send), and a saved file opened
        /// again lists the same.
        ///
        /// An older bridge lists nothing. Then the rule is the one before:
        /// this SolidWorks session has sent the document
        /// (<paramref name="sentThisSession"/>), and that Blender is running.
        /// </summary>
        public static bool AnyHolding(string documentPath, bool sentThisSession)
        {
            if ((DateTime.UtcNow - _readAt) >= TimeSpan.FromSeconds(3))
            {
                _readAt = DateTime.UtcNow;
                _running = ReadRegistry(RegistryDir).Where(i => IsBlender(i.Pid)).ToList();
            }
            return Holding(_running, documentPath, sentThisSession) != null;
        }

        /// <summary>The first of <paramref name="instances"/> that holds a
        /// scene of the document, by the rule of AnyHolding, or null.
        /// </summary>
        internal static BlenderInstance Holding(
            IEnumerable<BlenderInstance> instances, string documentPath, bool sentThisSession)
        {
            if (string.IsNullOrEmpty(documentPath) || instances == null) return null;
            foreach (var inst in instances)
            {
                if (inst == null) continue;
                if (inst.Documents == null ? sentThisSession : inst.Holds(documentPath))
                    return inst;
            }
            return null;
        }

        /// <summary>
        /// The Blenders a refresh of the document goes to: the ones that hold
        /// a scene of it, when any says so. Otherwise all of them, which is
        /// how an older bridge was chosen.
        /// </summary>
        internal static List<BlenderInstance> ForRefresh(
            List<BlenderInstance> instances, string documentPath)
        {
            var holding = instances.Where(i => i != null && i.Holds(documentPath)).ToList();
            return holding.Count > 0 ? holding : instances;
        }

        /// <summary>Every registry entry that reads, with no ping. A file that
        /// Blender is still writing is left out.</summary>
        internal static List<BlenderInstance> ReadRegistry(string dir)
        {
            var found = new List<BlenderInstance>();
            string[] files;
            try
            {
                if (!Directory.Exists(dir)) return found;
                files = Directory.GetFiles(dir, "*.json");
            }
            catch (IOException) { return found; }
            catch (UnauthorizedAccessException) { return found; }
            foreach (var file in files)
            {
                try
                {
                    var inst = ReadRegistryFile(file);
                    if (inst != null) found.Add(inst);
                }
                catch (Exception) { }
            }
            return found;
        }

        public static List<BlenderInstance> Discover(Action<string> log)
        {
            return Discover(RegistryDir, log, IsBlender, 2000);
        }

        /// <summary>
        /// Every Blender in <paramref name="dir"/> that answers a ping.
        ///
        /// An entry is deleted only when its Blender is gone
        /// (<paramref name="running"/> says no for its process id) or
        /// nothing listens on its port. Blender answers a ping from Python,
        /// and a long import holds Python, so a live Blender can miss a
        /// ping. Blender writes its entry only when its bridge starts, and
        /// deleting it after one slow ping hid that Blender until it
        /// started again: the next send launched a second Blender. A read
        /// can also meet a file that Blender is still writing. Such an
        /// entry stays, and this call leaves the Blender out.
        /// </summary>
        internal static List<BlenderInstance> Discover(
            string dir, Action<string> log, Func<int, bool> running, int pingMs)
        {
            var result = new List<BlenderInstance>();
            if (!Directory.Exists(dir)) return result;
            foreach (var file in Directory.GetFiles(dir, "*.json"))
            {
                BlenderInstance inst = null;
                try { inst = ReadRegistryFile(file); }
                catch (Exception ex)
                {
                    if (log != null) log("bridge registry " + file + ": " + ex.Message);
                }
                var answer = inst == null ? PingAnswer.NoAnswer : Knock(inst, log, pingMs);
                if (answer == PingAnswer.Ok)
                {
                    result.Add(inst);
                    continue;
                }
                int pid = inst != null && inst.Pid > 0 ? inst.Pid : PidOf(file);
                if (answer == PingAnswer.NoAnswer && running != null && running(pid))
                {
                    if (log != null) log("bridge registry " + Path.GetFileName(file)
                        + ": Blender " + pid + " is running but did not answer. "
                        + "The entry stays");
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

        /// <summary>Whether a Blender has that process id now. The name
        /// comes from the process list, which opens no process.</summary>
        internal static bool IsBlender(int pid)
        {
            if (pid <= 0) return false;
            try
            {
                using (var proc = Process.GetProcessById(pid))
                    return proc.ProcessName.StartsWith("blender", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { return false; }
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
                AddonName = MiniJson.Str(obj, "addon_name"),
                BlendFile = MiniJson.Str(obj, "blend_file"),
                RegistryFile = path,
                Documents = BlenderInstance.DocumentsOf(obj),
            };
            if (inst.Port <= 0 || string.IsNullOrEmpty(inst.Token)) return null;
            return inst;
        }

        /// <summary>The process id in a registry file's name
        /// (&lt;pid&gt;.json), or 0.</summary>
        private static int PidOf(string file)
        {
            int pid;
            return int.TryParse(Path.GetFileNameWithoutExtension(file), out pid) ? pid : 0;
        }

        /// <summary>What a ping got back.</summary>
        private enum PingAnswer
        {
            /// <summary>A CADder bridge answered.</summary>
            Ok,
            /// <summary>Something answered that is not a CADder bridge, or
            /// nothing listens on the port.</summary>
            Refused,
            /// <summary>No answer in time: the Blender can be busy.</summary>
            NoAnswer,
        }

        public static bool Ping(BlenderInstance inst, Action<string> log, int timeoutMs = 2000)
        {
            return Knock(inst, log, timeoutMs) == PingAnswer.Ok;
        }

        private static PingAnswer Knock(
            BlenderInstance inst, Action<string> log, int timeoutMs)
        {
            try
            {
                var obj = Request(inst, "GET", "/cadlink/ping", null, timeoutMs);
                if (!MiniJson.Flag(obj, "ok")) return PingAnswer.Refused;
                // The live answer beats the registry file: the blend file
                // changes as the user works.
                inst.BlendFile = MiniJson.Str(obj, "blend_file", inst.BlendFile);
                inst.BlenderVersion = MiniJson.Str(obj, "blender_version", inst.BlenderVersion);
                inst.AddonVersion = MiniJson.Str(obj, "addon_version", inst.AddonVersion);
                inst.AddonName = MiniJson.Str(obj, "addon_name", inst.AddonName);
                inst.Documents = BlenderInstance.DocumentsOf(obj) ?? inst.Documents;
                return PingAnswer.Ok;
            }
            catch (WebException ex) when (ex.Status == WebExceptionStatus.ConnectFailure)
            {
                if (log != null) log("ping 127.0.0.1:" + inst.Port + " refused: " + ex.Message);
                return PingAnswer.Refused;
            }
            catch (Exception ex)
            {
                if (log != null) log("ping 127.0.0.1:" + inst.Port + " failed: " + ex.Message);
                return PingAnswer.NoAnswer;
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
