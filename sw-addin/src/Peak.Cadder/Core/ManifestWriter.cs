using System;
using System.Globalization;
using System.IO;
using System.Text;
using Peak.Cadder.Core.Model;

namespace Peak.Cadder.Core
{
    /// <summary>
    /// Hand-written JSON emitter for the rig manifest. House rule: no
    /// Newtonsoft, no System.Text.Json on net48: this ~200-line writer keeps
    /// the add-in dependency-free, and the manifest's shape is fixed enough
    /// that a general serializer buys nothing. Key names and their order come
    /// from schema/rig-manifest.schema.json; arrays keep list order, so two
    /// runs over the same manifest produce byte-identical output.
    /// </summary>
    public static class ManifestWriter
    {
        public static string Write(RigManifest m)
        {
            var w = new JsonWriter();
            w.BeginObject();

            w.Field("manifest_version").String(m.ManifestVersion);

            w.Field("generator").BeginObject();
            w.Field("name").String(m.Generator.Name);
            w.Field("version").String(m.Generator.Version);
            if (m.Generator.SolidWorksVersion != null)
                w.Field("solidworks_version").String(m.Generator.SolidWorksVersion);
            if (m.Generator.ExportedUtc != null)
                w.Field("exported_utc").String(m.Generator.ExportedUtc);
            w.EndObject();

            w.Field("units").BeginObject();
            w.Field("length").String("meter");
            w.Field("angle").String("radian");
            w.EndObject();

            w.Field("frame").BeginObject();
            w.Field("handedness").String("right");
            w.Field("up_axis").String("Z");
            w.Field("transform_convention").String("row_major_4x4_global");
            w.EndObject();

            w.Field("step_export").BeginObject();
            w.Field("file").String(m.StepExport.File);
            if (m.StepExport.Ap != null) w.Field("ap").String(m.StepExport.Ap);
            w.Field("sha1").String(m.StepExport.Sha1);
            w.Field("occurrence_matching").String(m.StepExport.OccurrenceMatching);
            w.EndObject();

            w.Field("components").BeginArray();
            foreach (var c in m.Components)
            {
                w.BeginObject();
                w.Field("id").String(c.Id);
                w.Field("sw_path").String(c.SwPath);
                w.Field("sw_persistent_id").String(c.SwPersistentId);
                w.Field("step_name").String(c.StepName);
                w.Field("step_occurrence_path").String(c.StepOccurrencePath);
                w.Field("transform").Matrix(c.Transform);
                w.Field("bbox_local");
                if (c.BboxMin == null || c.BboxMax == null) w.Null();
                else
                {
                    w.BeginObject();
                    w.Field("min").Vector(c.BboxMin);
                    w.Field("max").Vector(c.BboxMax);
                    w.EndObject();
                }
                w.Field("suppressed").Bool(c.Suppressed);
                w.Field("subassembly_solving").String(c.SubassemblySolving);
                w.EndObject();
            }
            w.EndArray();

            w.Field("rigid_groups").BeginArray();
            foreach (var g in m.RigidGroups)
            {
                w.BeginObject();
                w.Field("id").String(g.Id);
                w.Field("name").String(g.Name);
                w.Field("components").BeginArray();
                foreach (var id in g.Components) w.String(id);
                w.EndArray();
                w.Field("grounded").Bool(g.Grounded);
                w.Field("frame").Matrix(g.Frame);
                w.Field("bbox_diag").Number(g.BboxDiag);
                w.EndObject();
            }
            w.EndArray();

            w.Field("joints").BeginArray();
            foreach (var j in m.Joints)
            {
                w.BeginObject();
                w.Field("id").String(j.Id);
                w.Field("type").String(j.Type);
                w.Field("parent_group").String(j.ParentGroup);
                w.Field("child_group").String(j.ChildGroup);
                w.Field("origin").Vector(j.Origin);
                w.Field("axis").Vector(j.Axis);
                w.Field("secondary_axis").Vector(j.SecondaryAxis);
                w.Field("limits");
                if (j.RotationLimit == null && j.TranslationLimit == null) w.Null();
                else
                {
                    w.BeginObject();
                    w.Field("rotation").Limit(j.RotationLimit);
                    w.Field("translation").Limit(j.TranslationLimit);
                    w.EndObject();
                }
                w.Field("coupling");
                if (j.Coupling == null) w.Null();
                else
                {
                    w.BeginObject();
                    w.Field("kind").String(j.Coupling.Kind);
                    w.Field("driver_joint").String(j.Coupling.DriverJoint);
                    w.Field("ratio").Number(j.Coupling.Ratio);
                    w.Field("meters_per_radian").Number(j.Coupling.MetersPerRadian);
                    w.Field("lead_m_per_rev").Number(j.Coupling.LeadMPerRev);
                    if (j.Coupling.Samples != null)
                    {
                        w.Field("samples").BeginArray();
                        foreach (var s in j.Coupling.Samples)
                        {
                            w.BeginArray();
                            w.Number(s[0]);
                            w.Number(s[1]);
                            w.EndArray();
                        }
                        w.EndArray();
                        w.Field("periodic").Bool(j.Coupling.Periodic);
                        w.Field("period").Number(j.Coupling.Period);
                    }
                    if (j.Coupling.MirrorPlaneNormal != null)
                    {
                        w.Field("mirror_scope").String(j.Coupling.MirrorScope);
                        w.Field("mirror_plane").BeginObject();
                        w.Field("point").Vector(j.Coupling.MirrorPlanePoint);
                        w.Field("normal").Vector(j.Coupling.MirrorPlaneNormal);
                        w.EndObject();
                    }
                    if (j.Coupling.Kind == "cam" && j.Coupling.CamSurfacePoints != null)
                    {
                        w.Field("cam").BeginObject();
                        w.Field("axis").Vector(j.Coupling.CamAxis);
                        w.Field("origin").Vector(j.Coupling.CamOrigin);
                        w.Field("surface").BeginObject();
                        w.Field("points").BeginArray();
                        foreach (var p in j.Coupling.CamSurfacePoints) w.Vector(p);
                        w.EndArray();
                        w.Field("triangles").BeginArray();
                        foreach (var t in j.Coupling.CamSurfaceTriangles) w.Indices(t);
                        w.EndArray();
                        w.EndObject();
                        w.Field("follower").BeginObject();
                        w.Field("kind").String(j.Coupling.FollowerKind);
                        w.Field("point").Vector(j.Coupling.FollowerPoint);
                        w.Field("axis").Vector(j.Coupling.FollowerAxis);
                        w.Field("radius").Number(j.Coupling.FollowerRadius);
                        w.Field("normal").Vector(j.Coupling.FollowerNormal);
                        w.EndObject();
                        w.EndObject();
                    }
                    w.EndObject();
                }
                // Only path joints carry a curve, only surface joints carry a
                // patch; every other type omits the field entirely (the
                // schema keeps both optional).
                if (j.PathPoints != null)
                {
                    w.Field("path").BeginObject();
                    w.Field("points").BeginArray();
                    foreach (var p in j.PathPoints) w.Vector(p);
                    w.EndArray();
                    w.Field("closed").Bool(j.PathClosed);
                    w.EndObject();
                }
                if (j.SurfacePoints != null && j.SurfaceTriangles != null)
                {
                    w.Field("surface").BeginObject();
                    w.Field("points").BeginArray();
                    foreach (var p in j.SurfacePoints) w.Vector(p);
                    w.EndArray();
                    w.Field("triangles").BeginArray();
                    foreach (var t in j.SurfaceTriangles) w.Indices(t);
                    w.EndArray();
                    w.EndObject();
                }
                w.Field("source_mates").BeginArray();
                foreach (var s in j.SourceMates)
                {
                    w.BeginObject();
                    w.Field("sw_feature").String(s.SwFeature);
                    w.Field("type").String(s.Type);
                    w.EndObject();
                }
                w.EndArray();
                w.Field("confidence").String(j.Confidence);
                w.Field("notes").String(j.Notes);
                w.EndObject();
            }
            w.EndArray();

            w.Field("loops").BeginArray();
            foreach (var l in m.Loops) WriteLoop(w, l);
            w.EndArray();

            w.Field("mechanisms").BeginArray();
            foreach (var mech in m.Mechanisms)
            {
                w.BeginObject();
                w.Field("id").String(mech.Id);
                w.Field("loops").BeginArray();
                foreach (var id in mech.LoopIds) w.String(id);
                w.EndArray();
                w.Field("inputs").BeginArray();
                foreach (var opt in mech.Inputs)
                {
                    w.BeginObject();
                    w.Field("joint").String(opt.Joint);
                    w.Field("loops").BeginArray();
                    foreach (var l in opt.Loops) WriteLoop(w, l);
                    w.EndArray();
                    w.Field("flipped_joints").BeginArray();
                    foreach (var id in opt.FlippedJoints) w.String(id);
                    w.EndArray();
                    w.Field("joint_limits").BeginArray();
                    foreach (var lim in opt.JointLimits)
                    {
                        w.BeginObject();
                        w.Field("joint").String(lim.Joint);
                        w.Field("limits");
                        if (lim.RotationLimit == null && lim.TranslationLimit == null) w.Null();
                        else
                        {
                            w.BeginObject();
                            w.Field("rotation").Limit(lim.RotationLimit);
                            w.Field("translation").Limit(lim.TranslationLimit);
                            w.EndObject();
                        }
                        w.EndObject();
                    }
                    w.EndArray();
                    w.EndObject();
                }
                w.EndArray();
                w.EndObject();
            }
            w.EndArray();

            w.Field("warnings").BeginArray();
            foreach (var warning in m.Warnings)
            {
                w.BeginObject();
                w.Field("code").String(warning.Code);
                w.Field("components").BeginArray();
                foreach (var id in warning.Components) w.String(id);
                w.EndArray();
                w.Field("joints").BeginArray();
                foreach (var id in warning.Joints) w.String(id);
                w.EndArray();
                w.Field("message").String(warning.Message);
                w.EndObject();
            }
            w.EndArray();

            w.EndObject();
            return w.ToString();
        }

        /// <summary>UTF-8 without BOM. A BOM'd or ASCII-mangled manifest is a
        /// known upstream bug class: a BOM breaks strict JSON consumers and
        /// non-ASCII component names must survive the trip intact.</summary>
        public static void WriteFile(RigManifest m, string path)
        {
            File.WriteAllText(path, Write(m), new UTF8Encoding(false));
        }

        // ── The emitter ─────────────────────────────────────────────────────

        private static void WriteLoop(JsonWriter w, RigLoop l)
        {
            w.BeginObject();
            w.Field("id").String(l.Id);
            w.Field("member_joints").BeginArray();
            foreach (var id in l.MemberJoints) w.String(id);
            w.EndArray();
            w.Field("closure_joint").String(l.ClosureJoint);
            w.Field("closure_kind").String(l.ClosureKind);
            w.Field("suggested_driver_joint").String(l.SuggestedDriverJoint);
            w.Field("planar").Bool(l.Planar);
            w.Field("plane_normal").Vector(l.PlaneNormal);
            w.Field("driver_candidates").BeginArray();
            foreach (var c in l.DriverCandidates)
            {
                w.BeginObject();
                w.Field("joint").String(c.DriverJoint);
                w.Field("closure_joint").String(c.ClosureJoint);
                w.Field("closure_kind").String(c.ClosureKind);
                w.EndObject();
            }
            w.EndArray();
            w.EndObject();
        }

        private sealed class JsonWriter
        {
            private readonly StringBuilder _sb = new StringBuilder(16 * 1024);
            private int _indent;
            private bool _hasSibling;    // something already emitted at this level
            private bool _afterField;    // the next value continues a "key": inline

            public override string ToString() => _sb.ToString();

            /// <summary>Comma/newline/indent bookkeeping. A value directly
            /// after Field() stays on the field's line; everything else starts
            /// a fresh indented line, comma-separated from its sibling.</summary>
            private void Prefix()
            {
                if (_afterField) { _afterField = false; return; }
                if (_hasSibling) _sb.Append(',');
                if (_sb.Length > 0) _sb.Append('\n');
                _sb.Append(' ', _indent * 2);
            }

            public JsonWriter Field(string name)
            {
                Prefix();
                _sb.Append('"').Append(name).Append("\": ");
                _hasSibling = true;
                _afterField = true;
                return this;
            }

            public JsonWriter BeginObject() { Open('{'); return this; }
            public void EndObject() { Close('}'); }
            public JsonWriter BeginArray() { Open('['); return this; }
            public void EndArray() { Close(']'); }

            private void Open(char ch)
            {
                Prefix();
                _sb.Append(ch);
                _indent++;
                _hasSibling = false;
            }

            private void Close(char ch)
            {
                _indent--;
                if (_hasSibling) _sb.Append('\n').Append(' ', _indent * 2);
                _sb.Append(ch);
                _hasSibling = true;
            }

            public void Null()
            {
                Prefix();
                _sb.Append("null");
                _hasSibling = true;
            }

            public void Bool(bool value)
            {
                Prefix();
                _sb.Append(value ? "true" : "false");
                _hasSibling = true;
            }

            public void String(string value)
            {
                Prefix();
                AppendString(value);
                _hasSibling = true;
            }

            private void AppendString(string value)
            {
                if (value == null) { _sb.Append("null"); return; }
                _sb.Append('"');
                foreach (char ch in value)
                {
                    switch (ch)
                    {
                        case '"': _sb.Append("\\\""); break;
                        case '\\': _sb.Append("\\\\"); break;
                        case '\b': _sb.Append("\\b"); break;
                        case '\f': _sb.Append("\\f"); break;
                        case '\n': _sb.Append("\\n"); break;
                        case '\r': _sb.Append("\\r"); break;
                        case '\t': _sb.Append("\\t"); break;
                        default:
                            if (ch < 0x20)
                                _sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                            else
                                _sb.Append(ch);
                            break;
                    }
                }
                _sb.Append('"');
            }

            public void Number(double value)
            {
                Prefix();
                _sb.Append(FormatDouble(value));
                _hasSibling = true;
            }

            public void Number(double? value)
            {
                if (value == null) Null(); else Number(value.Value);
            }

            public void Vector(double[] v)
            {
                if (v == null) { Null(); return; }
                Prefix();
                _sb.Append('[');
                for (int i = 0; i < v.Length; i++)
                {
                    if (i > 0) _sb.Append(", ");
                    _sb.Append(FormatDouble(v[i]));
                }
                _sb.Append(']');
                _hasSibling = true;
            }

            public void Indices(int[] v)
            {
                if (v == null) { Null(); return; }
                Prefix();
                _sb.Append('[');
                for (int i = 0; i < v.Length; i++)
                {
                    if (i > 0) _sb.Append(", ");
                    _sb.Append(v[i].ToString(CultureInfo.InvariantCulture));
                }
                _sb.Append(']');
                _hasSibling = true;
            }

            public void Matrix(double[,] m)
            {
                if (m == null) { Null(); return; }
                BeginArray();
                for (int r = 0; r < 4; r++)
                {
                    Prefix();
                    _sb.Append('[');
                    for (int c = 0; c < 4; c++)
                    {
                        if (c > 0) _sb.Append(", ");
                        _sb.Append(FormatDouble(m[r, c]));
                    }
                    _sb.Append(']');
                    _hasSibling = true;
                }
                EndArray();
            }

            public void Limit(JointLimit limit)
            {
                if (limit == null) { Null(); return; }
                BeginObject();
                Field("min").Number(limit.Min);
                Field("max").Number(limit.Max);
                Field("value_at_rest").Number(limit.ValueAtRest);
                EndObject();
            }

            /// <summary>Round-trip exact, invariant culture, and no scientific
            /// notation for integer-valued doubles: "1000000", never "1E+06".
            /// "R" is not always shortest-exact on net48, so a parse-back
            /// guards it and G17 is the fallback.</summary>
            private static string FormatDouble(double value)
            {
                if (double.IsNaN(value) || double.IsInfinity(value)) return "null";
                string s = value.ToString("R", CultureInfo.InvariantCulture);
                double back;
                if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out back)
                    || back != value)
                {
                    s = value.ToString("G17", CultureInfo.InvariantCulture);
                }
                if (s.IndexOf('E') >= 0 && value == Math.Floor(value) && Math.Abs(value) < 1e15)
                    s = value.ToString("F0", CultureInfo.InvariantCulture);
                return s;
            }
        }
    }
}
