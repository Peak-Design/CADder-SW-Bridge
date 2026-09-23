using System;
using System.IO;
using Peak.Cadder.Bridge;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// The files the listener writes to the temp folder for Blender to
    /// read. A refine answers with a .swmesh there, and Blender reads it at
    /// once. Nothing removed them, and a machine in daily use had 149 of
    /// them, 469 MB. The listener now removes its own old ones before it
    /// writes a new one.
    /// </summary>
    public class TempFileTests : IDisposable
    {
        private readonly string _dir;
        private readonly DateTime _now = new DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);

        public TempFileTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "cadder-temp-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        private string File(string name, TimeSpan age)
        {
            string path = Path.Combine(_dir, name);
            System.IO.File.WriteAllText(path, "x");
            System.IO.File.SetLastWriteTimeUtc(path, _now - age);
            return path;
        }

        [Fact]
        public void AnOldRefineFileIsRemoved()
        {
            string old = File("cadlink-refine-aaa.swmesh", TimeSpan.FromHours(2));
            Assert.Equal(1, SwCommandHandler.PruneTemp(_dir, "cadlink-refine-*.swmesh", _now));
            Assert.False(System.IO.File.Exists(old));
        }

        [Fact]
        public void ARecentRefineFileStays()
        {
            // Blender can still be reading it.
            string recent = File("cadlink-refine-bbb.swmesh", TimeSpan.FromMinutes(2));
            SwCommandHandler.PruneTemp(_dir, "cadlink-refine-*.swmesh", _now);
            Assert.True(System.IO.File.Exists(recent));
        }

        [Fact]
        public void OtherFilesStay()
        {
            string other = File("scene.swmesh", TimeSpan.FromDays(3));
            string view = File("cadlink-view-ccc.bmp", TimeSpan.FromDays(3));
            SwCommandHandler.PruneTemp(_dir, "cadlink-refine-*.swmesh", _now);
            Assert.True(System.IO.File.Exists(other));
            Assert.True(System.IO.File.Exists(view));
        }

        [Fact]
        public void AFileInUseIsLeftForNextTime()
        {
            string old = File("cadlink-refine-ddd.swmesh", TimeSpan.FromHours(2));
            using (new FileStream(old, FileMode.Open, FileAccess.Read, FileShare.None))
                Assert.Equal(0, SwCommandHandler.PruneTemp(_dir, "cadlink-refine-*.swmesh", _now));
            Assert.True(System.IO.File.Exists(old));
        }
    }
}
