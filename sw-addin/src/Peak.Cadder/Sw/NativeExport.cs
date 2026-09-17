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
            ISldWorks app, IModelDoc2 model, string path, double quality,
            Action<string> log, bool separateSolids = false,
            HashSet<string> keepPaths = null, ExportProgress progress = null,
            AppearanceOptions appearance = null, DefeatureOptions defeature = null)
        {
            var scene = Build(app, model, quality, log, separateSolids, keepPaths,
                progress, appearance, defeature);
            MeshWriter.Write(path, scene);
            return scene;
        }

        public static MeshScene Build(
            ISldWorks app, IModelDoc2 model, double quality, Action<string> log,
            bool separateSolids = false, HashSet<string> keepPaths = null,
            ExportProgress progress = null, AppearanceOptions appearance = null,
            DefeatureOptions defeature = null)
        {
            var assembly = model as IAssemblyDoc;
            if (assembly != null)
            {
                var walked = AssemblyWalker.Walk(assembly, log);
                if (progress != null)
                    progress.Stage("Building the geometry of " + walked.Count
                        + " component(s)", 0, 100, walked.Count);
                return NativeSceneBuilder.Build(
                    walked, quality, log, null, separateSolids, keepPaths, progress,
                    appearance, defeature);
            }

            // A PART has no components to walk, so it is its own single
            // instance at the origin: the same shape of scene, one entry
            // long, which keeps the consumer from needing a second case.
            return BuildSinglePart(
                model, quality, log, separateSolids, appearance, defeature);
        }

        /// <summary>The plan for what to leave out of one body, or null
        /// when this component travels as it is. Worked out from the body's
        /// topology alone.</summary>
        internal static SmallFeatureSurvey.Plan Defeature(
            IBody2 body, DefeatureSpec spec, Action<string> log)
        {
            if (spec == null || !spec.Any) return null;
            var plan = SmallFeatureSurvey.Choose(body, spec.Size, log, spec.Curved);
            return plan != null && plan.Any ? plan : null;
        }

        private static MeshScene BuildSinglePart(
            IModelDoc2 model, double quality, Action<string> log,
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
            object[] bodies = null;
            try { bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; }
            catch (Exception ex)
            {
                if (log != null) log("native export: GetBodies2 failed: " + ex.Message);
            }
            if (bodies == null) return scene;

            double tolerance = 0.0;
            int count = 0;
            MeshDefinition shared = null;
            var defs = new List<MeshDefinition>();
            foreach (var o in bodies)
            {
                var body = o as IBody2;
                if (body == null) continue;
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
                double tol = BodyTessellator.ToleranceFor(quality, 0.1);
                tolerance = Math.Max(tolerance, tol);
                BodyTessellator.Append(body, def, tol,
                    (face, b) => materials.Resolve(face, b, appearance, null), log,
                    Defeature(body, spec, log));
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
            try { return model.GetTitle(); } catch { return "part"; }
        }
    }
}
