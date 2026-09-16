using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Peak.SwToBlender.Core;
using Peak.SwToBlender.Core.Model;
using Peak.SwToBlender.Sw;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Peak.SwToBlender.Bridge
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
    /// they run only while AppSettings.LabOps is on, and NOTHING here ever
    /// saves a document: a lab session leaves every file as it found it.
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
                case "log": return LogTail(request);
                case "screenshot": return Screenshot(app, request);
                case "apply_appearance": return Lab(request, () => ApplyAppearance(app, request));
                case "appearances":
                    return AppearanceProbe.Run(ModelFor(app, request),
                        (int)MiniJson.Num(request, "max_faces", 60),
                        (int)MiniJson.Num(request, "face", -1));
                case "mates": return Mates(app, request);
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
                default:
                    return Fail("unknown op " + (string.IsNullOrEmpty(op) ? "(none)" : op));
            }
        }

        /// <summary>The gate for operations that change the open model.</summary>
        private static Dictionary<string, object> Lab(
            Dictionary<string, object> request, Func<Dictionary<string, object>> run)
        {
            var settings = AppSettings.Load(AddIn.Log);
            if (!settings.LabOps)
                return Fail("lab operations are off (lab_ops in the add-in settings)");
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
                { "lab_ops", settings.LabOps },
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
                path = Path.Combine(Path.GetTempPath(),
                    "cadlink-view-" + Guid.NewGuid().ToString("N") + ".bmp");
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
            var graph = MateReader.Read(walked, AddIn.Log);

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
        private static Dictionary<string, object> Export(
            ISldWorks app, Dictionary<string, object> request)
        {
            var model = ModelFor(app, request);
            if (model == null) return Fail("no document is open in SolidWorks");
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
            var model = ModelFor(app, request);
            if (model == null) return Fail("no document is open in SolidWorks");
            if (string.IsNullOrEmpty(SafePath(model)))
                return Fail("the document has never been saved");
            var settings = AppSettings.Load(AddIn.Log);
            bool native = MiniJson.Flag(request, "native", true);
            long mark = LogMark();
            var paths = ExportFiles(app, model, settings, request, !native, native);

            var instances = BlenderBridge.Discover(AddIn.Log);
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
            var payload = SendToBlenderCommand.BuildPayload(
                settings, native ? null : stepPath, native ? meshPath : null, manifestPath);
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

        /// <summary>Manifest (assemblies), STEP and mesh as asked. Keys of the
        /// result: manifest, step, mesh, warnings, joints.</summary>
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
            string stepPath = Path.Combine(dir, baseName + ".step");
            string meshPath = Path.Combine(dir, baseName + ".swmesh");
            string manifestPath = Path.Combine(dir, baseName + ".rig.json");
            var result = new Dictionary<string, object>();

            if (assembly != null)
            {
                var outcome = ExportCommand.ExportBundle(
                    app, model, assembly, stepPath, manifestPath, settings,
                    manifestOnly: !withStep);
                result["manifest"] = outcome.ManifestPath;
                result["warnings"] = outcome.Warnings;
                result["joints"] = JointShape(outcome.ManifestPath);
                if (withStep) result["step"] = outcome.StepPath ?? stepPath;
            }
            else if (withStep)
            {
                StepPlusCommand.ExportAppearanceOnly(app, model, stepPath, settings);
                result["step"] = stepPath;
            }
            if (withMesh)
            {
                double quality = request.ContainsKey("quality")
                    ? MiniJson.Num(request, "quality", 0.45)
                    : SendToBlenderCommand.QualityDial(settings.QualityPreset);
                NativeExport.Write(app, model, meshPath, quality, AddIn.Log);
                result["mesh"] = meshPath;
            }
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

        /// <summary>The active document, or the open one whose title the
        /// request names.</summary>
        private static IModelDoc2 ModelFor(ISldWorks app, Dictionary<string, object> request)
        {
            if (app == null) return null;
            string title = MiniJson.Str(request, "title", null);
            if (string.IsNullOrEmpty(title)) return app.ActiveDoc as IModelDoc2;
            var doc = app.GetFirstDocument() as IModelDoc2;
            while (doc != null)
            {
                if (string.Equals(SafeTitle(doc), title, StringComparison.OrdinalIgnoreCase))
                    return doc;
                doc = doc.GetNext() as IModelDoc2;
            }
            return null;
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
            var model = app == null ? null : app.ActiveDoc as IModelDoc2;
            if (model == null) return Fail("no document is open in SolidWorks");
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
                poses.Add(new Dictionary<string, object>
                {
                    { "id", w.Id },
                    { "sw_path", w.Graph.Path },
                    { "sw_persistent_id", persistent[w.Id] },
                    { "transform", Flatten(w.Graph.Transform) },
                });
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

        private static Dictionary<string, object> Retessellate(
            ISldWorks app, Dictionary<string, object> request)
        {
            var model = app == null ? null : app.ActiveDoc as IModelDoc2;
            if (model == null) return Fail("no document is open in SolidWorks");

            double quality = MiniJson.Num(request, "quality", 0.75);
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
                scene = NativeSceneBuilder.Build(
                    walked, quality, AddIn.Log, selection.Everything ? null : selection.Ids);
                if (!selection.Everything && scene.Instances.Count == 0)
                    return Fail("none of those components are in the open assembly");
            }
            else
            {
                // A part document is one component; a filter naming anything
                // else simply does not apply to it.
                scene = NativeExport.Build(app, model, quality, AddIn.Log);
            }
            if (scene.Definitions.Count == 0) return Fail("nothing to tessellate");

            string path = MiniJson.Str(request, "out", null);
            if (string.IsNullOrEmpty(path))
                path = Path.Combine(Path.GetTempPath(),
                    "cadlink-refine-" + Guid.NewGuid().ToString("N") + ".swmesh");
            MeshWriter.Write(path, scene);

            int triangles = 0;
            foreach (var d in scene.Definitions) triangles += d.TriangleCount;
            AddIn.Log("sw bridge: retessellated " + scene.Instances.Count
                + " instance(s) at quality "
                + quality.ToString("G3", CultureInfo.InvariantCulture)
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
