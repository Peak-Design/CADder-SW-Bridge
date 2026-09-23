using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Peak.Cadder.Core;
using Peak.Cadder.Core.Model;
using Peak.Cadder.Sw;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Peak.Cadder.Bridge
{
    /// <summary>
    /// What the localhost listener lets a caller ask SolidWorks for. Every
    /// method here runs on the SolidWorks thread (SwCommandServer guarantees
    /// it).
    ///
    /// Two callers, two rules. Blender asks for READ-ONLY things: "this part
    /// is too coarse, send it again finer". A test harness (tools/swlab.py)
    /// asks for the lab operations as well: open and close documents,
    /// export, send, read the mates, suppress a mate, set a dimension,
    /// rebuild, take a screenshot, quit. Those change the open model, so
    /// they run only in a Debug build with AppSettings.LabOps on, and
    /// nothing here saves a document the user or the corpus owns: a lab
    /// session leaves every such file as it found it. The two operations
    /// that make test models (LabCompose) save only under the temp folder.
    /// A request that cannot be honoured comes back as ok:false rather than
    /// changing anything.
    ///
    /// Every reply from an operation that runs the readers carries the
    /// lines the add-in logged meanwhile ("log"), so a caller sees the mate
    /// block and the probe verdicts without opening the log file.
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
                case "retessellate": return Retessellate(app, request);
                case "poses": return Poses(app, request);
                case "documents": return Documents(app);
                case "ribbon": return Ribbon();
                case "log": return LogTail(request);
                case "screenshot": return Screenshot(app, request);
                case "apply_appearance": return Lab(request, () => ApplyAppearance(app, request));
                case "select": return Lab(request, () => Select(app, request));
                case "progress_demo": return Lab(request, () => ProgressDemo(app, request));
                case "refresh": return Lab(request, () => RefreshPoses(app, request));
                case "move": return Lab(request, () => Move(app, request));
                case "tess_uv": return TessUv(app, request);
                case "appearances":
                    return AppearanceProbe.Run(ModelFor(app, request),
                        (int)MiniJson.Num(request, "max_faces", 60),
                        (int)MiniJson.Num(request, "face", -1));
                case "mates": return Mates(app, request);
                case "small_features": return SmallFeatures(app, request);
                case "plane_uv": return PlaneUv(app, request);
                case "export": return Export(app, request);
                case "send": return Send(app, request);
                case "open": return Lab(request, () => Open(app, request));
                case "close": return Lab(request, () => Close(app, request));
                case "activate": return Lab(request, () => Activate(app, request));
                case "rebuild": return Lab(request, () => Rebuild(app, request));
                case "suppress": return Lab(request, () => Suppress(app, request, true));
                case "unsuppress": return Lab(request, () => Suppress(app, request, false));
                case "dimension": return Lab(request, () => Dimension(app, request));
                case "quit": return Lab(request, () => Quit(app));
                case "status_probe": return Lab(request, () => StatusProbe(app, request));
                case "compose": return Lab(request, () => LabCompose.Compose(app, request));
                case "configure":
                    return Lab(request, () => LabCompose.Configure(app, request, ModelFor(app, request)));
                default:
                    return Fail("unknown op " + (string.IsNullOrEmpty(op) ? "(none)" : op));
            }
        }

        /// <summary>The gate for operations that change the open model.</summary>
        private static Dictionary<string, object> Lab(
            Dictionary<string, object> request, Func<Dictionary<string, object>> run)
        {
            var settings = AppSettings.Load(AddIn.Log);
            if (!settings.LabOpsAllowed)
                return Fail(AppSettings.LabBuild
                    ? "the test harness is off (lab_ops in the add-in settings)"
                    : "this build has no test harness");
            return run();
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
                { "lab_ops", settings.LabOpsAllowed },
                { "quality_preset", settings.QualityPreset },
                { "hierarchy", settings.Hierarchy },
                { "log_path", AddIn.LogPath },
            };
        }

        private static Dictionary<string, object> Documents(ISldWorks app)
        {
            var docs = new List<object>();
            if (app != null)
            {
                var active = app.ActiveDoc as IModelDoc2;
                var doc = app.GetFirstDocument() as IModelDoc2;
                while (doc != null)
                {
                    docs.Add(new Dictionary<string, object>
                    {
                        { "title", SafeTitle(doc) },
                        { "path", SafePath(doc) },
                        { "type", DocType(doc) },
                        { "active", active != null && SafeTitle(doc) == SafeTitle(active) },
                    });
                    doc = doc.GetNext() as IModelDoc2;
                }
            }
            return new Dictionary<string, object> { { "ok", true }, { "documents", docs } };
        }

        /// <summary>The last N lines of the add-in log.</summary>
        private static Dictionary<string, object> LogTail(Dictionary<string, object> request)
        {
            int lines = MiniJson.Int(request, "lines", 50);
            var all = new List<string>();
            try
            {
                if (File.Exists(AddIn.LogPath))
                    all.AddRange(File.ReadAllLines(AddIn.LogPath));
            }
            catch (IOException ex) { return Fail("log unreadable: " + ex.Message); }
            int start = Math.Max(0, all.Count - Math.Max(1, lines));
            var tail = new List<object>();
            for (int i = start; i < all.Count; i++) tail.Add(all[i]);
            return new Dictionary<string, object>
            {
                { "ok", true }, { "path", AddIn.LogPath }, { "lines", tail },
            };
        }

        /// <summary>The graphics area as a bitmap, so a caller without eyes on
        /// the screen can still see the model.</summary>
        private static Dictionary<string, object> Screenshot(
            ISldWorks app, Dictionary<string, object> request)
        {
            var model = ModelFor(app, request);
            if (model == null) return Fail("no document is open in SolidWorks");
            string path = MiniJson.Str(request, "out", null);
            if (string.IsNullOrEmpty(path))
            {
                PruneTemp(Path.GetTempPath(), "cadlink-view-*.bmp", DateTime.UtcNow);
                path = Path.Combine(Path.GetTempPath(),
                    "cadlink-view-" + Guid.NewGuid().ToString("N") + ".bmp");
            }
            int width = MiniJson.Int(request, "width", 1280);
            int height = MiniJson.Int(request, "height", 800);
            string view = MiniJson.Str(request, "view", null);
            if (!string.IsNullOrEmpty(view))
            {
                try { model.ShowNamedView2(view, -1); } catch { }
            }
            if (MiniJson.Flag(request, "zoom_to_fit", true))
            {
                try { model.ViewZoomtofit2(); } catch { }
            }
            bool ok;
            try { ok = model.SaveBMP(path, width, height); }
            catch (Exception ex) { return Fail("SaveBMP failed: " + ex.Message); }
            if (!ok) return Fail("SaveBMP refused (is a graphics view open?)");
            var reply = new Dictionary<string, object> { { "ok", true }, { "image", path } };
            // The view, so a renderer can stand its camera where SolidWorks
            // looked from: orientation (rotation, model to view), scale and
            // translation, as IModelView reports them.
            try
            {
                var mv = model.ActiveView as IModelView;
                if (mv != null)
                {
                    reply["view_orientation"] = ((IMathTransform)mv.Orientation3).ArrayData;
                    reply["view_scale"] = mv.Scale2;
                    double[] box = (model as IPartDoc) != null ? ((IPartDoc)model).GetPartBox(true) as double[] : ((model as IAssemblyDoc) != null ? ((IAssemblyDoc)model).GetBox((int)swBoundingBoxOptions_e.swBoundingBoxIncludeRefPlanes) as double[] : null);
                    reply["model_box"] = box;
                }
            }
            catch (Exception ex) { reply["view_error"] = ex.Message; }
            return reply;
        }

        /// <summary>
        /// The mate graph as the readers see it: every component with its
        /// placement and status, every mate with its entities, limits and
        /// suppression. This is the raw material the classifier works from,
        /// returned without running the classifier, so a caller can check the
        /// reading before blaming the reasoning.
        /// </summary>
        private static Dictionary<string, object> Mates(
            ISldWorks app, Dictionary<string, object> request)
        {
            var model = ModelFor(app, request);
            var assembly = model as IAssemblyDoc;
            if (assembly == null) return Fail("the document is not an assembly");
            long mark = LogMark();
            var walked = AssemblyWalker.Walk(assembly, AddIn.Log);
            var graph = MateReader.Read(walked, AddIn.Log, model);

            var components = new List<object>();
            foreach (var c in graph.Components)
            {
                components.Add(new Dictionary<string, object>
                {
                    { "id", c.Id },
                    { "path", c.Path },
                    { "name", c.Name },
                    { "file", c.FileName },
                    { "fixed", c.IsFixed },
                    { "suppressed", c.Suppressed },
                    { "solving", c.Solving },
                    { "parent", c.ParentId },
                    { "constrained_status", c.ConstrainedStatus },
                    { "transform", Flatten(c.Transform) },
                });
            }
            var mates = new List<object>();
            foreach (var m in graph.Mates)
            {
                var entities = new List<object>();
                foreach (var e in m.Entities)
                {
                    entities.Add(new Dictionary<string, object>
                    {
                        { "component", e.ComponentId },
                        { "type", e.EntityTypeName },
                        { "point", ToList(e.Point) },
                        { "direction", ToList(e.Direction) },
                        { "radius", e.Radius },
                        { "half_angle", e.HalfAngle },
                    });
                }
                mates.Add(new Dictionary<string, object>
                {
                    { "name", m.FeatureName },
                    { "type", m.TypeName },
                    { "suppressed", m.Suppressed },
                    { "alignment", m.Alignment },
                    { "flipped", m.Flipped },
                    { "min", double.IsNaN(m.MinimumVariation) ? (object)null : m.MinimumVariation },
                    { "max", double.IsNaN(m.MaximumVariation) ? (object)null : m.MaximumVariation },
                    { "current", double.IsNaN(m.CurrentValue) ? (object)null : m.CurrentValue },
                    { "lock_rotation", m.LockRotation },
                    { "error", m.Error },
                    { "entities", entities },
                });
            }
            return new Dictionary<string, object>
            {
                { "ok", true },
                { "document", SafePath(model) },
                { "components", components },
                { "mates", mates },
                { "log", LogSince(mark) },
            };
        }

        /// <summary>
        /// The ribbon export without the ribbon: manifest always (assemblies),
        /// STEP and the direct-link mesh on request. Files land beside the
        /// model unless "dir" says otherwise. The reply carries the paths, the
        /// manifest's joint shape and every log line the export wrote.
        /// </summary>
        /// <summary>
        /// Counts the small features every part of the open document could
        /// be sent without, and the triangles that would save. Reads only:
        /// it opens nothing, changes nothing and writes nothing. The first
        /// step of the defeature work is this measurement, so the feature can
        /// be judged before any of it reaches the ribbon.
        /// </summary>
        private static Dictionary<string, object> SmallFeatures(
            ISldWorks app, Dictionary<string, object> request)
        {
            var model = ModelFor(app, request);
            if (model == null) return Fail("no document is open in SolidWorks");
            double maxExtent = MiniJson.Num(request, "max_extent_m", 0.012);
            bool curved = MiniJson.Flag(request, "curved", false);
            var settings = AppSettings.Load(AddIn.Log);
            var fineness = FinenessFrom(request, settings);

            var parts = new List<object>();
            foreach (var kv in PartsOf(model))
            {
                var rows = new List<object>();
                int bodyIndex = 0;
                foreach (var body in SolidBodiesOf(kv.Value))
                {
                    bodyIndex++;
                    double tolerance = fineness.ChordFor(body);
                    var tess = TessellationOf(body, tolerance, needParams: true);
                    var survey = SmallFeatureSurvey.Survey(
                        body, maxExtent, tess, AddIn.Log, tolerance, curved);
                    // The body is closed before anything is taken out of it,
                    // so it has to be closed afterwards too. Measured both
                    // ways, because a baseline that is not zero would mean
                    // the measurement and not the plan is wrong.
                    var was = ClosureCheck.Run(body, tess, null, AddIn.Log);
                    var plan = SmallFeatureSurvey.Choose(
                        body, maxExtent, AddIn.Log, curved);
                    var now = ClosureCheck.Run(body, tess, plan, AddIn.Log);
                    var declined = new Dictionary<string, object>();
                    var sizes = new List<object>();
                    foreach (var f in survey.Features)
                    {
                        if (f.Declined == null) { sizes.Add(f.Extent); continue; }
                        object had;
                        declined[f.Declined] =
                            (declined.TryGetValue(f.Declined, out had) ? (int)had : 0) + 1;
                    }
                    rows.Add(new Dictionary<string, object>
                    {
                        { "body", bodyIndex },
                        { "faces", survey.Faces },
                        { "planar_faces", survey.PlanarFaces },
                        { "facets", survey.Facets },
                        { "facets_after", survey.FacetsAfter },
                        { "removed", survey.Removed },
                        { "declined", survey.Declined },
                        { "declined_why", declined },
                        { "filled_faces", survey.FilledFaces },
                        { "fill_before", survey.FilledFacetsBefore },
                        { "fill_after", survey.FilledFacetsAfter },
                        { "fill_refused", survey.FillRefused },
                        { "capped_faces", survey.CappedFaces },
                        { "open_before", was.Open + was.Doubled },
                        { "open_after", now.Open },
                        { "doubled_after", now.Doubled },
                        { "open_where", now.Where },
                        { "cap_facets", survey.CapFacets },
                        { "worst_area_slip", survey.WorstAreaSlip },
                        { "worst_area_where", survey.WorstAreaWhere },
                        { "removed_sizes_m", sizes },
                    });
                }
                if (rows.Count == 0) continue;
                parts.Add(new Dictionary<string, object>
                {
                    { "part", kv.Key },
                    { "bodies", rows },
                });
            }
            return new Dictionary<string, object>
            {
                { "ok", true },
                { "max_extent_m", maxExtent },
                { "parts", parts },
            };
        }

        /// <summary>
        /// Asks whether a planar face's texture coordinates can be rebuilt
        /// from its surface. Reads only. A fill of our own has to give its
        /// new points coordinates SolidWorks would agree with, or a textured
        /// part shifts where it was defeatured.
        /// </summary>
        private static Dictionary<string, object> PlaneUv(
            ISldWorks app, Dictionary<string, object> request)
        {
            var model = ModelFor(app, request);
            if (model == null) return Fail("no document is open in SolidWorks");
            var settings = AppSettings.Load(AddIn.Log);
            var fineness = FinenessFrom(request, settings);

            var parts = new List<object>();
            foreach (var kv in PartsOf(model))
            {
                var rows = new List<object>();
                int bodyIndex = 0;
                foreach (var body in SolidBodiesOf(kv.Value))
                {
                    bodyIndex++;
                    var tess = TessellationOf(
                        body, fineness.ChordFor(body),
                        needParams: true);
                    var check = PlaneUvCheck.Check(body, tess, AddIn.Log);
                    if (check.Vertices == 0) continue;
                    rows.Add(new Dictionary<string, object>
                    {
                        { "body", bodyIndex },
                        { "planar_faces", check.PlanarFaces },
                        { "vertices", check.Vertices },
                        { "agree", check.Agree },
                        { "sole", check.Sole },
                        { "sole_agree", check.SoleAgree },
                        { "worst_sole_m", check.Worst },
                        { "worst_any_m", check.WorstAny },
                        { "no_frame", check.NoFrame },
                        { "body_vertices", check.BodyVertices },
                        { "body_shared", check.BodyShared },
                    });
                }
                if (rows.Count == 0) continue;
                parts.Add(new Dictionary<string, object>
                {
                    { "part", kv.Key },
                    { "bodies", rows },
                });
            }
            return new Dictionary<string, object>
            {
                { "ok", true },
                { "tolerance_m", PlaneUvCheck.Tolerance },
                { "parts", parts },
            };
        }

        /// <summary>Every distinct PART document the model reaches, by
        /// file name: one reading per part, however many times it is
        /// placed.</summary>
        private static List<KeyValuePair<string, IModelDoc2>> PartsOf(IModelDoc2 model)
        {
            var found = new List<KeyValuePair<string, IModelDoc2>>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var assembly = model as IAssemblyDoc;
            if (assembly == null)
            {
                if (model is IPartDoc)
                    found.Add(new KeyValuePair<string, IModelDoc2>(SafeTitle(model), model));
                return found;
            }
            object[] components = null;
            try
            {
                components = assembly.GetComponents(false) as object[];
            }
            catch { }
            foreach (var o in components ?? new object[0])
            {
                var component = o as IComponent2;
                if (component == null) continue;
                bool suppressed = false;
                try { suppressed = component.IsSuppressed(); }
                catch { }
                if (suppressed) continue;
                IModelDoc2 doc = null;
                try { doc = component.GetModelDoc2() as IModelDoc2; }
                catch { }
                if (!(doc is IPartDoc)) continue;
                string path = SafePath(doc) ?? SafeTitle(doc);
                if (path == null || !seen.Add(path)) continue;
                found.Add(new KeyValuePair<string, IModelDoc2>(
                    Path.GetFileNameWithoutExtension(path), doc));
            }
            return found;
        }

        private static IEnumerable<IBody2> SolidBodiesOf(IModelDoc2 doc)
        {
            var part = doc as IPartDoc;
            object[] bodies = null;
            if (part != null)
            {
                try
                {
                    bodies = part.GetBodies2(
                        (int)swBodyType_e.swSolidBody, false) as object[];
                }
                catch { }
            }
            foreach (var o in bodies ?? new object[0])
            {
                var body = o as IBody2;
                if (body != null) yield return body;
            }
        }

        private static ITessellation TessellationOf(
            IBody2 body, double tolerance, bool needParams = false)
        {
            try
            {
                var tess = body.GetTessellation(null) as ITessellation;
                if (tess == null) return null;
                tess.NeedFaceFacetMap = true;
                // A curved face settles the winding of its triangles from
                // the normals, so a survey without them cannot find its rims.
                tess.NeedVertexNormal = true;
                tess.NeedVertexParams = needParams;
                tess.ImprovedQuality = true;
                // The export's own setting: facets either side of an edge
                // share vertices, which is what makes the result a mesh.
                // Anything measured here has to be measured on the mesh the
                // user actually gets.
                tess.MatchType =
                    (int)swTesselationMatchType_e.swTesselationMatchFacetTopology;
                tess.SurfacePlaneTolerance = tolerance;
                tess.SurfacePlaneAngleTolerance = 0.35;
                tess.CurveChordTolerance = tolerance;
                tess.CurveChordAngleTolerance = 0.35;
                return tess.Tessellate() ? tess : null;
            }
            catch { return null; }
        }

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
            var paths = ExportFiles(app, model, settings, request, withStep, withMesh);
            var reply = new Dictionary<string, object> { { "ok", true } };
            foreach (var kv in paths) reply[kv.Key] = kv.Value;
            reply["log"] = LogSince(mark);
            return reply;
        }

        /// <summary>
        /// The one-click send, without its dialogs: export as the ribbon
        /// would, hand the files to a running Blender (or launch one when the
        /// settings allow), and return Blender's own reply.
        /// </summary>
        private static Dictionary<string, object> Send(
            ISldWorks app, Dictionary<string, object> request)
        {
            string error;
            var model = ModelFor(app, request, out error);
            if (model == null) return Fail(error);
            if (string.IsNullOrEmpty(SafePath(model)))
                return Fail("the document has never been saved");
            var settings = AppSettings.Load(AddIn.Log);
            bool native = MiniJson.Flag(request, "native", true);
            long mark = LogMark();
            var paths = ExportFiles(app, model, settings, request, !native, native, send: true);

            // "update" and "rig_mode" make this the payload Refresh Model
            // sends, so a lab session can refresh a scene the way the ribbon
            // does, into the Blender the ribbon would choose.
            bool update = MiniJson.Flag(request, "update", false);
            var instances = BlenderBridge.Discover(AddIn.Log);
            if (update) instances = BlenderBridge.ForRefresh(instances, SafePath(model));
            BlenderInstance target = instances.Count > 0 ? instances[0] : null;
            if (target == null)
            {
                if (!settings.AutoLaunchBlender)
                    return Fail("no running Blender with the bridge, and auto-launch is off");
                string exe = BlenderBridge.ResolveExe(settings);
                if (exe == null) return Fail("no Blender installation was found to launch");
                target = BlenderBridge.Launch(exe, AddIn.Log);
            }
            string stepPath = paths.ContainsKey("step") ? (string)paths["step"] : null;
            string meshPath = paths.ContainsKey("mesh") ? (string)paths["mesh"] : null;
            string manifestPath = paths.ContainsKey("manifest") ? (string)paths["manifest"] : null;
            string rigMode = request.ContainsKey("rig_mode")
                ? request["rig_mode"] as string : null;
            var payload = SendToBlenderCommand.BuildPayload(
                settings, native ? null : stepPath, native ? meshPath : null,
                manifestPath, update: update, rigMode: rigMode,
                view: settings.MatchView ? ViewReader.Read(model) : null,
                sourceDocument: SafePath(model));
            int timeoutMs = (int)(MiniJson.Num(request, "timeout_s", 600) * 1000);
            var resp = BlenderBridge.PostImport(target, payload, timeoutMs, AddIn.Log);
            var reply = new Dictionary<string, object>
            {
                { "ok", MiniJson.Flag(resp, "ok") },
                { "blender", resp },
                { "blender_pid", target.Pid },
            };
            foreach (var kv in paths) reply[kv.Key] = kv.Value;
            reply["log"] = LogSince(mark);
            return reply;
        }

        // ── Lab operations (change the open model, never save) ────────────

        private static Dictionary<string, object> Open(
            ISldWorks app, Dictionary<string, object> request)
        {
            string path = MiniJson.Str(request, "path", null);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return Fail("no such file: " + path);
            int type;
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".sldasm": type = (int)swDocumentTypes_e.swDocASSEMBLY; break;
                case ".sldprt": type = (int)swDocumentTypes_e.swDocPART; break;
                case ".slddrw": type = (int)swDocumentTypes_e.swDocDRAWING; break;
                default: return Fail("not a SolidWorks document: " + path);
            }
            int err = 0, warn = 0;
            long mark = LogMark();
            var model = app.OpenDoc6(path, type,
                (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "",
                ref err, ref warn) as IModelDoc2;
            if (model == null)
                return Fail("open failed (error " + err + ", warning " + warn + ")");
            int activateErr = 0;
            try
            {
                app.ActivateDoc3(model.GetTitle(), false,
                    (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref activateErr);
            }
            catch { }
            return new Dictionary<string, object>
            {
                { "ok", true },
                { "title", SafeTitle(model) },
                { "document", SafePath(model) },
                { "type", DocType(model) },
                { "open_error", err },
                { "open_warning", warn },
                { "log", LogSince(mark) },
            };
        }

        private static Dictionary<string, object> Close(
            ISldWorks app, Dictionary<string, object> request)
        {
            if (MiniJson.Flag(request, "all", false))
            {
                // true: no save prompts, changes discarded.
                app.CloseAllDocuments(true);
                return new Dictionary<string, object> { { "ok", true }, { "closed", "all" } };
            }
            var model = ModelFor(app, request);
            if (model == null) return Fail("no document is open in SolidWorks");
            string title = SafeTitle(model);
            app.CloseDoc(title);
            return new Dictionary<string, object> { { "ok", true }, { "closed", title } };
        }

        private static Dictionary<string, object> Activate(
            ISldWorks app, Dictionary<string, object> request)
        {
            string title = MiniJson.Str(request, "title", null);
            if (string.IsNullOrEmpty(title)) return Fail("title is required");
            int err = 0;
            var model = app.ActivateDoc3(title, false,
                (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref err) as IModelDoc2;
            if (model == null) return Fail("no open document titled " + title + " (error " + err + ")");
            return new Dictionary<string, object>
            {
                { "ok", true }, { "title", SafeTitle(model) }, { "document", SafePath(model) },
            };
        }

        private static Dictionary<string, object> Rebuild(
            ISldWorks app, Dictionary<string, object> request)
        {
            var model = ModelFor(app, request);
            if (model == null) return Fail("no document is open in SolidWorks");
            bool ok;
            try { ok = model.ForceRebuild3(false); }
            catch (Exception ex) { return Fail("rebuild failed: " + ex.Message); }
            return new Dictionary<string, object> { { "ok", ok }, { "rebuilt", SafeTitle(model) } };
        }

        /// <summary>
        /// What SolidWorks calls each component (fixed, fully defined or
        /// under-defined) with every mate in place, with the limit mates (and
        /// coupling mates, unless "couplings" is false) taken out, and again
        /// once they are back, plus how far each component moved over the
        /// whole round trip. "rebuild" picks the call that makes the solver
        /// take the change in: edit, mates or force. The lab uses it to find
        /// out what the status means before the exporter trusts it.
        /// </summary>
        private static Dictionary<string, object> StatusProbe(
            ISldWorks app, Dictionary<string, object> request)
        {
            var model = ModelFor(app, request);
            var assembly = model as IAssemblyDoc;
            if (assembly == null) return Fail("the document is not an assembly");
            bool couplings = MiniJson.Flag(request, "couplings", true);
            string how = MiniJson.Str(request, "rebuild", "edit");

            var walked = AssemblyWalker.Walk(assembly, AddIn.Log);
            var before = new Dictionary<WalkedComponent, int>();
            var free = new Dictionary<WalkedComponent, int>();
            var poses = new Dictionary<WalkedComponent, double[]>();
            foreach (var w in walked)
            {
                before[w] = StatusOf(w);
                poses[w] = PoseOf(w);
            }

            var state = SolveState.Suppress(model, walked, couplings, AddIn.Log);
            bool rebuilt = SolveState.Rebuild(model, how);
            foreach (var w in walked) free[w] = StatusOf(w);
            var held = state.Describe();
            var failed = state.Restore();
            bool rebuiltBack = SolveState.Rebuild(model, how);

            var rows = new List<object>();
            double worst = 0;
            foreach (var w in walked)
            {
                double drift = Drift(poses[w], PoseOf(w));
                if (drift > worst) worst = drift;
                rows.Add(new Dictionary<string, object>
                {
                    { "path", w.Graph.Path },
                    { "parent", w.Parent == null ? null : w.Parent.Graph.Path },
                    { "fixed", w.Graph.IsFixed },
                    { "solving", w.Graph.Solving },
                    { "suppressed", w.Graph.Suppressed },
                    { "on", before[w] },
                    { "free", free[w] },
                    { "after", StatusOf(w) },
                    { "drift", drift },
                });
            }
            return new Dictionary<string, object>
            {
                { "ok", true },
                { "rebuild", how },
                { "rebuilt", rebuilt },
                { "rebuilt_back", rebuiltBack },
                { "taken_out", held },
                { "not_restored", failed },
                { "worst_drift", worst },
                { "components", rows },
            };
        }

        private static int StatusOf(WalkedComponent w)
        {
            try { return w.Comp.GetConstrainedStatus(); } catch { return 0; }
        }

        private static double[] PoseOf(WalkedComponent w)
        {
            try { return w.Comp.Transform2.ArrayData as double[]; } catch { return null; }
        }

        private static double Drift(double[] a, double[] b)
        {
            if (a == null || b == null) return 0;
            double worst = 0;
            for (int i = 0; i < Math.Min(12, Math.Min(a.Length, b.Length)); i++)
                worst = Math.Max(worst, Math.Abs(a[i] - b[i]));
            return worst;
        }

        /// <summary>Suppresses or unsuppresses one mate by feature name, the
        /// way the tree's right-click does.</summary>
        private static Dictionary<string, object> Suppress(
            ISldWorks app, Dictionary<string, object> request, bool suppress)
        {
            var model = ModelFor(app, request);
            if (model == null) return Fail("no document is open in SolidWorks");
            string name = MiniJson.Str(request, "mate", null);
            if (string.IsNullOrEmpty(name)) return Fail("mate is required (the feature name)");
            var feature = FindFeature(model, name);
            if (feature == null) return Fail("no feature named " + name);
            bool ok;
            try
            {
                ok = feature.SetSuppression2(
                    suppress ? (int)swFeatureSuppressionAction_e.swSuppressFeature
                             : (int)swFeatureSuppressionAction_e.swUnSuppressFeature,
                    (int)swInConfigurationOpts_e.swThisConfiguration, null);
            }
            catch (Exception ex) { return Fail("SetSuppression2 failed: " + ex.Message); }
            if (!ok) return Fail("SolidWorks refused to change the suppression of " + name);
            try { model.EditRebuild3(); } catch { }
            return new Dictionary<string, object>
            {
                { "ok", true }, { "mate", name }, { "suppressed", suppress },
            };
        }

        /// <summary>Applies a library appearance (.p2m) to the active part
        /// document, its first body, or one face of it ("document", "body",
        /// "face:N"), in memory. Nothing is saved: the lab uses it to put a
        /// known texture on a model and compare SolidWorks with Blender.
        /// Optional width and height set the texture tile size, metres.</summary>
        private static Dictionary<string, object> ApplyAppearance(
            ISldWorks app, Dictionary<string, object> request)
        {
            var model = ModelFor(app, request);
            var part = model as IPartDoc;
            if (part == null) return Fail("the active document is not a part");
            string path = MiniJson.Str(request, "path", null);
            string target = MiniJson.Str(request, "target", "document");
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return Fail("no appearance file at " + path);
            IRenderMaterial rm;
            try { rm = model.Extension.CreateRenderMaterial(path) as IRenderMaterial; }
            catch (Exception ex) { return Fail("CreateRenderMaterial failed: " + ex.Message); }
            if (rm == null) return Fail("CreateRenderMaterial returned nothing");
            double width = MiniJson.Num(request, "width", 0.0);
            double height = MiniJson.Num(request, "height", 0.0);
            if (width > 0) rm.Width = width;
            if (height > 0) rm.Height = height;
            int mapping = (int)MiniJson.Num(request, "mapping_type", -1);
            if (mapping >= 0) rm.MappingType = mapping;
            double rotation = MiniJson.Num(request, "rotation", double.NaN);
            if (!double.IsNaN(rotation)) rm.RotationAngle = rotation;
            var u = MiniJson.NumArray(request, "u");
            if (u != null && u.Length == 3) rm.SetUDirection2(u[0], u[1], u[2]);
            var v = MiniJson.NumArray(request, "v");
            if (v != null && v.Length == 3) rm.SetVDirection2(v[0], v[1], v[2]);

            object entity = model;
            if (target == "body" || target.StartsWith("face:"))
            {
                var bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, true) as object[];
                var body = bodies == null || bodies.Length == 0 ? null : bodies[0] as IBody2;
                if (body == null) return Fail("the part has no solid body");
                entity = body;
                if (target.StartsWith("face:"))
                {
                    int index;
                    if (!int.TryParse(target.Substring(5), out index)) return Fail("face index is not a number");
                    var faces = body.GetFaces() as object[];
                    if (faces == null || index < 0 || index >= faces.Length) return Fail("no face " + index);
                    entity = faces[index];
                }
            }
            bool added;
            int id = 0;
            try
            {
                rm.AddEntity(entity);
                added = model.Extension.AddRenderMaterial((RenderMaterial)rm, out id);
            }
            catch (Exception ex) { return Fail("AddRenderMaterial failed: " + ex.Message); }
            try { model.GraphicsRedraw2(); } catch { }
            return new Dictionary<string, object>
            {
                { "ok", added }, { "material_id", id }, { "target", target },
                { "width", rm.Width }, { "height", rm.Height }, { "file", rm.FileName },
                { "mapping_type", rm.MappingType }, { "rotation", rm.RotationAngle },
            };
        }

        /// <summary>Sets a dimension by its full name ("D1@Distance1") in
        /// metres or radians, then rebuilds. Reads back what SolidWorks kept.</summary>
        private static Dictionary<string, object> Dimension(
            ISldWorks app, Dictionary<string, object> request)
        {
            var model = ModelFor(app, request);
            if (model == null) return Fail("no document is open in SolidWorks");
            string name = MiniJson.Str(request, "name", null);
            if (string.IsNullOrEmpty(name)) return Fail("name is required (D1@Distance1)");
            var dim = model.Parameter(name) as IDimension;
            if (dim == null) return Fail("no dimension named " + name);
            double before;
            try { before = dim.SystemValue; } catch { before = double.NaN; }
            if (request.ContainsKey("value"))
            {
                double value = MiniJson.Num(request, "value", 0.0);
                int result;
                try
                {
                    result = dim.SetSystemValue3(value,
                        (int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration, null);
                }
                catch (Exception ex) { return Fail("SetSystemValue3 failed: " + ex.Message); }
                if (result != (int)swSetValueReturnStatus_e.swSetValue_Successful)
                    return Fail("SolidWorks refused the value (status " + result + ")");
                try { model.EditRebuild3(); } catch { }
            }
            double after;
            try { after = dim.SystemValue; } catch { after = double.NaN; }
            return new Dictionary<string, object>
            {
                { "ok", true }, { "name", name }, { "before", before }, { "after", after },
            };
        }

        /// <summary>Closes every document without saving and exits. The
        /// reply goes out first; the exit runs a moment later on the
        /// SolidWorks thread.</summary>
        private static Dictionary<string, object> Quit(ISldWorks app)
        {
            SwCommandServer.RunLater(500, () =>
            {
                try { app.CloseAllDocuments(true); } catch { }
                try { app.ExitApp(); } catch { }
            });
            return new Dictionary<string, object> { { "ok", true }, { "quitting", true } };
        }

        // ── Shared ────────────────────────────────────────────────────────

        /// <summary>
        /// Whether an export the listener runs keeps only the selected
        /// components. A lab request can ask for the option without the
        /// settings. Otherwise only the lab's send reads the settings, the
        /// way the ribbon's does, and an update never does
        /// (SendToBlenderCommand.GeometryFollowsSelection). Blender asks
        /// for an export to bring the whole assembly over again or up to
        /// date, so a selection left in SolidWorks does not cut it.
        /// </summary>
        internal static bool OnlySelectedFor(
            Dictionary<string, object> request, AppSettings settings, bool send)
        {
            if (request != null && request.ContainsKey("only_selected"))
                return MiniJson.Flag(request, "only_selected", false);
            if (!send) return false;
            return SendToBlenderCommand.GeometryFollowsSelection(
                settings, MiniJson.Flag(request, "update", false));
        }

        /// <summary>Manifest (assemblies), STEP and mesh as asked. Keys of the
        /// result: manifest, step, mesh, warnings, joints. <paramref
        /// name="send"/> is true for the lab's send, which follows the
        /// ribbon's.</summary>
        private static Dictionary<string, object> ExportFiles(
            ISldWorks app, IModelDoc2 model, AppSettings settings,
            Dictionary<string, object> request, bool withStep, bool withMesh,
            bool send = false)
        {
            var assembly = model as IAssemblyDoc;
            string baseName = Path.GetFileNameWithoutExtension(model.GetPathName());
            string dir = MiniJson.Str(request, "dir", null);
            if (string.IsNullOrEmpty(dir))
                dir = SendToBlenderCommand.ExportDir(settings, model, baseName);
            Directory.CreateDirectory(dir);
            string stepPath = Path.Combine(dir, baseName + ".step");
            string meshPath = Path.Combine(dir, baseName + ".swmesh");
            string manifestPath = Path.Combine(dir, baseName + ".rig.json");
            var result = new Dictionary<string, object>();

            bool onlySelected = OnlySelectedFor(request, settings, send);
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

        private static IFeature FindFeature(IModelDoc2 model, string name)
        {
            var feature = model.FirstFeature() as IFeature;
            while (feature != null)
            {
                if (string.Equals(feature.Name, name, StringComparison.Ordinal)) return feature;
                var sub = feature.GetFirstSubFeature() as IFeature;
                while (sub != null)
                {
                    if (string.Equals(sub.Name, name, StringComparison.Ordinal)) return sub;
                    sub = sub.GetNextSubFeature() as IFeature;
                }
                feature = feature.GetNextFeature() as IFeature;
            }
            return null;
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

        private static List<object> ToList(double[] v)
        {
            if (v == null) return null;
            var list = new List<object>();
            foreach (var x in v) list.Add(x);
            return list;
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
        /// Re-reads named components at a new tolerance and writes them as a
        /// .swmesh. The reply is a FILE PATH, not the geometry: a megabyte of
        /// triangles through an HTTP body would be JSON-escaped and parsed
        /// twice for no reason, when both ends are on the same disk.
        /// </summary>
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

        /// <summary>The tessellation of one face with the texture
        /// coordinates SolidWorks gives it, so a caller can see what the
        /// appearance mapping does to a real surface rather than reading it
        /// off a picture. Points come back in the part's own space.
        /// </summary>
        private static Dictionary<string, object> TessUv(
            ISldWorks app, Dictionary<string, object> request)
        {
            var model = ModelFor(app, request);
            if (model == null) return Fail("no document is open in SolidWorks");
            var part = model as IPartDoc;
            if (part == null) return Fail("the document is not a part");
            int want = (int)MiniJson.Num(request, "face", 0);
            int limit = (int)MiniJson.Num(request, "limit", 40);

            object[] bodies = null;
            try
            {
                bodies = part.GetBodies2(
                    (int)swBodyType_e.swSolidBody, true) as object[];
            }
            catch (Exception ex) { return Fail("GetBodies2 failed: " + ex.Message); }
            if (bodies == null || bodies.Length == 0) return Fail("the part has no solid body");

            var faces = new List<IFace2>();
            foreach (var b in bodies)
            {
                var body = b as IBody2;
                if (body == null) continue;
                var list = body.GetFaces() as object[];
                if (list == null) continue;
                foreach (var f in list)
                {
                    var face = f as IFace2;
                    if (face != null) faces.Add(face);
                }
            }
            if (want < 0 || want >= faces.Count)
                return Fail("face " + want + " of " + faces.Count);

            var chosen = faces[want];
            float[] tris = null;
            float[] uvs = null;
            try { tris = chosen.GetTessTriangles(true) as float[]; } catch { }
            try { uvs = chosen.GetTessTextures() as float[]; } catch { }

            var points = new List<object>();
            if (tris != null)
            {
                int vertices = tris.Length / 9 * 3;
                int step = Math.Max(1, vertices / Math.Max(1, limit));
                for (int i = 0; i < vertices; i += step)
                {
                    var one = new Dictionary<string, object>
                    {
                        { "x", tris[i * 3 + 0] },
                        { "y", tris[i * 3 + 1] },
                        { "z", tris[i * 3 + 2] },
                    };
                    if (uvs != null && i * 2 + 1 < uvs.Length)
                    {
                        one["u"] = uvs[i * 2 + 0];
                        one["v"] = uvs[i * 2 + 1];
                    }
                    points.Add(one);
                }
            }
            return new Dictionary<string, object>
            {
                { "ok", true },
                { "faces", faces.Count },
                { "face", want },
                { "surface", SurfaceName(chosen) },
                { "triangles", tris == null ? 0 : tris.Length / 9 },
                { "has_textures", uvs != null },
                { "texture_values", uvs == null ? 0 : uvs.Length },
                { "points", points },
            };
        }

        /// <summary>What kind of surface a face sits on, in words.</summary>
        private static string SurfaceName(IFace2 face)
        {
            try
            {
                var surface = face.GetSurface() as ISurface;
                if (surface == null) return null;
                if (surface.IsPlane()) return "plane";
                if (surface.IsCylinder()) return "cylinder";
                if (surface.IsCone()) return "cone";
                if (surface.IsSphere()) return "sphere";
                if (surface.IsTorus()) return "torus";
                return "other";
            }
            catch { return null; }
        }

        /// <summary>Drags one component, as a user would with the mouse,
        /// so a harness session can check what a moved assembly does. The
        /// move stays: nothing here saves the document, and the next
        /// rebuild or close puts the assembly back the way the mates want
        /// it.</summary>
        private static Dictionary<string, object> Move(
            ISldWorks app, Dictionary<string, object> request)
        {
            var model = ModelFor(app, request);
            var assembly = model as IAssemblyDoc;
            if (assembly == null) return Fail("the document is not an assembly");
            string name = MiniJson.Str(request, "component", null);
            if (string.IsNullOrEmpty(name)) return Fail("no component named");
            var comp = FindComponent(assembly, name);
            if (comp == null) return Fail("no component named " + name);

            var axis = new double[]
            {
                MiniJson.Num(request, "x", 0.0),
                MiniJson.Num(request, "y", 0.0),
                MiniJson.Num(request, "z", 0.0),
            };
            double angle = MiniJson.Num(request, "angle", 0.0);
            double length = Math.Sqrt(axis[0] * axis[0] + axis[1] * axis[1]
                                    + axis[2] * axis[2]);
            if (length < 1e-12) return Fail("the move has no direction");

            var mover = new Sw.ComponentMover(app, model);
            if (!mover.Ready) return Fail("the mover could not start");
            bool ok;
            if (Math.Abs(angle) > 1e-12)
            {
                var origin = new double[]
                {
                    MiniJson.Num(request, "ox", 0.0),
                    MiniJson.Num(request, "oy", 0.0),
                    MiniJson.Num(request, "oz", 0.0),
                };
                for (int i = 0; i < 3; i++) axis[i] /= length;
                ok = mover.DragBy(comp,
                    Sw.ComponentMover.RotationAboutAxis(axis, origin, angle));
            }
            else
            {
                var unit = new double[] { axis[0] / length, axis[1] / length, axis[2] / length };
                ok = mover.DragBy(comp,
                    Sw.ComponentMover.TranslationAlong(unit, length));
            }
            return new Dictionary<string, object>
            {
                { "ok", ok },
                { "component", name },
                { "moved", ok },
            };
        }

        /// <summary>Pushes the poses of the open assembly to a running
        /// Blender: the cheap half of an export, for dragging a mechanism
        /// and seeing the result. The harness has no one to ask, so it
        /// takes the only Blender it finds.</summary>
        private static Dictionary<string, object> RefreshPoses(
            ISldWorks app, Dictionary<string, object> request)
        {
            var model = ModelFor(app, request);
            var assembly = model as IAssemblyDoc;
            if (assembly == null) return Fail("the document is not an assembly");
            var instances = BlenderBridge.Discover(AddIn.Log);
            if (instances.Count == 0) return Fail("no running Blender with the bridge");
            if (instances.Count > 1)
                return Fail(instances.Count + " Blender instances are running: "
                            + "the harness cannot choose");
            long mark = LogMark();
            var payload = PosePush.Payload(model, assembly);
            var reply = BlenderBridge.PostImport(
                instances[0], payload, 5 * 60 * 1000, AddIn.Log);
            return new Dictionary<string, object>
            {
                { "ok", MiniJson.Flag(reply, "ok") },
                { "blender", reply },
                { "summary", PosePush.Summary(reply, Sent(payload)) },
                { "log", LogSince(mark) },
            };
        }

        /// <summary>How many component poses a refresh payload carries.</summary>
        private static int Sent(Dictionary<string, object> payload)
        {
            var poses = MiniJson.Obj(payload, "poses");
            if (poses == null) return 0;
            var list = poses.ContainsKey("components")
                ? poses["components"] as List<object> : null;
            return list == null ? 0 : list.Count;
        }

        /// <summary>Runs the export's progress bar through its stages,
        /// without an export, and says what SolidWorks answered. The bar
        /// draws in the status bar, which the graphics-view screenshot
        /// cannot show, so this is how the bar is checked live.</summary>
        private static Dictionary<string, object> ProgressDemo(
            ISldWorks app, Dictionary<string, object> request)
        {
            int steps = (int)MiniJson.Num(request, "steps", 20);
            if (steps < 1) steps = 1;
            int hold = (int)MiniJson.Num(request, "hold_ms", 40);
            var bar = Sw.SwProgressBar.Open(app, "Progress bar check", AddIn.Log);
            var real = bar as Sw.SwProgressBar;
            if (real == null) return Fail("SolidWorks gave no progress bar");
            try
            {
                real.Stage("Reading the assembly", 0, 8);
                real.Stage("Measuring the freedom of " + steps + " pair(s)", 24, 58, steps);
                for (int i = 1; i <= steps; i++)
                {
                    real.Step(i);
                    if (hold > 0) System.Threading.Thread.Sleep(hold);
                }
                real.Stage("Writing the manifest", 95, 100);
                real.Step(1);
            }
            finally { real.Dispose(); }
            return new Dictionary<string, object>
            {
                { "ok", true },
                { "updates", real.Updates },
                { "last_answer", real.LastAnswer },
                { "cancelled", real.Cancelled },
            };
        }

        /// <summary>Selects components by their instance paths ("rod-1",
        /// "lifterassy-1/rod-1"), or clears the selection when no name is
        /// given, so a lab session can drive the "only the selected
        /// components" option. SelectByID2 wants the tree syntax
        /// ("rod-1@lifterassy-1@cam-follower"), so the component itself is
        /// found in the walk and selected through Select4. Selection changes
        /// nothing in the document.</summary>
        private static Dictionary<string, object> Select(
            ISldWorks app, Dictionary<string, object> request)
        {
            var model = ModelFor(app, request);
            if (model == null) return Fail("no document is open in SolidWorks");
            var assembly = model as IAssemblyDoc;
            if (assembly == null) return Fail("the document is not an assembly");

            var wanted = new List<string>();
            string one = MiniJson.Str(request, "component", null);
            if (!string.IsNullOrEmpty(one)) wanted.Add(one);
            var many = MiniJson.Arr(request, "components");
            if (many != null)
                foreach (var item in many)
                    if (item is string && ((string)item).Length > 0) wanted.Add((string)item);

            if (wanted.Count == 0)
            {
                try { model.ClearSelection2(true); } catch { }
                return new Dictionary<string, object> { { "ok", true }, { "selected", 0 } };
            }

            bool append = MiniJson.Flag(request, "append", false);
            if (!append) { try { model.ClearSelection2(true); } catch { } }

            var found = new List<string>();
            var missing = new List<object>();
            foreach (string name in wanted)
            {
                var comp = FindComponent(assembly, name);
                if (comp == null) { missing.Add(name); continue; }
                bool ok = false;
                try { ok = comp.Select4(true, null, false); } catch { }
                if (ok) found.Add(name); else missing.Add(name);
            }
            int count = 0;
            try { count = model.SelectionManager.GetSelectedObjectCount2(-1); } catch { }
            var reply = new Dictionary<string, object>
            {
                { "ok", missing.Count == 0 },
                { "components", found.ToArray() },
                { "selected", count },
            };
            if (missing.Count > 0)
            {
                reply["missing"] = missing.ToArray();
                reply["error"] = "no component named " + missing[0];
            }
            return reply;
        }

        /// <summary>The component whose Name2 is that instance path, at any
        /// depth. Name2 is the full path with instance numbers, so it names
        /// one occurrence.</summary>
        private static Component2 FindComponent(IAssemblyDoc assembly, string path)
        {
            object[] comps = null;
            try { comps = assembly.GetComponents(false) as object[]; } catch { }
            if (comps == null) return null;
            foreach (var o in comps)
            {
                var comp = o as Component2;
                if (comp == null) continue;
                string name = null;
                try { name = comp.Name2; } catch { }
                if (string.Equals(name, path, StringComparison.OrdinalIgnoreCase))
                    return comp;
            }
            return null;
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

        private static Dictionary<string, object> Retessellate(
            ISldWorks app, Dictionary<string, object> request)
        {
            string error;
            var model = ModelFor(app, request, out error);
            if (model == null) return Fail(error);

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
                    appearance: appearance, defeature: defeature);
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

        /// <summary>
        /// What the add-in put on the ribbon, for each document type.
        ///
        /// A button that a user cannot find is either one the add-in never
        /// added or one SolidWorks dropped. This reads the tab back from
        /// SolidWorks, so the answer says which.
        /// </summary>
        private static Dictionary<string, object> Ribbon()
        {
            var manager = AddIn.LabCommandManager;
            if (manager == null) return Fail("the add-in built no command UI");

            var tabs = new List<object>();
            foreach (var docType in new[] { swDocumentTypes_e.swDocASSEMBLY,
                                            swDocumentTypes_e.swDocPART })
            {
                var buttons = new List<object>();
                var tab = manager.GetCommandTab((int)docType, AddIn.AddInTitle);
                if (tab != null)
                {
                    var boxes = tab.CommandTabBoxes() as object[];
                    foreach (var raw in boxes ?? new object[0])
                    {
                        var box = raw as ICommandTabBox;
                        if (box == null) continue;
                        object ids, styles;
                        box.GetCommands(out ids, out styles);
                        foreach (var id in (ids as int[]) ?? new int[0])
                        {
                            string title;
                            buttons.Add(AddIn.CommandTitles.TryGetValue(id, out title)
                                ? title : "command " + id);
                        }
                    }
                }
                tabs.Add(new Dictionary<string, object>
                {
                    { "document", docType == swDocumentTypes_e.swDocASSEMBLY
                                  ? "assembly" : "part" },
                    { "tab", tab != null },
                    { "buttons", buttons },
                });
            }

            return new Dictionary<string, object>
            {
                { "ok", true },
                { "advanced", AppSettings.Load(AddIn.Log).AdvancedCommands },
                { "commands", new List<object>(AddIn.CommandOrder) },
                { "tabs", tabs },
                // What the Refresh Model button shows for the active document,
                // from the callback SolidWorks itself calls (1 enabled, 0 grey).
                { "refresh_model_enabled", new CommandCallbacks().EnableRefreshModel() },
            };
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
