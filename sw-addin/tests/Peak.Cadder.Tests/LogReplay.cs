using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Peak.Cadder.Core;
using Peak.Cadder.Core.Model;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// Rebuilds a MateGraph from an add-in debug log and the manifest it
    /// wrote, so a live export can be run through the kinematics engine
    /// again WITHOUT SolidWorks.
    ///
    /// The add-in logs one line per mate with every entity SolidWorks handed
    /// it: kind, owning component and the raw EntityParams. That is the whole
    /// input the engine ever sees from a mate, so a log block plus the
    /// component list is enough to replay grouping, classification and loop
    /// analysis exactly. Live TongRig (2026-09-14) is why this exists: the
    /// export ran on a stale DLL, everything welded into one body, and the
    /// question "what does the CURRENT engine make of this assembly" could
    /// only be answered by re-exporting, which needs a machine with
    /// SolidWorks on it.
    ///
    /// Since 2026-09-14 the diag line also says whether the mate is
    /// suppressed and, for a limit, its range and as-mated value, so a log
    /// from the current add-in replays without assumptions. An older log has
    /// neither, and the caller supplies both through Options, which also
    /// override what the log says. What no log carries is which components
    /// are FIXED: the manifest records grounding only through the rigid
    /// groups.
    ///
    /// Raw entity values are lifted to the global frame by the mate's
    /// RESIDENCE transform before the engine sees them. The log prints the
    /// unlifted array, so this replay is exact for mates that live in the top
    /// assembly (residence = identity) and needs the residence transform
    /// supplied for mates read out of a flexible subassembly's own document.
    /// </summary>
    internal static class LogReplay
    {
        public sealed class Options
        {
            public HashSet<string> FixedComponents = new HashSet<string>();
            public HashSet<string> SuppressedMates = new HashSet<string>();
            /// <summary>mate name -> (min, max, current), all in metres or
            /// radians as the mate measures them.</summary>
            public Dictionary<string, double[]> Limits = new Dictionary<string, double[]>();
        }

        private static readonly Regex MateLine = new Regex(
            @"^mate (\S+) \[(\w+)\]( suppressed)?(?: range=\[([^\]]*)\])? \| (.*)$",
            RegexOptions.Compiled);
        private static readonly Regex EntityText = new Regex(
            @"^(\w+)\((\d+)/(\d+)\)@([^\s\[]+)(\[![^\]]*\])? raw=(\[[^\]]*\]|null)$",
            RegexOptions.Compiled);
        /// <summary>The reader's own line for a rack-pinion mate. The mate
        /// line carries the entities and not the numbers, so the coupling
        /// would replay without its ratio.</summary>
        private static readonly Regex RackLine = new Regex(
            @"^rack mate (\S+): diameterVal=([-+0-9.eE]+) type=(\d+) reverse=(True|False)$",
            RegexOptions.Compiled);
        /// <summary>The reader's own line for a screw mate. The lead is
        /// worked out from the mate's numbers and the document's linear
        /// unit, neither of which the mate line carries. A log written
        /// before the unit was read has no unit= field: it replays as the
        /// metre, which is what that export used.</summary>
        private static readonly Regex ScrewLine = new Regex(
            @"^screw mate (\S+): revolutionVal=([-+0-9.eE]+) type=(\d+) "
            + @"reverse=(True|False)( unit=[-+0-9.eE]+)? -> lead=[-+0-9.eE]+$",
            RegexOptions.Compiled);
        /// <summary>An entity the reader rebuilt from its selection: the
        /// mate line above it still says "unknown", and its param slots are
        /// filler. Logs written before 2026-09-16 carry no dir= field and
        /// cannot be replayed, so they are left alone.</summary>
        private static readonly Regex RecoverLine = new Regex(
            @"^recovered edge direction on (\S+) @(\S+) dir=\[([^\]]*)\] at=\[([^\]]*)\]$",
            RegexOptions.Compiled);
        private static readonly Regex RetypeLine = new Regex(
            @"^retyped (\w+)?->(\w+)(?: \(half-angle ([-+0-9.eE]+)\))? on (\S+) @(\S+)$",
            RegexOptions.Compiled);
        private static readonly Regex LockLine = new Regex(
            @"^concentric (\S+) LockRotation=True$", RegexOptions.Compiled);

        /// <summary>Strips the timestamp the add-in prefixes every line with.</summary>
        private static string Body(string line)
        {
            // "2026-09-14 17:37:52 mate ..." -> "mate ..."
            return line.Length > 20 && line[4] == '-' && line[10] == ' '
                ? line.Substring(20) : line;
        }

        public static MateGraph FromLog(string manifestJson, IEnumerable<string> logLines,
                                        Options options = null)
        {
            options = options ?? new Options();
            var graph = new MateGraph();

            // ── Components, from the manifest ──────────────────────────────
            var manifest = MiniJson.ParseObject(manifestJson);
            foreach (var item in MiniJson.Arr(manifest, "components"))
            {
                var c = (Dictionary<string, object>)item;
                var comp = new GraphComponent();
                comp.Id = MiniJson.Str(c, "id");
                comp.Path = MiniJson.Str(c, "sw_path");
                comp.Name = StripInstance(comp.Path);
                comp.FileName = MiniJson.Str(c, "step_name") ?? comp.Name;
                comp.Solving = MiniJson.Str(c, "subassembly_solving");
                comp.Suppressed = ((c.ContainsKey("suppressed") && c["suppressed"] is bool)
                                   && (bool)c["suppressed"]);
                comp.Transform = MathOps.Identity4();
                var rows = MiniJson.Arr(c, "transform");
                for (int r = 0; r < 4; r++)
                {
                    var row = (List<object>)rows[r];
                    for (int k = 0; k < 4; k++)
                        comp.Transform[r, k] = Convert.ToDouble(row[k], CultureInfo.InvariantCulture);
                }
                comp.IsFixed = options.FixedComponents.Contains(comp.Id);
                graph.Components.Add(comp);
            }

            // ── Mates, from the log ────────────────────────────────────────
            var byName = new Dictionary<string, GraphMate>();
            var retypes = new List<Match>();
            var racks = new List<Match>();
            var screws = new List<Match>();
            var recovered = new List<Match>();
            var locks = new HashSet<string>();
            foreach (var raw in logLines)
            {
                string line = Body(raw).TrimEnd();
                var mm = MateLine.Match(line);
                if (mm.Success)
                {
                    string name = mm.Groups[1].Value;
                    if (byName.ContainsKey(name)) continue;   // logged once per read pass
                    var mate = new GraphMate();
                    mate.FeatureName = name;
                    mate.TypeName = mm.Groups[2].Value;
                    mate.Suppressed = mm.Groups[3].Success
                        || options.SuppressedMates.Contains(name);
                    foreach (string ent in mm.Groups[5].Value.Split(new[] { " | " }, StringSplitOptions.None))
                        mate.Entities.Add(ParseEntity(ent.Trim(), mate.TypeName));
                    double[] lim;
                    if (!options.Limits.TryGetValue(name, out lim) && mm.Groups[4].Success)
                        lim = ParseRange(mm.Groups[4].Value);
                    if (lim != null)
                    {
                        mate.MinimumVariation = lim[0];
                        mate.MaximumVariation = lim[1];
                        mate.CurrentValue = lim[2];
                    }
                    byName[name] = mate;
                    graph.Mates.Add(mate);
                    continue;
                }
                var rm = RetypeLine.Match(line);
                if (rm.Success) { retypes.Add(rm); continue; }
                var lm = LockLine.Match(line);
                if (lm.Success) { locks.Add(lm.Groups[1].Value); continue; }
                var km = RackLine.Match(line);
                if (km.Success) { racks.Add(km); continue; }
                var sm = ScrewLine.Match(line);
                if (sm.Success) { screws.Add(sm); continue; }
                var vm = RecoverLine.Match(line);
                if (vm.Success) recovered.Add(vm);
            }

            foreach (var sm in screws)
            {
                GraphMate mate;
                if (!byName.TryGetValue(sm.Groups[1].Value, out mate)) continue;
                double value = double.Parse(sm.Groups[2].Value, CultureInfo.InvariantCulture);
                double unit = 1.0;
                if (sm.Groups[5].Success)
                    unit = double.Parse(sm.Groups[5].Value.Substring(" unit=".Length),
                                        CultureInfo.InvariantCulture);
                mate.LeadMPerRev = ScrewLead.Metres(
                    value, sm.Groups[3].Value == "1", sm.Groups[4].Value == "True", unit);
            }

            foreach (var vm in recovered)
            {
                GraphMate mate;
                if (!byName.TryGetValue(vm.Groups[1].Value, out mate)) continue;
                foreach (var e in mate.Entities)
                {
                    if ((e.ComponentId ?? "asm") != vm.Groups[2].Value) continue;
                    e.EntityTypeName = "edge";
                    e.Direction = ParseVector(vm.Groups[3].Value);
                    var at = ParseVector(vm.Groups[4].Value);
                    if (at != null) e.Point = at;
                }
            }

            foreach (var km in racks)
            {
                GraphMate mate;
                if (!byName.TryGetValue(km.Groups[1].Value, out mate)) continue;
                double value = double.Parse(km.Groups[2].Value, CultureInfo.InvariantCulture);
                // 1 is travel per revolution, anything else a pitch diameter
                // (MateReader).
                double perRadian = km.Groups[3].Value == "1"
                    ? value / (2.0 * Math.PI) : value / 2.0;
                mate.MetersPerRadian =
                    km.Groups[4].Value == "True" ? -perRadian : perRadian;
            }

            foreach (var rm in retypes)
            {
                string to = rm.Groups[2].Value, name = rm.Groups[4].Value, comp = rm.Groups[5].Value;
                GraphMate mate;
                if (!byName.TryGetValue(name, out mate)) continue;
                foreach (var e in mate.Entities)
                {
                    if ((e.ComponentId ?? "asm") != comp) continue;
                    e.EntityTypeName = to;
                    if (to == "cone" && rm.Groups[3].Success)
                    {
                        e.HalfAngle = double.Parse(rm.Groups[3].Value, CultureInfo.InvariantCulture);
                        e.Radius = 0.0;
                    }
                    if (to == "sphere") e.Direction = null;
                }
            }
            foreach (string name in locks)
            {
                GraphMate mate;
                if (byName.TryGetValue(name, out mate)) mate.LockRotation = true;
            }
            // MateReader runs this after reading, so the replay does too.
            EntityRepair.Apply(graph, null);
            return graph;
        }

        /// <summary>A bracketed vector as the diag lines write one.</summary>
        private static double[] ParseVector(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var parts = text.Split(',');
            var v = new double[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                v[i] = double.Parse(parts[i].Trim(), CultureInfo.InvariantCulture);
            return v;
        }

        /// <summary>"min,max,current" as the diag line prints a limit.</summary>
        private static double[] ParseRange(string text)
        {
            var parts = text.Split(',');
            if (parts.Length != 3) throw new FormatException("unrecognised range: " + text);
            var r = new double[3];
            for (int i = 0; i < 3; i++)
                r[i] = double.Parse(parts[i].Trim(), CultureInfo.InvariantCulture);
            return r;
        }

        private static GraphMateEntity ParseEntity(string text, string mateType)
        {
            var m = EntityText.Match(text);
            if (!m.Success)
                throw new FormatException("unrecognised entity text: " + text);
            var e = new GraphMateEntity();
            e.EntityTypeName = m.Groups[1].Value;
            int kind = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            string comp = m.Groups[4].Value;
            e.ComponentId = comp == "asm" ? null : comp;

            string rawText = m.Groups[6].Value;
            if (rawText == "null") return e;
            var parts = rawText.Trim('[', ']').Split(',');
            var p = new double[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                p[i] = double.Parse(parts[i], CultureInfo.InvariantCulture);

            // Mirrors MateReader: the point is always taken; a direction only
            // for kinds whose EntityParams row defines one (line 2, plane 3,
            // cylinder 4, cone 5, circle 7) or when the mate itself is about
            // directions; a radius only for the round kinds.
            if (p.Length >= 3) e.Point = new[] { p[0], p[1], p[2] };
            bool directional = kind == 2 || kind == 3 || kind == 4 || kind == 5 || kind == 7
                            // a datum axis: point-typed, direction slots real
                            || (kind == 1 && e.EntityTypeName == "axis");
            bool aboutDirections = mateType.IndexOf("PARALLEL", StringComparison.OrdinalIgnoreCase) >= 0
                                || mateType.IndexOf("PERPENDICULAR", StringComparison.OrdinalIgnoreCase) >= 0
                                // The rack side of a rack-pinion mate carries
                                // the edge the rack runs along. MateReader.
                                || mateType.IndexOf("RACKPINION", StringComparison.OrdinalIgnoreCase) >= 0;
            if (p.Length >= 6)
            {
                var raw = new[] { p[3], p[4], p[5] };
                if (MathOps.Norm(raw) > MathOps.Epsilon) e.RawDirection = raw;
            }
            if ((directional || aboutDirections) && p.Length >= 6)
            {
                var dir = new[] { p[3], p[4], p[5] };
                if (MathOps.Norm(dir) > MathOps.Epsilon) e.Direction = dir;
            }
            bool hasRadius = kind == 4 || kind == 5 || kind == 6 || kind == 7;
            if (hasRadius && p.Length >= 7) e.Radius = p[6];
            return e;
        }

        private static string StripInstance(string path)
        {
            if (path == null) return null;
            int slash = path.LastIndexOf('/');
            string leaf = slash >= 0 ? path.Substring(slash + 1) : path;
            int dash = leaf.LastIndexOf('-');
            return dash > 0 ? leaf.Substring(0, dash) : leaf;
        }

        // ── Running the engine the way ExportCommand does, minus the probe ──

        public sealed class Outcome
        {
            public RigidGroupingResult Grouping;
            public ClassificationResult Classification;
            public LoopAnalysisResult Loops;
            public List<ManifestWarning> Warnings = new List<ManifestWarning>();
        }

        public static Outcome Run(MateGraph graph)
        {
            var o = new Outcome();
            o.Grouping = RigidGrouper.Group(graph);
            o.Classification = JointClassifier.Classify(graph, o.Grouping);
            var all = new List<RigidGroup>(o.Grouping.Groups);
            all.AddRange(o.Classification.VirtualGroups);
            o.Loops = LoopAnalyzer.Analyze(all, o.Classification.Joints);
            o.Warnings.AddRange(o.Classification.Warnings);
            o.Warnings.AddRange(SymmetricCoupler.Resolve(
                graph, o.Grouping, o.Loops.Joints, o.Loops.Loops));
            return o;
        }

        /// <summary>The replayed result as the manifest the add-in would
        /// have written, so the rig can be built and posed in Blender from
        /// it. STEP names are the path leaves, occurrence paths are null
        /// (the consumer falls back to transform matching), boxes are
        /// absent (the bone-length heuristic falls back to its default).</summary>
        public static RigManifest ToManifest(MateGraph graph, Outcome o, string stepFile)
        {
            var m = new RigManifest();
            m.Generator.Name = "LogReplay";
            m.Generator.Version = "replay";
            m.StepExport.File = stepFile;
            m.StepExport.Ap = "AP214";
            foreach (var g in graph.Components)
            {
                var c = new ManifestComponent();
                c.Id = g.Id;
                c.SwPath = g.Path;
                c.StepName = g.Name;
                c.Transform = g.Transform;
                c.Suppressed = g.Suppressed;
                c.SubassemblySolving = g.Solving;
                m.Components.Add(c);
            }
            m.RigidGroups.AddRange(o.Grouping.Groups);
            m.RigidGroups.AddRange(o.Classification.VirtualGroups);
            m.Joints.AddRange(o.Loops.Joints);
            m.Loops.AddRange(o.Loops.Loops);
            m.Mechanisms.AddRange(o.Loops.Mechanisms);
            m.Warnings.AddRange(o.Warnings);
            return m;
        }

        public static string Report(MateGraph graph, Outcome o)
        {
            var sb = new StringBuilder();
            var nameOf = new Dictionary<string, string>();
            foreach (var c in graph.Components) nameOf[c.Id] = c.Path;

            sb.AppendLine("GROUPS (" + o.Grouping.Groups.Count + ")");
            foreach (var g in o.Grouping.Groups)
            {
                sb.Append("  ").Append(g.Id).Append(g.Grounded ? " *" : "  ")
                  .Append(" ").Append(g.Name).Append("  [");
                var names = new List<string>();
                foreach (var cid in g.Components)
                    names.Add(nameOf.ContainsKey(cid) ? nameOf[cid] : cid);
                sb.Append(string.Join(", ", names.ToArray())).AppendLine("]");
            }
            foreach (var g in o.Classification.VirtualGroups)
                sb.Append("  ").Append(g.Id).Append("   ").Append(g.Name).AppendLine("  [carrier]");

            sb.AppendLine("JOINTS (" + o.Loops.Joints.Count + ")");
            foreach (var j in o.Loops.Joints)
            {
                sb.Append("  ").Append(j.Id).Append(" ").Append(j.Type.PadRight(11))
                  .Append(j.ParentGroup).Append(" -> ").Append(j.ChildGroup);
                if (j.Axis != null)
                    sb.Append("  axis ").Append(Vec(j.Axis));
                if (j.Origin != null)
                    sb.Append("  at ").Append(Vec(j.Origin));
                if (j.TranslationLimit != null)
                    sb.Append("  trlim [").Append(F(j.TranslationLimit.Min)).Append(", ")
                      .Append(F(j.TranslationLimit.Max)).Append("]");
                if (j.RotationLimit != null)
                    sb.Append("  rotlim [").Append(F(j.RotationLimit.Min)).Append(", ")
                      .Append(F(j.RotationLimit.Max)).Append("]");
                if (j.Coupling != null)
                    sb.Append("  coupling ").Append(j.Coupling.Kind)
                      .Append("<-").Append(j.Coupling.DriverJoint);
                sb.Append("  conf=").Append(j.Confidence);
                if (!string.IsNullOrEmpty(j.Notes))
                    sb.Append("\n        ").Append(j.Notes);
                sb.AppendLine();
            }

            sb.AppendLine("LOOPS (" + o.Loops.Loops.Count + ")");
            foreach (var lp in o.Loops.Loops)
                sb.Append("  ").Append(lp.Id).Append(" members ")
                  .Append(string.Join(",", lp.MemberJoints.ToArray()))
                  .Append(" cut ").Append(lp.ClosureJoint)
                  .Append(" closure ").Append(lp.ClosureKind)
                  .Append(" driver ").Append(lp.SuggestedDriverJoint).AppendLine();

            sb.AppendLine("WARNINGS (" + o.Warnings.Count + ")");
            foreach (var w in o.Warnings)
                sb.Append("  ").Append(w.Code).Append(": ").AppendLine(w.Message);
            return sb.ToString();
        }

        private static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

        private static string Vec(double[] v)
        {
            return "(" + F(v[0]) + ", " + F(v[1]) + ", " + F(v[2]) + ")";
        }

        public static string FixturePath(params string[] parts)
        {
            var all = new List<string> { AppContext.BaseDirectory, "Fixtures" };
            all.AddRange(parts);
            return Path.Combine(all.ToArray());
        }
    }
}
