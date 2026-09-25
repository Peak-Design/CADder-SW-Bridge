---
title: CADder Bridge mate reading, constraint status probe and component drag
applies to: development of the CADder Bridge: the mate readers and the solver probes, on an assembly in a lab SolidWorks
tags: cadder, bridge, mates, mate graph, reader, classifier, constrained status, dof, probe, drag, move, limit
run: return CadderMates();
uses: cadder-bridge
---
## What it does

- `CadderMates()`: the mate graph of the active assembly as the Bridge
  reads it, before the classifier. Each component with its place, its
  status and its solving, and each mate with its type, entities, limits and
  suppression. The log lines of the readers come with it. Use it to check
  the reading before you blame the classifier.
- `CadderStatusProbe(couplings, rebuild)`: the constraint status of each
  component with all mates, with the limit mates (and the coupling mates
  when `couplings` is true) taken out, and after they are back. It also
  gives the drift of each component over the round trip. `rebuild` is
  `"edit"`, `"mates"` or `"force"`. The mates go back also when the probe
  fails.
- `CadderDrag(component, axis, angle, origin)`: drags one component with
  the mover of the Bridge, as a user drags it. `component` is the instance
  path, for example `"lifterassy-1/rod-1"`. With `angle` 0 it moves along
  `axis` by the length of `axis` in metres. Else it turns by `angle`
  radians about `axis` through `origin`.

## Steps

1. A lab session with the assembly open and active (see `cadder-bridge`).
2. Use `run` with recipe `cadder-mates`, or give code:

```
return CadderStatusProbe(true, "edit");
return CadderDrag("rod-1", new[] { 0.0, 0.0, 0.01 });
return CadderDrag("crank-1", new[] { 0.0, 0.0, 1.0 }, FromDeg(30), new[] { 0.0, 0.0, 0.0 });
```

A drag and a probe change the assembly in memory. A drag also changes the
stored value of a limit mate. Close the document without a save, or undo,
before the next test. To see the drag in Blender, use `CadderPushPoses()`
of `cadder-send`.
