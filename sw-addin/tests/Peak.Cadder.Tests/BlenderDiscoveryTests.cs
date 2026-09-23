using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Peak.Cadder.Bridge;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// How SolidWorks finds the running Blenders, and which registry
    /// entries it removes on the way.
    ///
    /// Blender writes its entry once, when its bridge starts. Blender
    /// answers a ping from Python, and a long import holds Python, so a
    /// busy Blender misses a ping. Removing its entry then made that
    /// Blender invisible until it started again, and the next send
    /// launched a second one. An entry goes only when its Blender is gone
    /// or nothing listens on its port.
    /// </summary>
    public class BlenderDiscoveryTests : IDisposable
    {
        private readonly string _dir;
        private readonly List<TcpListener> _listeners = new List<TcpListener>();

        public BlenderDiscoveryTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "cadder-discover-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            foreach (var l in _listeners) { try { l.Stop(); } catch (SocketException) { } }
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        private static int Me { get { return Process.GetCurrentProcess().Id; } }

        private const int Gone = int.MaxValue - 3;

        private string Entry(string name, int pid, int port)
        {
            string path = Path.Combine(_dir, name + ".json");
            File.WriteAllText(path, "{\"pid\": " + pid + ", \"port\": " + port
                + ", \"token\": \"t\", \"blender_version\": \"5.1.0\"}");
            return path;
        }

        /// <summary>A port that takes the connection and never answers: a
        /// Blender whose Python is busy.</summary>
        private int SilentPort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            _listeners.Add(l);
            return ((IPEndPoint)l.LocalEndpoint).Port;
        }

        /// <summary>A port nothing listens on: the connection is refused.</summary>
        private static int ClosedPort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        /// <summary>A port that answers every request as a Blender bridge
        /// does.</summary>
        private int AnsweringPort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            _listeners.Add(l);
            var thread = new Thread(() =>
            {
                try
                {
                    while (true)
                    {
                        using (var client = l.AcceptTcpClient())
                        using (var stream = client.GetStream())
                        {
                            var buffer = new byte[4096];
                            stream.Read(buffer, 0, buffer.Length);
                            var body = Encoding.UTF8.GetBytes("{\"ok\": true, \"blender_version\": \"5.1.0\"}");
                            var head = Encoding.ASCII.GetBytes(
                                "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n"
                                + "Content-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                            stream.Write(head, 0, head.Length);
                            stream.Write(body, 0, body.Length);
                        }
                    }
                }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            }) { IsBackground = true };
            thread.Start();
            return ((IPEndPoint)l.LocalEndpoint).Port;
        }

        private List<BlenderInstance> Discover(int pingMs = 400)
        {
            return BlenderBridge.Discover(_dir, null, pid => pid == Me, pingMs);
        }

        [Fact]
        public void ABusyBlenderKeepsItsEntry()
        {
            string busy = Entry("busy", Me, SilentPort());
            var found = Discover();
            Assert.Empty(found);
            Assert.True(File.Exists(busy), "the entry of a live, busy Blender was removed");
        }

        [Fact]
        public void AnEntryBeingWrittenIsKept()
        {
            // Blender writes the file without a rename, so a read can see
            // half of it while Blender starts.
            string path = Path.Combine(_dir, Me + ".json");
            File.WriteAllText(path, "{\"pid\": ");
            Discover();
            Assert.True(File.Exists(path));
        }

        [Fact]
        public void TheEntryOfABlenderThatIsGoneIsRemoved()
        {
            string dead = Entry("dead", Gone, SilentPort());
            Discover();
            Assert.False(File.Exists(dead));
        }

        [Fact]
        public void AnEntryWhosePortRefusesIsRemoved()
        {
            string stopped = Entry("stopped", Me, ClosedPort());
            // Windows takes about a second to report a refused connection
            // on this machine's own address.
            Discover(4000);
            Assert.False(File.Exists(stopped));
        }

        [Fact]
        public void ABlenderThatAnswersIsFound()
        {
            string live = Entry("live", Me, AnsweringPort());
            var found = Discover();
            Assert.Single(found);
            Assert.True(File.Exists(live));
        }

        [Fact]
        public void TheProcessCheckKnowsBlender()
        {
            Assert.False(BlenderBridge.IsBlender(Me));
            Assert.False(BlenderBridge.IsBlender(Gone));
        }
    }
}
