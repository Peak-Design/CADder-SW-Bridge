using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Peak.Cadder.Appearance;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// Builds small Part 21 files for the tests of the STEP appearance
    /// engine. The layout follows what SolidWorks writes: one product chain
    /// for each document, one shape representation for each product that
    /// lists the placements of its children, one placement chain for each
    /// occurrence, and for a part a B-rep behind a
    /// shape_representation_relationship. The ids come out in call order,
    /// so a test controls which entity has the lower id.
    /// </summary>
    internal sealed class AppearanceStepFixture
    {
        private sealed class Rep
        {
            public int Line;
            public string Type;
            public string Name;
            public int Context;
            public readonly List<int> Items = new List<int>();
        }

        private readonly List<string> _lines = new List<string>();
        private readonly Dictionary<int, Rep> _reps = new Dictionary<int, Rep>();
        private readonly Dictionary<int, int> _srOfPd = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _originOfSr = new Dictionary<int, int>();
        private int _next = 1;
        private int _nauos;

        public readonly int ProductContext;
        public readonly int DefinitionContext;
        /// <summary>A geometric context in millimetres, the default for
        /// every product.</summary>
        public readonly int MmContext;

        /// <summary>The representation that the last call to Bodies
        /// made.</summary>
        public int LastBrep { get; private set; }

        public AppearanceStepFixture()
        {
            int app = Add("APPLICATION_CONTEXT('automotive design')");
            ProductContext = Add($"PRODUCT_CONTEXT('',#{app},'mechanical')");
            DefinitionContext = Add($"PRODUCT_DEFINITION_CONTEXT('part definition',#{app},'design')");
            MmContext = LengthContext("mm");
        }

        public int Add(string entity)
        {
            int id = _next++;
            _lines.Add("#" + id + "=" + entity + ";");
            return id;
        }

        /// <summary>A geometric context with one length unit: "m", "mm" or
        /// "in".</summary>
        public int LengthContext(string unit)
        {
            int u;
            switch (unit)
            {
                case "m":
                    u = Add("( LENGTH_UNIT() NAMED_UNIT(*) SI_UNIT($,.METRE.) )");
                    break;
                case "mm":
                    u = Add("( LENGTH_UNIT() NAMED_UNIT(*) SI_UNIT(.MILLI.,.METRE.) )");
                    break;
                case "in":
                    int mm = Add("( LENGTH_UNIT() NAMED_UNIT(*) SI_UNIT(.MILLI.,.METRE.) )");
                    int measure = Add($"LENGTH_MEASURE_WITH_UNIT(LENGTH_MEASURE(25.4),#{mm})");
                    int dims = Add("DIMENSIONAL_EXPONENTS(1.,0.,0.,0.,0.,0.,0.)");
                    u = Add($"( CONVERSION_BASED_UNIT('INCH',#{measure}) LENGTH_UNIT() "
                        + $"NAMED_UNIT(#{dims}) )");
                    break;
                default:
                    throw new ArgumentException(unit);
            }
            return Add($"( GEOMETRIC_REPRESENTATION_CONTEXT(3) "
                + $"GLOBAL_UNIT_ASSIGNED_CONTEXT((#{u})) REPRESENTATION_CONTEXT('','') )");
        }

        public int Placement(double x, double y, double z)
        {
            int pt = Add($"CARTESIAN_POINT('',({N(x)},{N(y)},{N(z)}))");
            int ax = Add("DIRECTION('',(0.,0.,1.))");
            int rd = Add("DIRECTION('',(1.,0.,0.))");
            return Add($"AXIS2_PLACEMENT_3D('',#{pt},#{ax},#{rd})");
        }

        /// <summary>One document: product, formation, definition, and a
        /// shape representation with its origin. SolidWorks writes the file
        /// name into the product and the representation names. Returns the
        /// product definition.</summary>
        public int Product(string name, int context = 0)
        {
            int product = Add($"PRODUCT({Part21.Str(name)},{Part21.Str(name)},'',(#{ProductContext}))");
            int pdf = Add($"PRODUCT_DEFINITION_FORMATION('','',#{product})");
            int pd = Add($"PRODUCT_DEFINITION('design','',#{pdf},#{DefinitionContext})");
            int pds = Add($"PRODUCT_DEFINITION_SHAPE('','',#{pd})");
            int origin = Placement(0, 0, 0);
            int sr = DeferredRep("SHAPE_REPRESENTATION", name, context == 0 ? MmContext : context);
            _reps[sr].Items.Add(origin);
            Add($"SHAPE_DEFINITION_REPRESENTATION(#{pds},#{sr})");
            _srOfPd[pd] = sr;
            _originOfSr[sr] = origin;
            return pd;
        }

        public int ShapeRepresentationOf(int pd) => _srOfPd[pd];

        /// <summary>
        /// Adds bodies to a part: a representation of the given type behind
        /// a new shape_representation_relationship. solidTypes holds
        /// MANIFOLD_SOLID_BREP or SHELL_BASED_SURFACE_MODEL. Returns the
        /// solids, in order.
        /// </summary>
        public List<int> Bodies(int partPd, string repType, params string[] solidTypes)
        {
            var solids = new List<int>();
            foreach (var t in solidTypes)
            {
                int plane = Add($"PLANE('',#{Placement(0, 0, 0)})");
                int face = Add($"ADVANCED_FACE('',(),#{plane},.T.)");
                if (t == "SHELL_BASED_SURFACE_MODEL")
                {
                    int shell = Add($"OPEN_SHELL('',(#{face}))");
                    solids.Add(Add($"SHELL_BASED_SURFACE_MODEL('',(#{shell}))"));
                }
                else
                {
                    int shell = Add($"CLOSED_SHELL('',(#{face}))");
                    solids.Add(Add($"MANIFOLD_SOLID_BREP('',#{shell})"));
                }
            }
            int sr = _srOfPd[partPd];
            int rep = DeferredRep(repType, "", _reps[sr].Context);
            _reps[rep].Items.AddRange(solids);
            _reps[rep].Items.Add(Placement(0, 0, 0));
            Add($"SHAPE_REPRESENTATION_RELATIONSHIP('','',#{sr},#{rep})");
            LastBrep = rep;
            return solids;
        }

        /// <summary>A plain styled item on one item, with the full chain
        /// that SolidWorks writes. A transparency above zero adds the
        /// rendering properties.</summary>
        public int Style(int item, double r, double g, double b, double transparency = 0)
        {
            int col = Add($"COLOUR_RGB('',{N(r)},{N(g)},{N(b)})");
            int fac = Add($"FILL_AREA_STYLE_COLOUR('',#{col})");
            int fas = Add($"FILL_AREA_STYLE('',(#{fac}))");
            int ssfa = Add($"SURFACE_STYLE_FILL_AREA(#{fas})");
            string elements = "#" + ssfa;
            if (transparency > 0)
            {
                int tr = Add($"SURFACE_STYLE_TRANSPARENT({N(transparency)})");
                int rend = Add($"SURFACE_STYLE_RENDERING_WITH_PROPERTIES(.NORMAL_SHADING.,#{col},(#{tr}))");
                elements += ",#" + rend;
            }
            int sss = Add($"SURFACE_SIDE_STYLE('',({elements}))");
            int ssu = Add($"SURFACE_STYLE_USAGE(.BOTH.,#{sss})");
            int psa = Add($"PRESENTATION_STYLE_ASSIGNMENT((#{ssu}))");
            return Add($"STYLED_ITEM('color',(#{psa}),#{item})");
        }

        /// <summary>A part with one solid, styled on the solid and on its
        /// B-rep representation, the way SolidWorks writes a part
        /// colour.</summary>
        public int ColouredPart(string name, double r, double g, double b, double transparency = 0)
        {
            int pd = Product(name);
            var solids = Bodies(pd, "ADVANCED_BREP_SHAPE_REPRESENTATION", "MANIFOLD_SOLID_BREP");
            Style(solids[0], r, g, b, transparency);
            Style(LastBrep, r, g, b, transparency);
            return pd;
        }

        /// <summary>One occurrence of child inside parent, at a position in
        /// the unit of the parent's context. Returns the NAUO.</summary>
        public int Use(int parentPd, int childPd, double x, double y, double z)
        {
            _nauos++;
            int nauo = Add($"NEXT_ASSEMBLY_USAGE_OCCURRENCE('NAUO{_nauos}','','',#{parentPd},#{childPd},$)");
            int pds = Add($"PRODUCT_DEFINITION_SHAPE('Placement','',#{nauo})");
            int placement = Placement(x, y, z);
            int parentSr = _srOfPd[parentPd], childSr = _srOfPd[childPd];
            _reps[parentSr].Items.Add(placement);
            int idt = Add($"ITEM_DEFINED_TRANSFORMATION('','',#{placement},#{_originOfSr[childSr]})");
            int rel = Add($"( REPRESENTATION_RELATIONSHIP('','',#{childSr},#{parentSr}) "
                + $"REPRESENTATION_RELATIONSHIP_WITH_TRANSFORMATION(#{idt}) "
                + "SHAPE_REPRESENTATION_RELATIONSHIP() )");
            Add($"CONTEXT_DEPENDENT_SHAPE_REPRESENTATION(#{rel},#{pds})");
            return nauo;
        }

        public string Write(ICollection<string> cleanup)
        {
            foreach (var kv in _reps)
            {
                var r = kv.Value;
                _lines[r.Line] = "#" + kv.Key + "=" + r.Type + "(" + Part21.Str(r.Name) + ",("
                    + string.Join(",", r.Items.Select(i => "#" + i)) + "),#" + r.Context + ");";
            }
            var sb = new StringBuilder();
            sb.Append("ISO-10303-21;\r\nHEADER;\r\nFILE_DESCRIPTION((''),'2;1');\r\n")
              .Append("FILE_NAME('t','2026-09-23',(''),(''),'','','');\r\n")
              .Append("FILE_SCHEMA(('AUTOMOTIVE_DESIGN'));\r\nENDSEC;\r\nDATA;\r\n");
            foreach (var line in _lines) sb.Append(line).Append("\r\n");
            sb.Append("ENDSEC;\r\nEND-ISO-10303-21;\r\n");

            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "cadder-appearance-" + Guid.NewGuid().ToString("N") + ".step");
            File.WriteAllText(path, sb.ToString());
            cleanup.Add(path);
            return path;
        }

        private int DeferredRep(string type, string name, int context)
        {
            int id = Add("PLACEHOLDER()");
            _reps[id] = new Rep { Line = _lines.Count - 1, Type = type, Name = name, Context = context };
            return id;
        }

        private static string N(double v) => Part21.Num(v) + (v == Math.Floor(v) ? "." : "");

        // ── Reading the output back ─────────────────────────────────────────

        /// <summary>The style entities below a styled item: the walk follows
        /// only the presentation chain, never the styled geometry.</summary>
        public static List<int> StyleEntitiesUnder(Part21 step, int styledItem, string type)
        {
            var chain = new HashSet<string>(StringComparer.Ordinal)
            {
                "PRESENTATION_STYLE_ASSIGNMENT", "SURFACE_STYLE_USAGE", "SURFACE_SIDE_STYLE",
                "SURFACE_STYLE_FILL_AREA", "FILL_AREA_STYLE", "FILL_AREA_STYLE_COLOUR",
                "SURFACE_STYLE_RENDERING_WITH_PROPERTIES", "COLOUR_RGB", "SURFACE_STYLE_TRANSPARENT",
            };
            var found = new List<int>();
            var seen = new HashSet<int>();
            var stack = new Stack<int>(step.Refs(styledItem));
            while (stack.Count > 0)
            {
                int id = stack.Pop();
                string t = step.TypeOf(id);
                if (t == null || !chain.Contains(t) || !seen.Add(id)) continue;
                if (t == type) found.Add(id);
                foreach (var r in step.Refs(id)) stack.Push(r);
            }
            return found;
        }

        /// <summary>The numbers in the arguments of an entity, with the
        /// quoted strings left out.</summary>
        public static double[] Numbers(Part21 step, int id)
        {
            string args = step.ArgsOf(id) ?? "";
            var sb = new StringBuilder();
            bool inStr = false;
            foreach (char c in args)
            {
                if (c == '\'') { inStr = !inStr; sb.Append(' '); continue; }
                sb.Append(inStr ? ' ' : c);
            }
            return System.Text.RegularExpressions.Regex
                .Matches(sb.ToString(), @"(?<![#\w])-?\d+\.?\d*(?:[eE][-+]?\d+)?")
                .Cast<System.Text.RegularExpressions.Match>()
                .Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture))
                .ToArray();
        }

        /// <summary>Every plain STYLED_ITEM whose styled item is this
        /// entity.</summary>
        public static List<int> StyledItemsOn(Part21 step, int item)
            => step.ByType("STYLED_ITEM").Where(s => step.Refs(s).LastOrDefault() == item).ToList();

        /// <summary>The colour of every plain styled item on this entity, as
        /// "r,g,b".</summary>
        public static List<string> ColoursOn(Part21 step, int item)
            => StyledItemsOn(step, item)
                .SelectMany(s => StyleEntitiesUnder(step, s, "COLOUR_RGB"))
                .Select(c => string.Join(",", Numbers(step, c).Select(v => Part21.Num(v))))
                .Distinct().ToList();

        /// <summary>The placement chain of one occurrence: the relationship,
        /// the placement and its point.</summary>
        public static (int Rel, int Placement, int Point) PlacementOf(Part21 step, int nauo)
        {
            int pds = step.ByType("PRODUCT_DEFINITION_SHAPE").First(p => step.Refs(p).Contains(nauo));
            int cdsr = step.ByType("CONTEXT_DEPENDENT_SHAPE_REPRESENTATION")
                .First(c => step.Refs(c).Contains(pds));
            int rel = step.Refs(cdsr).First(r => (step.TypeOf(r) ?? "").Contains("REPRESENTATION_RELATIONSHIP"));
            int idt = step.Refs(rel).First(r => step.TypeOf(r) == "ITEM_DEFINED_TRANSFORMATION");
            int placement = step.Refs(idt)[0];
            return (rel, placement, step.Refs(placement)[0]);
        }
    }
}
