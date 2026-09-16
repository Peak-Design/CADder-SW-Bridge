using System;
using System.IO;
using Xunit;

namespace Peak.SwToBlender.Tests
{
    /// <summary>
    /// SolidWorks reads the ribbon artwork by absolute path and says nothing
    /// when a file is missing or the wrong shape: the button just shows the
    /// stock icon. AddIn.ApplyIcons expects one strip per size with one
    /// icon per command side by side, in registration order, and one
    /// square group icon per size. These tests pin that shape against the
    /// files tools/Make-Icons.py writes from the masters in icons/.
    /// </summary>
    public class IconStripTests
    {
        private static readonly int[] Sizes = { 20, 32, 40, 64, 96, 128 };
        private const int Commands = 5;

        [Fact]
        public void EveryStripHoldsOneIconPerCommandAtItsSize()
        {
            string dir = IconsDir();
            foreach (int size in Sizes)
            {
                var strip = PngSize(Path.Combine(dir, "SwToBlender_" + size + ".png"));
                Assert.Equal(Commands * size, strip.Item1);
                Assert.Equal(size, strip.Item2);
            }
        }

        [Fact]
        public void EveryGroupIconIsSquareAtItsSize()
        {
            string dir = IconsDir();
            foreach (int size in Sizes)
            {
                var main = PngSize(Path.Combine(dir, "SwToBlenderMain_" + size + ".png"));
                Assert.Equal(size, main.Item1);
                Assert.Equal(size, main.Item2);
            }
        }

        [Fact]
        public void EveryCommandHasAMaster()
        {
            string masters = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(IconsDir())),
                                          "..", "..", "icons");
            foreach (string name in new[] { "send_direct", "options", "refresh",
                                            "step+", "export_rig", "logo" })
            {
                var size = PngSize(Path.Combine(masters, name + ".png"));
                Assert.Equal(128, size.Item1);
                Assert.Equal(128, size.Item2);
            }
        }

        /// <summary>The icons folder in the source tree, found by walking up
        /// from the test output directory.</summary>
        private static string IconsDir()
        {
            string dir = AppContext.BaseDirectory;
            while (dir != null)
            {
                string candidate = Path.Combine(dir, "src", "Peak.SwToBlender", "icons");
                if (Directory.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            throw new DirectoryNotFoundException("src/Peak.SwToBlender/icons above " + AppContext.BaseDirectory);
        }

        /// <summary>Width and height from the PNG header: the IHDR chunk
        /// follows the eight-byte signature, big-endian.</summary>
        private static Tuple<int, int> PngSize(string path)
        {
            Assert.True(File.Exists(path), path + " is missing");
            var bytes = new byte[24];
            using (var f = File.OpenRead(path))
                Assert.Equal(24, f.Read(bytes, 0, 24));
            Assert.Equal(0x89, bytes[0]);
            Assert.Equal((byte)'P', bytes[1]);
            int w = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
            int h = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
            return Tuple.Create(w, h);
        }
    }
}
