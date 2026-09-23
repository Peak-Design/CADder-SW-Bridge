using System;
using System.Collections.Generic;
using System.Globalization;
using Peak.Cadder.Core;
using Peak.Cadder.Core.Model;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Peak.Cadder.Sw
{
    /// <summary>
    /// The direct link's export: walk the assembly, tessellate it here, write
    /// a .swmesh. No STEP file, and nothing for the consumer to match up
    /// afterwards: the component ids travel with the geometry.
    /// </summary>
    public static class NativeExport
    {
        /// <summary>
        /// Writes the whole document. Returns the scene it wrote, so a caller
        /// that wants to report triangle counts does not have to read the
        /// file back.
        /// </summary>
        public static MeshScene Write(
            ISldWorks app, IModelDoc2 model, string path, BodyTessellator.Fineness fineness,
            Action<string> log, bool separateSolids = false,
            HashSet<string> keepPaths = null, ExportProgress progress = null,
            AppearanceOptions appearance = null, DefeatureOptions defeature = null)
        {
            var scene = Build(app, model, fineness, log, separateSolids, keepPaths,
                progress, appearance, defeature);
            MeshWriter.Write(path, scene);
            return scene;
        }

        public static MeshScene Build(
            ISldWorks app, IModelDoc2 model, BodyTessellator.Fineness fineness, Action<string> log,
            bool separateSolids = false, HashSet<string> keepPaths = null,
            ExportProgress progress = null, AppearanceOptions appearance = null,
            DefeatureOptions defeature = null)
        {
            var assembly = model as IAssemblyDoc;
            if (assembly != null)
            {
                var walked = AssemblyWalker.Walk(assembly, log);
                defeature = ForThisWalk(model, walked, defeature, log);
                if (progress != null)
                    progress.Stage("Building the geometry of " + walked.Count
                        + " component(s)", 0, 100, walked.Count);
                return NativeSceneBuilder.Build(
                    walked, fineness, log, null, separateSolids, keepPaths, progress,
                    appearance, defeature);
            }

            // A PART has no components to walk, so it is its own single
            // instance at the origin: the same shape of scene, one entry
            // long, which keeps the consumer from needing a second case.
            return BuildSinglePart(
                model, fineness, log, separateSolids, appearance, defeature);
        }

        /// <summary>
        /// The defeature rows keyed by the ids of THIS walk. A row names the
        /// component by the id of the export the scene was built from, and
        /// an edit to the assembly moves those ids. Where the row also
        /// carries the persistent id, that finds the part it is about.
        /// </summary>
        internal static DefeatureOptions ForThisWalk(
            IModelDoc2 model, List<WalkedComponent> walked, DefeatureOptions defeature,
            Action<string> log)
        {
            if (defeature == null || !defeature.NamesPersistent) return defeature;
            var present = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var w in walked)
                if (w != null && w.Graph != null)
                    present[w.Id] = ComponentIdentity.PersistIdBase64(model, w.Comp);
            var resolved = defeature.ResolvedAgainst(present);
            if (resolved.Dropped > 0 && log != null)
                log("native export: " + resolved.Dropped + " defeature request(s) name a "
                    + "part the assembly no longer holds, so nothing was defeatured for them");
            return resolved;
        }

        /// <summary>The plan for what to leave out of one body, or null
        /// when this component travels as it is. Worked out from the body's
        /// topology alone.</summary>
        internal static SmallFeatureSurvey.Plan Defeature(
            IBody2 body, DefeatureSpec spec, Action<string> log)
        {
            if (spec == null || !spec.Any) return null;
            // A surface body has an open border by design, and the closure
            // check after the plan counts every border edge as a hole. It
            // would throw the plan away every time, after a second
            // tessellation, so a surface travels as it is.
            if (IsSheet(body)) return null;
            var plan = SmallFeatureSurvey.Choose(body, spec.Size, log, spec.Curved);
            return plan != null && plan.Any ? plan : null;
        }

        /// <summary>
        /// The bodies of one part, in the order they are numbered: every
        /// solid body, then every surface body SolidWorks draws. A part made
        /// only of surfaces sent no geometry at all while only solids were
        /// asked for (live CutterRig, 2026-09-21). Solids go first, so
        /// ".body001" of a part is the same solid as before surfaces
        /// travelled. A hidden surface stays behind: it is usually
        /// construction geometry. Wire bodies are never asked for, because
        /// they have no faces to draw.
        /// </summary>
        internal static List<T> BodiesToSend<T>(
            object[] solids, object[] sheets, Func<T, bool> shown) where T : class
        {
            var kept = new List<T>();
            foreach (var o in solids ?? new object[0])
            {
                var body = o as T;
                if (body != null) kept.Add(body);
            }
            foreach (var o in sheets ?? new object[0])
            {
                var body = o as T;
                if (body != null && shown(body)) kept.Add(body);
            }
            return kept;
        }

        internal static List<IBody2> BodiesToSend(object[] solids, object[] sheets)
        {
            return BodiesToSend<IBody2>(solids, sheets, Shown);
        }

        /// <summary>Drawn in SolidWorks. A surface body that cannot answer
        /// stays behind, which is where every surface was before.</summary>
        private static bool Shown(IBody2 body)
        {
            try { return body.Visible; } catch { return false; }
        }

        private static bool IsSheet(IBody2 body)
        {
            try { return body.GetType() == (int)swBodyType_e.swSheetBody; }
            catch { return false; }
        }

        /// <summary>The line for a body that neither the tessellator nor
        /// the display mesh could read. Append logs why. This line says
        /// which part and which body, in the order BodiesToSend gives.
        /// </summary>
        internal static string NoTriangles(string part, int body)
        {
            return "native export: body " + body.ToString(CultureInfo.InvariantCulture)
                + " of " + (part ?? "part") + " gave no triangles";
        }

        private static MeshScene BuildSinglePart(
            IModelDoc2 model, BodyTessellator.Fineness fineness, Action<string> log,
            bool separateSolids = false, AppearanceOptions options = null,
            DefeatureOptions defeature = null)
        {
            var scene = new MeshScene();
            var part = model as IPartDoc;
            if (part == null) return scene;
            // A part document is one component and carries no component id,
            // so the one spec the request sent is the one it gets.
            var spec = defeature == null ? null : defeature.For(null);

            string title = SafeTitle(model);
            var materials = new AppearanceTable(scene, log, options);
            var appearance = materials.ForPart(model);
            object[] solids = null, sheets = null;
            try { solids = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; }
            catch (Exception ex)
            {
                if (log != null) log("native export: GetBodies2 failed: " + ex.Message);
            }
            try { sheets = part.GetBodies2((int)swBodyType_e.swSheetBody, true) as object[]; }
            catch (Exception ex)
            {
                if (log != null) log("native export: GetBodies2 failed for surfaces: " + ex.Message);
            }
            var bodies = BodiesToSend(solids, sheets);
            if (bodies.Count == 0) return scene;

            double tolerance = 0.0;
            int count = 0;
            MeshDefinition shared = null;
            var defs = new List<MeshDefinition>();
            foreach (var body in bodies)
            {
                count++;
                MeshDefinition def;
                if (separateSolids)
                {
                    // The STEP importer's spelling for a body of a multibody
                    // part, which is what the assembly route writes too.
                    def = new MeshDefinition
                    {
                        Id = defs.Count,
                        Name = title + ".body" + count.ToString(
                            "000", CultureInfo.InvariantCulture),
                    };
                    defs.Add(def);
                }
                else
                {
                    if (shared == null)
                    {
                        shared = new MeshDefinition { Id = 0, Name = title };
                        defs.Add(shared);
                    }
                    def = shared;
                }
                tolerance = Math.Max(tolerance, fineness.ChordFor(body));
                if (!BodyTessellator.Append(body, def, fineness,
                        (face, b) => materials.Resolve(face, b, appearance, null), log,
                        Defeature(body, spec, log))
                    && log != null)
                    log(NoTriangles(title, count));
            }
            // A part with one body keeps the plain name whichever way.
            if (separateSolids && defs.Count == 1) defs[0].Name = title;
            defs.RemoveAll(d => d.TriangleCount == 0);
            if (defs.Count == 0) return scene;

            scene.Tolerance = tolerance;
            foreach (var def in defs)
            {
                scene.Definitions.Add(def);
                scene.Instances.Add(new MeshInstance
                {
                    DefinitionId = def.Id,
                    ComponentId = "c001",
                    Name = def.Name,
                    // A part on its own is the whole tree: one occurrence, at
                    // the root, under its own name. A body of it carries the
                    // same name with its body suffix, so the pieces stay
                    // apart without making a branch of their own.
                    Path = def.Name,
                    Transform = Identity(),
                });
            }
            return scene;
        }

        private static double[] Identity()
        {
            var m = new double[16];
            m[0] = m[5] = m[10] = m[15] = 1.0;
            return m;
        }

        private static string SafeTitle(IModelDoc2 model)
        {
            string path = null, title = null;
            try { path = model.GetPathName(); } catch { }
            try { title = model.GetTitle(); } catch { }
            return PartName(path, title);
        }

        /// <summary>
        /// The name of a part sent on its own: its file name without the
        /// extension, as the assembly route (DocNameOf) and the STEP route
        /// name it. The window title was used before, and it carries
        /// ".SLDPRT" when Windows shows extensions for known file types, so
        /// the same part got a different name on another PC. A document
        /// that was never saved has only its title, with any SolidWorks
        /// extension taken off.
        /// </summary>
        internal static string PartName(string pathName, string title)
        {
            if (!string.IsNullOrEmpty(pathName))
            {
                string name = System.IO.Path.GetFileNameWithoutExtension(pathName);
                if (!string.IsNullOrEmpty(name)) return name;
            }
            if (string.IsNullOrEmpty(title)) return "part";
            foreach (var extension in new[] { ".sldprt", ".sldasm", ".slddrw" })
                if (title.Length > extension.Length
                    && title.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                    return title.Substring(0, title.Length - extension.Length);
            return title;
        }
    }
}
