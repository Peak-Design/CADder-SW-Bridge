using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using Peak.Cadder.Core;
using SolidWorks.Interop.sldworks;

namespace Peak.Cadder.Bridge
{
    /// <summary>
    /// The return leg: a localhost listener so BLENDER can ask SolidWorks for
    /// something, rather than only ever being sent to.
    ///
    /// It is the mirror image of the bridge inside the Blender add-on, down to
    /// the registry file and the shared-secret header, and it exists for one
    /// job in particular: "this part is too coarse, send it again finer",
    /// asked from the viewport where you can actually see that it is.
    ///
    /// The hard part is not HTTP. Every SolidWorks API call has to happen on
    /// the thread SolidWorks owns, and HttpListener hands requests to pool
    /// threads; touching COM from one is undefined at best. So a request does
    /// no work of its own. It parks a job and blocks, the UI thread runs it
    /// through a hidden control's Invoke, and the reply goes back on the pool
    /// thread that was waiting. Same shape as the Blender side's timer pump,
    /// for the same reason.
    ///
    /// CommandListener does the HTTP part, and JobGate the waiting. A ping
    /// never waits for a job, and the jobs take turns on the SolidWorks
    /// thread.
    /// </summary>
    public static class SwCommandServer
    {
        /// <summary>Handles one request, ON THE SOLIDWORKS THREAD. Returns the
        /// object to serialise back.</summary>
        public delegate Dictionary<string, object> Handler(
            ISldWorks app, Dictionary<string, object> request);

        private static CommandListener _listener;
        private static JobGate _gate;
        private static Control _marshal;
        private static string _token;
        private static string _registryFile;
        private static Handler _handler;
        private static ISldWorks _app;

        public static bool Running { get { return _listener != null; } }
        public static int Port { get; private set; }

        public static string RegistryDir
        {
            get
            {
                // System.Environment spelled out: the sldworks interop also
                // declares an Environment type.
                return Path.Combine(
                    System.Environment.GetFolderPath(
                        System.Environment.SpecialFolder.LocalApplicationData),
                    "PeakDesign", "CADder", "solidworks");
            }
        }

        /// <summary>
        /// Starts listening. MUST be called from the SolidWorks thread: the
        /// hidden control it creates for marshalling belongs to whichever
        /// thread makes it, and that has to be the one SolidWorks runs on.
        /// </summary>
        public static void Start(ISldWorks app, Handler handler, Action<string> log)
        {
            if (Running || app == null || handler == null) return;
            _app = app;
            _handler = handler;
            _marshal = new Control();
            _marshal.CreateControl();
            var _ = _marshal.Handle;   // forces the window handle to exist now
            _gate = new JobGate(PostToSolidWorks);

            _listener = CommandListener.Open(51820, 51839, RunOnSolidWorksThread, Ping, log);
            if (_listener == null)
            {
                if (log != null) log("sw bridge: no free port in 51820-51839");
                return;
            }
            Port = _listener.Port;
            _token = _listener.Token;
            WriteRegistry(log);
            if (log != null)
                log("sw bridge: listening on 127.0.0.1:" + Port);
        }

        public static void Stop(Action<string> log)
        {
            try { if (_listener != null) _listener.Close(); } catch { }
            _listener = null;
            _gate = null;
            try { if (_registryFile != null) File.Delete(_registryFile); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            _registryFile = null;
            try { if (_marshal != null) _marshal.Dispose(); } catch { }
            _marshal = null;
            if (log != null) log("sw bridge: stopped");
        }

        /// <summary>What a ping answers.</summary>
        private static Dictionary<string, object> Ping()
        {
            return new Dictionary<string, object>
            {
                { "ok", true },
                { "app", "Peak.Cadder" },
                { "version", AddIn.AddInVersion },
                { "pid", System.Diagnostics.Process.GetCurrentProcess().Id },
            };
        }

        /// <summary>
        /// Hops onto the thread SolidWorks owns and waits. A failing job
        /// throws here the same as it would if it had run inline.
        /// </summary>
        private static Dictionary<string, object> RunOnSolidWorksThread(
            Dictionary<string, object> request)
        {
            var gate = _gate;
            if (gate == null)
                throw new InvalidOperationException("the add-in is shutting down");
            // Up to the request's own "timeout_s" (default ten minutes). A
            // job still running then is most likely behind a dialog
            // SolidWorks put up: the reply says so instead of hanging the
            // caller, and the job finishes on its own when the dialog goes.
            double timeoutS = MiniJson.Num(request, "timeout_s", 600);
            return gate.Run(() => _handler(_app, request),
                TimeSpan.FromSeconds(Math.Max(1, timeoutS)));
        }

        /// <summary>Queues an action on the SolidWorks thread through the
        /// hidden control, and returns at once.</summary>
        private static void PostToSolidWorks(Action action)
        {
            var marshal = _marshal;
            if (marshal == null || marshal.IsDisposed)
                throw new InvalidOperationException("the add-in is shutting down");
            if (!marshal.InvokeRequired) action();
            else marshal.BeginInvoke(action);
        }

        /// <summary>Runs an action on the SolidWorks thread after a delay,
        /// for work that must follow the reply out of the door (quitting).</summary>
        public static void RunLater(int delayMs, Action action)
        {
            var marshal = _marshal;
            if (marshal == null || marshal.IsDisposed) return;
            var timer = new System.Windows.Forms.Timer { Interval = Math.Max(1, delayMs) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                timer.Dispose();
                try { action(); } catch { }
            };
            timer.Start();
        }

        /// <summary>
        /// Entries left by instances that are gone: a crash or a kill never
        /// reaches Stop(), and a caller listing the directory would
        /// otherwise try every corpse first.
        ///
        /// An entry stays only while a process of this program
        /// (<paramref name="ownName"/>, SLDWORKS) has its process id. After
        /// a reboot the id of an old entry often belongs to a service or a
        /// system process. HasExited opens the process and throws "Access
        /// is denied" for those, which stopped the prune and the registry
        /// write, so Blender never found this SolidWorks. The name comes
        /// from the process list and opens nothing. An id that another
        /// program has now also marks a SolidWorks that is gone, and its
        /// entry held a dead token that Blender tried first.
        /// </summary>
        internal static void PruneRegistry(string dir, string ownName)
        {
            string[] files;
            try { files = Directory.GetFiles(dir, "*.json"); }
            catch { return; }
            foreach (var file in files)
            {
                int pid;
                if (!int.TryParse(Path.GetFileNameWithoutExtension(file), out pid)) continue;
                if (IsRunning(pid, ownName)) continue;
                try { File.Delete(file); } catch { }
            }
        }

        /// <summary>Whether a process of that name has that process id
        /// now.</summary>
        internal static bool IsRunning(int pid, string name)
        {
            try
            {
                using (var proc = System.Diagnostics.Process.GetProcessById(pid))
                    return string.Equals(proc.ProcessName, name, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { return false; }
        }

        private static void WriteRegistry(Action<string> log)
        {
            try
            {
                Directory.CreateDirectory(RegistryDir);
                // A failed prune leaves old entries behind. It must never
                // stop this instance from writing its own.
                try
                {
                    PruneRegistry(RegistryDir,
                        System.Diagnostics.Process.GetCurrentProcess().ProcessName);
                }
                catch (Exception ex)
                {
                    if (log != null) log("sw bridge: registry prune failed: " + ex.Message);
                }
                _registryFile = Path.Combine(
                    RegistryDir,
                    System.Diagnostics.Process.GetCurrentProcess().Id + ".json");
                File.WriteAllText(_registryFile, MiniJson.Write(
                    new Dictionary<string, object>
                    {
                        { "pid", System.Diagnostics.Process.GetCurrentProcess().Id },
                        { "port", Port },
                        { "token", _token },
                        { "addin_version", AddIn.AddInVersion },
                    }));
            }
            catch (Exception ex)
            {
                if (log != null) log("sw bridge: registry write failed: " + ex.Message);
            }
        }
    }
}
