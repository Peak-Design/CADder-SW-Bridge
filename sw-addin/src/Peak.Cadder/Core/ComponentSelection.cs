using System;
using System.Collections.Generic;

namespace Peak.Cadder.Core
{
    /// <summary>
    /// Which components a request is about.
    ///
    /// A request can name them two ways. The component id (c001, c002) is
    /// the manifest's own numbering, which is stable for one assembly that
    /// has not changed: the walk sorts by instance name, so a re-export
    /// numbers the same occurrence the same way. It is NOT stable across an
    /// edit: adding a part in front of another moves every id after it.
    ///
    /// The persistent id is SolidWorks' own reference for the occurrence. It
    /// survives an edit, so an update that has been sitting in a Blender
    /// scene for a week still finds the right parts.
    ///
    /// Both are honoured, the persistent id first, because the caller may
    /// hold ids from an older export. Anything a request asks for that the
    /// assembly no longer holds is reported, never guessed at.
    ///
    /// Blender sends both lists for the same objects. So once a persistent
    /// id has answered, a number that now names a part whose persistent id
    /// nobody asked for is a stale number, and it is left out. Taking it
    /// added the part that moved into that number, and Blender put that
    /// part's pose on the part it asked for.
    /// </summary>
    public sealed class ComponentSelection
    {
        /// <summary>Component ids the request resolved to.</summary>
        public readonly HashSet<string> Ids = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>The persistent id that asked for a component, by the
        /// component id it resolved to. A component asked for by number
        /// only is not in it.</summary>
        public readonly Dictionary<string, string> RequestedBy =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Persistent ids the assembly no longer holds: a part that
        /// has been deleted.</summary>
        public readonly List<string> MissingPersistent = new List<string>();

        /// <summary>Component ids the assembly no longer holds. Ignored when
        /// a persistent id answered the request, because a stale number after
        /// an edit is expected, not an error.</summary>
        public readonly List<string> MissingComponents = new List<string>();

        /// <summary>Component ids that now name a part the request did not
        /// ask for by its persistent id. Left out, and not reported.</summary>
        public readonly List<string> StaleComponents = new List<string>();

        private bool _persistentAnswered;

        /// <summary>What the request asked for and the assembly does not
        /// hold, worth telling the caller about.</summary>
        public List<string> Missing
        {
            get
            {
                var all = new List<string>(MissingPersistent);
                if (!_persistentAnswered) all.AddRange(MissingComponents);
                return all;
            }
        }

        /// <summary>True when the request named nothing, which means the
        /// whole assembly.</summary>
        public bool Everything
        {
            get
            {
                return Ids.Count == 0 && MissingPersistent.Count == 0
                    && MissingComponents.Count == 0 && StaleComponents.Count == 0;
            }
        }

        /// <summary>
        /// Resolves a request against what the assembly holds.
        /// <paramref name="present"/> maps a component id to its persistent
        /// id (null where SolidWorks would not give one).
        /// </summary>
        public static ComponentSelection Resolve(
            IEnumerable<string> componentIds, IEnumerable<string> persistentIds,
            IDictionary<string, string> present)
        {
            var selection = new ComponentSelection();
            var byPersistent = new Dictionary<string, string>(StringComparer.Ordinal);
            if (present != null)
                foreach (var kv in present)
                    if (!string.IsNullOrEmpty(kv.Value) && !byPersistent.ContainsKey(kv.Value))
                        byPersistent[kv.Value] = kv.Key;

            var asked = new HashSet<string>(StringComparer.Ordinal);
            if (persistentIds != null)
                foreach (var persistent in persistentIds)
                {
                    if (string.IsNullOrEmpty(persistent)) continue;
                    asked.Add(persistent);
                    string id;
                    if (byPersistent.TryGetValue(persistent, out id))
                    {
                        selection.Ids.Add(id);
                        selection.RequestedBy[id] = persistent;
                        selection._persistentAnswered = true;
                    }
                    else if (!selection.MissingPersistent.Contains(persistent))
                    {
                        selection.MissingPersistent.Add(persistent);
                    }
                }

            if (componentIds != null)
                foreach (var id in componentIds)
                {
                    if (string.IsNullOrEmpty(id)) continue;
                    string now;
                    if (present == null || !present.TryGetValue(id, out now))
                    {
                        if (!selection.MissingComponents.Contains(id))
                            selection.MissingComponents.Add(id);
                        continue;
                    }
                    // A number is taken for a part that has no persistent
                    // id, which the number is the only name for, or whose
                    // persistent id was asked for anyway.
                    if (selection._persistentAnswered && !string.IsNullOrEmpty(now)
                        && !asked.Contains(now))
                    {
                        if (!selection.StaleComponents.Contains(id))
                            selection.StaleComponents.Add(id);
                        continue;
                    }
                    selection.Ids.Add(id);
                }
            return selection;
        }

        public override string ToString()
        {
            var missing = Missing;
            return Ids.Count + " component(s)"
                + (missing.Count > 0 ? ", " + missing.Count + " not in the assembly" : "");
        }
    }
}
