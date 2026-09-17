using System;
using System.Collections.Generic;
using System.Globalization;

namespace Peak.Cadder.Core
{
    /// <summary>
    /// What to leave out of ONE component's geometry.
    ///
    /// Which parts travel defeatured is the consumer's decision, not the CAD
    /// application's. A bolt hole in a bracket that nobody looks at can go,
    /// and the same hole in the part on the cover of the manual cannot, and
    /// only the person building the scene knows which is which. So this
    /// arrives with the request, one entry per component, and the add-in
    /// holds no setting of its own (Oscar, 2026-09-17).
    /// </summary>
    public sealed class DefeatureSpec
    {
        /// <summary>How wide a feature may be and still be left out, in
        /// metres. Zero or less leaves everything in.</summary>
        public double Size;

        /// <summary>Also leave out a feature whose face is curved: a hole in
        /// a cylindrical boss, for example. The owner face keeps its own
        /// triangles and the hole is capped, so the shape stays exact
        /// everywhere but across the hole itself.</summary>
        public bool Curved;

        public bool Any { get { return Size > 0.0; } }

        /// <summary>
        /// A name for this spec, to tell two otherwise identical parts apart.
        ///
        /// Two occurrences of one document share one mesh, which is most of
        /// why the native path is fast. Two occurrences with DIFFERENT
        /// defeature settings are no longer the same geometry, so the sharing
        /// key has to carry this.
        /// </summary>
        public string Key
        {
            get
            {
                if (!Any) return "";
                return (Curved ? "|sc" : "|sp")
                    + Size.ToString("G9", CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>
    /// The defeature specs of a whole request, by component id.
    ///
    /// A component the request does not name travels as it is. That is the
    /// safe way round: a scene that says nothing gets the geometry it has
    /// always got.
    /// </summary>
    public sealed class DefeatureOptions
    {
        private readonly Dictionary<string, DefeatureSpec> _byComponent =
            new Dictionary<string, DefeatureSpec>(StringComparer.Ordinal);
        private DefeatureSpec _first;

        public static readonly DefeatureOptions None = new DefeatureOptions();

        /// <summary>Whether any component at all is to be defeatured.</summary>
        public bool Any { get { return _byComponent.Count > 0; } }

        public int Count { get { return _byComponent.Count; } }

        public void Set(string componentId, DefeatureSpec spec)
        {
            if (string.IsNullOrEmpty(componentId) || spec == null || !spec.Any) return;
            _byComponent[componentId] = spec;
            if (_first == null) _first = spec;
        }

        /// <summary>
        /// The spec for a component, or null when it travels as it is.
        ///
        /// A part document is one component and carries no id of its own, so
        /// a request about a part document is answered by the one spec it
        /// sent.
        /// </summary>
        public DefeatureSpec For(string componentId)
        {
            if (string.IsNullOrEmpty(componentId)) return _first;
            DefeatureSpec spec;
            return _byComponent.TryGetValue(componentId, out spec) ? spec : null;
        }

        public string KeyFor(string componentId)
        {
            var spec = For(componentId);
            return spec == null ? "" : spec.Key;
        }

        /// <summary>
        /// Reads the "defeature" array of a bridge request: one entry per
        /// component, each naming the component, the size and whether curved
        /// faces are included.
        /// </summary>
        public static DefeatureOptions From(Dictionary<string, object> request)
        {
            var options = new DefeatureOptions();
            var rows = MiniJson.Arr(request, "defeature");
            if (rows == null) return options;
            foreach (var row in rows)
            {
                var entry = row as Dictionary<string, object>;
                if (entry == null) continue;
                double size = MiniJson.Num(entry, "size_m", 0.0);
                if (!(size > 0.0)) continue;
                var spec = new DefeatureSpec
                {
                    Size = size,
                    Curved = MiniJson.Flag(entry, "curved", false),
                };
                options.Set(MiniJson.Str(entry, "component", null), spec);
            }
            return options;
        }
    }
}
