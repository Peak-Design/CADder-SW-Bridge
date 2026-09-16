using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Peak.SwToBlender.Core;
using Peak.SwToBlender.Core.Model;
using Xunit;

namespace Peak.SwToBlender.Tests
{
    public class ManifestWriterTests
    {
        // ── Validity and shape ──────────────────────────────────────────────

        [Fact]
        public void OutputIsValidJson()
        {
            var parsed = TestJson.Parse(ManifestWriter.Write(BuildFullManifest()));

            Assert.Equal(JsonKind.Object, parsed.Kind);
            Assert.Equal(
                new[]
                {
                    "manifest_version", "generator", "units", "frame", "step_export",
                    "components", "rigid_groups", "joints", "loops", "mechanisms", "warnings",
                },
                parsed.Keys);
        }

        [Fact]
        public void NonAsciiStringsSurviveTheTrip()
        {
            var manifest = BuildFullManifest();
            manifest.Components[0].StepName = "Gehäuse-Ø12 \"rev\"\t1";

            var parsed = TestJson.Parse(ManifestWriter.Write(manifest));

            Assert.Equal("Gehäuse-Ø12 \"rev\"\t1", parsed["components"].Items[0]["step_name"].Str);
        }

        /// <summary>Every key the writer can emit, checked against the
        /// property names in schema/rig-manifest.schema.json. The lists here
        /// restate the schema on purpose: a writer key drifting out of
        /// snake_case, or a new key missing from the schema, fails loudly.</summary>
        [Fact]
        public void KeysMatchSchemaExactly()
        {
            var parsed = TestJson.Parse(ManifestWriter.Write(BuildFullManifest()));

            Assert.Equal(new[] { "name", "version", "solidworks_version", "exported_utc" },
                parsed["generator"].Keys);
            Assert.Equal(new[] { "length", "angle" }, parsed["units"].Keys);
            Assert.Equal(new[] { "handedness", "up_axis", "transform_convention" },
                parsed["frame"].Keys);
            Assert.Equal(new[] { "file", "ap", "sha1", "occurrence_matching" },
                parsed["step_export"].Keys);

            var component = parsed["components"].Items[0];
            Assert.Equal(
                new[]
                {
                    "id", "sw_path", "sw_persistent_id", "step_name", "step_occurrence_path",
                    "transform", "bbox_local", "suppressed", "subassembly_solving",
                },
                component.Keys);
            Assert.Equal(new[] { "min", "max" }, component["bbox_local"].Keys);

            Assert.Equal(new[] { "id", "name", "components", "grounded", "frame", "bbox_diag" },
                parsed["rigid_groups"].Items[0].Keys);

            var joint = parsed["joints"].Items[0];
            Assert.Equal(
                new[]
                {
                    "id", "type", "parent_group", "child_group", "origin", "axis",
                    "secondary_axis", "limits", "coupling", "source_mates", "confidence", "notes",
                },
                joint.Keys);
            Assert.Equal(new[] { "rotation", "translation" }, joint["limits"].Keys);
            Assert.Equal(new[] { "min", "max", "value_at_rest" }, joint["limits"]["rotation"].Keys);
            Assert.Equal(new[] { "kind", "driver_joint", "ratio", "meters_per_radian", "lead_m_per_rev" },
                joint["coupling"].Keys);
            Assert.Equal(new[] { "sw_feature", "type" }, joint["source_mates"].Items[0].Keys);

            Assert.Equal(
                new[] { "id", "member_joints", "closure_joint", "closure_kind", "suggested_driver_joint", "planar", "plane_normal", "driver_candidates" },
                parsed["loops"].Items[0].Keys);
            var mechanism = parsed["mechanisms"].Items[0];
            Assert.Equal(new[] { "id", "loops", "inputs" }, mechanism.Keys);
            Assert.Equal(new[] { "joint", "loops", "flipped_joints", "joint_limits" },
                mechanism["inputs"].Items[1].Keys);
            Assert.Equal(new[] { "joint", "limits" },
                mechanism["inputs"].Items[1]["joint_limits"].Items[0].Keys);
            Assert.Equal(parsed["loops"].Items[0].Keys,
                mechanism["inputs"].Items[1]["loops"].Items[0].Keys);
            Assert.Equal(new[] { "code", "components", "joints", "message" },
                parsed["warnings"].Items[0].Keys);

            AssertAllKeysSnakeCase(parsed);
        }

        private static void AssertAllKeysSnakeCase(JsonValue value)
        {
            if (value.Kind == JsonKind.Object)
            {
                foreach (var kv in value.Members)
                {
                    foreach (char ch in kv.Key)
                        Assert.True((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '_',
                            "Key not snake_case: " + kv.Key);
                    AssertAllKeysSnakeCase(kv.Value);
                }
            }
            else if (value.Kind == JsonKind.Array)
            {
                foreach (var item in value.Items) AssertAllKeysSnakeCase(item);
            }
        }

        // ── Culture, BOM, determinism ───────────────────────────────────────

        /// <summary>tr-TR writes 0,0745 and dots the capital I; a culture leak
        /// anywhere in the writer changes the output text.</summary>
        [Fact]
        public void DoublesAreCultureInvariantUnderTurkishCulture()
        {
            var manifest = BuildFullManifest();
            string invariant = ManifestWriter.Write(manifest);

            var oldCulture = Thread.CurrentThread.CurrentCulture;
            var oldUiCulture = Thread.CurrentThread.CurrentUICulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("tr-TR");
                Thread.CurrentThread.CurrentUICulture = new CultureInfo("tr-TR");
                string turkish = ManifestWriter.Write(manifest);

                Assert.Equal(invariant, turkish);
                Assert.Contains("0.0745", turkish);
                TestJson.Parse(turkish);    // the strict parser rejects comma decimals
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = oldCulture;
                Thread.CurrentThread.CurrentUICulture = oldUiCulture;
            }
        }

        [Fact]
        public void FileOutputHasNoBom()
        {
            var manifest = BuildFullManifest();
            string path = Path.Combine(Path.GetTempPath(), "sw2b-writer-" + Guid.NewGuid().ToString("N") + ".rig.json");
            try
            {
                ManifestWriter.WriteFile(manifest, path);
                byte[] bytes = File.ReadAllBytes(path);

                Assert.True(bytes.Length > 3, "file is implausibly small");
                Assert.False(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "file starts with a BOM");
                Assert.Equal(new UTF8Encoding(false).GetBytes(ManifestWriter.Write(manifest)), bytes);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void OutputIsIdenticalAcrossRuns()
        {
            var manifest = BuildFullManifest();
            Assert.Equal(ManifestWriter.Write(manifest), ManifestWriter.Write(manifest));
        }

        // ── Golden ──────────────────────────────────────────────────────────

        /// <summary>Builds the hinge golden's manifest in code and compares
        /// the written JSON with schema/examples/hinge.rig.json token by
        /// token. The generator object is ignored: the golden carries a
        /// hand-written note field there.</summary>
        [Fact]
        public void HingeGoldenMatchesFieldForField()
        {
            var golden = TestJson.Parse(File.ReadAllText(FindRepoFile("schema/examples/hinge.rig.json")));
            var written = TestJson.Parse(ManifestWriter.Write(BuildHingeManifest()));

            golden.Remove("generator");
            written.Remove("generator");

            string diff;
            bool equal = TestJson.DeepEquals(written, golden, out diff);
            Assert.True(equal, "Golden mismatch at " + diff);
        }

        // ── Fixtures ────────────────────────────────────────────────────────

        private static double[,] Translation(double x, double y, double z)
        {
            var m = MathOps.Identity4();
            m[0, 3] = x;
            m[1, 3] = y;
            m[2, 3] = z;
            return m;
        }

        /// <summary>The golden's content, restated in code. Values must track
        /// schema/examples/hinge.rig.json exactly.</summary>
        private static RigManifest BuildHingeManifest()
        {
            var m = new RigManifest();
            m.ManifestVersion = "1.0.0";
            m.StepExport.File = "hinge.step";
            m.StepExport.Ap = "AP214";
            m.StepExport.Sha1 = null;
            m.StepExport.OccurrenceMatching = null;

            m.Components.Add(new ManifestComponent
            {
                Id = "c001",
                SwPath = "hinge-base-1",
                SwPersistentId = null,
                StepName = "hinge-base",
                StepOccurrencePath = "hinge/hinge-base-1",
                Transform = MathOps.Identity4(),
                BboxMin = new[] { -0.03, -0.02, 0.0 },
                BboxMax = new[] { 0.03, 0.02, 0.018 },
            });
            m.Components.Add(new ManifestComponent
            {
                Id = "c002",
                SwPath = "hinge-leaf-1",
                SwPersistentId = null,
                StepName = "hinge-leaf",
                StepOccurrencePath = "hinge/hinge-leaf-1",
                Transform = Translation(0, 0, 0.01),
                BboxMin = new[] { -0.005, -0.02, 0.0 },
                BboxMax = new[] { 0.05, 0.02, 0.008 },
            });

            var g000 = new RigidGroup();
            g000.Id = "g000";
            g000.Name = "hinge-base";
            g000.Components.Add("c001");
            g000.Grounded = true;
            g000.Frame = MathOps.Identity4();
            g000.BboxDiag = 0.0745;
            m.RigidGroups.Add(g000);

            var g001 = new RigidGroup();
            g001.Id = "g001";
            g001.Name = "hinge-leaf";
            g001.Components.Add("c002");
            g001.Grounded = false;
            g001.Frame = null;
            g001.BboxDiag = 0.0685;
            m.RigidGroups.Add(g001);

            var joint = new RigJoint();
            joint.Id = "j001";
            joint.Type = JointType.Revolute;
            joint.ParentGroup = "g000";
            joint.ChildGroup = "g001";
            joint.Origin = new double[] { 0, 0, 0 };
            joint.Axis = new double[] { 0, 0, 1 };
            joint.SecondaryAxis = new double[] { 1, 0, 0 };
            joint.RotationLimit = new JointLimit { Min = -0.5236, Max = 0.7854, ValueAtRest = 0.0 };
            joint.SourceMates.Add(new SourceMate { SwFeature = "Concentric1", Type = "swMateCONCENTRIC" });
            joint.SourceMates.Add(new SourceMate { SwFeature = "Coincident1", Type = "swMateCOINCIDENT" });
            joint.SourceMates.Add(new SourceMate { SwFeature = "LimitAngle1", Type = "swMateANGLE" });
            m.Joints.Add(joint);

            return m;
        }

        /// <summary>Exercises every field the writer can emit, including the
        /// null branches and the number formats that have burned upstream:
        /// integer-valued doubles, tiny exponents, negatives.</summary>
        private static RigManifest BuildFullManifest()
        {
            var m = new RigManifest();
            m.Generator.Version = "0.1.0";
            m.Generator.SolidWorksVersion = "SOLIDWORKS 2024 SP5.0";
            m.Generator.ExportedUtc = "2026-08-21T10:00:00Z";
            m.StepExport.File = "rig.step";
            m.StepExport.Ap = "AP203";
            m.StepExport.Sha1 = "0123456789abcdef0123456789abcdef01234567";
            m.StepExport.OccurrenceMatching = "matcher-v1";

            m.Components.Add(new ManifestComponent
            {
                Id = "c001",
                SwPath = "rig/base-1",
                SwPersistentId = "UEsDBA==",
                StepName = "base",
                StepOccurrencePath = "rig/base-1",
                Transform = Translation(1000000.0, -0.25, 1e-11),
                BboxMin = new[] { -0.1, -0.1, 0.0 },
                BboxMax = new[] { 0.1, 0.1, 0.0745 },
                SubassemblySolving = "flexible",
            });
            m.Components.Add(new ManifestComponent
            {
                Id = "c002",
                SwPath = "rig/ghost-1",
                SwPersistentId = null,
                StepName = "ghost",
                StepOccurrencePath = null,
                Transform = MathOps.Identity4(),
                BboxMin = null,
                BboxMax = null,
                Suppressed = true,
            });

            var g000 = new RigidGroup();
            g000.Id = "g000";
            g000.Name = "base";
            g000.Components.Add("c001");
            g000.Grounded = true;
            g000.Frame = MathOps.Identity4();
            g000.BboxDiag = 0.0745;
            m.RigidGroups.Add(g000);

            var g001 = new RigidGroup();
            g001.Id = "g001";
            g001.Name = "arm";
            g001.Components.Add("c002");
            g001.Grounded = false;
            g001.Frame = null;
            g001.BboxDiag = null;
            m.RigidGroups.Add(g001);

            var j001 = new RigJoint();
            j001.Id = "j001";
            j001.Type = JointType.Revolute;
            j001.ParentGroup = "g000";
            j001.ChildGroup = "g001";
            j001.Origin = new double[] { 0, 0, 0.01 };
            j001.Axis = new double[] { 0, 0, 1 };
            j001.SecondaryAxis = new double[] { 1, 0, 0 };
            j001.RotationLimit = new JointLimit { Min = -0.5236, Max = 0.7854, ValueAtRest = 0.1 };
            j001.TranslationLimit = new JointLimit { Min = 0.0, Max = 0.01, ValueAtRest = 0.002 };
            j001.Coupling = new JointCoupling
            {
                Kind = "gear",
                DriverJoint = "j002",
                Ratio = -2.0,
                MetersPerRadian = null,
                LeadMPerRev = null,
            };
            j001.SourceMates.Add(new SourceMate { SwFeature = "Concentric1", Type = "swMateCONCENTRIC" });
            j001.Notes = "fixture joint";
            m.Joints.Add(j001);

            var j002 = new RigJoint();
            j002.Id = "j002";
            j002.Type = JointType.Free;
            j002.ParentGroup = "g000";
            j002.ChildGroup = "g001";
            j002.Confidence = "low";
            m.Joints.Add(j002);

            var loop = new RigLoop();
            loop.Id = "loop001";
            loop.MemberJoints.Add("j001");
            loop.MemberJoints.Add("j002");
            loop.MemberJoints.Add("j003");
            loop.ClosureJoint = "j002";
            loop.SuggestedDriverJoint = "j001";
            loop.Planar = true;
            loop.PlaneNormal = new double[] { 0, 0, 1 };
            m.Loops.Add(loop);

            var mech = new RigMechanism();
            mech.Id = "mech001";
            mech.LoopIds.Add("loop001");
            var chosen = new RigInputOption();
            chosen.Joint = "j001";
            chosen.Loops.Add(loop);
            mech.Inputs.Add(chosen);
            var other = new RigInputOption();
            other.Joint = "j003";
            var altLoop = new RigLoop();
            altLoop.Id = "loop001";
            altLoop.MemberJoints.AddRange(loop.MemberJoints);
            altLoop.ClosureJoint = "j001";
            altLoop.SuggestedDriverJoint = "j003";
            other.Loops.Add(altLoop);
            other.FlippedJoints.Add("j002");
            other.JointLimits.Add(new RigOptionLimit
            {
                Joint = "j001",
                RotationLimit = null,
                TranslationLimit = new JointLimit { Min = -0.01, Max = 0.01, ValueAtRest = 0.0 },
            });
            mech.Inputs.Add(other);
            m.Mechanisms.Add(mech);

            var warning = new ManifestWarning();
            warning.Code = "UNDER_DEFINED";
            warning.Components.Add("c002");
            warning.Joints.Add("j002");
            warning.Message = "fixture warning";
            m.Warnings.Add(warning);

            return m;
        }

        /// <summary>The golden lives in the repo, the tests run from bin\:
        /// walk up from the test assembly until the repo-relative path
        /// resolves.</summary>
        private static string FindRepoFile(string relative)
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            throw new FileNotFoundException(relative + " not found above " + AppDomain.CurrentDomain.BaseDirectory);
        }
    }
}
