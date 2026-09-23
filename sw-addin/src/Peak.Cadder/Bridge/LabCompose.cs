using System;
using System.Collections.Generic;
using System.IO;
using Peak.Cadder.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Peak.Cadder.Bridge
{
    /// <summary>
    /// Lab operations that make test models: "compose" builds an assembly
    /// from a recipe, and "configure" adds a configuration to an open
    /// document. They exist so a case no corpus assembly has can be made in
    /// the lab, for example a subassembly instance that uses another
    /// configuration than its document shows.
    ///
    /// These are the only lab operations that save, and they save only
    /// under the temp folder: a lab session never writes over a model the
    /// user or the corpus owns.
    ///
    /// compose:
    ///   { "op": "compose", "save_as": "...\\top.SLDASM",
    ///     "components": [ { "file": "...\\hinge.SLDASM", "at": [x, y, z],
    ///                       "configuration": "Wide", "flexible": true,
    ///                       "fixed": true } ],
    ///     "mates": [ { "type": "coincident", "align": "aligned",
    ///                  "a": { "component": "hinge-1/leaf-1", "feature": "Top Plane" },
    ///                  "b": { "feature": "Top Plane" },
    ///                  "value": 0.0, "min": 0.0, "max": 0.0 } ] }
    /// A component name is its path in the new assembly. An entity with no
    /// component is the new assembly's own feature.
    ///
    /// configure:
    ///   { "op": "configure", "document": "<title or path>", "add": "Wide",
    ///     "dimensions": [ { "name": "D2@LimitAngle1", "value": 1.2 } ],
    ///     "suppress": [ "Coincident3" ], "show": "Default", "save": true }
    /// The dimensions and suppressions apply in the added configuration.
    /// </summary>
    internal static class LabCompose
    {
        public static Dictionary<string, object> Compose(
            ISldWorks app, Dictionary<string, object> request)
        {
            string saveAs = MiniJson.Str(request, "save_as", null);
            string refused = RefusedPath(saveAs);
            if (refused != null) return Fail(refused);
            if (File.Exists(saveAs)) return Fail("will not write over " + saveAs);

            // The recipe may name one: the default can point at a template
            // that is not on this machine.
            string template = MiniJson.Str(request, "template", null);
            if (string.IsNullOrEmpty(template))
                template = app.GetUserPreferenceStringValue(
                    (int)swUserPreferenceStringValue_e.swDefaultTemplateAssembly);
            var model = app.NewDocument(template, 0, 0, 0) as IModelDoc2;
            var asm = model as IAssemblyDoc;
            if (asm == null) return Fail("no assembly could be made from " + template);

            var added = new List<object>();
            foreach (var o in MiniJson.Arr(request, "components") ?? new List<object>())
            {
                var spec = o as Dictionary<string, object>;
                if (spec == null) continue;
                string file = MiniJson.Str(spec, "file", null);
                if (string.IsNullOrEmpty(file) || !File.Exists(file))
                    return Fail("no such file: " + file);
                int err = 0, warn = 0;
                var doc = app.OpenDoc6(file, DocType(file),
                    (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref err, ref warn) as IModelDoc2;
                if (doc == null) return Fail("could not open " + file + " (error " + err + ")");
                int activateErr = 0;
                app.ActivateDoc3(model.GetTitle(), false,
                    (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref activateErr);

                var at = MiniJson.NumArray(spec, "at") ?? new double[3];
                string config = MiniJson.Str(spec, "configuration", "");
                var comp = asm.AddComponent5(file,
                    (int)swAddComponentConfigOptions_e.swAddComponentConfigOptions_CurrentSelectedConfig,
                    "", false, "", at[0], at.Length > 1 ? at[1] : 0.0, at.Length > 2 ? at[2] : 0.0)
                    as Component2;
                if (comp == null) return Fail("AddComponent5 refused " + file);
                bool flexible = MiniJson.Flag(spec, "flexible");
                if (flexible || !string.IsNullOrEmpty(config))
                {
                    model.ClearSelection2(true);
                    comp.Select4(false, null, false);
                    bool set = asm.CompConfigProperties5(
                        (int)swComponentSuppressionState_e.swComponentFullyResolved,
                        flexible ? (int)swComponentSolvingOption_e.swComponentFlexibleSolving
                                 : (int)swComponentSolvingOption_e.swComponentRigidSolving,
                        true, !string.IsNullOrEmpty(config), config ?? "", false, false);
                    model.ClearSelection2(true);
                    if (!set) return Fail("could not set the configuration or solving of " + comp.Name2);
                }
                // SolidWorks fixes the first component of a new assembly, so
                // "fixed": false unfixes it.
                model.ClearSelection2(true);
                comp.Select4(false, null, false);
                if (MiniJson.Flag(spec, "fixed")) asm.FixComponent();
                else asm.UnfixComponent();
                model.ClearSelection2(true);
                added.Add(comp.Name2);
            }

            var mates = new List<object>();
            foreach (var o in MiniJson.Arr(request, "mates") ?? new List<object>())
            {
                var spec = o as Dictionary<string, object>;
                if (spec == null) continue;
                model.ClearSelection2(true);
                foreach (string side in new[] { "a", "b" })
                {
                    string why = SelectEntity(model, asm, MiniJson.Obj(spec, side));
                    if (why != null) return Fail("mate " + mates.Count + " " + side + ": " + why);
                }
                int status;
                var mate = asm.AddMate5(MateType(MiniJson.Str(spec, "type", "coincident")),
                    Align(MiniJson.Str(spec, "align", "closest")), false,
                    MiniJson.Num(spec, "value", 0.0),
                    MiniJson.Num(spec, "max", 0.0), MiniJson.Num(spec, "min", 0.0),
                    1.0, 1.0,
                    MiniJson.Num(spec, "value", 0.0),
                    MiniJson.Num(spec, "max", 0.0), MiniJson.Num(spec, "min", 0.0),
                    false, false, 0, out status) as IFeature;
                model.ClearSelection2(true);
                if (mate == null) return Fail("mate " + mates.Count + " refused (status " + status + ")");
                string name = MiniJson.Str(spec, "name", null);
                if (!string.IsNullOrEmpty(name)) mate.Name = name;
                mates.Add(mate.Name);
            }

            model.EditRebuild3();
            int saveErr = 0, saveWarn = 0;
            bool saved = model.Extension.SaveAs(saveAs,
                (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, ref saveErr, ref saveWarn);
            if (!saved) return Fail("save failed (error " + saveErr + ", warning " + saveWarn + ")");
            return new Dictionary<string, object>
            {
                { "ok", true }, { "document", saveAs }, { "components", added },
                { "mates", mates },
            };
        }

        public static Dictionary<string, object> Configure(
            ISldWorks app, Dictionary<string, object> request, IModelDoc2 model)
        {
            if (model == null) return Fail("no such document is open");
            string path = model.GetPathName();
            bool save = MiniJson.Flag(request, "save");
            if (save)
            {
                string refused = RefusedPath(path);
                if (refused != null) return Fail(refused);
            }
            string add = MiniJson.Str(request, "add", null);
            if (!string.IsNullOrEmpty(add))
            {
                var config = model.AddConfiguration3(add, "", "", 0) as IConfiguration;
                if (config == null) return Fail("could not add configuration " + add);
            }
            string only = add;
            var set = new List<object>();
            foreach (var o in MiniJson.Arr(request, "dimensions") ?? new List<object>())
            {
                var spec = o as Dictionary<string, object>;
                if (spec == null) continue;
                string name = MiniJson.Str(spec, "name", null);
                var dim = model.Parameter(name) as IDimension;
                if (dim == null) return Fail("no dimension named " + name);
                int result = string.IsNullOrEmpty(only)
                    ? dim.SetSystemValue3(MiniJson.Num(spec, "value", 0.0),
                        (int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration, null)
                    : dim.SetSystemValue3(MiniJson.Num(spec, "value", 0.0),
                        (int)swSetValueInConfiguration_e.swSetValue_InSpecificConfigurations,
                        new[] { only });
                set.Add(name + " status " + result);
            }
            foreach (var o in MiniJson.Arr(request, "suppress") ?? new List<object>())
            {
                string name = o as string;
                var feat = FindFeature(model, name);
                if (feat == null) return Fail("no feature named " + name);
                bool done = string.IsNullOrEmpty(only)
                    ? feat.SetSuppression2((int)swFeatureSuppressionAction_e.swSuppressFeature,
                        (int)swInConfigurationOpts_e.swThisConfiguration, null)
                    : feat.SetSuppression2((int)swFeatureSuppressionAction_e.swSuppressFeature,
                        (int)swInConfigurationOpts_e.swSpecifyConfiguration, new[] { only });
                set.Add(name + (done ? " suppressed" : " not suppressed"));
            }
            string show = MiniJson.Str(request, "show", null);
            if (!string.IsNullOrEmpty(show) && !model.ShowConfiguration2(show))
                return Fail("could not show configuration " + show);
            model.EditRebuild3();
            if (save)
            {
                int err = 0, warn = 0;
                if (!model.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref err, ref warn))
                    return Fail("save failed (error " + err + ", warning " + warn + ")");
            }
            var names = model.GetConfigurationNames() as string[];
            return new Dictionary<string, object>
            {
                { "ok", true }, { "document", path }, { "configurations", names },
                { "shown", model.ConfigurationManager.ActiveConfiguration.Name },
                { "set", set },
            };
        }

        /// <summary>Null when a lab operation may save at this path: a
        /// SolidWorks file under the temp folder.</summary>
        internal static string RefusedPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "a path is required";
            string full, temp;
            try
            {
                full = Path.GetFullPath(path);
                temp = Path.GetFullPath(Path.GetTempPath());
            }
            catch (Exception ex) { return "bad path " + path + ": " + ex.Message; }
            if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase))
                return "a lab operation saves only under " + temp;
            return null;
        }

        private static string SelectEntity(
            IModelDoc2 model, IAssemblyDoc asm, Dictionary<string, object> spec)
        {
            if (spec == null) return "no entity";
            string feature = MiniJson.Str(spec, "feature", null);
            string component = MiniJson.Str(spec, "component", null);
            IFeature feat;
            if (string.IsNullOrEmpty(component))
                feat = asm.FeatureByName(feature) as IFeature;
            else
            {
                var comp = asm.GetComponentByName(component) as Component2
                           ?? FindComponent(asm, component);
                if (comp == null)
                    return "no component " + component + " (have " + string.Join(", ", Names(asm)) + ")";
                feat = comp.FeatureByName(feature) as IFeature;
            }
            if (feat == null) return "no feature " + feature + (component == null ? "" : " on " + component);
            return feat.Select2(true, 1) ? null : "could not select " + feature;
        }

        /// <summary>A component by its path, walking the tree: the name
        /// lookup does not reach every child of a subassembly.</summary>
        private static Component2 FindComponent(IAssemblyDoc asm, string path)
        {
            object[] all = null;
            try { all = asm.GetComponents(false) as object[]; } catch { }
            foreach (var o in all ?? new object[0])
            {
                var c = o as Component2;
                if (c != null && string.Equals(c.Name2, path, StringComparison.OrdinalIgnoreCase))
                    return c;
            }
            return null;
        }

        private static List<string> Names(IAssemblyDoc asm)
        {
            var names = new List<string>();
            object[] all = null;
            try { all = asm.GetComponents(false) as object[]; } catch { }
            foreach (var o in all ?? new object[0])
            {
                var c = o as Component2;
                if (c != null) names.Add(c.Name2);
            }
            return names;
        }

        private static IFeature FindFeature(IModelDoc2 model, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var asm = model as IAssemblyDoc;
            var part = model as IPartDoc;
            var feat = (asm != null ? asm.FeatureByName(name)
                        : part != null ? part.FeatureByName(name) : null) as IFeature;
            if (feat != null) return feat;
            // A mate is not a top-level feature: it sits in the mate group.
            for (var f = model.FirstFeature() as IFeature; f != null; f = f.GetNextFeature() as IFeature)
            {
                if (f.GetTypeName2() != "MateGroup") continue;
                for (var s = f.GetFirstSubFeature() as IFeature; s != null; s = s.GetNextSubFeature() as IFeature)
                    if (string.Equals(s.Name, name, StringComparison.Ordinal)) return s;
            }
            return null;
        }

        private static int MateType(string type)
        {
            switch ((type ?? "").ToLowerInvariant())
            {
                case "concentric": return (int)swMateType_e.swMateCONCENTRIC;
                case "parallel": return (int)swMateType_e.swMatePARALLEL;
                case "perpendicular": return (int)swMateType_e.swMatePERPENDICULAR;
                case "distance": return (int)swMateType_e.swMateDISTANCE;
                case "angle": return (int)swMateType_e.swMateANGLE;
                case "tangent": return (int)swMateType_e.swMateTANGENT;
                default: return (int)swMateType_e.swMateCOINCIDENT;
            }
        }

        private static int Align(string align)
        {
            switch ((align ?? "").ToLowerInvariant())
            {
                case "aligned": return (int)swMateAlign_e.swMateAlignALIGNED;
                case "anti": return (int)swMateAlign_e.swMateAlignANTI_ALIGNED;
                default: return (int)swMateAlign_e.swMateAlignCLOSEST;
            }
        }

        private static int DocType(string file)
        {
            return string.Equals(Path.GetExtension(file), ".sldprt", StringComparison.OrdinalIgnoreCase)
                ? (int)swDocumentTypes_e.swDocPART
                : (int)swDocumentTypes_e.swDocASSEMBLY;
        }

        private static Dictionary<string, object> Fail(string why)
        {
            return new Dictionary<string, object> { { "ok", false }, { "error", why } };
        }
    }
}
