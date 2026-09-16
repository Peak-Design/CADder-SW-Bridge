using System;
using System.Collections.Generic;
using System.Globalization;
using Peak.SwToBlender.Core;
using Peak.SwToBlender.Core.Model;

namespace Peak.SwToBlender.Tests
{
    /// <summary>
    /// Re-runs the loop analysis from a manifest the add-in wrote, WITHOUT
    /// SolidWorks and without the mate log. The rigid groups and the
    /// classified joints are the whole input LoopAnalyzer.Analyze takes,
    /// and a live export writes both, so a manifest is a fixture for
    /// everything from loop selection on: cut narrowing, driver choice,
    /// mechanism options.
    ///
    /// What it cannot replay is what happened BEFORE: grouping, the DOF
    /// probe's verdicts, classification. The joints come back as the
    /// exporter left them, narrowed and oriented, so a round of Analyze on
    /// them shows what the CURRENT analyzer adds to that export (live
    /// plunger.sldasm, 2026-09-15: the nut rings the probe could not weld).
    /// </summary>
    internal static class ManifestReplay
    {
        public sealed class Inputs
        {
            public List<ManifestComponent> Components = new List<ManifestComponent>();
            public List<RigidGroup> Groups = new List<RigidGroup>();
            public List<RigJoint> Joints = new List<RigJoint>();
        }

        public static Inputs Load(string json)
        {
            var root = MiniJson.ParseObject(json);
            var inputs = new Inputs();

            foreach (var item in MiniJson.Arr(root, "components"))
            {
                var c = (Dictionary<string, object>)item;
                var comp = new ManifestComponent();
                comp.Id = MiniJson.Str(c, "id");
                comp.SwPath = MiniJson.Str(c, "sw_path");
                comp.StepName = MiniJson.Str(c, "step_name");
                comp.StepOccurrencePath = MiniJson.Str(c, "step_occurrence_path");
                comp.Suppressed = MiniJson.Flag(c, "suppressed");
                comp.SubassemblySolving = MiniJson.Str(c, "subassembly_solving");
                comp.Transform = Mat4(MiniJson.Arr(c, "transform"));
                inputs.Components.Add(comp);
            }

            foreach (var item in MiniJson.Arr(root, "rigid_groups"))
            {
                var g = (Dictionary<string, object>)item;
                var group = new RigidGroup();
                group.Id = MiniJson.Str(g, "id");
                group.Name = MiniJson.Str(g, "name");
                foreach (var cid in MiniJson.Arr(g, "components")) group.Components.Add((string)cid);
                group.Grounded = MiniJson.Flag(g, "grounded");
                if (g.ContainsKey("bbox_diag") && g["bbox_diag"] != null)
                    group.BboxDiag = MiniJson.Num(g, "bbox_diag");
                inputs.Groups.Add(group);
            }

            foreach (var item in MiniJson.Arr(root, "joints"))
            {
                var j = (Dictionary<string, object>)item;
                var joint = new RigJoint();
                joint.Id = MiniJson.Str(j, "id");
                joint.Type = MiniJson.Str(j, "type");
                joint.ParentGroup = MiniJson.Str(j, "parent_group");
                joint.ChildGroup = MiniJson.Str(j, "child_group");
                joint.Origin = Vec(j, "origin");
                joint.Axis = Vec(j, "axis");
                joint.SecondaryAxis = Vec(j, "secondary_axis");
                joint.Confidence = MiniJson.Str(j, "confidence", "high");
                joint.Notes = MiniJson.Str(j, "notes");
                var limits = MiniJson.Obj(j, "limits");
                if (limits != null)
                {
                    joint.RotationLimit = Limit(MiniJson.Obj(limits, "rotation"));
                    joint.TranslationLimit = Limit(MiniJson.Obj(limits, "translation"));
                }
                var coupling = MiniJson.Obj(j, "coupling");
                if (coupling != null)
                {
                    var c = new JointCoupling();
                    c.Kind = MiniJson.Str(coupling, "kind");
                    c.DriverJoint = MiniJson.Str(coupling, "driver_joint");
                    if (coupling.ContainsKey("ratio") && coupling["ratio"] != null)
                        c.Ratio = MiniJson.Num(coupling, "ratio");
                    if (coupling.ContainsKey("meters_per_radian") && coupling["meters_per_radian"] != null)
                        c.MetersPerRadian = MiniJson.Num(coupling, "meters_per_radian");
                    if (coupling.ContainsKey("lead_m_per_rev") && coupling["lead_m_per_rev"] != null)
                        c.LeadMPerRev = MiniJson.Num(coupling, "lead_m_per_rev");
                    joint.Coupling = c;
                }
                inputs.Joints.Add(joint);
            }
            return inputs;
        }

        public static RigManifest ToManifest(Inputs inputs, LoopAnalysisResult loops, string stepFile)
        {
            var m = new RigManifest();
            m.Generator.Name = "ManifestReplay";
            m.Generator.Version = "replay";
            m.StepExport.File = stepFile;
            m.StepExport.Ap = "AP214";
            m.Components.AddRange(inputs.Components);
            m.RigidGroups.AddRange(inputs.Groups);
            m.Joints.AddRange(loops.Joints);
            m.Loops.AddRange(loops.Loops);
            m.Mechanisms.AddRange(loops.Mechanisms);
            return m;
        }

        private static JointLimit Limit(Dictionary<string, object> o)
        {
            if (o == null) return null;
            return new JointLimit
            {
                Min = MiniJson.Num(o, "min"),
                Max = MiniJson.Num(o, "max"),
                ValueAtRest = MiniJson.Num(o, "value_at_rest"),
            };
        }

        private static double[] Vec(Dictionary<string, object> o, string key)
        {
            if (!o.ContainsKey(key) || o[key] == null) return null;
            var arr = (List<object>)o[key];
            var v = new double[arr.Count];
            for (int i = 0; i < arr.Count; i++) v[i] = Convert.ToDouble(arr[i], CultureInfo.InvariantCulture);
            return v;
        }

        private static double[,] Mat4(List<object> rows)
        {
            var m = MathOps.Identity4();
            if (rows == null) return m;
            for (int r = 0; r < 4 && r < rows.Count; r++)
            {
                var row = (List<object>)rows[r];
                for (int k = 0; k < 4 && k < row.Count; k++)
                    m[r, k] = Convert.ToDouble(row[k], CultureInfo.InvariantCulture);
            }
            return m;
        }
    }
}
