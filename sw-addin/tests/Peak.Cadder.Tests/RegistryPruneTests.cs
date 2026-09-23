using System;
using System.Diagnostics;
using System.IO;
using Peak.Cadder.Bridge;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// SolidWorks removes the registry entries that dead instances left
    /// behind before it writes its own. After a crash and a reboot, the
    /// process id in an old entry often belongs to a Windows service, and
    /// asking such a process whether it has exited throws "Access is
    /// denied". That stopped the prune and the registry write with it, so
    /// Blender never found this SolidWorks.
    /// </summary>
    public class RegistryPruneTests : IDisposable
    {
        private readonly string _dir;

        public RegistryPruneTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "cadder-prune-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        private string Entry(int pid)
        {
            string path = Path.Combine(_dir, pid + ".json");
            File.WriteAllText(path, "{\"pid\": " + pid + ", \"port\": 51820, \"token\": \"old\"}");
            return path;
        }

        private static int Me { get { return Process.GetCurrentProcess().Id; } }

        [Fact]
        public void AnEntryWhoseProcessIdASystemProcessNowHasIsRemoved()
        {
            // Process 4 is the Windows kernel ("System"). It is always
            // running, and a user process cannot open it.
            string stale = Entry(4);
            SwCommandServer.PruneRegistry(_dir, "SLDWORKS");
            Assert.False(File.Exists(stale));
        }

        [Fact]
        public void AnEntryWhoseProcessIdAnotherProgramNowHasIsRemoved()
        {
            // The id belongs to a live process that is not SolidWorks: the
            // entry names a SolidWorks that is gone.
            string stale = Entry(Me);
            SwCommandServer.PruneRegistry(_dir, "SLDWORKS");
            Assert.False(File.Exists(stale));
        }

        [Fact]
        public void AnEntryOfARunningInstanceStays()
        {
            string live = Entry(Me);
            SwCommandServer.PruneRegistry(_dir, Process.GetCurrentProcess().ProcessName);
            Assert.True(File.Exists(live));
        }

        [Fact]
        public void AnEntryOfAProcessThatIsGoneIsRemoved()
        {
            string dead = Entry(int.MaxValue - 3);
            SwCommandServer.PruneRegistry(_dir, "SLDWORKS");
            Assert.False(File.Exists(dead));
        }

        [Fact]
        public void AFileThatIsNotAnEntryStays()
        {
            string other = Path.Combine(_dir, "notes.json");
            File.WriteAllText(other, "{}");
            SwCommandServer.PruneRegistry(_dir, "SLDWORKS");
            Assert.True(File.Exists(other));
        }
    }
}
