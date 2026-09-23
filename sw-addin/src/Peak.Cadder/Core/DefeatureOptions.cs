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
    ///
    /// A row names the component by the id of the export the scene was
    /// built from. That number is NOT stable across an edit: the walk
    /// numbers by instance name, so a part added in front of another moves
    /// every id after it. A row can also carry the persistent id, which
    /// survives the edit, and ResolvedAgainst uses it to find the part the
    /// row is about. Before that, a Refresh after such an edit defeatured
    /// the part that took over the number and gave the chosen part its
    /// holes back, with no message.
    /// </summary>
    public sealed class DefeatureOptions
    {
        private sealed class Row
        {
            public string Component;
            public string Persistent;
            public DefeatureSpec Spec;
        }

        private readonly Dictionary<string, DefeatureSpec> _byComponent =
            new Dictionary<string, DefeatureSpec>(StringComparer.Ordinal);
        private readonly List<Row> _rows = new List<Row>();
        private DefeatureSpec _first;

        public static readonly DefeatureOptions None = new DefeatureOptions();

        /// <summary>Whether any component at all is to be defeatured.</summary>
        public bool Any { get { return _byComponent.Count > 0; } }

        public int Count { get { return _byComponent.Count; } }

        /// <summary>Rows that ResolvedAgainst could not give to a part the
        /// assembly holds now: the part was deleted, or its number now
        /// belongs to another part.</summary>
        public int Dropped { get; private set; }

        /// <summary>Whether any row names its part by persistent id. Only
        /// then is it worth reading the persistent ids of the walk.</summary>
        public bool NamesPersistent
        {
            get
            {
                foreach (var row in _rows)
                    if (!string.IsNullOrEmpty(row.Persistent)) return true;
                return false;
            }
        }

        public void Set(string componentId, DefeatureSpec spec)
        {
            Set(componentId, null, spec);
        }

        public void Set(string componentId, string persistentId, DefeatureSpec spec)
        {
            if (spec == null || !spec.Any) return;
            if (string.IsNullOrEmpty(componentId) && string.IsNullOrEmpty(persistentId)) return;
            _rows.Add(new Row { Component = componentId, Persistent = persistentId, Spec = spec });
            if (!string.IsNullOrEmpty(componentId)) _byComponent[componentId] = spec;
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

        /// <summary>
        /// The same rows, keyed by the ids of the walk just made.
        /// <paramref name="present"/> maps each component id of that walk
        /// to its persistent id (null where SolidWorks gave none). It is the
        /// same map that ComponentSelection resolves a selection with.
        ///
        /// The persistent id of a row decides first. The component id of the
        /// row is used only when it cannot contradict that: the row has no
        /// persistent id (an older consumer), or the component that has the
        /// number now has none. When both have one and they differ, the
        /// number belongs to another part, and the row is dropped. A part
        /// that keeps its holes is easy to see and to put right. A part that
        /// loses holes that nobody chose is not.
        /// </summary>
        public DefeatureOptions ResolvedAgainst(IDictionary<string, string> present)
        {
            if (present == null) return this;
            var byPersistent = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in present)
                if (!string.IsNullOrEmpty(kv.Value) && !byPersistent.ContainsKey(kv.Value))
                    byPersistent[kv.Value] = kv.Key;

            var resolved = new DefeatureOptions { _first = _first };
            var answered = new HashSet<string>(StringComparer.Ordinal);
            var waiting = new List<Row>();
            foreach (var row in _rows)
            {
                string id;
                if (!string.IsNullOrEmpty(row.Persistent)
                    && byPersistent.TryGetValue(row.Persistent, out id))
                {
                    resolved._byComponent[id] = row.Spec;
                    answered.Add(id);
                }
                else waiting.Add(row);
            }
            int dropped = 0;
            foreach (var row in waiting)
            {
                string now;
                if (string.IsNullOrEmpty(row.Component)
                    || !present.TryGetValue(row.Component, out now))
                {
                    dropped++;
                    continue;
                }
                // A persistent id already said which spec this part gets.
                if (answered.Contains(row.Component)) continue;
                if (!string.IsNullOrEmpty(row.Persistent) && !string.IsNullOrEmpty(now))
                {
                    dropped++;
                    continue;
                }
                resolved._byComponent[row.Component] = row.Spec;
            }
            resolved.Dropped = dropped;
            return resolved;
        }

        public string KeyFor(string componentId)
        {
            var spec = For(componentId);
            return spec == null ? "" : spec.Key;
        }

        /// <summary>
        /// Reads the "defeature" array of a bridge request: one entry per
        /// component, each naming the component (and its persistent id when
        /// the consumer has it), the size and whether curved faces are
        /// included.
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
                options.Set(MiniJson.Str(entry, "component", null),
                            MiniJson.Str(entry, "persistent_id", null), spec);
            }
            return options;
        }
    }
}
