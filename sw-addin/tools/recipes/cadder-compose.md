---
title: CADder Bridge test models: a test assembly from a spec, and a configuration
applies to: development of the CADder Bridge: cases that no corpus assembly has, saved only under the temp folder
tags: cadder, bridge, compose, test assembly, configuration, flexible, subassembly, mate, limit, fixture
uses: cadder-bridge
---
## What it does

- `CadderCompose(spec)`: builds a test assembly: components with their
  place, configuration, flexible solving and fixed state, and mates
  between their features. It saves the assembly only under the temp
  folder, and never over a file that exists.
- `CadderConfigure(spec)`: adds a configuration to an open document, with
  dimensions and suppressions in it, and shows a configuration. It saves
  only a document under the temp folder.

Use them for a case that no corpus assembly has, for example a
subassembly instance that uses another configuration than its document
shows. The recipe has no run line: give the spec in the code of the run.

## Example

```
string tmp = Path.Combine(Path.GetTempPath(), "cadder-lab");
Directory.CreateDirectory(tmp);
return CadderCompose(new CadderComposeSpec
{
    SaveAs = Path.Combine(tmp, "flex-top.SLDASM"),
    Components =
    {
        new CadderComposeComponent { File = @"C:\PeakDesign\CADder-SW-Bridge\test-assemblies\01-hinge\hinge.SLDASM", Flexible = true, Fixed = true },
    },
    Mates =
    {
        new CadderComposeMate
        {
            Type = "coincident", Align = "aligned",
            A = new CadderComposeEntity { Component = "hinge-1", Feature = "Top Plane" },
            B = new CadderComposeEntity { Feature = "Top Plane" },
        },
    },
});
```

```
return CadderConfigure(new CadderConfigureSpec
{
    Add = "Wide",
    Dimensions = { { "D2@LimitAngle1", 1.2 } },
    Suppress = { "Coincident3" },
    Show = "Default",
    Save = true,
});
```

Set `undo_label` on the runs. Open the saved assembly again for the export
test (`cadder-export`).
