using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Peak.SwToBlender;
using Peak.SwToBlender.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Peak.SwToBlender.Tools
{
    /// <summary>
    /// Re-exports a folder of assemblies through the real export pipeline,
    /// with no ribbon and no dialogs.
    ///
    /// The corpus is the only test that sees what SolidWorks actually hands
    /// out — fixtures encode what a live entity looked like ONCE, which is
    /// how a mate type missing from a reader lookup survived to a live round
    /// (corpus 15 cone3, 2026-08-23). Re-exporting every assembly after a
    /// reader change turns that from a manual click-through into one command,
    /// and the printed summary diffs round to round.
    ///
    ///   CorpusExport &lt;path&gt; [&lt;path&gt; ...] [--step] [--quit]
    ///
    /// A path is an .SLDASM or a folder to search. --step also writes the
    /// STEP file (default is manifest-only, which is what a kinematics
    /// change needs). --quit closes SolidWorks afterwards, which is only
    /// honoured when this harness started it.
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            var paths = new List<string>();
            bool withStep = false, quit = false;
            foreach (var a in args)
            {
                if (a == "--step") withStep = true;
                else if (a == "--quit") quit = true;
                else paths.Add(a);
            }
            if (paths.Count == 0)
            {
                Console.Error.WriteLine(
                    "usage: CorpusExport <assembly-or-folder> [...] [--step] [--quit]");
                return 2;
            }

            var files = new List<string>();
            foreach (var p in paths)
            {
                if (Directory.Exists(p))
                    files.AddRange(Directory.GetFiles(p, "*.sldasm",
                        SearchOption.AllDirectories));
                else if (File.Exists(p))
                    files.Add(p);
                else
                    Console.Error.WriteLine("skipped (not found): " + p);
            }
            files.Sort(StringComparer.OrdinalIgnoreCase);
            if (files.Count == 0)
            {
                Console.Error.WriteLine("no assemblies found");
                return 2;
            }

            bool started;
            ISldWorks app = Connect(out started);
            if (app == null)
            {
                Console.Error.WriteLine("could not reach SolidWorks");
                return 2;
            }

            int failed = 0;
            var settings = new AppSettings();
            try
            {
                foreach (var file in files)
                {
                    try
                    {
                        Console.WriteLine(Export(app, file, settings, withStep));
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        Console.WriteLine(Path.GetFileNameWithoutExtension(file)
                            + ": FAILED — " + ex.Message.Replace("\n", " "));
                    }
                }
            }
            finally
            {
                if (started && quit)
                {
                    try { app.ExitApp(); } catch { }
                }
            }
            Console.WriteLine(files.Count + " assembly(s), " + failed + " failed");
            return failed == 0 ? 0 : 1;
        }

        private static string Export(
            ISldWorks app, string file, AppSettings settings, bool withStep)
        {
            int err = 0, warn = 0;
            var model = app.OpenDoc6(
                file, (int)swDocumentTypes_e.swDocASSEMBLY,
                (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "",
                ref err, ref warn) as IModelDoc2;
            var assembly = model as IAssemblyDoc;
            if (assembly == null)
                throw new InvalidOperationException(
                    "could not open (error " + err + ")");
            try
            {
                string basePath = Path.Combine(
                    Path.GetDirectoryName(file) ?? ".",
                    Path.GetFileNameWithoutExtension(file));
                var outcome = ExportCommand.ExportBundle(
                    app, model, assembly, basePath + ".step",
                    basePath + ".rig.json", settings, !withStep);
                return Path.GetFileNameWithoutExtension(file) + ": "
                    + Summarise(outcome.ManifestPath) + " ("
                    + outcome.Warnings + " warning(s))";
            }
            finally
            {
                try { app.CloseDoc(model.GetTitle()); } catch { }
            }
        }

        /// <summary>The joint shape of a manifest, as one line to diff. Read
        /// back off the written file so it reports what a consumer will see,
        /// not what the writer believed.</summary>
        private static string Summarise(string manifestPath)
        {
            string text;
            try { text = File.ReadAllText(manifestPath); }
            catch (IOException ex) { return "unreadable — " + ex.Message; }
            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            int at = 0;
            const string key = "\"type\": \"";
            while ((at = text.IndexOf(key, at, StringComparison.Ordinal)) >= 0)
            {
                at += key.Length;
                int end = text.IndexOf('"', at);
                if (end < 0) break;
                string type = text.Substring(at, end - at);
                counts.TryGetValue(type, out int n);
                counts[type] = n + 1;
            }
            if (counts.Count == 0) return "no joints";
            var parts = new List<string>();
            foreach (var kv in counts) parts.Add(kv.Value + " " + kv.Key);
            return string.Join(", ", parts.ToArray());
        }

        /// <summary>An already-running SolidWorks first: starting a second
        /// instance while one is open is not supported and costs a licence
        /// round trip.</summary>
        private static ISldWorks Connect(out bool started)
        {
            started = false;
            try
            {
                var running = Marshal.GetActiveObject("SldWorks.Application") as ISldWorks;
                if (running != null)
                {
                    Console.WriteLine("using the running SolidWorks");
                    return running;
                }
            }
            catch (COMException) { /* none running */ }

            var type = Type.GetTypeFromProgID("SldWorks.Application");
            if (type == null) return null;
            Console.WriteLine("starting SolidWorks...");
            var app = Activator.CreateInstance(type) as ISldWorks;
            if (app == null) return null;
            started = true;
            // Invisible is the point, but SolidWorks needs the flag set
            // explicitly or it comes up on screen.
            app.Visible = false;
            app.UserControl = false;
            return app;
        }
    }
}
