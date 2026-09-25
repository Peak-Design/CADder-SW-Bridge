public sealed partial class SwMcpScript
{
    /// <summary>
    /// The mate graph of the active assembly as the CADder Bridge reads it
    /// (AssemblyWalker and MateReader), before the classifier: every
    /// component with its place and status, every mate with its entities,
    /// limits and suppression, and the log lines of the readers. Use it to
    /// check the reading before you blame the classifier.
    /// </summary>
    private object CadderMates()
    {
        if (!(doc is AssemblyDoc)) throw new InvalidOperationException("The active document is not an assembly.");
        long mark = Cadder.LogMark();
        try
        {
            var walked = Cadder.Call("Sw.AssemblyWalker", "Walk", doc, Cadder.Log);
            var graph = Cadder.Call("Sw.MateReader", "Read", walked, Cadder.Log, doc);
            return new
            {
                document = doc.GetPathName(),
                graph = Cadder.Plain(graph, 5),
                log = Cadder.LogSince(mark),
            };
        }
        finally { Cadder.Flush(); }
    }

    /// <summary>
    /// What SolidWorks calls each component (fixed, fully defined or
    /// under-defined) with every mate in place, then with the limit mates
    /// (and the coupling mates, unless couplings is false) taken out, and
    /// again after they are back, with the drift of each component over the
    /// round trip. rebuild is the call that makes the solver take the change:
    /// "edit", "mates" or "force". This is how the meaning of the status was
    /// found before the export trusted it.
    /// </summary>
    private object CadderStatusProbe(bool couplings = true, string rebuild = "edit")
    {
        if (!(doc is AssemblyDoc)) throw new InvalidOperationException("The active document is not an assembly.");
        try
        {
            // One walk for all of it. SolveState.Suppress takes the list of the walk as it is.
            var walked = Cadder.Call("Sw.AssemblyWalker", "Walk", doc, Cadder.Log);
            var items = ((System.Collections.IEnumerable)walked).Cast<object>().ToList();
            var before = items.ToDictionary(w => w, CadderStatusOf);
            var poses = items.ToDictionary(w => w, CadderPoseOf);

            var state = Cadder.Call("Sw.SolveState", "Suppress", doc, walked, couplings, Cadder.Log);
            bool rebuilt, rebuiltBack;
            Dictionary<object, int> free;
            object held, failed;
            try
            {
                rebuilt = (bool)Cadder.Call("Sw.SolveState", "Rebuild", doc, rebuild);
                free = items.ToDictionary(w => w, CadderStatusOf);
                held = Cadder.Plain(Cadder.CallOn(state, "Describe"));
            }
            finally
            {
                // The mates go back also when a step above fails.
                failed = Cadder.Plain(Cadder.CallOn(state, "Restore"));
                rebuiltBack = (bool)Cadder.Call("Sw.SolveState", "Rebuild", doc, rebuild);
            }
            return CadderStatusRows(items, before, free, poses, rebuild, rebuilt, rebuiltBack, held, failed);
        }
        finally { Cadder.Flush(); }
    }

    private object CadderStatusRows(List<object> items, Dictionary<object, int> before, Dictionary<object, int> free,
        Dictionary<object, double[]> poses, string rebuild, bool rebuilt, bool rebuiltBack, object held, object failed)
    {
        double worst = 0;
        var rows = new List<object>();
        foreach (var w in items)
        {
            double drift = CadderDrift(poses[w], CadderPoseOf(w));
            worst = Math.Max(worst, drift);
            var graph = Cadder.Get(w, "Graph");
            var parent = Cadder.Get(w, "Parent");
            rows.Add(new Dictionary<string, object>
            {
                { "path", Cadder.Get(graph, "Path") },
                { "parent", parent == null ? null : Cadder.Get(Cadder.Get(parent, "Graph"), "Path") },
                { "fixed", Cadder.Get(graph, "IsFixed") },
                { "solving", Cadder.Get(graph, "Solving") },
                { "suppressed", Cadder.Get(graph, "Suppressed") },
                { "on", before[w] },
                { "free", free[w] },
                { "after", CadderStatusOf(w) },
                { "drift", drift },
            });
        }
        return new
        {
            rebuild,
            rebuilt,
            rebuiltBack,
            takenOut = held,
            notRestored = failed,
            worstDrift = worst,
            components = rows,
        };
    }

    private static int CadderStatusOf(object walked)
    {
        try { return ((Component2)Cadder.Get(walked, "Comp")).GetConstrainedStatus(); }
        catch (Exception) { return 0; }
    }

    private static double[] CadderPoseOf(object walked)
    {
        try { return ((Component2)Cadder.Get(walked, "Comp")).Transform2.ArrayData as double[]; }
        catch (Exception) { return null; }
    }

    private static double CadderDrift(double[] a, double[] b)
    {
        if (a == null || b == null) return 0;
        double worst = 0;
        for (int i = 0; i < Math.Min(12, Math.Min(a.Length, b.Length)); i++)
            worst = Math.Max(worst, Math.Abs(a[i] - b[i]));
        return worst;
    }

    /// <summary>
    /// Drags one component as a user drags it with the mouse, with the mover
    /// of the Bridge (Sw.ComponentMover). component is the instance path
    /// (Name2, for example "lifterassy-1/rod-1"). With angle 0: a move along
    /// axis by its length in metres. Else: a turn of angle radians about axis
    /// through origin. The move stays in memory. Close without a save or
    /// undo to put the assembly back.
    /// </summary>
    private object CadderDrag(string component, double[] axis, double angle = 0, double[] origin = null)
    {
        var assembly = doc as AssemblyDoc;
        if (assembly == null) throw new InvalidOperationException("The active document is not an assembly.");
        var comp = ((object[])assembly.GetComponents(false) ?? new object[0]).Cast<Component2>()
            .FirstOrDefault(c => string.Equals(c.Name2, component, StringComparison.OrdinalIgnoreCase));
        if (comp == null) throw new InvalidOperationException("No component named " + component + ".");
        if (axis == null || axis.Length != 3) throw new ArgumentException("axis needs three values.");
        double length = Math.Sqrt(axis[0] * axis[0] + axis[1] * axis[1] + axis[2] * axis[2]);
        if (length < 1e-12) throw new ArgumentException("The move has no direction.");
        try
        {
            var mover = Cadder.New("Sw.ComponentMover", swApp, doc, Cadder.Log);
            if (!(bool)Cadder.Get(mover, "Ready")) throw new InvalidOperationException("The mover could not start.");
            var unit = new[] { axis[0] / length, axis[1] / length, axis[2] / length };
            object delta = Math.Abs(angle) > 1e-12
                ? Cadder.Call("Sw.ComponentMover", "RotationAboutAxis", unit, origin ?? new double[3], angle)
                : Cadder.Call("Sw.ComponentMover", "TranslationAlong", unit, length);
            bool moved = (bool)Cadder.CallOn(mover, "DragBy", comp, delta);
            return new { component, moved };
        }
        finally { Cadder.Flush(); }
    }
}
