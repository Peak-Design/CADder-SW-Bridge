# Third-party notices: sw-addin

## SW2URDF (solidworks_urdf_exporter)

Portions of this add-in are adapted from SW2URDF,
https://github.com/ros/solidworks_urdf_exporter, MIT licence,
Copyright (c) 2015–2020 Stephen Brawner. The full licence text is in
`vendor\sw2urdf\LICENSE`.

Vendored from upstream commit `c8b70b5069c69c290c83c95529052fc9f9e6ff63`
(master, fetched 2026-08-21). Reference copies of the upstream files live in
`vendor\sw2urdf\` (not compiled); to check for relevant upstream changes, diff
those files against the same paths at a newer upstream commit.

| Upstream file | Used as | In this tree |
|---|---|---|
| `SW2URDF/Utilities/MathOPS.cs` | Adapted (MathNet removed, MathTransform overloads split off) | `src/Peak.Cadder/Core/MathOps.cs`, `src/Peak.Cadder/Sw/SwFrames.cs` |
| `SW2URDF/Test/TestMathOps.cs` | Ported MSTest → xUnit | `tests/Peak.Cadder.Tests/MathOpsTests.cs` |
| `SW2URDF/URDFExport/ExportHelperExtension.cs` ("Joint methods" region) | Transcribed: the GetRemainingDOFs fix/suppress/query/restore sequence and the bounding-box origin heuristic | `src/Peak.Cadder/Sw/DofProbe.cs`, `src/Peak.Cadder/Sw/LimitExtractor.cs` |
| `SW2URDF/URDFExport/CommonSwOperations.cs` (PID save/load) | Transcribed | `src/Peak.Cadder/Sw/ComponentIdentity.cs` |

The mate-classification algorithm in `Core/JointClassifier.cs` is an original
implementation. Its design was informed by reading
`jsk-ros-pkg/solidworks_urdf_exporter2` (Apache-2.0) as prior art; no code was
copied from that project.
