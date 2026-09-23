using System;
using System.Collections.Generic;
using System.IO;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Peak.Cadder.Sw
{
    /// <summary>
    /// Takes the mates out that make SolidWorks' own reading of an assembly
    /// lie about what can move, and puts them back afterwards.
    ///
    /// A limit mate bounds a freedom, but the solver counts it as a fixed
    /// dimension, so a part behind one reads fully defined and GetRemaining
    /// DOFs reads it rigid (live TongRig, 2026-09-14). A coupling mate (gear,
    /// rack and pinion, screw, cam) ties one freedom to another the same way.
    /// With both taken out, what SolidWorks reports is about the mates that
    /// really hold a part.
    ///
    /// Inside a flexible subassembly a mate belongs to the subassembly's
    /// document, and a top-context handle cannot suppress it (live corpus
    /// 07). So those are reached the way MateReader reads them: through the
    /// document's own components, and suppressed in the configuration the
    /// instance uses, which need not be the document's active one.
    ///
    /// This changes the documents while it runs, the way the DOF probe
    /// always has. Restore puts every mate back and says which it could not.
    /// </summary>
    public sealed class SolveState
    {
        private sealed class Held
        {
            public IFeature Feature;
            public string Document;
            public string Name;
            public string Configuration;
            public string Kind;
        }

        private readonly List<Held> _held = new List<Held>();
        private readonly Action<string> _log;

        /// <summary>Subassembly documents rebuilt for ReadSubStatus, deepest
        /// first, with their children's transforms before the rebuild.</summary>
        private readonly List<IModelDoc2> _rebuiltDocs = new List<IModelDoc2>();
        private readonly List<KeyValuePair<Component2, double[]>> _subPoses =
            new List<KeyValuePair<Component2, double[]>>();

        private SolveState(Action<string> log) { _log = log; }

        /// <summary>How many mates are out.</summary>
        public int Count { get { return _held.Count; } }

        /// <summary>The mates SolidWorks would not suppress, as "document:
        /// mate". They are still in.</summary>
        public readonly List<string> NotTakenOut = new List<string>();

        /// <summary>The mates that are out, as "document: mate (kind)".</summary>
        public List<string> Describe()
        {
            var list = new List<string>();
            foreach (var h in _held)
                list.Add(h.Document + ": " + h.Name + " (" + h.Kind + ")");
            return list;
        }

        /// <summary>
        /// Suppresses every limit mate, and every coupling mate when asked,
        /// at the top level and inside each walked flexible subassembly.
        /// </summary>
        public static SolveState Suppress(
            IModelDoc2 top, IList<WalkedComponent> walked, bool couplings,
            Action<string> log)
        {
            var state = new SolveState(log);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var asm = top as IAssemblyDoc;
            if (asm != null)
                state.SuppressIn(asm, top, ActiveConfiguration(top), couplings, seen);

            if (walked != null)
            {
                foreach (var w in walked)
                {
                    if (w == null || w.Graph == null || w.Comp == null) continue;
                    if (w.Graph.Solving != "flexible" || w.Graph.Suppressed) continue;
                    IModelDoc2 doc = null;
                    try { doc = w.Comp.GetModelDoc2() as IModelDoc2; } catch { }
                    var sub = doc as IAssemblyDoc;
                    if (sub == null) continue;
                    string config = w.ReferencedConfiguration;
                    if (string.IsNullOrEmpty(config)) config = ActiveConfiguration(doc);
                    state.SuppressIn(sub, doc, config, couplings, seen);
                }
            }

            if (log != null)
                log("solve state: " + state._held.Count + " mate(s) taken out"
                    + (couplings ? " (limits and couplings)" : " (limits)"));
            return state;
        }

        /// <summary>
        /// Reads each walked flexible subassembly's children in the
        /// subassembly's own document (GraphComponent.SubStatusFree). There
        /// they are top level, and the status is SolidWorks' own. The
        /// document is rebuilt first when any mate is out, deepest first, so
        /// the reading follows the limits taken out inside it. A document
        /// whose active configuration is not the one the instance uses is
        /// not read: its components would describe the other configuration.
        /// </summary>
        public void ReadSubStatus(IList<WalkedComponent> walked)
        {
            if (walked == null) return;
            var subs = new List<WalkedComponent>();
            foreach (var w in walked)
            {
                if (w == null || w.Graph == null || w.Comp == null) continue;
                if (w.Graph.Solving != "flexible" || w.Graph.Suppressed) continue;
                subs.Add(w);
            }
            subs.Sort((a, b) => Depth(b).CompareTo(Depth(a)));

            var byDoc = new Dictionary<string, Dictionary<string, int>>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var p in subs)
            {
                IModelDoc2 doc = null;
                try { doc = p.Comp.GetModelDoc2() as IModelDoc2; } catch { }
                var asm = doc as IAssemblyDoc;
                if (asm == null) continue;
                string config = p.ReferencedConfiguration;
                string active = ActiveConfiguration(doc);
                if (!string.IsNullOrEmpty(config) && !string.Equals(config, active, StringComparison.Ordinal))
                {
                    if (_log != null)
                        _log("solve state: " + p.Graph.Path + " uses configuration " + config
                            + ", its document shows " + active + ": its own status is not read");
                    continue;
                }

                string key = SafePath(doc) + "|" + active;
                Dictionary<string, int> statusByName;
                if (!byDoc.TryGetValue(key, out statusByName))
                {
                    object[] comps = null;
                    if (_held.Count > 0)
                    {
                        try { comps = asm.GetComponents(true) as object[]; } catch { }
                        if (comps != null)
                            foreach (var o in comps)
                            {
                                var sc = o as Component2;
                                if (sc != null) _subPoses.Add(new KeyValuePair<Component2, double[]>(sc, Pose(sc)));
                            }
                        if (Rebuild(doc, "edit")) _rebuiltDocs.Add(doc);
                        else if (_log != null) _log("solve state: rebuilding " + SafePath(doc) + " failed");
                    }
                    try { comps = asm.GetComponents(true) as object[]; } catch { comps = null; }
                    statusByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    if (comps != null)
                        foreach (var o in comps)
                        {
                            var sc = o as Component2;
                            if (sc == null) continue;
                            string name = null;
                            int status = 0;
                            try { name = sc.Name2; status = sc.GetConstrainedStatus(); } catch { }
                            if (!string.IsNullOrEmpty(name)) statusByName[name] = status;
                        }
                    byDoc[key] = statusByName;
                }

                foreach (var c in p.Children)
                {
                    if (c == null || c.Graph == null || c.Comp == null) continue;
                    string name = null;
                    try { name = c.Comp.Name2; } catch { }
                    if (string.IsNullOrEmpty(name)) continue;
                    int slash = name.LastIndexOf('/');
                    if (slash >= 0) name = name.Substring(slash + 1);
                    int status;
                    if (statusByName.TryGetValue(name, out status)) c.Graph.SubStatusFree = status;
                }
            }
        }

        /// <summary>Rebuilds the subassembly documents ReadSubStatus rebuilt,
        /// once the mates are back, and logs how far any child moved.</summary>
        public void RebuildSubDocuments()
        {
            foreach (var doc in _rebuiltDocs)
                if (!Rebuild(doc, "edit") && _log != null)
                    _log("solve state: rebuilding " + SafePath(doc) + " after the limits went back failed");
            double worst = 0.0;
            foreach (var kv in _subPoses)
            {
                var now = Pose(kv.Key);
                if (now == null || kv.Value == null) continue;
                for (int i = 0; i < Math.Min(now.Length, kv.Value.Length); i++)
                    worst = Math.Max(worst, Math.Abs(now[i] - kv.Value[i]));
            }
            if (_log != null && _rebuiltDocs.Count > 0)
                _log("solve state: " + _rebuiltDocs.Count + " subassembly document(s) rebuilt, "
                    + "worst drift " + worst.ToString("G3", System.Globalization.CultureInfo.InvariantCulture));
        }

        private static int Depth(WalkedComponent w)
        {
            int d = 0;
            for (var p = w.Parent; p != null; p = p.Parent) d++;
            return d;
        }

        private static double[] Pose(Component2 comp)
        {
            try
            {
                var t = comp.Transform2;
                return t == null ? null : t.ArrayData as double[];
            }
            catch { return null; }
        }

        private void SuppressIn(
            IAssemblyDoc asm, IModelDoc2 doc, string config, bool couplings,
            HashSet<string> seen)
        {
            object[] comps = null;
            try { comps = asm.GetComponents(true) as object[]; } catch { }
            if (comps == null) return;
            string docPath = SafePath(doc);

            foreach (var co in comps)
            {
                var comp = co as Component2;
                if (comp == null) continue;
                object[] mates = null;
                try { mates = comp.GetMates() as object[]; } catch { }
                if (mates == null) continue;

                foreach (var o in mates)
                {
                    var mate = o as IMate2;
                    var feat = o as IFeature;
                    if (mate == null || feat == null) continue;
                    string name = null;
                    try { name = feat.Name; } catch { }
                    if (string.IsNullOrEmpty(name)) continue;
                    // A mate is reported by every component it touches, and a
                    // document shared by two instances holds one mate.
                    if (!seen.Add(docPath + "|" + name + "|" + config)) continue;

                    string kind = Kind(mate, feat, couplings);
                    if (kind == null) continue;
                    if (IsSuppressed(feat, config)) continue;   // nothing to undo

                    bool ok = false;
                    try
                    {
                        ok = feat.SetSuppression2(
                            (int)swFeatureSuppressionAction_e.swSuppressFeature,
                            (int)swInConfigurationOpts_e.swSpecifyConfiguration,
                            new[] { config });
                    }
                    catch (Exception ex)
                    {
                        if (_log != null) _log("solve state: suppress " + name + ": " + ex.Message);
                    }
                    if (ok)
                        _held.Add(new Held
                        {
                            Feature = feat, Document = Path.GetFileName(docPath),
                            Name = name, Configuration = config, Kind = kind,
                        });
                    else
                    {
                        NotTakenOut.Add(Path.GetFileName(docPath) + ": " + name);
                        if (_log != null)
                            _log("solve state: " + name + " (" + kind + ") in "
                                + Path.GetFileName(docPath) + " could not be suppressed");
                    }
                }
            }
        }

        /// <summary>Why a mate has to come out, or null when it stays.</summary>
        private static string Kind(IMate2 mate, IFeature feat, bool couplings)
        {
            int type = 0;
            try { type = mate.Type; } catch { }
            if (type == (int)swMateType_e.swMateDISTANCE
                || type == (int)swMateType_e.swMateANGLE)
            {
                double min = 0, max = 0;
                try { min = mate.MinimumVariation; max = mate.MaximumVariation; } catch { }
                return min != max ? "limit" : null;
            }
            if (type == (int)swMateType_e.swMateHINGE)
            {
                IHingeMateFeatureData data = null;
                try { data = feat.GetDefinition() as IHingeMateFeatureData; } catch { }
                bool limited = false;
                try { limited = data != null && data.AngleSelection; } catch { }
                return limited ? "limit" : null;
            }
            if (!couplings) return null;
            if (type == (int)swMateType_e.swMateGEAR
                || type == (int)swMateType_e.swMateRACKPINION
                || type == (int)swMateType_e.swMateSCREW
                || type == (int)swMateType_e.swMateLINEARCOUPLER
                || type == (int)swMateType_e.swMateCAMFOLLOWER
                || type == (int)swMateType_e.swMateUNIVERSALJOINT)
                return "coupling";
            return null;
        }

        /// <summary>
        /// Puts every mate back, and returns the ones SolidWorks would not
        /// put back, as "document: mate". An empty list means the documents
        /// are as they were, apart from the modified flag, which the API has
        /// no way to clear.
        /// </summary>
        public List<string> Restore()
        {
            var failed = new List<string>();
            foreach (var h in _held)
            {
                bool ok = false;
                try
                {
                    ok = h.Feature.SetSuppression2(
                        (int)swFeatureSuppressionAction_e.swUnSuppressFeature,
                        (int)swInConfigurationOpts_e.swSpecifyConfiguration,
                        new[] { h.Configuration });
                }
                catch (Exception ex)
                {
                    if (_log != null) _log("solve state: unsuppress " + h.Name + ": " + ex.Message);
                }
                if (!ok || IsSuppressed(h.Feature, h.Configuration))
                    failed.Add(h.Document + ": " + h.Name);
            }
            if (_log != null)
            {
                if (failed.Count == 0)
                    _log("solve state: " + _held.Count + " mate(s) restored");
                else
                    foreach (var f in failed)
                        _log("solve state WARNING: could not restore " + f
                            + "; the mate is still suppressed in that document");
            }
            _held.Clear();
            return failed;
        }

        /// <summary>
        /// Makes the solver take the change in. `how` is "edit"
        /// (EditRebuild3), "mates" (Extension.Rebuild with swUpdateMates) or
        /// "force" (ForceRebuild3, top level only).
        /// </summary>
        public static bool Rebuild(IModelDoc2 top, string how)
        {
            if (top == null) return false;
            try
            {
                switch (how)
                {
                    case "force": return top.ForceRebuild3(true);
                    case "mates":
                        return top.Extension.Rebuild(
                            (int)swRebuildOptions_e.swUpdateMates);
                    default: return top.EditRebuild3();
                }
            }
            catch { return false; }
        }

        private static bool IsSuppressed(IFeature feat, string config)
        {
            try
            {
                var states = feat.IsSuppressed2(
                    (int)swInConfigurationOpts_e.swSpecifyConfiguration,
                    new[] { config }) as bool[];
                if (states != null && states.Length > 0) return states[0];
            }
            catch { }
            try { return feat.IsSuppressed(); } catch { return false; }
        }

        internal static string ActiveConfiguration(IModelDoc2 doc)
        {
            try
            {
                var active = doc.ConfigurationManager.ActiveConfiguration;
                if (active != null) return active.Name;
            }
            catch { }
            return null;
        }

        private static string SafePath(IModelDoc2 doc)
        {
            try { return doc.GetPathName() ?? ""; } catch { return ""; }
        }
    }
}
