using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
// Vendored from NEXT-STEP-SW (Peak.NextStep) @ b081285: the STEP appearance engine, merged into CADder Bridge.

namespace Peak.Cadder.Appearance
{
    /// <summary>
    /// Adds the appearance of each occurrence to the STEP output of SolidWorks.
    ///
    /// For a part used twice, SolidWorks writes one shared shape representation
    /// with one styled_item on it. Both occurrences must therefore show the same
    /// colour. See S0 section 2.3. This class adds one
    /// CONTEXT_DEPENDENT_OVER_RIDING_STYLED_ITEM for each occurrence, tied to
    /// the NEXT_ASSEMBLY_USAGE_OCCURRENCE of that occurrence. ISO 10303-46
    /// defines this entity for this purpose, and SolidWorks never writes it.
    ///
    /// This class does not change geometry.
    /// </summary>
    public sealed class StepRewriter
    {
        private readonly Part21 _step;
        private readonly Action<string> _log;

        public StepRewriter(string stepPath, Action<string> log)
        {
            _step = new Part21(stepPath);
            _log = log;
        }

        /// <summary>
        /// The appearance group that each product in the file carries. The
        /// numbers start again for each part. Group 0 keeps the shared geometry.
        /// Groups 1 and above are the copies.
        ///
        /// This map lets MaterialWriter give each group its own material name.
        /// STEP has no link between a material and an appearance. See
        /// MaterialWriter. But a consumer that builds one material for each
        /// material NAME builds one for each group when the names differ. Each
        /// material then takes the colour of the first product that uses it.
        /// </summary>
        public Dictionary<int, int> BucketIndexByProduct { get; } = new Dictionary<int, int>();

        /// <summary>One NEXT_ASSEMBLY_USAGE_OCCURRENCE: one use of a product
        /// inside its parent assembly.</summary>
        public sealed class OccurrenceRef
        {
            public int NauoId;
            /// <summary>The position of this occurrence in the frame of its
            /// PARENT assembly, in file units. STEP placements are parent
            /// relative. The root-relative position of a nested occurrence
            /// appears nowhere in the file.</summary>
            public double[] Translation;
            public int ParentPd;
            public int ChildPd;
            /// <summary>The PRODUCT name of the child. SolidWorks builds it
            /// from the file name, plus the configuration name for a
            /// non-default configuration.</summary>
            public string ProductName;
            /// <summary>The solids of the child product, when the child is a
            /// part. Empty for a sub-assembly.</summary>
            public List<int> TargetItems = new List<int>();
            /// <summary>The first solid, for the occurrence styling path.</summary>
            public int TargetItemId => TargetItems.Count > 0 ? TargetItems[0] : 0;
            public int BaseStyledItemId = -1;
        }

        public int RootPd { get; private set; } = -1;

        /// <summary>The uses inside each assembly product definition. A shared
        /// sub-assembly definition appears once here, whatever the number of
        /// uses above it.</summary>
        public Dictionary<int, List<OccurrenceRef>> ChildrenByParentPd { get; }
            = new Dictionary<int, List<OccurrenceRef>>();

        /// <summary>
        /// Finds every occurrence: its parent and child products, and its
        /// position within the parent. For the position, this walks the
        /// CONTEXT_DEPENDENT_SHAPE_REPRESENTATION of the occurrence to
        /// item_defined_transformation to axis2_placement_3d.
        /// </summary>
        public List<OccurrenceRef> FindOccurrences()
        {
            var result = new List<OccurrenceRef>();
            var byNauo = new Dictionary<int, OccurrenceRef>();

            foreach (var nauo in _step.ByType("NEXT_ASSEMBLY_USAGE_OCCURRENCE"))
            {
                var pds = _step.Refs(nauo)
                               .Where(r => _step.TypeOf(r) == "PRODUCT_DEFINITION").ToList();
                if (pds.Count < 2) continue;

                // NAUO(id, name, description, relating, related): the relating
                // product definition is the assembly, the related one the child.
                var occ = new OccurrenceRef
                {
                    NauoId = nauo,
                    ParentPd = pds[0],
                    ChildPd = pds[pds.Count - 1],
                };
                occ.ProductName = _step.NameOf(ProductOf(occ.ChildPd));
                result.Add(occ);
                byNauo[nauo] = occ;

                if (!ChildrenByParentPd.TryGetValue(occ.ParentPd, out var list))
                    ChildrenByParentPd[occ.ParentPd] = list = new List<OccurrenceRef>();
                list.Add(occ);
            }

            foreach (var cdsr in _step.ByType("CONTEXT_DEPENDENT_SHAPE_REPRESENTATION"))
            {
                int pds = _step.Refs(cdsr).FirstOrDefault(
                    r => _step.TypeOf(r) == "PRODUCT_DEFINITION_SHAPE");
                int nauo = pds == 0 ? 0 : _step.Refs(pds).FirstOrDefault(
                    r => _step.TypeOf(r) == "NEXT_ASSEMBLY_USAGE_OCCURRENCE");
                if (nauo != 0 && byNauo.TryGetValue(nauo, out var occ))
                    occ.Translation = ReadOccurrencePlacement(cdsr);
            }

            // The root is the assembly that no occurrence uses as a child.
            var children = new HashSet<int>(result.Select(o => o.ChildPd));
            RootPd = ChildrenByParentPd.Keys.FirstOrDefault(p => !children.Contains(p));
            if (RootPd == 0) RootPd = -1;

            // The solids of each part, resolved once per child product.
            var targets = new Dictionary<int, KeyValuePair<List<int>, int>>();
            foreach (var occ in result)
            {
                if (ChildrenByParentPd.ContainsKey(occ.ChildPd)) continue;  // sub-assembly
                if (!targets.TryGetValue(occ.ChildPd, out var t))
                    targets[occ.ChildPd] = t = ResolvePartTarget(occ.ChildPd);
                occ.TargetItems = t.Key;
                occ.BaseStyledItemId = t.Value;
            }

            result.Sort((a, b) => a.NauoId.CompareTo(b.NauoId));
            return result;
        }

        /// <summary>
        /// The solids of one part product, and the styled item on the first of
        /// them. The walk goes product_definition to product_definition_shape
        /// to shape_definition_representation to shape_representation, then
        /// over shape_representation_relationship to the B-rep.
        /// </summary>
        private KeyValuePair<List<int>, int> ResolvePartTarget(int childPd)
        {
            var solids = new List<int>();
            int styled = -1;

            int pds = FirstReferrer("PRODUCT_DEFINITION_SHAPE", childPd);
            int sdr = pds == 0 ? 0 : FirstReferrer("SHAPE_DEFINITION_REPRESENTATION", pds);
            int sr = sdr == 0 ? 0 : _step.Refs(sdr).FirstOrDefault(
                r => _step.TypeOf(r) == "SHAPE_REPRESENTATION"
                  || r == RepWithBrep(r));
            if (sr == 0) return new KeyValuePair<List<int>, int>(solids, styled);

            var brepReps = new List<int>();
            if (RepWithBrep(sr) == sr) brepReps.Add(sr);
            foreach (var srr in Referrers("SHAPE_REPRESENTATION_RELATIONSHIP", sr))
                foreach (var r in _step.Refs(srr))
                    if (r != sr && RepWithBrep(r) == r) brepReps.Add(r);

            foreach (var rep in brepReps)
                foreach (var item in _step.Refs(rep))
                {
                    var t = _step.TypeOf(item);
                    if (t == "MANIFOLD_SOLID_BREP" || t == "SHELL_BASED_SURFACE_MODEL"
                     || t == "BREP_WITH_VOIDS")
                        solids.Add(item);
                }

            if (solids.Count > 0)
            {
                var owners = StyledByItem();
                foreach (var s in solids)
                    if (owners.TryGetValue(s, out var st)) { styled = st; break; }
            }
            return new KeyValuePair<List<int>, int>(solids, styled);
        }

        private int RepWithBrep(int id)
        {
            var t = _step.TypeOf(id);
            return (t == "ADVANCED_BREP_SHAPE_REPRESENTATION"
                 || t == "MANIFOLD_SURFACE_SHAPE_REPRESENTATION") ? id : 0;
        }

        // ── reverse lookup, built once ──────────────────────────────────────

        private Dictionary<int, List<int>> _referrers;

        private void BuildReferrers(string type)
        {
            foreach (var id in _step.ByType(type))
                foreach (var r in _step.Refs(id))
                {
                    if (!_referrers.TryGetValue(r, out var list))
                        _referrers[r] = list = new List<int>();
                    list.Add(id);
                }
        }

        private List<int> Referrers(string type, int target)
        {
            if (_referrers == null)
            {
                _referrers = new Dictionary<int, List<int>>();
                BuildReferrers("PRODUCT_DEFINITION_SHAPE");
                BuildReferrers("SHAPE_DEFINITION_REPRESENTATION");
                BuildReferrers("SHAPE_REPRESENTATION_RELATIONSHIP");
            }
            if (!_referrers.TryGetValue(target, out var all)) return new List<int>();
            return all.Where(i => _step.TypeOf(i) == type).ToList();
        }

        private int FirstReferrer(string type, int target)
            => Referrers(type, target).FirstOrDefault();

        private Dictionary<int, int> _styledByItem;

        private Dictionary<int, int> StyledByItem()
        {
            if (_styledByItem != null) return _styledByItem;
            _styledByItem = new Dictionary<int, int>();
            foreach (var sid in _step.ByType("STYLED_ITEM"))
                foreach (var r in _step.Refs(sid))
                {
                    var t = _step.TypeOf(r);
                    if (t == "MANIFOLD_SOLID_BREP" || t == "ADVANCED_BREP_SHAPE_REPRESENTATION"
                        || t == "SHELL_BASED_SURFACE_MODEL" || t == "BREP_WITH_VOIDS")
                        if (!_styledByItem.ContainsKey(r)) _styledByItem[r] = sid;
                }
            return _styledByItem;
        }

        /// <summary>
        /// Reads the position of this occurrence in the frame of its parent.
        ///
        /// This code follows an exact chain. A breadth-first search does not
        /// work here:
        ///     CDSR -> (representation_relationship_with_transformation)
        ///          -> ITEM_DEFINED_TRANSFORMATION(name, desc, item_1, item_2)
        /// Here item_1 is the position in PARENT space, which identifies the
        /// occurrence. item_2 is the origin of the part itself, and every
        /// occurrence SHARES it. A breadth-first search finds the point of
        /// item_2 just as easily, and then reports every occurrence at the
        /// origin.
        /// </summary>
        private double[] ReadOccurrencePlacement(int cdsr)
        {
            foreach (var rel in _step.Refs(cdsr))
            {
                var relType = _step.TypeOf(rel) ?? "";
                if (relType.IndexOf("REPRESENTATION_RELATIONSHIP", StringComparison.Ordinal) < 0)
                    continue;

                foreach (var idt in _step.Refs(rel))
                {
                    if (!string.Equals(_step.TypeOf(idt), "ITEM_DEFINED_TRANSFORMATION",
                                       StringComparison.OrdinalIgnoreCase))
                        continue;

                    var items = _step.Refs(idt);           // [transform_item_1, transform_item_2]
                    if (items.Count < 1) continue;

                    var placement = items[0];
                    if (!string.Equals(_step.TypeOf(placement), "AXIS2_PLACEMENT_3D",
                                       StringComparison.OrdinalIgnoreCase))
                        continue;

                    var placementRefs = _step.Refs(placement);   // [location, axis, refDirection]
                    if (placementRefs.Count < 1) continue;

                    int location = placementRefs[0];
                    if (string.Equals(_step.TypeOf(location), "CARTESIAN_POINT",
                                      StringComparison.OrdinalIgnoreCase))
                    {
                        // In the unit of the parent's representation, which
                        // is whatever the top document was modelled in; the
                        // matcher compares millimetres.
                        var xyz = ReadTriple(location);
                        if (xyz == null) return null;
                        double mm = _step.PlacementUnitMm(rel, placement);
                        return new[] { xyz[0] * mm, xyz[1] * mm, xyz[2] * mm };
                    }
                }
            }
            return null;
        }

        private double[] ReadTriple(int cartesianPointId)
        {
            var args = _step.ArgsOf(cartesianPointId) ?? "";
            var nums = System.Text.RegularExpressions.Regex
                .Matches(args, @"-?\d+\.?\d*(?:[eE][-+]?\d+)?")
                .Cast<System.Text.RegularExpressions.Match>()
                .Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture))
                .ToArray();
            return nums.Length >= 3 ? new[] { nums[0], nums[1], nums[2] } : null;
        }

        /// <summary>
        /// Writes the colour of one occurrence. Returns the id of the new styled
        /// item.
        /// </summary>
        public int AddOccurrenceColour(OccurrenceRef occ, Rgb colour, double transparency)
        {
            int colourId = _step.NextId();
            _step.Append($"#{colourId}=COLOUR_RGB(''," +
                         $"{Part21.Num(colour.R)},{Part21.Num(colour.G)},{Part21.Num(colour.B)});");

            int fillId = _step.NextId();
            _step.Append($"#{fillId}=FILL_AREA_STYLE_COLOUR('',#{colourId});");
            int fasId = _step.NextId();
            _step.Append($"#{fasId}=FILL_AREA_STYLE('',(#{fillId}));");
            int ssfaId = _step.NextId();
            _step.Append($"#{ssfaId}=SURFACE_STYLE_FILL_AREA(#{fasId});");

            var sideElements = new List<string> { $"#{ssfaId}" };
            if (transparency > 1e-6)
            {
                int transpId = _step.NextId();
                _step.Append($"#{transpId}=SURFACE_STYLE_TRANSPARENT({Part21.Num(transparency)});");
                int rendId = _step.NextId();
                _step.Append($"#{rendId}=SURFACE_STYLE_RENDERING_WITH_PROPERTIES(" +
                             $".NORMAL_SHADING.,#{colourId},(#{transpId}));");
                sideElements.Add($"#{rendId}");
            }

            int sssId = _step.NextId();
            _step.Append($"#{sssId}=SURFACE_SIDE_STYLE('',({string.Join(",", sideElements)}));");
            int ssuId = _step.NextId();
            _step.Append($"#{ssuId}=SURFACE_STYLE_USAGE(.BOTH.,#{sssId});");
            int psaId = _step.NextId();
            _step.Append($"#{psaId}=PRESENTATION_STYLE_ASSIGNMENT((#{ssuId}));");

            int styledId = _step.NextId();
            if (occ.BaseStyledItemId > 0)
            {
                _step.Append($"#{styledId}=CONTEXT_DEPENDENT_OVER_RIDING_STYLED_ITEM(" +
                             $"'occurrence colour',(#{psaId}),#{occ.TargetItemId}," +
                             $"#{occ.BaseStyledItemId},(#{occ.NauoId}));");
            }
            else
            {
                // No base styled item to override: a plain styled item bound to
                // the item still carries the colour for readers that ignore
                // occurrence context.
                _step.Append($"#{styledId}=STYLED_ITEM('occurrence colour'," +
                             $"(#{psaId}),#{occ.TargetItemId});");
            }
            return styledId;
        }

        /// <summary>
        /// Applies the resolved appearance to every matched occurrence.
        ///
        /// Only the leaves take styling. An override on a sub-assembly reaches
        /// this method already cascaded onto every part below it by
        /// AppearanceLadder.
        ///
        /// deInstance=false keeps the instancing of SolidWorks and writes
        /// CONTEXT_DEPENDENT_OVER_RIDING_STYLED_ITEM. That output is compact and
        /// correct under ISO 10303-46. But Fusion 360 and CADder ignore it.
        /// This was measured. See FINDINGS.md 4.3.
        ///
        /// deInstance=true separates the occurrences by APPEARANCE, not one by
        /// one. For each part, this code groups the occurrences by the colour
        /// they resolved to. Each group gets one copy with a plain STYLED_ITEM,
        /// which every consumer reads. Two occurrences of a part with the same
        /// colour therefore stay true instances of one product. Only occurrences
        /// that look different cost extra geometry.
        /// </summary>
        public int ApplyOccurrenceColours(
            List<KeyValuePair<OccurrenceAppearance, OccurrenceRef>> pairs, bool deInstance)
        {
            // A shared sub-assembly definition holds ONE set of occurrence
            // entities, however many times the definition is used. Several
            // component paths therefore land on the same NAUO. When they agree
            // on the appearance, one write serves them all. When they disagree,
            // the definition is copied first, so that each divergent group of
            // uses styles its own occurrence entities.
            int splits = SplitSharedDefinitions(pairs);
            if (splits > 0)
                _log?.Invoke($"    {splits} shared sub-assembly definition(s) copied, because "
                           + "their uses need different colours");

            var leaves = pairs.Where(p => p.Value != null && p.Key.Children.Count == 0).ToList();

            var byNauo = new List<KeyValuePair<OccurrenceAppearance, OccurrenceRef>>();
            foreach (var group in leaves.GroupBy(p => p.Value.NauoId))
            {
                var signatures = group
                    .Select(p => p.Key.OverridesPartInternals ? AppearanceKey(p.Key) : "original")
                    .Distinct().ToList();
                if (signatures.Count > 1)
                {
                    // The split above makes this unreachable. If it fires, the
                    // occurrences keep the SolidWorks colour: a missing colour
                    // is honest, a wrong one is not.
                    _log?.Invoke($"    CONFLICT: {string.Join(" / ", group.Select(p => p.Key.Path))} "
                               + "share one sub-assembly definition but need different colours; "
                               + "they keep the SolidWorks colour");
                    continue;
                }
                var lead = group.FirstOrDefault(p => p.Key.OverridesPartInternals);
                byNauo.Add(lead.Key != null ? lead : group.First());
            }

            int applied = 0;
            var matched = byNauo;
            if (!matched.Any(p => p.Key.OverridesPartInternals)) return 0;

            if (!deInstance)
            {
                foreach (var p in matched.Where(p => p.Key.OverridesPartInternals))
                {
                    AddOccurrenceColour(p.Value, p.Key.Colour, p.Key.Transparency);
                    applied++;
                }
                return applied;
            }

            var di = new DeInstancer(_step, _log);

            // Every occurrence entity in the file that uses each part,
            // matched or not.
            var usesOfPart = new Dictionary<int, List<int>>();
            foreach (var nauo in _step.ByType("NEXT_ASSEMBLY_USAGE_OCCURRENCE"))
            {
                int child = _step.Refs(nauo).LastOrDefault(r => _step.TypeOf(r) == "PRODUCT_DEFINITION");
                if (child == 0) continue;
                if (!usesOfPart.TryGetValue(child, out var list))
                    usesOfPart[child] = list = new List<int>();
                list.Add(nauo);
            }

            foreach (var partGroup in matched.GroupBy(p => p.Value.ChildPd))
            {
                // An occurrence with no override must keep the part exactly as
                // SolidWorks wrote it, including the colour of each face. If any
                // such occurrence exists, the shared geometry belongs to it, and
                // EVERY overridden group must be a copy. To recolour the shared
                // geometry would then repaint occurrences that have no
                // override.
                //
                // That includes an occurrence that the matcher could not
                // match, or that a conflict above left out. The log says it
                // keeps the SolidWorks colour, and it still uses the original
                // part in the file. The matched leaves alone do not show it.
                var overridden = new HashSet<int>(partGroup
                    .Where(p => p.Key.OverridesPartInternals).Select(p => p.Value.NauoId));
                bool sharedGeometryTaken = partGroup.Any(p => !p.Key.OverridesPartInternals)
                    || (usesOfPart.TryGetValue(partGroup.Key, out var allUses)
                        && allUses.Any(n => !overridden.Contains(n)));

                var buckets = partGroup
                    .Where(p => p.Key.OverridesPartInternals)
                    .GroupBy(p => AppearanceKey(p.Key))
                    .ToList();
                if (buckets.Count == 0) continue;

                int originalProduct = ProductOf(partGroup.Key);
                int bucketIndex = sharedGeometryTaken ? 1 : 0;
                if (originalProduct > 0 && !BucketIndexByProduct.ContainsKey(originalProduct))
                    BucketIndexByProduct[originalProduct] = 0;

                foreach (var bucket in buckets)
                {
                    var lead = bucket.First();
                    int n = bucket.Count();
                    string who = string.Join(", ", bucket.Select(b => b.Key.Path));

                    if (!sharedGeometryTaken)
                    {
                        sharedGeometryTaken = true;
                        int repointed = di.RecolourPart(lead.Value, lead.Key.Colour, lead.Key.Transparency);
                        if (repointed == 0 && lead.Value.TargetItems.Count > 0)
                            foreach (var solid in lead.Value.TargetItems)
                                di.AddPlainStyle(solid, lead.Key.Colour, lead.Key.Transparency);
                        // The shared geometry is group 0, recorded above. It
                        // still uses an index. The next group is a copy and must
                        // have the number 1, not 0. With the number 0 its
                        // material name matches this one, and a reader merges
                        // the two groups again.
                        bucketIndex++;

                        _log?.Invoke($"    {who}: {n} occurrence(s) keep the shared geometry, " +
                                     $"recoloured {repointed} styled item(s) to {lead.Key.Colour}");
                        applied += n;
                        continue;
                    }

                    var copy = di.ClonePart(lead.Value);
                    if (copy == null || copy.SolidIds.Count == 0)
                    {
                        _log?.Invoke($"    {who}: de-instancing FAILED, falling back to " +
                                     "occurrence styling");
                        foreach (var p in bucket)
                        {
                            AddOccurrenceColour(p.Value, p.Key.Colour, p.Key.Transparency);
                            applied++;
                        }
                        continue;
                    }

                    foreach (var solid in copy.SolidIds)
                        di.AddPlainStyle(solid, lead.Key.Colour, lead.Key.Transparency);
                    foreach (var p in bucket) di.PointOccurrenceAt(p.Value, copy);

                    if (originalProduct > 0 && copy.Map.TryGetValue(originalProduct, out int clonedProduct))
                        BucketIndexByProduct[clonedProduct] = bucketIndex;
                    bucketIndex++;

                    _log?.Invoke($"    {who}: {n} occurrence(s) share one copy " +
                                 $"({copy.EntityCount} entities), {copy.SolidIds.Count} solid(s) " +
                                 $"= {lead.Key.Colour}");
                    applied += n;
                }
            }
            return applied;
        }

        /// <summary>
        /// Copies a sub-assembly definition for each group of uses that must
        /// not look like the others. Mutates the matched pairs so that the
        /// repointed uses and their direct children reference the copies.
        /// Returns the number of copies made.
        ///
        /// Definitions split parents before children, in the order of the
        /// definition tree. When a parent splits, the uses of a definition
        /// nested inside it land on distinct occurrence entities, one set per
        /// parent copy. A divergence deeper down can then split the nested
        /// definition on its own, and the recursion needs no special case.
        ///
        /// The depth of the uses is not that order. A definition N used at
        /// the top level and also inside a container P has a use as shallow
        /// as any use of P. When N split first, the occurrence entity inside
        /// P that serves every use of P moved to the copy of N, and every use
        /// of P changed colour with it.
        /// </summary>
        private int SplitSharedDefinitions(
            List<KeyValuePair<OccurrenceAppearance, OccurrenceRef>> pairs)
        {
            var indexOf = new Dictionary<OccurrenceAppearance, int>();
            for (int i = 0; i < pairs.Count; i++)
                if (pairs[i].Value != null) indexOf[pairs[i].Key] = i;

            // The uses of each assembly definition, over the whole matched tree.
            var usesByDef = new Dictionary<int, List<OccurrenceAppearance>>();
            foreach (var p in pairs)
            {
                if (p.Value == null) continue;
                if (!ChildrenByParentPd.ContainsKey(p.Value.ChildPd)) continue;   // a part
                if (!usesByDef.TryGetValue(p.Value.ChildPd, out var list))
                    usesByDef[p.Value.ChildPd] = list = new List<OccurrenceAppearance>();
                list.Add(p.Key);
            }

            var rank = DefinitionOrder();
            var ordered = usesByDef.Where(kv => kv.Value.Count > 1)
                .OrderBy(kv => rank.TryGetValue(kv.Key, out var r) ? r : int.MaxValue)
                .ThenBy(kv => kv.Value.Min(u => Depth(u.Path)))
                .ToList();

            var di = new DeInstancer(_step, _log);
            int clones = 0;

            foreach (var def in ordered)
            {
                var groups = def.Value.GroupBy(SubtreeSignature).ToList();
                if (groups.Count <= 1) continue;

                var childRefs = ChildrenByParentPd[def.Key];
                string defName = _step.NameOf(ProductOf(def.Key)) ?? ("#" + def.Key);

                // The groups that sit on each occurrence entity.
                var groupsOfNauo = new Dictionary<int, HashSet<int>>();
                for (int g = 0; g < groups.Count; g++)
                    foreach (var use in groups[g])
                    {
                        int n = pairs[indexOf[use]].Value.NauoId;
                        if (!groupsOfNauo.TryGetValue(n, out var set))
                            groupsOfNauo[n] = set = new HashSet<int>();
                        set.Add(g);
                    }

                // The largest group keeps the original definition.
                foreach (var group in groups.OrderByDescending(g => g.Count()).Skip(1))
                {
                    // A use that shares its occurrence entity with a use of
                    // another group cannot move alone. This happens when the
                    // container above could not be copied. To repoint the
                    // entity would change the other group too, so both keep
                    // the SolidWorks colour, and the conflict check below
                    // reports them.
                    if (group.Any(u => groupsOfNauo[pairs[indexOf[u]].Value.NauoId].Count > 1))
                    {
                        _log?.Invoke($"    {defName}: {group.Count()} use(s) share an occurrence "
                                   + "entity with uses that need other colours; not split");
                        continue;
                    }

                    var map = di.CloneAssemblyStructure(def.Key,
                        childRefs.Select(c => c.NauoId).ToList());
                    if (map == null)
                    {
                        _log?.Invoke($"    {defName}: clone failed; "
                                   + $"{group.Count()} use(s) keep the SolidWorks colour");
                        continue;
                    }
                    clones++;

                    // The occurrence records of the copy mirror the originals.
                    // The child definitions and the geometry stay shared.
                    var cloneRefByNauo = new Dictionary<int, OccurrenceRef>();
                    var clonedKids = new List<OccurrenceRef>();
                    foreach (var c in childRefs)
                    {
                        var nc = new OccurrenceRef
                        {
                            NauoId = map[c.NauoId],
                            ParentPd = map[def.Key],
                            ChildPd = c.ChildPd,
                            ProductName = c.ProductName,
                            Translation = c.Translation,
                            TargetItems = c.TargetItems,
                            BaseStyledItemId = c.BaseStyledItemId,
                        };
                        cloneRefByNauo[c.NauoId] = nc;
                        clonedKids.Add(nc);
                    }
                    ChildrenByParentPd[map[def.Key]] = clonedKids;

                    // Point each use of this group at the copy. Two paths
                    // through one shared parent share one use entity, so the
                    // rewiring runs once per entity. The check above makes
                    // sure that no other group sits on the entity, so its
                    // record in ChildrenByParentPd can follow it.
                    var repointed = new HashSet<int>();
                    foreach (var use in group)
                    {
                        int idx = indexOf[use];
                        var occ = pairs[idx].Value;
                        if (repointed.Add(occ.NauoId))
                        {
                            di.PointOccurrenceAt(occ, new DeInstancer.PartCopy { Map = map });
                            if (ChildrenByParentPd.TryGetValue(occ.ParentPd, out var siblings))
                                foreach (var s in siblings)
                                    if (s.NauoId == occ.NauoId) s.ChildPd = map[def.Key];
                        }
                        pairs[idx] = Pair(use, new OccurrenceRef
                        {
                            NauoId = occ.NauoId,
                            ParentPd = occ.ParentPd,
                            ChildPd = map[def.Key],
                            ProductName = occ.ProductName,
                            Translation = occ.Translation,
                        });
                        RemapDescendants(use, cloneRefByNauo, pairs, indexOf);
                    }

                    _log?.Invoke($"    split {defName}: {group.Count()} use(s) get their own "
                               + $"copy of the structure ({map.Count} entities), because "
                               + "their insides need different colours");
                }
            }
            return clones;
        }

        /// <summary>Repoints the matched pairs of the direct children under one
        /// use at the cloned occurrence entities. Deeper descendants stay: their
        /// entities live in nested definitions, which stay shared until their
        /// own uses diverge.</summary>
        private static void RemapDescendants(OccurrenceAppearance node,
            Dictionary<int, OccurrenceRef> cloneRefByNauo,
            List<KeyValuePair<OccurrenceAppearance, OccurrenceRef>> pairs,
            Dictionary<OccurrenceAppearance, int> indexOf)
        {
            foreach (var child in node.Children)
            {
                if (indexOf.TryGetValue(child, out var i))
                {
                    var r = pairs[i].Value;
                    if (r != null && cloneRefByNauo.TryGetValue(r.NauoId, out var nc))
                        pairs[i] = Pair(child, nc);
                }
                RemapDescendants(child, cloneRefByNauo, pairs, indexOf);
            }
        }

        private static KeyValuePair<OccurrenceAppearance, OccurrenceRef> Pair(
            OccurrenceAppearance a, OccurrenceRef b)
            => new KeyValuePair<OccurrenceAppearance, OccurrenceRef>(a, b);

        /// <summary>
        /// A rank for each assembly definition in which a container always
        /// comes before every definition nested in it: the reverse
        /// post-order of a walk down ChildrenByParentPd.
        /// </summary>
        private Dictionary<int, int> DefinitionOrder()
        {
            var postOrder = new List<int>();
            var seen = new HashSet<int>();

            void Visit(int pd)
            {
                if (!seen.Add(pd)) return;
                if (ChildrenByParentPd.TryGetValue(pd, out var kids))
                    foreach (var k in kids)
                        if (ChildrenByParentPd.ContainsKey(k.ChildPd)) Visit(k.ChildPd);
                postOrder.Add(pd);
            }

            if (RootPd > 0) Visit(RootPd);
            foreach (var pd in ChildrenByParentPd.Keys.ToList()) Visit(pd);

            var rank = new Dictionary<int, int>();
            for (int i = 0; i < postOrder.Count; i++)
                rank[postOrder[i]] = postOrder.Count - 1 - i;
            return rank;
        }

        private static int Depth(string path)
        {
            int n = 0;
            foreach (var c in path ?? "") if (c == '/') n++;
            return n;
        }

        /// <summary>
        /// What this use will look like, as a string. Two uses with equal
        /// signatures can share one definition. The signature covers every
        /// exported leaf below the use: its path relative to the use, and the
        /// colour it resolved to, or "original" for a leaf with no override.
        /// </summary>
        private static string SubtreeSignature(OccurrenceAppearance use)
        {
            var entries = new List<string>();
            CollectSignature(use, use.Path?.Length ?? 0, entries);
            entries.Sort(StringComparer.Ordinal);
            return string.Join("|", entries);
        }

        private static void CollectSignature(OccurrenceAppearance n, int prefixLen,
                                             List<string> entries)
        {
            if (!n.Exported) return;
            if (n.Children.Count == 0)
            {
                string rel = n.Path != null && n.Path.Length > prefixLen
                    ? n.Path.Substring(prefixLen) : n.Path ?? "";
                entries.Add(rel + "="
                    + (n.OverridesPartInternals ? AppearanceKey(n) : "original"));
                return;
            }
            foreach (var c in n.Children) CollectSignature(c, prefixLen, entries);
        }

        /// <summary>
        /// The key that makes two occurrences equal. The numbers are rounded.
        /// Colours from different SolidWorks scopes can differ in the last bit
        /// of a double, and they must still fall in the same group.
        /// </summary>
        private static string AppearanceKey(OccurrenceAppearance a)
            => string.Format(CultureInfo.InvariantCulture, "{0:F3}/{1:F3}/{2:F3}/{3:F3}",
                             a.Colour.R, a.Colour.G, a.Colour.B, a.Transparency);

        /// <summary>Walks PRODUCT_DEFINITION to formation to PRODUCT.</summary>
        private int ProductOf(int productDefinition)
        {
            if (productDefinition <= 0) return -1;
            foreach (var f in _step.Refs(productDefinition))
            {
                var t = _step.TypeOf(f) ?? "";
                if (!t.StartsWith("PRODUCT_DEFINITION_FORMATION", StringComparison.Ordinal)) continue;
                foreach (var p in _step.Refs(f))
                    if (_step.TypeOf(p) == "PRODUCT") return p;
            }
            return -1;
        }

        public void Save(string path)
        {
            _step.Save(path);
            _log?.Invoke($"    wrote {path}");
        }

        public Part21 Document => _step;
    }
}
