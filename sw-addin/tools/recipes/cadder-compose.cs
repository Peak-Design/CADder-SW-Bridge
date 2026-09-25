/// <summary>A test assembly for CadderCompose. It is saved only under the temp folder.</summary>
public sealed class CadderComposeSpec
{
    /// <summary>The path of the new assembly, under the temp folder. An existing file is not written over.</summary>
    public string SaveAs = null;

    /// <summary>The assembly template. Null: the default template of SolidWorks.</summary>
    public string Template = null;

    public List<CadderComposeComponent> Components = new List<CadderComposeComponent>();

    public List<CadderComposeMate> Mates = new List<CadderComposeMate>();
}

/// <summary>One component of a test assembly.</summary>
public sealed class CadderComposeComponent
{
    /// <summary>The part or assembly file.</summary>
    public string File = null;

    /// <summary>Where it goes, in metres.</summary>
    public double[] At = { 0, 0, 0 };

    /// <summary>The configuration that the instance uses. Null: the one that the document shows.</summary>
    public string Configuration = null;

    /// <summary>A subassembly that solves flexible.</summary>
    public bool Flexible = false;

    /// <summary>Fixed. SolidWorks fixes the first component of a new assembly, so false unfixes it.</summary>
    public bool Fixed = false;
}

/// <summary>A feature to mate: a plane, axis or other feature of a component, or of the new assembly when Component is null.</summary>
public sealed class CadderComposeEntity
{
    /// <summary>The path of the component in the new assembly, for example "hinge-1/leaf-1". Null: the new assembly.</summary>
    public string Component = null;

    /// <summary>The feature name, for example "Top Plane".</summary>
    public string Feature = null;
}

/// <summary>One mate of a test assembly.</summary>
public sealed class CadderComposeMate
{
    /// <summary>coincident, concentric, parallel, perpendicular, distance, angle or tangent.</summary>
    public string Type = "coincident";

    /// <summary>aligned, anti or closest.</summary>
    public string Align = "closest";

    public CadderComposeEntity A = null, B = null;

    /// <summary>The distance (metres) or the angle (radians), and the limits of a limit mate.</summary>
    public double Value = 0, Min = 0, Max = 0;

    /// <summary>The name of the mate feature. Null: the name SolidWorks gives.</summary>
    public string Name = null;
}

/// <summary>A configuration for CadderConfigure.</summary>
public sealed class CadderConfigureSpec
{
    /// <summary>The title or path of an open document. Null: the active document.</summary>
    public string Document = null;

    /// <summary>The name of a configuration to add. The dimensions and suppressions apply in it.</summary>
    public string Add = null;

    /// <summary>Dimensions by full name ("D2@LimitAngle1") and value in metres or radians.</summary>
    public Dictionary<string, double> Dimensions = new Dictionary<string, double>();

    /// <summary>Features and mates to suppress.</summary>
    public List<string> Suppress = new List<string>();

    /// <summary>The configuration to show at the end.</summary>
    public string Show = null;

    /// <summary>Save the document. Only a document under the temp folder is saved.</summary>
    public bool Save = false;
}

public sealed partial class SwMcpScript
{
    /// <summary>
    /// Builds a test assembly from a spec: components (with configuration,
    /// flexible solving and fixed), and mates between their features. Saves
    /// it under the temp folder only, so a test never writes over a model of
    /// the user or of the corpus. Use it for a case that no corpus assembly
    /// has. (Was the lab operation compose.)
    /// </summary>
    private object CadderCompose(CadderComposeSpec spec)
    {
        if (spec == null) throw new ArgumentException("Give a CadderComposeSpec.");
        CadderTempOnly(spec.SaveAs);
        if (File.Exists(spec.SaveAs)) throw new InvalidOperationException("Will not write over " + spec.SaveAs + ".");
        // No template: the SW-MCP helper tries the default template, then the template folders.
        var model = spec.Template != null ? swApp.NewDocument(spec.Template, 0, 0, 0) as ModelDoc2 : NewAssembly();
        var assembly = model as AssemblyDoc;
        if (assembly == null) throw new InvalidOperationException("No assembly could be made" + (spec.Template != null ? " from " + spec.Template : "") + ".");
        doc = model;

        var added = new List<string>();
        foreach (var c in spec.Components)
        {
            if (string.IsNullOrEmpty(c.File) || !File.Exists(c.File)) throw new InvalidOperationException("No such file: " + c.File);
            int err = 0, warn = 0;
            int type = string.Equals(Path.GetExtension(c.File), ".sldprt", StringComparison.OrdinalIgnoreCase)
                ? (int)swDocumentTypes_e.swDocPART : (int)swDocumentTypes_e.swDocASSEMBLY;
            if (swApp.OpenDoc6(c.File, type, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref err, ref warn) == null)
                throw new InvalidOperationException("Could not open " + c.File + " (error " + err + ").");
            swApp.ActivateDoc3(model.GetTitle(), false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref err);
            var at = c.At ?? new double[3];
            var comp = assembly.AddComponent5(c.File, (int)swAddComponentConfigOptions_e.swAddComponentConfigOptions_CurrentSelectedConfig,
                "", false, "", at[0], at.Length > 1 ? at[1] : 0, at.Length > 2 ? at[2] : 0) as Component2;
            if (comp == null) throw new InvalidOperationException("AddComponent5 refused " + c.File + ".");
            if (c.Flexible || !string.IsNullOrEmpty(c.Configuration))
            {
                model.ClearSelection2(true);
                comp.Select4(false, null, false);
                bool set = assembly.CompConfigProperties5((int)swComponentSuppressionState_e.swComponentFullyResolved,
                    c.Flexible ? (int)swComponentSolvingOption_e.swComponentFlexibleSolving : (int)swComponentSolvingOption_e.swComponentRigidSolving,
                    true, !string.IsNullOrEmpty(c.Configuration), c.Configuration ?? "", false, false);
                model.ClearSelection2(true);
                if (!set) throw new InvalidOperationException("Could not set the configuration or the solving of " + comp.Name2 + ".");
            }
            model.ClearSelection2(true);
            comp.Select4(false, null, false);
            if (c.Fixed) assembly.FixComponent(); else assembly.UnfixComponent();
            model.ClearSelection2(true);
            added.Add(comp.Name2);
        }

        var mates = new List<string>();
        foreach (var m in spec.Mates)
        {
            model.ClearSelection2(true);
            CadderSelectEntity(model, assembly, m.A, "mate " + mates.Count + " a");
            CadderSelectEntity(model, assembly, m.B, "mate " + mates.Count + " b");
            int status;
            var mate = assembly.AddMate5(CadderMateType(m.Type), CadderAlign(m.Align), false, m.Value, m.Max, m.Min, 1.0, 1.0,
                m.Value, m.Max, m.Min, false, false, 0, out status) as Feature;
            model.ClearSelection2(true);
            if (mate == null) throw new InvalidOperationException("Mate " + mates.Count + " refused (status " + status + ").");
            if (!string.IsNullOrEmpty(m.Name)) mate.Name = m.Name;
            mates.Add(mate.Name);
        }
        model.EditRebuild3();
        int saveErr = 0, saveWarn = 0;
        if (!model.Extension.SaveAs(spec.SaveAs, (int)swSaveAsVersion_e.swSaveAsCurrentVersion, (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                null, ref saveErr, ref saveWarn))
            throw new InvalidOperationException("The save failed (error " + saveErr + ", warning " + saveWarn + ").");
        return new { document = spec.SaveAs, components = added, mates };
    }

    /// <summary>
    /// Adds a configuration to an open document, with dimensions and
    /// suppressions in it, and shows a configuration. Saves only a document
    /// under the temp folder. (Was the lab operation configure.)
    /// </summary>
    private object CadderConfigure(CadderConfigureSpec spec)
    {
        if (spec == null) throw new ArgumentException("Give a CadderConfigureSpec.");
        ModelDoc2 model = doc;
        if (!string.IsNullOrEmpty(spec.Document))
        {
            model = null;
            for (var d = swApp.GetFirstDocument() as ModelDoc2; d != null; d = d.GetNext() as ModelDoc2)
                if (string.Equals(d.GetTitle(), spec.Document, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(d.GetPathName(), spec.Document, StringComparison.OrdinalIgnoreCase)) { model = d; break; }
        }
        if (model == null) throw new InvalidOperationException("No such document is open: " + (spec.Document ?? "(no active document)"));
        string path = model.GetPathName();
        if (spec.Save) CadderTempOnly(path);
        string only = spec.Add;
        if (!string.IsNullOrEmpty(only) && model.AddConfiguration3(only, "", "", 0) == null)
            throw new InvalidOperationException("Could not add the configuration " + only + ".");
        var set = new List<string>();
        foreach (var kv in spec.Dimensions)
        {
            var dim = model.Parameter(kv.Key) as Dimension;
            if (dim == null) throw new InvalidOperationException("No dimension named " + kv.Key + ".");
            int result = string.IsNullOrEmpty(only)
                ? dim.SetSystemValue3(kv.Value, (int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration, null)
                : dim.SetSystemValue3(kv.Value, (int)swSetValueInConfiguration_e.swSetValue_InSpecificConfigurations, new[] { only });
            set.Add(kv.Key + " status " + result);
        }
        foreach (string name in spec.Suppress)
        {
            var feat = Features(model, true).FirstOrDefault(f => f.Name == name);
            if (feat == null) throw new InvalidOperationException("No feature named " + name + ".");
            bool done = string.IsNullOrEmpty(only)
                ? feat.SetSuppression2((int)swFeatureSuppressionAction_e.swSuppressFeature, (int)swInConfigurationOpts_e.swThisConfiguration, null)
                : feat.SetSuppression2((int)swFeatureSuppressionAction_e.swSuppressFeature, (int)swInConfigurationOpts_e.swSpecifyConfiguration, new[] { only });
            set.Add(name + (done ? " suppressed" : " not suppressed"));
        }
        // ShowConfiguration2 returns false for the configuration that is shown already
        // (a configuration that was just added is shown).
        if (!string.IsNullOrEmpty(spec.Show) && model.ConfigurationManager.ActiveConfiguration.Name != spec.Show
            && !model.ShowConfiguration2(spec.Show))
            throw new InvalidOperationException("Could not show the configuration " + spec.Show + ".");
        model.EditRebuild3();
        if (spec.Save)
        {
            int err = 0, warn = 0;
            if (!model.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref err, ref warn))
                throw new InvalidOperationException("The save failed (error " + err + ", warning " + warn + ").");
        }
        return new
        {
            document = path,
            configurations = model.GetConfigurationNames() as string[],
            shown = model.ConfigurationManager.ActiveConfiguration.Name,
            set,
        };
    }

    /// <summary>Stops a save that is not under the temp folder.</summary>
    private static void CadderTempOnly(string path)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("A path under the temp folder is necessary.");
        string full = Path.GetFullPath(path), temp = Path.GetFullPath(Path.GetTempPath());
        if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A CADder test model is saved only under " + temp + ", not at " + full + ".");
    }

    private static void CadderSelectEntity(ModelDoc2 model, AssemblyDoc assembly, CadderComposeEntity e, string what)
    {
        if (e == null || string.IsNullOrEmpty(e.Feature)) throw new ArgumentException(what + ": no feature.");
        Feature feat;
        if (string.IsNullOrEmpty(e.Component))
        {
            feat = assembly.FeatureByName(e.Feature) as Feature;
        }
        else
        {
            var all = ((object[])assembly.GetComponents(false) ?? new object[0]).Cast<Component2>().ToList();
            var comp = assembly.GetComponentByName(e.Component) as Component2
                ?? all.FirstOrDefault(c => string.Equals(c.Name2, e.Component, StringComparison.OrdinalIgnoreCase));
            if (comp == null) throw new InvalidOperationException(what + ": no component " + e.Component + " (have " + string.Join(", ", all.Select(c => c.Name2)) + ").");
            feat = comp.FeatureByName(e.Feature) as Feature;
        }
        if (feat == null) throw new InvalidOperationException(what + ": no feature " + e.Feature + (e.Component == null ? "" : " on " + e.Component) + ".");
        if (!feat.Select2(true, 1)) throw new InvalidOperationException(what + ": could not select " + e.Feature + ".");
    }

    private static int CadderMateType(string type)
    {
        switch ((type ?? "").ToLowerInvariant())
        {
            case "concentric": return (int)swMateType_e.swMateCONCENTRIC;
            case "parallel": return (int)swMateType_e.swMatePARALLEL;
            case "perpendicular": return (int)swMateType_e.swMatePERPENDICULAR;
            case "distance": return (int)swMateType_e.swMateDISTANCE;
            case "angle": return (int)swMateType_e.swMateANGLE;
            case "tangent": return (int)swMateType_e.swMateTANGENT;
            default: return (int)swMateType_e.swMateCOINCIDENT;
        }
    }

    private static int CadderAlign(string align)
    {
        switch ((align ?? "").ToLowerInvariant())
        {
            case "aligned": return (int)swMateAlign_e.swMateAlignALIGNED;
            case "anti": return (int)swMateAlign_e.swMateAlignANTI_ALIGNED;
            default: return (int)swMateAlign_e.swMateAlignCLOSEST;
        }
    }
}
