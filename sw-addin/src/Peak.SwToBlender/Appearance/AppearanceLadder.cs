using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
// Vendored from NEXT-STEP-SW (Peak.NextStep) @ b081285: the STEP appearance engine, merged into SW To Blender.

namespace Peak.SwToBlender.Appearance
{
    public struct Rgb
    {
        public double R, G, B;
        public Rgb(double r, double g, double b) { R = r; G = g; B = b; }

        /// <summary>SolidWorks stores an appearance colour as a COLORREF, in
        /// the form 0x00BBGGRR.</summary>
        public static Rgb FromColorRef(int c)
            => new Rgb((c & 0xFF) / 255.0, ((c >> 8) & 0xFF) / 255.0, ((c >> 16) & 0xFF) / 255.0);

        public bool ApproxEquals(Rgb o, double tol = 1e-4)
            => Math.Abs(R - o.R) < tol && Math.Abs(G - o.G) < tol && Math.Abs(B - o.B) < tol;

        public override string ToString() => $"({R:F3},{G:F3},{B:F3})";
    }

    /// <summary>
    /// One component occurrence, as a node in the assembly tree.
    ///
    /// The tree shape matters as much as the appearance. The STEP file is a
    /// tree of NEXT_ASSEMBLY_USAGE_OCCURRENCEs whose placements are relative
    /// to the PARENT assembly. A flat list with root-relative positions cannot
    /// be matched against that beyond the first level. This was measured on a
    /// four-level conveyor assembly: 7 matches out of 4564.
    /// </summary>
    public sealed class OccurrenceAppearance
    {
        /// <summary>The component path, such as "sub-1/part-2". Each occurrence
        /// has its own path.</summary>
        public string Path;
        public string ComponentName;
        /// <summary>The file name of the referenced document, without the
        /// extension. SolidWorks builds the STEP product name from this.</summary>
        public string DocName;
        public string ReferencedConfiguration;

        public OccurrenceAppearance Parent;
        public List<OccurrenceAppearance> Children = new List<OccurrenceAppearance>();

        /// <summary>The 16 doubles of IComponent2.Transform2, root relative,
        /// in metres. Null when the transform is unavailable.</summary>
        public double[] Transform;

        /// <summary>The live component, for callers inside the process.</summary>
        public IComponent2 Comp;

        public Rgb Colour;
        public double Transparency;
        /// <summary>The scope that gave the winning colour. This is for
        /// diagnosis.</summary>
        public string WinningScope;
        /// <summary>True when the winning colour comes from above the part. The
        /// colour must then override the face, body and feature styling of the
        /// part in the STEP file.</summary>
        public bool OverridesPartInternals;
        /// <summary>
        /// False when SolidWorks does not write this occurrence at all, because
        /// it is hidden or suppressed, or because a component above it is. A
        /// silent SaveAs3 leaves out every such branch. See evidence/S8/hidden.log.
        /// </summary>
        public bool Exported = true;
        /// <summary>Holds "hidden", "suppressed" or "envelope" when Exported is
        /// false. A child inherits the reason of its parent.</summary>
        public string ExcludedBecause;
    }

    /// <summary>
    /// Resolves the appearance that SolidWorks displays for each component
    /// occurrence.
    ///
    /// The rule, stated by the maintainer and confirmed by the corpus images:
    ///
    ///   ACROSS documents, the HIGHEST level wins:
    ///     top assembly > sub-assembly override > sub-assembly >
    ///     part override (component level) > part
    ///   WITHIN one part, the MOST EXACT scope wins:
    ///     face > feature > body > part
    ///
    /// The first half means an override CASCADES. An override on a
    /// sub-assembly occurrence covers every part below it, at any depth, and
    /// beats any override deeper down. This class therefore resolves top-down
    /// over the occurrence tree: the attachment nearest the root wins for its
    /// whole subtree.
    ///
    /// Overrides live in documents. The top document holds the overrides made
    /// in the top assembly, at any path depth. A sub-assembly document holds
    /// its own overrides, which apply under EVERY use of that sub-assembly.
    /// When both paint the same node, the shallower document wins.
    /// </summary>
    public static class AppearanceLadder
    {
        private sealed class Attachment
        {
            public Rgb Colour;
            public double Transparency;
            /// <summary>The depth of the document that holds the override.
            /// 0 is the top document. Lower wins.</summary>
            public int Rank;
            public string Source;
        }

        /// <summary>The exclusion reason for a component an "only the
        /// selected ones" export leaves out. Named, because the caller counts
        /// these apart from hidden and suppressed ones.</summary>
        public const string NotSelected = "not selected";

        public static List<OccurrenceAppearance> Resolve(IModelDoc2 model, Action<string> log,
                                                         bool includeHidden = false,
                                                         HashSet<string> keep = null)
        {
            var results = new List<OccurrenceAppearance>();
            if (!(model is IAssemblyDoc))
            {
                // A part on its own has no occurrences to resolve. The STEP
                // output of SolidWorks already holds the face, feature, body and
                // part styling correctly. See S0 section 2.2.
                return results;
            }

            // ── 1. The occurrence tree ──────────────────────────────────────
            var roots = BuildTree(model, results, log);
            log?.Invoke($"    {results.Count} occurrence(s), {roots.Count} at the top level");

            // ── 2. Exclusion, inherited down the tree ───────────────────────
            foreach (var r in roots) MarkExcluded(r, null, includeHidden, keep);
            int excluded = results.Count(o => !o.Exported);
            if (excluded > 0)
            {
                log?.Invoke($"    {excluded} occurrence(s) not exported "
                          + "(hidden, suppressed or envelope, or below one)");
                foreach (var o in results)
                    if (!o.Exported && (o.Parent == null || o.Parent.Exported))
                        log?.Invoke($"      excluded subtree {o.Path} ({o.ExcludedBecause}, "
                                  + $"{CountSubtree(o)} occurrence(s))");
            }

            // ── 3. Every override, from every assembly document ─────────────
            var attach = HarvestAttachments(model, results, log, out Attachment topDocLevel);

            // ── 4. Cascade: the attachment nearest the root wins ────────────
            foreach (var r in roots) Cascade(r, topDocLevel, attach);

            int overridden = results.Count(o => o.OverridesPartInternals && o.Exported);
            log?.Invoke($"    {overridden} exported occurrence(s) carry an override");
            foreach (var o in results)
                if (o.OverridesPartInternals && o.Exported
                    && (o.Parent == null || !o.Parent.OverridesPartInternals))
                    log?.Invoke($"      override root {o.Path} {o.Colour} [{o.WinningScope}] "
                              + $"covers {CountSubtree(o)} occurrence(s)");

            return results;
        }

        private static int CountSubtree(OccurrenceAppearance n)
            => 1 + n.Children.Sum(CountSubtree);

        // ── tree ────────────────────────────────────────────────────────────

        private static List<OccurrenceAppearance> BuildTree(IModelDoc2 model,
            List<OccurrenceAppearance> flat, Action<string> log)
        {
            var roots = new List<OccurrenceAppearance>();
            object[] top = null;
            try
            {
                var rootComp = model.ConfigurationManager?.ActiveConfiguration?.GetRootComponent3(true);
                top = rootComp?.GetChildren() as object[];
            }
            catch (Exception ex) { log?.Invoke("    root component unavailable: " + ex.Message); }
            if (top == null)
                top = (model as IAssemblyDoc)?.GetComponents(true) as object[] ?? new object[0];

            foreach (var o in top)
            {
                var node = BuildNode(o as IComponent2, null, flat, log);
                if (node != null) roots.Add(node);
            }
            return roots;
        }

        private static OccurrenceAppearance BuildNode(IComponent2 comp,
            OccurrenceAppearance parent, List<OccurrenceAppearance> flat, Action<string> log)
        {
            if (comp == null) return null;

            var node = new OccurrenceAppearance
            {
                Path = comp.Name2,
                ComponentName = comp.Name2,
                ReferencedConfiguration = SafeStr(() => comp.ReferencedConfiguration),
                DocName = DocBaseName(comp),
                Parent = parent,
                Comp = comp,
                Transform = SafeArrayD(() => comp.Transform2?.ArrayData as double[]),
            };
            flat.Add(node);

            foreach (var c in comp.GetChildren() as object[] ?? new object[0])
            {
                var child = BuildNode(c as IComponent2, node, flat, log);
                if (child != null) node.Children.Add(child);
            }
            return node;
        }

        /// <summary>The base file name of the referenced document. A virtual
        /// component has no path, so its name comes from the last path segment
        /// without the instance number.</summary>
        private static string DocBaseName(IComponent2 comp)
        {
            try
            {
                var p = comp.GetPathName();
                if (!string.IsNullOrEmpty(p))
                    return Path.GetFileNameWithoutExtension(p);
            }
            catch { }
            return LastSegmentWithoutInstance(comp.Name2);
        }

        internal static string LastSegmentWithoutInstance(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            int slash = path.LastIndexOf('/');
            string seg = slash >= 0 ? path.Substring(slash + 1) : path;
            int dash = seg.LastIndexOf('-');
            return dash > 0 ? seg.Substring(0, dash) : seg;
        }

        // ── exclusion ───────────────────────────────────────────────────────

        private static bool InKeep(IComponent2 comp, HashSet<string> keep)
        {
            try { return keep.Contains(comp.Name2 ?? ""); }
            catch { return true; }    // unreadable name: keep, never drop silently
        }

        private static void MarkExcluded(OccurrenceAppearance n, string inherited,
                                         bool includeHidden, HashSet<string> keep)
        {
            string own = ExclusionReason(n.Comp);
            if (includeHidden && own == "hidden") own = null;   // the export reveals them
            // The keep set already holds every ancestor of every selection,
            // so a component outside it has nothing selected below it either
            // and the whole branch goes.
            if (own == null && keep != null && !InKeep(n.Comp, keep))
                own = NotSelected;
            string effective = inherited ?? own;

            n.ExcludedBecause = effective;
            n.Exported = effective == null;
            foreach (var c in n.Children) MarkExcluded(c, effective, includeHidden, keep);
        }

        /// <summary>
        /// The reason that SolidWorks leaves this component out of the STEP
        /// file, from the component's OWN state. Callers must also inherit the
        /// reason of the parent: SolidWorks omits the whole branch below a
        /// hidden or suppressed component, and the children of that branch
        /// still report themselves visible.
        ///
        /// Hidden and suppressed were measured, not assumed. A silent SaveAs3
        /// drops both kinds. See evidence/S8/hidden.log.
        /// </summary>
        public static string ExclusionReason(IComponent2 comp)
        {
            try
            {
                int suppression = comp.GetSuppression2();
                if (suppression == (int)swComponentSuppressionState_e.swComponentSuppressed)
                    return "suppressed";
            }
            catch { }

            try
            {
                if (comp.Visible == (int)swComponentVisibilityState_e.swComponentHidden)
                    return "hidden";
            }
            catch { }

            try { if (comp.IsEnvelope()) return "envelope"; }
            catch { }

            return null;
        }

        // ── overrides ───────────────────────────────────────────────────────

        /// <summary>
        /// Collects every override attachment: path of the node it paints, its
        /// colour, and the depth of the document that holds it.
        ///
        /// The top document is harvested with the paths as they come. Each
        /// sub-assembly document is harvested once, and its attachments apply
        /// under every use of that document, with the use path as a prefix.
        /// </summary>
        private static Dictionary<string, Attachment> HarvestAttachments(IModelDoc2 model,
            List<OccurrenceAppearance> flat, Action<string> log, out Attachment topDocLevel)
        {
            var attach = new Dictionary<string, Attachment>(StringComparer.OrdinalIgnoreCase);
            topDocLevel = null;

            // The top document, rank 0.
            topDocLevel = HarvestDoc(model, new[] { "" }, 0, attach, log, "top document");

            // Each distinct sub-assembly document, under every one of its uses.
            var uses = new Dictionary<string, List<OccurrenceAppearance>>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var n in flat)
            {
                if (n.Children.Count == 0 || !n.Exported) continue;
                string p = SafeStr(() => n.Comp.GetPathName());
                if (string.IsNullOrEmpty(p)) continue;
                if (!uses.TryGetValue(p, out var list)) uses[p] = list = new List<OccurrenceAppearance>();
                list.Add(n);
            }

            foreach (var kv in uses)
            {
                var first = kv.Value[0];
                var doc = SafeObj(() => first.Comp.GetModelDoc2()) as IModelDoc2;
                if (doc == null)
                {
                    log?.Invoke($"    {Path.GetFileName(kv.Key)}: document not loaded "
                              + "(lightweight?); its own overrides are not visible");
                    continue;
                }
                if (!(doc is IAssemblyDoc)) continue;

                foreach (var use in kv.Value)
                {
                    int rank = use.Path.Count(ch => ch == '/') + 1;
                    var docLevel = HarvestDoc(doc, new[] { use.Path + "/" }, rank, attach, log,
                                              Path.GetFileName(kv.Key) + " under " + use.Path);
                    // A document level appearance inside a sub-assembly paints
                    // the whole use.
                    if (docLevel != null)
                        Attach(attach, use.Path, docLevel);
                }
            }
            return attach;
        }

        /// <summary>
        /// Reads the appearances of one document, for the ACTIVE display state.
        /// Returns the document level appearance if the document has one, and
        /// adds one attachment per component entity.
        /// </summary>
        private static Attachment HarvestDoc(IModelDoc2 doc, string[] prefixes, int rank,
            Dictionary<string, Attachment> attach, Action<string> log, string what)
        {
            Attachment docLevel = null;
            var ext = doc.Extension;
            if (ext == null) return null;

            var raw = SafeObj(() => ext.GetRenderMaterials2(
                (int)swDisplayStateOpts_e.swThisDisplayState, null)) as object[];
            if (raw == null) return null;

            foreach (var r in raw)
            {
                var rm = r as IRenderMaterial;
                if (rm == null) continue;

                var a = new Attachment
                {
                    Colour = Rgb.FromColorRef(rm.PrimaryColor),
                    Transparency = SafeDouble(() => rm.Transparency),
                    Rank = rank,
                    Source = what,
                };

                foreach (var e in SafeArray(() => rm.GetEntities() as object[]))
                {
                    if (e is IComponent2 c)
                    {
                        string rel = SafeStr(() => c.Name2);
                        if (string.IsNullOrEmpty(rel)) continue;
                        foreach (var prefix in prefixes)
                            Attach(attach, prefix + rel, a);
                        log?.Invoke($"    appearance {a.Colour} on {prefixes[0]}{rel} [{what}]");
                    }
                    else if (e is IModelDoc2)
                    {
                        docLevel = a;
                        log?.Invoke($"    appearance {a.Colour} on the whole document [{what}]");
                    }
                    // Face, feature and body appearances stay with the part.
                    // SolidWorks exports them correctly. See S0 section 2.2.
                }
            }
            return docLevel;
        }

        private static void Attach(Dictionary<string, Attachment> attach, string path, Attachment a)
        {
            if (attach.TryGetValue(path, out var existing) && existing.Rank <= a.Rank) return;
            attach[path] = a;
        }

        /// <summary>
        /// Walks down from the root. The first attachment on the way down wins
        /// the whole subtree, which is exactly "the highest level wins".
        /// </summary>
        private static void Cascade(OccurrenceAppearance n, Attachment inherited,
                                    Dictionary<string, Attachment> attach)
        {
            Attachment eff = inherited;
            if (eff == null) attach.TryGetValue(n.Path, out eff);

            if (eff != null)
            {
                n.Colour = eff.Colour;
                n.Transparency = eff.Transparency;
                n.WinningScope = eff.Source;
                n.OverridesPartInternals = true;
            }
            else
            {
                // Nothing above the part paints it. The SolidWorks export
                // already carries the internal styling of the part correctly.
                // Record the colour for the report, and mark it as needing no
                // repair.
                var vals = SafeArrayD(() => n.Comp.MaterialPropertyValues as double[]);
                if (vals != null && vals.Length >= 3 && vals[0] >= 0)
                {
                    n.Colour = new Rgb(vals[0], vals[1], vals[2]);
                    n.Transparency = vals.Length > 7 ? vals[7] : 0.0;
                }
                n.WinningScope = "part";
                n.OverridesPartInternals = false;
            }

            foreach (var c in n.Children) Cascade(c, eff, attach);
        }

        // ── COM guards ──────────────────────────────────────────────────────

        private static double SafeDouble(Func<double> f)
        { try { return f(); } catch { return 0.0; } }

        private static string SafeStr(Func<string> f)
        { try { return f(); } catch { return null; } }

        private static object SafeObj(Func<object> f)
        { try { return f(); } catch { return null; } }

        private static object[] SafeArray(Func<object[]> f)
        { try { return f() ?? new object[0]; } catch { return new object[0]; } }

        private static double[] SafeArrayD(Func<double[]> f)
        { try { return f(); } catch { return null; } }
    }
}
