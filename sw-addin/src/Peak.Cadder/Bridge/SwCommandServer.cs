using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
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
    /// </summary>
    public static class SwCommandServer
    {
        /// <summary>Handles one request, ON THE SOLIDWORKS THREAD. Returns the
        /// object to serialise back.</summary>
        public delegate Dictionary<string, object> Handler(
            ISldWorks app, Dictionary<string, object> request);

        private static HttpListener _listener;
        private static Thread _thread;
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
            _token = Guid.NewGuid().ToString("N");
            _marshal = new Control();
            _marshal.CreateControl();
            var _ = _marshal.Handle;   // forces the window handle to exist now

            for (int port = 51820; port < 51840 && _listener == null; port++)
            {
                var listener = new HttpListener();
                listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
                try
                {
                    listener.Start();
                    _listener = listener;
                    Port = port;
                }
                catch (HttpListenerException) { }
                catch (ObjectDisposedException) { }
            }
            if (_listener == null)
            {
                if (log != null) log("sw bridge: no free port in 51820-51839");
                return;
            }

            _thread = new Thread(() => Serve(log)) { IsBackground = true };
            _thread.Start();
            WriteRegistry(log);
            if (log != null)
                log("sw bridge: listening on 127.0.0.1:" + Port);
        }

        public static void Stop(Action<string> log)
        {
            try { if (_listener != null) _listener.Close(); } catch { }
            _listener = null;
            try { if (_registryFile != null) File.Delete(_registryFile); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            _registryFile = null;
            try { if (_marshal != null) _marshal.Dispose(); } catch { }
            _marshal = null;
            if (log != null) log("sw bridge: stopped");
        }

        private static void Serve(Action<string> log)
        {
            while (true)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch { return; }        // Stop() closed it
                try { Handle(ctx, log); }
                catch (Exception ex)
                {
                    if (log != null) log("sw bridge: " + ex.Message);
                }
            }
        }

        private static void Handle(HttpListenerContext ctx, Action<string> log)
        {
            string path = ctx.Request.Url.AbsolutePath.TrimEnd('/');
            if (path == "/ping")
            {
                Respond(ctx, 200, new Dictionary<string, object>
                {
                    { "ok", true },
                    { "app", "Peak.Cadder" },
                    { "version", AddIn.AddInVersion },
                    { "pid", System.Diagnostics.Process.GetCurrentProcess().Id },
                });
                return;
            }
            string sent = ctx.Request.Headers["X-CADLink-Token"] ?? ctx.Request.Headers["X-SWTB-Token"];
            if (!string.Equals(sent, _token, StringComparison.Ordinal))
            {
                Respond(ctx, 403, new Dictionary<string, object>
                {
                    { "ok", false }, { "error", "bad token" },
                });
                return;
            }

            string body;
            using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                body = reader.ReadToEnd();
            Dictionary<string, object> request;
            try { request = MiniJson.ParseObject(body); }
            catch (Exception ex)
            {
                Respond(ctx, 400, new Dictionary<string, object>
                {
                    { "ok", false }, { "error", "bad json: " + ex.Message },
                });
                return;
            }

            Dictionary<string, object> reply;
            try { reply = RunOnSolidWorksThread(request); }
            catch (Exception ex)
            {
                if (log != null) log("sw bridge job: " + ex);
                reply = new Dictionary<string, object>
                {
                    { "ok", false }, { "error", ex.Message },
                };
            }
            Respond(ctx, 200, reply);
        }

        /// <summary>
        /// Hops onto the thread SolidWorks owns and waits. Invoke marshals the
        /// exception back too, so a failing job reads the same here as it
        /// would if it had run inline.
        /// </summary>
        private static Dictionary<string, object> RunOnSolidWorksThread(
            Dictionary<string, object> request)
        {
            var marshal = _marshal;
            if (marshal == null || marshal.IsDisposed)
                throw new InvalidOperationException("the add-in is shutting down");
            if (!marshal.InvokeRequired) return _handler(_app, request);
            // Up to the request's own "timeout_s" (default ten minutes). A
            // job still running then is most likely behind a dialog
            // SolidWorks put up: the reply says so instead of hanging the
            // caller, and the job finishes on its own when the dialog goes.
            double timeoutS = MiniJson.Num(request, "timeout_s", 600);
            var call = new Func<Dictionary<string, object>>(() => _handler(_app, request));
            var pending = marshal.BeginInvoke(call);
            if (!pending.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(Math.Max(1, timeoutS))))
            {
                return new Dictionary<string, object>
                {
                    { "ok", false },
                    { "error", "still running after " + timeoutS + " s. SolidWorks may be "
                               + "showing a dialog; the job completes when it is dismissed" },
                };
            }
            return (Dictionary<string, object>)marshal.EndInvoke(pending);
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

        private static void Respond(
            HttpListenerContext ctx, int status, Dictionary<string, object> payload)
        {
            var bytes = Encoding.UTF8.GetBytes(MiniJson.Write(payload));
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            using (var output = ctx.Response.OutputStream)
                output.Write(bytes, 0, bytes.Length);
        }

        /// <summary>Entries left by instances that are gone: a crash or a
        /// kill never reaches Stop(), and a caller listing the directory would
        /// otherwise try every corpse first.</summary>
        private static void PruneRegistry()
        {
            string[] files;
            try { files = Directory.GetFiles(RegistryDir, "*.json"); }
            catch { return; }
            foreach (var file in files)
            {
                int pid;
                if (!int.TryParse(Path.GetFileNameWithoutExtension(file), out pid)) continue;
                bool alive = false;
                try
                {
                    var proc = System.Diagnostics.Process.GetProcessById(pid);
                    alive = !proc.HasExited;
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
                if (alive) continue;
                try { File.Delete(file); } catch { }
            }
        }

        private static void WriteRegistry(Action<string> log)
        {
            try
            {
                Directory.CreateDirectory(RegistryDir);
                PruneRegistry();
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
