using System;
using System.Collections.Generic;
using Peak.SwToBlender.Core;
using Peak.SwToBlender.Core.Model;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Peak.SwToBlender.Sw
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
            HashSet<string> keepPaths = null)
        {
            var scene = Build(app, model, quality, log, separateSolids, keepPaths);
            MeshWriter.Write(path, scene);
            return scene;
        }

        public static MeshScene Build(
            ISldWorks app, IModelDoc2 model, double quality, Action<string> log,
            bool separateSolids = false, HashSet<string> keepPaths = null)
        {
            var assembly = model as IAssemblyDoc;
            if (assembly != null)
                return NativeSceneBuilder.Build(
                    AssemblyWalker.Walk(assembly, log), quality, log, null, separateSolids,
                    keepPaths);

            // A PART has no components to walk, so it is its own single
            // instance at the origin: the same shape of scene, one entry
            // long, which keeps the consumer from needing a second case.
            return BuildSinglePart(model, quality, log);
        }

        private static MeshScene BuildSinglePart(
            IModelDoc2 model, double quality, Action<string> log)
        {
            var scene = new MeshScene();
            var part = model as IPartDoc;
            if (part == null) return scene;

            var def = new MeshDefinition { Id = 0, Name = SafeTitle(model) };
            var materials = new AppearanceTable(scene, log);
            var appearance = materials.ForPart(model);
            object[] bodies = null;
            try { bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; }
            catch (Exception ex)
            {
                if (log != null) log("native export: GetBodies2 failed: " + ex.Message);
            }
            if (bodies == null) return scene;

            double tolerance = 0.0;
            foreach (var o in bodies)
            {
                var body = o as IBody2;
                if (body == null) continue;
                double tol = BodyTessellator.ToleranceFor(quality, 0.1);
                tolerance = Math.Max(tolerance, tol);
                BodyTessellator.Append(body, def, tol,
                    (face, b) => materials.Resolve(face, b, appearance, null), log);
            }
            if (def.TriangleCount == 0) return scene;

            scene.Tolerance = tolerance;
            scene.Definitions.Add(def);
            scene.Instances.Add(new MeshInstance
            {
                DefinitionId = 0,
                ComponentId = "c001",
                Name = def.Name,
                Transform = Identity(),
            });
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
