using System;
using System.Collections.Generic;
using System.IO;
using Peak.Cadder.Core;
using Peak.Cadder.Core.Model;
using Peak.Cadder.Sw;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Peak.Cadder.Bridge
{
    /// <summary>
    /// What the localhost listener lets Blender ask SolidWorks for. Every
    /// method here runs on the SolidWorks thread (SwCommandServer guarantees
    /// it).
    ///
    /// Blender asks for four things: what is open ("status"), where the
    /// components are ("poses"), the geometry of some parts again at another
    /// fineness ("retessellate"), and the whole export again ("export", for
    /// Rebuild from CAD). None of them saves a document. A request that
    /// cannot be honoured comes back as ok:false rather than changing
    /// anything.
    ///
    /// Development tests do not go through the listener. The SW-MCP recipes
    /// in sw-addin\tools\recipes run the code of the add-in in a lab
    /// SolidWorks, and one of them calls Handle in the SolidWorks process to
    /// see what Blender gets.
    ///
    /// The reply of an export carries the lines the add-in logged meanwhile
    /// ("log"), so a caller sees the mate block and the probe verdicts
    /// without opening the log file.
    /// </summary>
    public static class SwCommandHandler
    {
        public static Dictionary<string, object> Handle(
            ISldWorks app, Dictionary<string, object> request)
        {
            string op = MiniJson.Str(request, "op", "");
            switch (op)
            {
                case "status": return Status(app);
                case "poses": return Poses(app, request);
                case "retessellate": return Retessellate(app, request);
                case "export": return Export(app, request);
                default:
                    return Fail("unknown op " + (string.IsNullOrEmpty(op) ? "(none)" : op));
            }
        }

        // ── Reads ─────────────────────────────────────────────────────────

        /// <summary>What is open, so the consumer can say whether the model it
        /// is looking at is still the one SolidWorks has.</summary>
        private static Dictionary<string, object> Status(ISldWorks app)
        {
            var model = app == null ? null : app.ActiveDoc as IModelDoc2;
            var settings = AppSettings.Load(AddIn.Log);
            return new Dictionary<string, object>
            {
                { "ok", true },
                { "document", model == null ? null : SafePath(model) },
                { "title", model == null ? null : SafeTitle(model) },
                { "type", model == null ? null : DocType(model) },
                { "pid", System.Diagnostics.Process.GetCurrentProcess().Id },
                { "quality_preset", settings.QualityPreset },
                { "hierarchy", settings.Hierarchy },
                { "log_path", AddIn.LogPath },
            };
        }

        /// <summary>
        /// The ribbon export without the ribbon, for Rebuild from CAD:
        /// manifest always (assemblies), STEP and the direct-link mesh on
        /// request. Files go to the export folder of the settings unless
        /// "dir" says otherwise. The reply carries the paths, the manifest's
        /// joint shape and every log line the export wrote.
        /// </summary>
        private static Dictionary<string, object> Export(
            ISldWorks app, Dictionary<string, object> request)
        {
            string error;
            var model = ModelFor(app, request, out error);
            if (model == null) return Fail(error);
            if (string.IsNullOrEmpty(SafePath(model)))
                return Fail("the document has never been saved");
            var settings = AppSettings.Load(AddIn.Log);
            bool withStep = MiniJson.Flag(request, "step", false);
            bool withMesh = MiniJson.Flag(request, "mesh", false);
            long mark = LogMark();
            return InConfiguration(model, request, () =>
            {
                var paths = ExportFiles(app, model, settings, request, withStep, withMesh);
                var reply = new Dictionary<string, object> { { "ok", true } };
                foreach (var kv in paths) reply[kv.Key] = kv.Value;
                reply["log"] = LogSince(mark);
                return reply;
            });
        }

        /// <summary>
        /// Runs one operation in the configuration the request names
        /// ("configuration"), and shows the configuration that was active
        /// again afterwards, also when the operation fails. Blender names
        /// the configuration of its import, because it can hold several
        /// configurations of one document, and an answer from another
        /// configuration puts the wrong parts and poses on its scene. A
        /// request that names none (an older Blender) gets the active
        /// configuration. A name the document does not have fails the
        /// request and says which name it is.
        /// </summary>
        private static Dictionary<string, object> InConfiguration(
            IModelDoc2 model, Dictionary<string, object> request,
            Func<Dictionary<string, object>> run)
        {
            ConfigurationSwitch shown;
            try
            {
                shown = ConfigurationSwitch.Showing(
                    model, MiniJson.Str(request, "configuration", null), AddIn.Log);
            }
            catch (InvalidOperationException ex) { return Fail(ex.Message); }
            using (shown) return run();
        }

        // ── Shared ────────────────────────────────────────────────────────

        /// <summary>
        /// Whether an export the listener runs keeps only the selected
        /// components: only when the request asks ("only_selected"). Blender
        /// asks for an export to bring the whole assembly over again or up to
        /// date, so a selection left in SolidWorks does not cut it, and the
        /// setting of the ribbon's send (GeometryFollowsSelection) does not
        /// apply.
        /// </summary>
        internal static bool OnlySelectedFor(Dictionary<string, object> request)
        {
            return request != null && MiniJson.Flag(request, "only_selected", false);
        }

        /// <summary>Manifest (assemblies), STEP and mesh as asked, of the
        /// configuration that SolidWorks shows now. Keys of the result:
        /// configuration, manifest, step, mesh, warnings, joints.</summary>
        private static Dictionary<string, object> ExportFiles(
            ISldWorks app, IModelDoc2 model, AppSettings settings,
            Dictionary<string, object> request, bool withStep, bool withMesh)
        {
            var assembly = model as IAssemblyDoc;
            string baseName = Path.GetFileNameWithoutExtension(model.GetPathName());
            string dir = MiniJson.Str(request, "dir", null);
            if (string.IsNullOrEmpty(dir))
                dir = SendToBlenderCommand.ExportDir(settings, model, baseName);
            Directory.CreateDirectory(dir);
            // The files are named after the configuration, as a send names
            // them, so two configurations do not write over each other.
            string configuration = Configurations.Active(model);
            string stem = ConfigurationNames.Stem(baseName, configuration);
            string stepPath = Path.Combine(dir, stem + ".step");
            string meshPath = Path.Combine(dir, stem + ".swmesh");
            string manifestPath = Path.Combine(dir, stem + ".rig.json");
            var result = new Dictionary<string, object>();
            result["configuration"] = configuration;

            bool onlySelected = OnlySelectedFor(request);
            settings.OnlySelected = onlySelected;
            HashSet<string> keep = null;
            // An update from Blender runs the same stages as the ribbon's
            // export, so SolidWorks shows the same bar. A user watching
            // SolidWorks then sees what Blender asked it to do.
            var bar = Sw.SwProgressBar.Open(app, "Exporting for Blender", AddIn.Log);
            try
            {
                // The rig export and the tessellation each number their own
                // stages, so each gets its share of the bar.
                if (withMesh) bar.Window(0, 78);
                if (assembly != null)
                {
                    // A mesh without a STEP is the direct link, and its
                    // manifest names no STEP file. A manifest on its own
                    // matches the STEP beside it, as Export Rig does.
                    var outcome = ExportCommand.ExportBundle(
                        app, model, assembly, stepPath, manifestPath, settings,
                        manifestOnly: !withStep, progress: bar,
                        matchStep: withStep || !withMesh);
                    keep = outcome.KeepPaths;
                    result["manifest"] = outcome.ManifestPath;
                    result["warnings"] = outcome.Warnings;
                    result["joints"] = JointShape(outcome.ManifestPath);
                    // Blender asked, so Blender says it: the user is there,
                    // not at SolidWorks.
                    if (outcome.LimitsLeftSuppressed.Count > 0)
                        result["limits_left_suppressed"] = outcome.LimitsLeftSuppressed;
                    if (withStep) result["step"] = outcome.StepPath ?? stepPath;
                }
                else if (withStep)
                {
                    StepPlusCommand.ExportAppearanceOnly(app, model, stepPath, settings);
                    result["step"] = stepPath;
                }
                if (withMesh)
                {
                    var fineness = FinenessFrom(request, settings);
                    if (keep == null && onlySelected)
                        keep = Sw.Selection.KeepSet(model, AddIn.Log);
                    bar.Window(78, 100);
                    NativeExport.Write(app, model, meshPath, fineness, AddIn.Log,
                        settings.SeparateSolids, keep, bar,
                        AppearanceOptions.From(settings),
                        // A consumer asking for the whole assembly again says
                        // which parts it holds defeatured, so that a rebuild
                        // gives back what the scene had rather than undoing it.
                        DefeatureOptions.From(request));
                    result["mesh"] = meshPath;
                }
            }
            finally { ExportCommand.CloseBar(bar); }
            return result;
        }

        /// <summary>The joint shape of a manifest, read back off the written
        /// file so it reports what a consumer will see.</summary>
        private static Dictionary<string, object> JointShape(string manifestPath)
        {
            var counts = new Dictionary<string, object>();
            string text;
            try { text = File.ReadAllText(manifestPath); }
            catch (IOException) { return counts; }
            int at = 0;
            const string key = "\"type\": \"";
            while ((at = text.IndexOf(key, at, StringComparison.Ordinal)) >= 0)
            {
                at += key.Length;
                int end = text.IndexOf('"', at);
                if (end < 0) break;
                string type = text.Substring(at, end - at);
                // The manifest also dumps the mates it read, typed by their
                // SolidWorks names; only the joints are the shape.
                if (type.StartsWith("swMate", StringComparison.Ordinal)) continue;
                object n;
                counts.TryGetValue(type, out n);
                counts[type] = (n == null ? 0 : (int)n) + 1;
            }
            return counts;
        }

        /// <summary>The open document the request names by its path or its
        /// title, or the active document when it names none. Null when
        /// there is no such document (DocumentFor).</summary>
        private static IModelDoc2 ModelFor(ISldWorks app, Dictionary<string, object> request)
        {
            string error;
            return ModelFor(app, request, out error);
        }

        private static IModelDoc2 ModelFor(
            ISldWorks app, Dictionary<string, object> request, out string error)
        {
            if (app == null)
            {
                error = NoDocument;
                return null;
            }
            return DocumentFor(request, () => app.ActiveDoc as IModelDoc2,
                OpenDocuments(app), SafePath, SafeTitle, out error);
        }

        private const string NoDocument = "no document is open in SolidWorks";

        private static IEnumerable<IModelDoc2> OpenDocuments(ISldWorks app)
        {
            var doc = app.GetFirstDocument() as IModelDoc2;
            while (doc != null)
            {
                yield return doc;
                doc = doc.GetNext() as IModelDoc2;
            }
        }

        /// <summary>
        /// The document a request is about, out of the open ones.
        ///
        /// Blender names the document its scene came from
        /// ("document_path"), and the answer is for that document, whether
        /// it is in front or not. Component ids are one assembly's
        /// numbering, and every assembly has a c001. An answer from the
        /// active document put the poses of another assembly on the scene,
        /// and the geometry of a part the user had opened to edit on the
        /// assembly's first component. So a named document that is not
        /// open fails the request, and says which document it is. A request
        /// that names none (an older Blender) gets the active document.
        /// </summary>
        internal static T DocumentFor<T>(
            Dictionary<string, object> request, Func<T> active, IEnumerable<T> open,
            Func<T, string> pathOf, Func<T, string> titleOf, out string error)
            where T : class
        {
            error = NoDocument;
            // A path names one document. A title can name two: with file
            // extensions hidden, plunger.SLDASM and its part plunger.SLDPRT
            // are both "plunger", and the lab exported the part (nothing).
            string path = MiniJson.Str(request, "document_path", null);
            if (!string.IsNullOrEmpty(path))
            {
                error = "The document " + path + " is not open in SolidWorks. "
                    + "Open it and try again.";
                string want;
                try { want = Path.GetFullPath(path); } catch { return null; }
                foreach (var doc in open)
                {
                    string have = pathOf(doc);
                    if (string.IsNullOrEmpty(have)) continue;
                    try { have = Path.GetFullPath(have); } catch { }
                    if (string.Equals(have, want, StringComparison.OrdinalIgnoreCase))
                    {
                        error = null;
                        return doc;
                    }
                }
                return null;
            }
            string title = MiniJson.Str(request, "title", null);
            if (string.IsNullOrEmpty(title))
            {
                var model = active();
                if (model != null) error = null;
                return model;
            }
            foreach (var doc in open)
                if (string.Equals(titleOf(doc), title, StringComparison.OrdinalIgnoreCase))
                {
                    error = null;
                    return doc;
                }
            return null;
        }

        /// <summary>
        /// Why a part document cannot answer a request, or null when it
        /// can. A part is one component, c001, placed under its own name
        /// (NativeExport). A request for another component, or for a
        /// placement inside an assembly, came from an assembly's scene, and
        /// the part answering as c001 put its geometry on that assembly's
        /// first component.
        /// </summary>
        internal static string PartMismatch(
            IList<string> ids, IList<string> paths, string title)
        {
            const string none = "none of those components are in the open part";
            if (ids != null && ids.Count > 0 && !ids.Contains("c001")) return none;
            if (paths == null || paths.Count == 0) return null;
            string name = WithoutPartExtension(title);
            foreach (var p in paths)
                if (string.Equals(WithoutPartExtension(p), name, StringComparison.OrdinalIgnoreCase))
                    return null;
            return none;
        }

        /// <summary>The title without ".SLDPRT". Windows shows the
        /// extension in a title or not, as the user set Explorer.</summary>
        private static string WithoutPartExtension(string name)
        {
            if (name == null) return "";
            return name.EndsWith(".sldprt", StringComparison.OrdinalIgnoreCase)
                ? name.Substring(0, name.Length - ".sldprt".Length) : name;
        }

        private static long LogMark()
        {
            try { return File.Exists(AddIn.LogPath) ? new FileInfo(AddIn.LogPath).Length : 0; }
            catch { return 0; }
        }

        private static List<object> LogSince(long mark)
        {
            var lines = new List<object>();
            try
            {
                if (!File.Exists(AddIn.LogPath)) return lines;
                using (var stream = new FileStream(AddIn.LogPath, FileMode.Open,
                                                   FileAccess.Read, FileShare.ReadWrite))
                {
                    if (mark > stream.Length) mark = 0;
                    stream.Seek(mark, SeekOrigin.Begin);
                    using (var reader = new StreamReader(stream))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                            if (line.Length > 0) lines.Add(line);
                    }
                }
            }
            catch (IOException) { }
            return lines;
        }

        private static string DocType(IModelDoc2 model)
        {
            try
            {
                switch ((swDocumentTypes_e)model.GetType())
                {
                    case swDocumentTypes_e.swDocASSEMBLY: return "assembly";
                    case swDocumentTypes_e.swDocPART: return "part";
                    case swDocumentTypes_e.swDocDRAWING: return "drawing";
                    default: return "other";
                }
            }
            catch { return null; }
        }

        private static List<object> Flatten(double[,] m)
        {
            if (m == null) return null;
            var list = new List<object>();
            for (int r = 0; r < m.GetLength(0); r++)
                for (int c = 0; c < m.GetLength(1); c++)
                    list.Add(m[r, c]);
            return list;
        }

        /// <summary>
        /// Where every component sits now, as the manifest states it: the
        /// component id, its path, and its world transform in metres. Read
        /// only, and cheap: the walk, no tessellation and no mates. What an
        /// update from CAD asks for when the parts have been moved in
        /// SolidWorks but nothing has been redrawn.
        /// </summary>
        private static Dictionary<string, object> Poses(
            ISldWorks app, Dictionary<string, object> request)
        {
            string error;
            var model = ModelFor(app, request, out error);
            if (model == null) return Fail(error);
            return InConfiguration(model, request, () => PosesShown(model, request));
        }

        private static Dictionary<string, object> PosesShown(
            IModelDoc2 model, Dictionary<string, object> request)
        {
            var assembly = model as IAssemblyDoc;
            if (assembly == null) return Fail("the document is not an assembly");

            var walked = AssemblyWalker.Walk(assembly, AddIn.Log);
            var persistent = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var w in walked)
                if (w.Graph != null)
                    persistent[w.Id] = ComponentIdentity.PersistIdBase64(model, w.Comp);
            var selection = Selection(request, persistent);

            var poses = new List<object>();
            foreach (var w in walked)
            {
                if (w.Graph == null) continue;
                if (!selection.Everything && !selection.Ids.Contains(w.Id)) continue;
                poses.Add(PoseRow(w.Id, w.Graph.Path, persistent[w.Id],
                    w.Graph.Transform, selection));
            }
            if (!selection.Everything && poses.Count == 0)
                return Fail("none of those components are in the open assembly");
            AddIn.Log("sw bridge: poses for " + poses.Count + " component(s)");
            return new Dictionary<string, object>
            {
                { "ok", true },
                { "document", SafeTitle(model) },
                { "components", poses },
                { "missing", selection.Missing },
            };
        }

        /// <summary>One row of the poses answer. "id" is the number the
        /// walk gives the component NOW, which an edit to the assembly
        /// moves. So the row also carries its persistent id, and the
        /// persistent id the request used for it ("requested_id"), and
        /// Blender puts the row on the part by those.</summary>
        internal static Dictionary<string, object> PoseRow(
            string id, string path, string persistentId, double[,] transform,
            ComponentSelection selection)
        {
            var row = new Dictionary<string, object>
            {
                { "id", id },
                { "sw_path", path },
                { "sw_persistent_id", persistentId },
                { "transform", Flatten(transform) },
            };
            string requested;
            if (selection != null && selection.RequestedBy.TryGetValue(id, out requested))
                row["requested_id"] = requested;
            return row;
        }

        /// <summary>The components a request is about: "components" names
        /// them by manifest id, "persistent_ids" by SolidWorks' own reference,
        /// which survives an edit to the assembly. Naming neither means the
        /// whole assembly.</summary>
        private static ComponentSelection Selection(
            Dictionary<string, object> request, Dictionary<string, string> present)
        {
            var selection = ComponentSelection.Resolve(
                Strings(request, "components"), Strings(request, "persistent_ids"), present);
            if (selection.Missing.Count > 0)
                AddIn.Log("sw bridge: " + selection.Missing.Count
                    + " requested component(s) are not in the assembly");
            if (selection.StaleComponents.Count > 0)
                AddIn.Log("sw bridge: " + selection.StaleComponents.Count
                    + " component number(s) now name other parts and were left out: "
                    + string.Join(", ", selection.StaleComponents.ToArray()));
            return selection;
        }

        private static List<string> Strings(Dictionary<string, object> request, string key)
        {
            var list = new List<string>();
            var raw = MiniJson.Arr(request, key);
            if (raw == null) return list;
            foreach (var item in raw)
            {
                var s = item as string;
                if (!string.IsNullOrEmpty(s)) list.Add(s);
            }
            return list;
        }

        /// <summary>
        /// What a request asks the parts to be cut to. Blender sends the
        /// distance and the angle, or Relative Tessellation, from its Mesh
        /// Quality settings. CADder 1.0.0 sends only the 0..1 dial, and a
        /// request with neither takes the Export Options.
        /// </summary>
        internal static BodyTessellator.Fineness FinenessFrom(
            Dictionary<string, object> request, AppSettings settings)
        {
            if (request != null && MiniJson.Flag(request, "relative", false))
                return BodyTessellator.RelativeTo(
                    MiniJson.Num(request, "relative_distance", settings.QualityRelativeDistance),
                    MiniJson.Num(request, "angle_rad", settings.QualityAngle));
            if (request != null && request.ContainsKey("chord_m"))
                return BodyTessellator.Custom(
                    MiniJson.Num(request, "chord_m", settings.QualityDistance),
                    MiniJson.Num(request, "angle_rad", settings.QualityAngle));
            if (request != null && request.ContainsKey("quality"))
                return BodyTessellator.FinenessFor(MiniJson.Num(request, "quality", 0.45));
            return SendToBlenderCommand.FinenessOf(settings);
        }

        /// <summary>
        /// Re-reads named components at a new tolerance and writes them as a
        /// .swmesh. The reply is a FILE PATH, not the geometry: a megabyte of
        /// triangles through an HTTP body would be JSON-escaped and parsed
        /// twice for no reason, when both ends are on the same disk.
        /// </summary>
        private static Dictionary<string, object> Retessellate(
            ISldWorks app, Dictionary<string, object> request)
        {
            string error;
            var model = ModelFor(app, request, out error);
            if (model == null) return Fail(error);
            return InConfiguration(model, request, () => RetessellateShown(app, model, request));
        }

        private static Dictionary<string, object> RetessellateShown(
            ISldWorks app, IModelDoc2 model, Dictionary<string, object> request)
        {
            var settings = AppSettings.Load(AddIn.Log);
            var fineness = FinenessFrom(request, settings);
            // The geometry has to come back in the SAME pieces it went out
            // in. Asking for a body-split part again without this returned
            // the whole part as one definition, and the consumer then put
            // that whole part on every one of its body objects: the part
            // drawn over itself once per body (Oscar, 2026-09-16). The
            // request may say, because the consumer knows what it holds;
            // the export setting answers when it does not.
            bool separateSolids = MiniJson.Flag(
                request, "separate_solids", settings.SeparateSolids);
            var appearance = AppearanceOptions.From(settings);
            // Which parts travel defeatured is the consumer's decision and
            // arrives with the request, one entry per component. The add-in
            // holds no setting of its own.
            var defeature = DefeatureOptions.From(request);
            var assembly = model as IAssemblyDoc;
            MeshScene scene;
            ComponentSelection selection = null;
            if (assembly != null)
            {
                var walked = AssemblyWalker.Walk(assembly, AddIn.Log);
                var persistent = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var w in walked)
                    if (w.Graph != null)
                        persistent[w.Id] = ComponentIdentity.PersistIdBase64(model, w.Comp);
                selection = Selection(request, persistent);
                // A defeature row finds its part the way the selection does.
                defeature = defeature.ResolvedAgainst(persistent);
                // The consumer may name the PLACEMENTS it wants as well as
                // the components. A component id is the rig body's, which
                // for a rigid subassembly is every part of it, so the paths
                // are what keep "this part again" to this part.
                var paths = Strings(request, "paths");
                var keepPaths = paths.Count == 0 ? null
                    : new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
                scene = NativeSceneBuilder.Build(
                    walked, fineness, AddIn.Log, selection.Everything ? null : selection.Ids,
                    separateSolids, keepPaths: keepPaths,
                    appearance: appearance, defeature: defeature, top: model);
                if (!selection.Everything && scene.Instances.Count == 0)
                    return Fail("none of those components are in the open assembly");
            }
            else
            {
                // A part document is one component. A filter that names
                // another component is not about this part.
                string mismatch = PartMismatch(
                    Strings(request, "components"), Strings(request, "paths"),
                    SafeTitle(model));
                if (mismatch != null) return Fail(mismatch);
                scene = NativeExport.Build(
                    app, model, fineness, AddIn.Log, separateSolids,
                    appearance: appearance, defeature: defeature);
            }
            if (scene.Definitions.Count == 0) return Fail("nothing to tessellate");

            string path = MiniJson.Str(request, "out", null);
            if (string.IsNullOrEmpty(path))
            {
                PruneTemp(Path.GetTempPath(), "cadlink-refine-*.swmesh", DateTime.UtcNow);
                path = Path.Combine(Path.GetTempPath(),
                    "cadlink-refine-" + Guid.NewGuid().ToString("N") + ".swmesh");
            }
            MeshWriter.Write(path, scene);

            int triangles = 0;
            foreach (var d in scene.Definitions) triangles += d.TriangleCount;
            AddIn.Log("sw bridge: retessellated " + scene.Instances.Count
                + " instance(s)"
                + (defeature.Any ? ", " + defeature.Count + " defeatured," : "")
                + " at " + fineness
                + " -> " + triangles + " triangle(s)");

            return new Dictionary<string, object>
            {
                { "ok", true },
                { "missing", selection == null ? new List<string>() : selection.Missing },
                { "mesh", path },
                { "definitions", scene.Definitions.Count },
                { "instances", scene.Instances.Count },
                { "triangles", triangles },
                { "tolerance_m", scene.Tolerance },
            };
        }

        /// <summary>How old a file of an earlier answer must be before it
        /// is removed. Blender reads the file as soon as the answer
        /// arrives, so this is a wide margin.</summary>
        private static readonly TimeSpan TempFileAge = TimeSpan.FromMinutes(30);

        /// <summary>
        /// Removes the files of earlier answers that match
        /// <paramref name="pattern"/> and are older than TempFileAge.
        /// Returns how many. Nothing else deletes them: a refine answers
        /// with a .swmesh in the temp folder, Blender reads it, and one
        /// machine had 149 of them, 469 MB. A file that is in use stays
        /// for the next time.
        /// </summary>
        internal static int PruneTemp(string dir, string pattern, DateTime nowUtc)
        {
            string[] files;
            try { files = Directory.GetFiles(dir, pattern); }
            catch (Exception) { return 0; }
            int removed = 0;
            foreach (var file in files)
            {
                try
                {
                    if (nowUtc - File.GetLastWriteTimeUtc(file) < TempFileAge) continue;
                    File.Delete(file);
                    removed++;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return removed;
        }

        private static Dictionary<string, object> Fail(string why)
        {
            return new Dictionary<string, object>
            {
                { "ok", false }, { "error", why },
            };
        }

        private static string SafePath(IModelDoc2 model)
        {
            try { return model.GetPathName(); } catch { return null; }
        }

        private static string SafeTitle(IModelDoc2 model)
        {
            try { return model.GetTitle(); } catch { return null; }
        }
    }
}