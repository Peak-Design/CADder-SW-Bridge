using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
// Vendored from NEXT-STEP-SW (Peak.NextStep) @ b081285: the STEP appearance engine, merged into CADder Bridge.

namespace Peak.Cadder.Appearance
{
    /// <summary>
    /// De-instance mode. Each overridden occurrence gets its own product, its
    /// own geometry and a plain STYLED_ITEM.
    ///
    /// Why this class exists. The instanced form,
    /// CONTEXT_DEPENDENT_OVER_RIDING_STYLED_ITEM, is correct under ISO 10303-46.
    /// But Fusion 360 and CADder both ignore it and show the colour of the
    /// referenced part. This was measured. De-instancing reduces the problem to
    /// a plain colour on each solid, which every consumer reads. The cost is
    /// duplicated geometry.
    ///
    /// This code makes one copy for each DIFFERENT APPEARANCE, not one for each
    /// occurrence. Two occurrences of a part with the same colour stay true
    /// instances of one product. They need separate products only when they look
    /// different. To split them in every case would duplicate geometry for no
    /// visible gain, and would take the instancing away from the consumer.
    /// </summary>
    public sealed class DeInstancer
    {
        private readonly Part21 _step;
        private readonly Action<string> _log;

        public DeInstancer(Part21 step, Action<string> log)
        {
            _step = step;
            _log = log;
        }

        /// <summary>One copy of a part, ready for a colour and for sharing.</summary>
        public sealed class PartCopy
        {
            /// <summary>Maps an original id to a copied id, to point the
            /// occurrences at the copy.</summary>
            public Dictionary<int, int> Map;
            /// <summary>The copied solids that take the colour. A part with
            /// several bodies has several.</summary>
            public List<int> SolidIds = new List<int>();
            public int EntityCount;
        }

        /// <summary>
        /// The whole file shares the contexts and the units. A copy of them
        /// duplicates the unit system, and some readers then reject the file.
        /// </summary>
        private static bool IsShared(string type)
        {
            if (type == null) return true;
            if (type.IndexOf("REPRESENTATION_CONTEXT", StringComparison.Ordinal) >= 0) return true;
            foreach (var p in new[]
            {
                "SI_UNIT", "CONVERSION_BASED_UNIT", "NAMED_UNIT", "DIMENSIONAL_EXPONENTS",
                "UNCERTAINTY_MEASURE_WITH_UNIT", "LENGTH_MEASURE_WITH_UNIT",
                "PLANE_ANGLE_MEASURE_WITH_UNIT", "SOLID_ANGLE_MEASURE_WITH_UNIT",
                "APPLICATION_CONTEXT", "APPLICATION_PROTOCOL_DEFINITION",
                "PRODUCT_CONTEXT", "PRODUCT_DEFINITION_CONTEXT", "MECHANICAL_CONTEXT",
                "DESIGN_CONTEXT", "PRODUCT_RELATED_PRODUCT_CATEGORY",
            })
                if (type.StartsWith(p, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>Every entity below the roots that the file does not
        /// share.</summary>
        private HashSet<int> Closure(IEnumerable<int> roots)
        {
            var set = new HashSet<int>();
            var stack = new Stack<int>(roots);
            while (stack.Count > 0)
            {
                int id = stack.Pop();
                if (!_step.Entities.ContainsKey(id)) continue;
                if (IsShared(_step.TypeOf(id))) continue;
                if (!set.Add(id)) continue;
                foreach (var r in _step.Refs(id)) stack.Push(r);
            }
            return set;
        }

        /// <summary>Copies a set of entities and points the internal references
        /// at the copies.</summary>
        private Dictionary<int, int> CloneAll(HashSet<int> ids)
        {
            var map = new Dictionary<int, int>();
            foreach (var id in ids) map[id] = _step.NextId();

            foreach (var id in ids)
            {
                string type = _step.TypeOf(id);
                string args = _step.ArgsOf(id);
                string remapped = Remap(args, map);
                // A complex instance already holds its own type list in args.
                string line = type.StartsWith("COMPLEX:", StringComparison.Ordinal)
                    ? $"#{map[id]}={remapped};"
                    : $"#{map[id]}={type}{remapped};";
                _step.Append(line);
            }
            return map;
        }

        private static string Remap(string args, Dictionary<int, int> map)
            => Regex.Replace(args, @"#(\d+)", m =>
            {
                int id = int.Parse(m.Groups[1].Value);
                return map.TryGetValue(id, out var n) ? "#" + n : m.Value;
            });

        /// <summary>Writes one existing entity again, with new references.</summary>
        private void RewireEntity(int id, Dictionary<int, int> map)
        {
            string type = _step.TypeOf(id);
            string args = Remap(_step.ArgsOf(id), map);
            string line = type.StartsWith("COMPLEX:", StringComparison.Ordinal)
                ? $"#{id}={args};"
                : $"#{id}={type}{args};";
            _step.Replace(id, line);
        }

        /// <summary>
        /// Makes one new copy of the part that this occurrence references.
        ///
        /// The copy and the reference update are separate calls on purpose.
        /// Every occurrence with the same appearance shares one copy. This code
        /// therefore makes the copy once and points many occurrences at it.
        /// </summary>
        public PartCopy ClonePart(StepRewriter.OccurrenceRef occ)
        {
            // The second product_definition of the NAUO is the part.
            var nauoRefs = _step.Refs(occ.NauoId);
            int partPd = nauoRefs.LastOrDefault(r => _step.TypeOf(r) == "PRODUCT_DEFINITION");
            if (partPd == 0) { _log?.Invoke($"    NAUO #{occ.NauoId}: no part product_definition"); return null; }

            // Walk product_definition_shape to shape_definition_representation
            // to shape_representation.
            int pds = _step.ByType("PRODUCT_DEFINITION_SHAPE")
                           .FirstOrDefault(p => _step.Refs(p).Contains(partPd));
            int sdr = pds == 0 ? 0 : _step.ByType("SHAPE_DEFINITION_REPRESENTATION")
                           .FirstOrDefault(s => _step.Refs(s).Contains(pds));
            int partSr = sdr == 0 ? 0 : _step.Refs(sdr)
                           .FirstOrDefault(r => _step.TypeOf(r) == "SHAPE_REPRESENTATION");
            if (partSr == 0) { _log?.Invoke($"    NAUO #{occ.NauoId}: no part shape_representation"); return null; }

            // shape_representation_relationship joins the SR of the part to
            // its B-rep.
            int srr = _step.ByType("SHAPE_REPRESENTATION_RELATIONSHIP")
                           .FirstOrDefault(s => _step.Refs(s).Contains(partSr));
            int absr = srr == 0 ? 0 : _step.Refs(srr).FirstOrDefault(
                           r => _step.TypeOf(r) == "ADVANCED_BREP_SHAPE_REPRESENTATION"
                             || _step.TypeOf(r) == "MANIFOLD_SURFACE_SHAPE_REPRESENTATION");

            // The product chain runs product_definition, formation, product.
            var chain = new List<int> { partPd, pds, sdr, partSr };
            if (srr != 0) chain.Add(srr);
            if (absr != 0) chain.Add(absr);

            var roots = new List<int> { partSr };
            if (absr != 0) roots.Add(absr);
            var toClone = Closure(roots);
            foreach (var c in chain) if (!IsShared(_step.TypeOf(c))) toClone.Add(c);
            // Closure already collects the formation and the product below
            // product_definition. But the chain members above can add new
            // references.
            foreach (var extra in Closure(chain)) toClone.Add(extra);

            var map = CloneAll(toClone);

            // The copied solids take the colour of the copy.
            var solids = toClone
                .Where(i => _step.TypeOf(i) == "MANIFOLD_SOLID_BREP"
                         || _step.TypeOf(i) == "SHELL_BASED_SURFACE_MODEL"
                         || _step.TypeOf(i) == "BREP_WITH_VOIDS")
                .Select(i => map[i]).ToList();
            return new PartCopy
            {
                Map = map,
                SolidIds = solids,
                EntityCount = toClone.Count,
            };
        }

        /// <summary>
        /// Clones the STRUCTURE of one sub-assembly definition: the product
        /// chain, the shape representation with the child placements, and the
        /// occurrence entities of its direct children. The children keep
        /// pointing at the same child definitions. No geometry is copied here.
        ///
        /// This exists for one case: two uses of a shared sub-assembly whose
        /// insides must show different colours. One definition cannot show
        /// both. After the clone, each divergent group of uses has its own
        /// definition, and the ordinary per-part logic colours their leaves
        /// independently.
        /// </summary>
        public Dictionary<int, int> CloneAssemblyStructure(int defPd, IEnumerable<int> childNauos)
        {
            int pds = _step.ByType("PRODUCT_DEFINITION_SHAPE")
                           .FirstOrDefault(p => _step.Refs(p).Contains(defPd));
            int sdr = pds == 0 ? 0 : _step.ByType("SHAPE_DEFINITION_REPRESENTATION")
                           .FirstOrDefault(s => _step.Refs(s).Contains(pds));
            int sr = sdr == 0 ? 0 : _step.Refs(sdr)
                           .FirstOrDefault(r => _step.TypeOf(r) == "SHAPE_REPRESENTATION");
            if (sr == 0)
            {
                _log?.Invoke($"    definition #{defPd}: no shape representation, cannot clone");
                return null;
            }

            // The representation closure holds only the child placements: an
            // assembly shape representation carries axis placements, no B-rep.
            var toClone = Closure(new[] { sr });
            foreach (var extra in Closure(new[] { defPd })) toClone.Add(extra);
            toClone.Add(defPd);
            toClone.Add(pds);
            toClone.Add(sdr);

            // The occurrence entities of each direct child, picked one by one.
            // A blind closure from a NAUO would walk into the child definition
            // and copy its geometry, which must stay shared.
            foreach (var nauo in childNauos)
            {
                toClone.Add(nauo);
                int pdsN = _step.ByType("PRODUCT_DEFINITION_SHAPE")
                                .FirstOrDefault(p => _step.Refs(p).Contains(nauo));
                if (pdsN == 0) continue;
                toClone.Add(pdsN);
                int cdsr = _step.ByType("CONTEXT_DEPENDENT_SHAPE_REPRESENTATION")
                                .FirstOrDefault(cs => _step.Refs(cs).Contains(pdsN));
                if (cdsr == 0) continue;
                toClone.Add(cdsr);
                foreach (var rel in _step.Refs(cdsr))
                {
                    var t = _step.TypeOf(rel) ?? "";
                    if (t.IndexOf("REPRESENTATION_RELATIONSHIP", StringComparison.Ordinal) < 0)
                        continue;
                    toClone.Add(rel);
                    foreach (var idt in _step.Refs(rel))
                        if (_step.TypeOf(idt) == "ITEM_DEFINED_TRANSFORMATION")
                            toClone.Add(idt);
                }
            }

            // References into the set follow the copies. References out of the
            // set, such as the child product definitions and their shape
            // representations, stay on the originals.
            return CloneAll(toClone);
        }

        /// <summary>
        /// Points one occurrence at a copy. This changes the part product
        /// definition of its NAUO, and the reference from the placement chain to
        /// the shape representation of the part. You can call this for several
        /// occurrences with the same copy. Each call changes only its own
        /// entities.
        /// </summary>
        public void PointOccurrenceAt(StepRewriter.OccurrenceRef occ, PartCopy copy)
        {
            var map = copy.Map;
            RewireEntity(occ.NauoId, map);

            foreach (var cdsr in _step.ByType("CONTEXT_DEPENDENT_SHAPE_REPRESENTATION"))
            {
                bool mine = _step.Refs(cdsr).Any(r =>
                    _step.TypeOf(r) == "PRODUCT_DEFINITION_SHAPE"
                    && _step.Refs(r).Contains(occ.NauoId));
                if (!mine) continue;

                foreach (var rel in _step.Refs(cdsr))
                {
                    var t = _step.TypeOf(rel) ?? "";
                    if (t.IndexOf("REPRESENTATION_RELATIONSHIP", StringComparison.Ordinal) >= 0)
                    {
                        RewireEntity(rel, map);
                        foreach (var idt in _step.Refs(rel))
                            if (_step.TypeOf(idt) == "ITEM_DEFINED_TRANSFORMATION")
                                RewireEntity(idt, map);
                    }
                }
            }
        }

        /// <summary>
        /// Recolours EVERY styled item that affects the part behind this
        /// occurrence. This applies to the occurrence that keeps the original
        /// geometry.
        ///
        /// SolidWorks writes two styled items for each part. One sits on the
        /// MANIFOLD_SOLID_BREP and one sits on the
        /// ADVANCED_BREP_SHAPE_REPRESENTATION. The OCCT reader lets the one at
        /// representation level win. To recolour only the solid therefore leaves
        /// the old colour in view. This is why C4_component_override_2 came back
        /// orange instead of green.
        ///
        /// Returns the number of styled items that this code changed.
        /// </summary>
        public int RecolourPart(StepRewriter.OccurrenceRef occ, Rgb colour, double transparency)
        {
            var geometry = PartGeometryOf(occ.NauoId);
            if (geometry.Count == 0)
            {
                _log?.Invoke($"    NAUO #{occ.NauoId}: no part geometry found to recolour");
                return 0;
            }

            var mine = new HashSet<int>(_step.ByType("STYLED_ITEM")
                            .Where(s => _step.Refs(s).Any(geometry.Contains)));
            if (mine.Count == 0) return 0;

            // Write the existing COLOUR_RGB again, in place. Do not build a
            // new style chain and point at that. A new chain leaves the old
            // chain in the file. Nothing references it, but the dead
            // FILL_AREA_STYLE_COLOUR entities still hold the ORIGINAL colour.
            // That is untidy, and it gives a reader a way to show the colour
            // that this code means to replace.
            //
            // The transparency is the exception. The chain that SolidWorks
            // wrote holds a SURFACE_STYLE_TRANSPARENT or none, and a colour
            // written in place keeps it. A glass override then came out
            // opaque, and an opaque override on glass stayed clear, while the
            // copies of the same part in other groups were correct. When the
            // chain does not hold the transparency of the override, the item
            // gets a new chain. The old colour is still written in place, so
            // the dead chain does not hold the original colour either.
            int count = 0;
            int newChain = 0;
            foreach (var styled in mine)
            {
                var colourIds = ColoursUnder(styled);
                bool shared = colourIds.Any(c => IsReferencedOutside(c, mine));

                if (!shared && colourIds.Count > 0)
                {
                    foreach (var c in colourIds)
                        _step.Replace(c, $"#{c}=COLOUR_RGB('',{Part21.Num(colour.R)}," +
                                         $"{Part21.Num(colour.G)},{Part21.Num(colour.B)});");
                    if (!HoldsTransparency(styled, transparency))
                    {
                        if (newChain == 0) newChain = BuildStyleChain(colour, transparency);
                        PointStyledItemAt(styled, newChain);
                    }
                    count++;
                }
                else
                {
                    // Another styled item shares this colour and must not
                    // change. Point this item only at a new chain.
                    if (newChain == 0) newChain = BuildStyleChain(colour, transparency);
                    PointStyledItemAt(styled, newChain);
                    count++;
                }
            }
            return count;
        }

        /// <summary>True when the style chain of this item already gives the
        /// transparency: the same SURFACE_STYLE_TRANSPARENT value, or none
        /// for an opaque override.</summary>
        private bool HoldsTransparency(int styledItem, double transparency)
        {
            var values = StyleEntitiesUnder(styledItem, "SURFACE_STYLE_TRANSPARENT")
                .Select(t => ReadFirstNumber(_step.ArgsOf(t)))
                .ToList();
            if (transparency <= 1e-6) return values.All(v => v <= 1e-6);
            return values.Count > 0 && values.All(v => Math.Abs(v - transparency) < 1e-4);
        }

        private static double ReadFirstNumber(string args)
        {
            var m = Regex.Match(args ?? "", @"-?\d+\.?\d*(?:[eE][-+]?\d+)?");
            return m.Success
                ? double.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture)
                : 0.0;
        }

        /// <summary>
        /// The geometric items of the part behind this occurrence. These are its
        /// shape representation, its B-rep representation, its solids and its
        /// faces.
        ///
        /// The list holds the faces because an override at component level or
        /// above beats everything inside the part. That includes the colour of
        /// each face, which SolidWorks exports correctly. Without the faces in
        /// this list, those colours keep winning over the override.
        ///
        /// The walk to the faces goes two levels deep, from solid to shell to
        /// face. It is not a full closure. A walk through the whole B-rep would
        /// visit every edge, vertex and surface in the part for no gain.
        /// </summary>
        private HashSet<int> PartGeometryOf(int nauoId)
        {
            var found = new HashSet<int>();
            int partPd = _step.Refs(nauoId).LastOrDefault(r => _step.TypeOf(r) == "PRODUCT_DEFINITION");
            if (partPd == 0) return found;

            int pds = _step.ByType("PRODUCT_DEFINITION_SHAPE")
                           .FirstOrDefault(p => _step.Refs(p).Contains(partPd));
            int sdr = pds == 0 ? 0 : _step.ByType("SHAPE_DEFINITION_REPRESENTATION")
                           .FirstOrDefault(s => _step.Refs(s).Contains(pds));
            int partSr = sdr == 0 ? 0 : _step.Refs(sdr)
                           .FirstOrDefault(r => _step.TypeOf(r) == "SHAPE_REPRESENTATION");
            if (partSr == 0) return found;
            found.Add(partSr);

            foreach (var srr in _step.ByType("SHAPE_REPRESENTATION_RELATIONSHIP"))
            {
                if (!_step.Refs(srr).Contains(partSr)) continue;
                foreach (var r in _step.Refs(srr))
                {
                    var t = _step.TypeOf(r);
                    if (t == "ADVANCED_BREP_SHAPE_REPRESENTATION"
                     || t == "MANIFOLD_SURFACE_SHAPE_REPRESENTATION")
                    {
                        found.Add(r);
                        foreach (var item in _step.Refs(r))
                            if (_step.TypeOf(item) == "MANIFOLD_SOLID_BREP"
                             || _step.TypeOf(item) == "SHELL_BASED_SURFACE_MODEL"
                             || _step.TypeOf(item) == "BREP_WITH_VOIDS")
                            {
                                found.Add(item);
                                AddFacesOf(item, found);
                            }
                    }
                }
            }
            return found;
        }

        /// <summary>Walks solid to shell to face, two levels only.</summary>
        private void AddFacesOf(int solid, HashSet<int> found)
        {
            foreach (var shell in _step.Refs(solid))
            {
                var st = _step.TypeOf(shell);
                if (st != "CLOSED_SHELL" && st != "OPEN_SHELL") continue;
                foreach (var face in _step.Refs(shell))
                    if (_step.TypeOf(face) == "ADVANCED_FACE") found.Add(face);
            }
        }

        /// <summary>Every COLOUR_RGB below a styled item.</summary>
        private List<int> ColoursUnder(int styledItem)
            => StyleEntitiesUnder(styledItem, "COLOUR_RGB");

        /// <summary>Every entity of one type below a styled item.</summary>
        private List<int> StyleEntitiesUnder(int styledItem, string type)
        {
            var found = new List<int>();
            var seen = new HashSet<int>();
            var stack = new Stack<int>(new[] { styledItem });
            while (stack.Count > 0)
            {
                int id = stack.Pop();
                if (!seen.Add(id) || !_step.Entities.ContainsKey(id)) continue;
                if (_step.TypeOf(id) == type) { found.Add(id); continue; }
                // Do not walk into the geometry. A styled item references the
                // solid that it styles, and that branch holds no colour to
                // change.
                var t = _step.TypeOf(id);
                if (t == "MANIFOLD_SOLID_BREP" || t == "ADVANCED_BREP_SHAPE_REPRESENTATION"
                 || t == "ADVANCED_FACE") continue;
                foreach (var r in _step.Refs(id)) stack.Push(r);
            }
            return found;
        }

        /// <summary>
        /// Maps a colour id to every styled item above it. This code builds the
        /// map once, from the file that SolidWorks wrote.
        ///
        /// The obvious method walks every styled item again for every colour.
        /// Its cost grows with the square of the number of styled items. A part
        /// with a colour on each face has one styled item for each face. On a
        /// large assembly, that is the difference between an export and a hang.
        ///
        /// The map holds no style chain that this code adds later. It does not
        /// need to. Those chains use new colour ids, and no earlier styled item
        /// can reference them.
        /// </summary>
        private Dictionary<int, HashSet<int>> _colourOwners;

        private Dictionary<int, HashSet<int>> ColourOwners()
        {
            if (_colourOwners != null) return _colourOwners;
            _colourOwners = new Dictionary<int, HashSet<int>>();
            foreach (var styled in _step.ByType("STYLED_ITEM")
                                        .Concat(_step.ByType("OVER_RIDING_STYLED_ITEM"))
                                        .Concat(_step.ByType("CONTEXT_DEPENDENT_OVER_RIDING_STYLED_ITEM")))
            {
                foreach (var c in ColoursUnder(styled))
                {
                    if (!_colourOwners.TryGetValue(c, out var owners))
                        _colourOwners[c] = owners = new HashSet<int>();
                    owners.Add(styled);
                }
            }
            return _colourOwners;
        }

        /// <summary>
        /// True when a styled item outside the set holds this colour. To write
        /// the colour again in place would then change the appearance of another
        /// part, with no message.
        /// </summary>
        private bool IsReferencedOutside(int colourId, HashSet<int> ours)
        {
            if (!ColourOwners().TryGetValue(colourId, out var owners)) return false;
            foreach (var styled in owners)
                if (!ours.Contains(styled)) return true;
            return false;
        }

        /// <summary>Points one existing styled item at a new colour.</summary>
        public void RecolourStyledItem(int styledItemId, Rgb colour, double transparency)
            => PointStyledItemAt(styledItemId, BuildStyleChain(colour, transparency));

        /// <summary>Points one existing styled item at an existing
        /// presentation style assignment.</summary>
        private void PointStyledItemAt(int styledItemId, int psa)
        {
            string args = _step.ArgsOf(styledItemId);
            var m = Regex.Match(args ?? "", @"^\(\s*('(?:[^']|'')*'|\$)\s*,\s*\(([^)]*)\)\s*,\s*#(\d+)");
            if (!m.Success)
            {
                _log?.Invoke($"    could not parse styled item #{styledItemId}; leaving it alone");
                return;
            }
            _step.Replace(styledItemId,
                $"#{styledItemId}=STYLED_ITEM({m.Groups[1].Value},(#{psa}),#{m.Groups[3].Value});");
        }

        /// <summary>A plain styled item on a solid. Every consumer reads
        /// this.</summary>
        public int AddPlainStyle(int itemId, Rgb colour, double transparency)
        {
            int psa = BuildStyleChain(colour, transparency);
            int styled = _step.NextId();
            _step.Append($"#{styled}=STYLED_ITEM('colour',(#{psa}),#{itemId});");
            return styled;
        }

        private int BuildStyleChain(Rgb colour, double transparency)
        {
            int col = _step.NextId();
            _step.Append($"#{col}=COLOUR_RGB('',{Part21.Num(colour.R)}," +
                         $"{Part21.Num(colour.G)},{Part21.Num(colour.B)});");
            int fac = _step.NextId();
            _step.Append($"#{fac}=FILL_AREA_STYLE_COLOUR('',#{col});");
            int fas = _step.NextId();
            _step.Append($"#{fas}=FILL_AREA_STYLE('',(#{fac}));");
            int ssfa = _step.NextId();
            _step.Append($"#{ssfa}=SURFACE_STYLE_FILL_AREA(#{fas});");

            var elems = new List<string> { $"#{ssfa}" };
            if (transparency > 1e-6)
            {
                int tr = _step.NextId();
                _step.Append($"#{tr}=SURFACE_STYLE_TRANSPARENT({Part21.Num(transparency)});");
                int rend = _step.NextId();
                _step.Append($"#{rend}=SURFACE_STYLE_RENDERING_WITH_PROPERTIES(" +
                             $".NORMAL_SHADING.,#{col},(#{tr}));");
                elems.Add($"#{rend}");
            }

            int sss = _step.NextId();
            _step.Append($"#{sss}=SURFACE_SIDE_STYLE('',({string.Join(",", elems)}));");
            int ssu = _step.NextId();
            _step.Append($"#{ssu}=SURFACE_STYLE_USAGE(.BOTH.,#{sss});");
            int psa = _step.NextId();
            _step.Append($"#{psa}=PRESENTATION_STYLE_ASSIGNMENT((#{ssu}));");
            return psa;
        }
    }
}
