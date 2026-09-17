using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using Peak.Cadder.Core;
using Xunit;

namespace Peak.Cadder.Tests
{
    public class MiniJsonTests
    {
        [Fact]
        public void RoundTripsTheBridgePayloadShape()
        {
            var payload = new Dictionary<string, object>
            {
                { "step", "C:\\exports\\asm.step" },
                { "manifest", null },
                { "steps", new Dictionary<string, object>
                    { { "import", true }, { "build_rig", false } } },
                { "count", 3 },
                { "scale", 0.001 },
                { "list", new List<object> { "a", 1.5, false } },
            };
            var parsed = MiniJson.ParseObject(MiniJson.Write(payload));

            Assert.Equal("C:\\exports\\asm.step", MiniJson.Str(parsed, "step"));
            Assert.Null(parsed["manifest"]);
            Assert.True(MiniJson.Flag(MiniJson.Obj(parsed, "steps"), "import"));
            Assert.False(MiniJson.Flag(MiniJson.Obj(parsed, "steps"), "build_rig"));
            Assert.Equal(3, MiniJson.Int(parsed, "count"));
            Assert.Equal(0.001, (double)parsed["scale"], 12);
            var list = MiniJson.Arr(parsed, "list");
            Assert.Equal(3, list.Count);
            Assert.Equal("a", list[0]);
        }

        [Fact]
        public void EscapesSurviveBothDirections()
        {
            var obj = new Dictionary<string, object>
            {
                { "path", "C:\\a\"b\\nc" },
                { "text", "line1\nline2\ttabbed" },
            };
            var parsed = MiniJson.ParseObject(MiniJson.Write(obj));
            Assert.Equal("C:\\a\"b\\nc", MiniJson.Str(parsed, "path"));
            Assert.Equal("line1\nline2\ttabbed", MiniJson.Str(parsed, "text"));
        }

        [Fact]
        public void ParsesWhatPythonJsonDumpsEmits()
        {
            const string text = "{\"ok\": true, \"stages\": {\"match\": "
                + "{\"matched\": 5, \"unmatched\": []}}, \"log\": [\"a b\"], "
                + "\"error\": null, \"pi\": 3.14159, \"neg\": -2e-3, "
                + "\"uni\": \"\\u00e9\"}";
            var parsed = MiniJson.ParseObject(text);
            Assert.True(MiniJson.Flag(parsed, "ok"));
            Assert.Equal(5, MiniJson.Int(MiniJson.Obj(
                MiniJson.Obj(parsed, "stages"), "match"), "matched"));
            Assert.Empty(MiniJson.Arr(MiniJson.Obj(
                MiniJson.Obj(parsed, "stages"), "match"), "unmatched"));
            Assert.Equal(3.14159, (double)parsed["pi"], 9);
            Assert.Equal(-0.002, (double)parsed["neg"], 9);
            Assert.Equal("\u00e9", MiniJson.Str(parsed, "uni"));
        }

        [Fact]
        public void NumbersStayInvariantUnderACommaCulture()
        {
            var saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var text = MiniJson.Write(new Dictionary<string, object>
                    { { "v", 1.25 } });
                Assert.Contains("1.25", text);
                Assert.DoesNotContain(",", text);
                var parsed = MiniJson.ParseObject("{\"v\": 1.25}");
                Assert.Equal(1.25, (double)parsed["v"], 12);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Fact]
        public void MalformedInputThrowsInsteadOfGuessing()
        {
            Assert.Throws<System.FormatException>(() => MiniJson.Parse("{\"a\": }"));
            Assert.Throws<System.FormatException>(() => MiniJson.Parse("{\"a\": 1"));
            Assert.Throws<System.FormatException>(() => MiniJson.Parse("[1, 2,,]"));
            Assert.Throws<System.FormatException>(() => MiniJson.ParseObject("[1]"));
            Assert.Throws<System.FormatException>(() => MiniJson.Parse("{} trailing"));
        }
    }

    public class AppSettingsTests
    {
        [Fact]
        public void RoundTripsThroughItsFile()
        {
            string path = Path.Combine(Path.GetTempPath(),
                "cadder-settings-" + System.Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var settings = new AppSettings
                {
                    Ap = 203,
                    RunDofProbe = false,
                    Hierarchy = "TREE",
                    QualityPreset = "FINE",
                    BlenderExe = "C:\\Program Files\\Blender Foundation\\Blender 5.1\\blender.exe",
                    ExportFolderMode = "beside",
                    ExportDecals = false,
                };
                settings.Save(null, path);

                var loaded = AppSettings.Load(null, path);
                Assert.Equal(203, loaded.Ap);
                Assert.False(loaded.RunDofProbe);
                Assert.Equal("TREE", loaded.Hierarchy);
                Assert.Equal("FINE", loaded.QualityPreset);
                Assert.Equal(settings.BlenderExe, loaded.BlenderExe);
                Assert.Equal("beside", loaded.ExportFolderMode);
                Assert.False(loaded.ExportDecals);
                // Untouched fields keep their defaults.
                Assert.True(loaded.BuildRig);
                Assert.True(loaded.ExportAppearances);
                Assert.True(loaded.RepairAppearances);
            }
            finally
            {
                try { File.Delete(path); } catch (IOException) { }
            }
        }

        [Fact]
        public void MissingOrBrokenFileYieldsDefaults()
        {
            var missing = AppSettings.Load(null,
                Path.Combine(Path.GetTempPath(), "cadder-none-"
                    + System.Guid.NewGuid().ToString("N") + ".json"));
            Assert.Equal(214, missing.Ap);
            Assert.Equal("EMPTIES", missing.Hierarchy);

            string broken = Path.Combine(Path.GetTempPath(),
                "cadder-broken-" + System.Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(broken, "{not json at all");
                var loaded = AppSettings.Load(null, broken);
                Assert.Equal(214, loaded.Ap);
                Assert.True(loaded.BuildRig);
                Assert.True(loaded.ExportAppearances);
            }
            finally
            {
                try { File.Delete(broken); } catch (IOException) { }
            }
        }
    }
}
