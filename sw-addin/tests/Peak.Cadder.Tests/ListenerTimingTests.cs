using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Peak.Cadder.Bridge;
using Peak.Cadder.Core;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// The listener Blender talks to, while SolidWorks is busy with a job.
    ///
    /// Blender pings every entry it finds with a short timeout, and an
    /// older Blender deletes the entry of an application that does not
    /// answer. The listener answered one request at a time, so a ping sat
    /// behind a job of minutes and SolidWorks vanished for the rest of the
    /// session. A ping must answer at once, and a job must answer (busy, if
    /// it must) inside the time its caller waits.
    ///
    /// SolidWorks is not here: a thread of the test stands in for its
    /// thread and runs the jobs one at a time, as SolidWorks does.
    /// </summary>
    public class ListenerTimingTests : IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();
        private readonly Thread _solidWorks;
        private readonly CommandListener _listener;
        private readonly ManualResetEventSlim _started = new ManualResetEventSlim(false);
        private readonly ManualResetEventSlim _release = new ManualResetEventSlim(false);
        private int _ran;

        public ListenerTimingTests()
        {
            _solidWorks = new Thread(() =>
            {
                foreach (var action in _queue.GetConsumingEnumerable()) action();
            }) { IsBackground = true };
            _solidWorks.Start();
            var gate = new JobGate(action => _queue.Add(action));
            _listener = CommandListener.Open(51900, 51999,
                request => gate.Run(() => Job(request),
                    TimeSpan.FromSeconds(MiniJson.Num(request, "timeout_s", 600))),
                () => new Dictionary<string, object> { { "ok", true } },
                null);
            Assert.True(_listener != null, "no free port for the test listener");
        }

        public void Dispose()
        {
            _release.Set();
            _listener.Close();
            _queue.CompleteAdding();
        }

        /// <summary>"hold" waits until the test lets it go. Anything else
        /// answers at once.</summary>
        private Dictionary<string, object> Job(Dictionary<string, object> request)
        {
            Interlocked.Increment(ref _ran);
            if (MiniJson.Str(request, "op") == "hold")
            {
                _started.Set();
                _release.Wait(TimeSpan.FromSeconds(60));
            }
            return new Dictionary<string, object>
            {
                { "ok", true }, { "op", MiniJson.Str(request, "op") },
            };
        }

        private Dictionary<string, object> Call(
            string path, Dictionary<string, object> body, int timeoutMs)
        {
            var req = (HttpWebRequest)WebRequest.Create(
                "http://127.0.0.1:" + _listener.Port + path);
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.Proxy = null;
            req.KeepAlive = false;
            req.Headers["X-CADLink-Token"] = _listener.Token;
            if (body == null)
            {
                req.Method = "GET";
            }
            else
            {
                req.Method = "POST";
                var bytes = Encoding.UTF8.GetBytes(MiniJson.Write(body));
                req.ContentType = "application/json";
                req.ContentLength = bytes.Length;
                using (var stream = req.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
            }
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return MiniJson.ParseObject(reader.ReadToEnd());
        }

        private static Dictionary<string, object> Op(string op, double timeoutS = 0)
        {
            var body = new Dictionary<string, object> { { "op", op } };
            if (timeoutS > 0) body["timeout_s"] = timeoutS;
            return body;
        }

        private Task<Dictionary<string, object>> Hold()
        {
            var job = Task.Run(() => Call("/job", Op("hold"), 60000));
            Assert.True(_started.Wait(TimeSpan.FromSeconds(10)), "the long job did not start");
            return job;
        }

        [Fact]
        public void APingAnswersWhileAJobRuns()
        {
            var job = Hold();
            var clock = Stopwatch.StartNew();
            var ping = Call("/ping", null, 1500);
            Assert.True(MiniJson.Flag(ping, "ok"));
            Assert.True(clock.ElapsedMilliseconds < 1500);
            _release.Set();
            Assert.True(MiniJson.Flag(job.Result, "ok"));
        }

        [Fact]
        public void AJobBehindAnotherAnswersBusyInItsOwnTime()
        {
            var job = Hold();
            var clock = Stopwatch.StartNew();
            // Blender waits its own timeout, and sends that less a margin.
            var reply = Call("/job", Op("poses", 1), 4000);
            Assert.False(MiniJson.Flag(reply, "ok"));
            Assert.True(MiniJson.Flag(reply, "busy"));
            Assert.True(clock.ElapsedMilliseconds < 4000);
            _release.Set();
            job.Wait();
        }

        [Fact]
        public void AJobThatTimedOutStillHoldsSolidWorks()
        {
            // The long job answers busy to its caller but still runs. A
            // second job must not start inside it: a dialog box of the
            // first would run it in the middle of the first.
            var first = Task.Run(() => Call("/job", Op("hold", 1), 10000));
            Assert.True(_started.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(MiniJson.Flag(first.Result, "busy"));
            var second = Call("/job", Op("poses", 1), 10000);
            Assert.True(MiniJson.Flag(second, "busy"));
            Assert.Equal(1, Volatile.Read(ref _ran));
            _release.Set();
            var third = Call("/job", Op("poses", 5), 10000);
            Assert.True(MiniJson.Flag(third, "ok"));
        }

        [Fact]
        public void AJobAnswersWithItsResult()
        {
            var reply = Call("/job", Op("status"), 10000);
            Assert.True(MiniJson.Flag(reply, "ok"));
            Assert.Equal("status", reply["op"]);
        }

        [Fact]
        public void AWrongTokenIsRefused()
        {
            var req = (HttpWebRequest)WebRequest.Create(
                "http://127.0.0.1:" + _listener.Port + "/job");
            req.Method = "POST";
            req.Proxy = null;
            req.Headers["X-CADLink-Token"] = "wrong";
            req.ContentLength = 0;
            var ex = Assert.Throws<WebException>(() => req.GetResponse().Dispose());
            Assert.Equal(HttpStatusCode.Forbidden, ((HttpWebResponse)ex.Response).StatusCode);
        }
    }
}
