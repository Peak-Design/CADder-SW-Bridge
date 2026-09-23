using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Peak.Cadder.Core;

namespace Peak.Cadder.Bridge
{
    /// <summary>
    /// The HTTP half of the return leg: a localhost port, the shared-secret
    /// header, and one JSON request in, one JSON reply out. What a request
    /// does is the caller's (SwCommandServer), so this runs without
    /// SolidWorks.
    /// </summary>
    internal sealed class CommandListener
    {
        private readonly HttpListener _listener;
        private readonly Func<Dictionary<string, object>, Dictionary<string, object>> _run;
        private readonly Func<Dictionary<string, object>> _ping;
        private readonly Action<string> _log;
        private Thread _thread;

        public int Port { get; private set; }
        public string Token { get; private set; }

        private CommandListener(
            HttpListener listener, int port,
            Func<Dictionary<string, object>, Dictionary<string, object>> run,
            Func<Dictionary<string, object>> ping, Action<string> log)
        {
            _listener = listener;
            Port = port;
            Token = Guid.NewGuid().ToString("N");
            _run = run;
            _ping = ping;
            _log = log;
        }

        /// <summary>
        /// Listens on the first free port from <paramref name="first"/> to
        /// <paramref name="last"/>, or returns null when none is free.
        /// <paramref name="run"/> answers a job and <paramref name="ping"/>
        /// a ping.
        /// </summary>
        public static CommandListener Open(
            int first, int last,
            Func<Dictionary<string, object>, Dictionary<string, object>> run,
            Func<Dictionary<string, object>> ping, Action<string> log)
        {
            for (int port = first; port <= last; port++)
            {
                var listener = new HttpListener();
                listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
                try
                {
                    listener.Start();
                }
                catch (HttpListenerException) { continue; }
                catch (ObjectDisposedException) { continue; }
                var server = new CommandListener(listener, port, run, ping, log);
                server._thread = new Thread(server.Serve) { IsBackground = true };
                server._thread.Start();
                return server;
            }
            return null;
        }

        public void Close()
        {
            try { _listener.Close(); } catch { }
        }

        /// <summary>
        /// Takes the requests as they come. A ping is answered here, at
        /// once. Every other request gets a thread of its own, because a
        /// job waits for SolidWorks for up to minutes. With one request at a
        /// time, a ping waited behind a job, and a Blender that got no
        /// answer in time deleted this SolidWorks' registry entry: no
        /// Blender found it again until SolidWorks restarted.
        /// </summary>
        private void Serve()
        {
            while (true)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch { return; }        // Close() stopped it
                if (IsPing(ctx))
                {
                    Handle(ctx);
                    continue;
                }
                var context = ctx;
                try
                {
                    new Thread(() => Handle(context)) { IsBackground = true }.Start();
                }
                catch (OutOfMemoryException) { Handle(ctx); }
            }
        }

        private static bool IsPing(HttpListenerContext ctx)
        {
            try { return ctx.Request.Url.AbsolutePath.TrimEnd('/') == "/ping"; }
            catch { return false; }
        }

        private void Handle(HttpListenerContext ctx)
        {
            try
            {
                Answer(ctx);
            }
            catch (Exception ex)
            {
                if (_log != null) _log("sw bridge: " + ex.Message);
            }
        }

        private void Answer(HttpListenerContext ctx)
        {
            string path = ctx.Request.Url.AbsolutePath.TrimEnd('/');
            if (path == "/ping")
            {
                Respond(ctx, 200, _ping());
                return;
            }
            string sent = ctx.Request.Headers["X-CADLink-Token"];
            if (!string.Equals(sent, Token, StringComparison.Ordinal))
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
            try { reply = _run(request); }
            catch (Exception ex)
            {
                if (_log != null) _log("sw bridge job: " + ex);
                reply = new Dictionary<string, object>
                {
                    { "ok", false }, { "error", ex.Message },
                };
            }
            Respond(ctx, 200, reply);
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
    }
}
