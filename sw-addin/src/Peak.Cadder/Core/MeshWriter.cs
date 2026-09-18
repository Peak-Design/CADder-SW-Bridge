using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Peak.Cadder.Core.Model;

namespace Peak.Cadder.Core
{
    /// <summary>
    /// Writes a MeshScene as .swmesh: the native path's answer to the STEP
    /// file.
    ///
    /// Binary, little-endian, and deliberately dull: fixed-width arrays a
    /// consumer can read straight into a typed buffer. Blender's mesh
    /// foreach_set wants exactly that, and a text format would be several
    /// times the size and far slower to parse for the assemblies this exists
    /// to make fast. There is no compression: these files are temporary,
    /// written and read on one machine, and the time saved zipping is time
    /// lost unzipping.
    ///
    /// The format carries geometry ONLY. What each component is, where it
    /// belongs in the assembly and how it moves stays in the rig manifest,
    /// which the STEP path already uses: the two are read together, and the
    /// component ids are what tie them.
    /// </summary>
    public static class MeshWriter
    {
        public const uint Magic = 0x484D5753;      // "SWMH"
        /// <summary>
        /// 2 (2026-09-15): every material record ends with the appearance
        /// JSON, length-prefixed with a uint32 (it outgrows a uint16 once a
        /// decal and a library file are in it).
        ///
        /// 3 (2026-09-16): the assembly tree travels with the geometry. The
        /// header carries a node count, every instance carries its occurrence
        /// path and its place inside its component, and a node table follows
        /// the instances. Before this the
        /// consumer could only read the tree out of the rig manifest, so a
        /// send with no rig arrived flat, and a part inside a rigid
        /// subassembly had no place in the tree at all.
        ///
        /// Sections (2026-09-18) follow the node table without a new version
        /// number, see WriteBodies.
        /// </summary>
        public const uint Version = 3;

        [Flags]
        public enum SceneFlags : uint
        {
            None = 0,
            Normals = 1,
            Uvs = 2,
        }

        public static void Write(string path, MeshScene scene)
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
                Write(stream, scene);
        }

        public static void Write(Stream stream, MeshScene scene)
        {
            bool normals = true, uvs = true;
            foreach (var d in scene.Definitions)
            {
                if (d.Normals.Count != d.Positions.Count) normals = false;
                if (d.Uvs.Count != d.VertexCount * 2) uvs = false;
            }
            // All or nothing per file: a consumer that must branch per mesh
            // cannot size its buffers up front, and a part with no normals is
            // rare enough not to be worth the complication.
            var flags = SceneFlags.None;
            if (normals) flags |= SceneFlags.Normals;
            if (uvs) flags |= SceneFlags.Uvs;

            var w = new BinaryWriter(stream, new UTF8Encoding(false), true);
            w.Write(Magic);
            w.Write(Version);
            w.Write((uint)flags);
            w.Write(scene.Tolerance);
            w.Write((uint)scene.Materials.Count);
            w.Write((uint)scene.Definitions.Count);
            w.Write((uint)scene.Instances.Count);
            w.Write((uint)scene.Nodes.Count);

            foreach (var m in scene.Materials)
            {
                WriteString(w, m.Name);
                w.Write((float)m.R);
                w.Write((float)m.G);
                w.Write((float)m.B);
                w.Write((float)m.A);
                w.Write((float)m.Roughness);
                w.Write((float)m.Metallic);
                WriteString(w, m.Texture);
                var json = m.Appearance == null ? new byte[0] : Encoding.UTF8.GetBytes(m.Appearance);
                w.Write((uint)json.Length);
                w.Write(json);
            }

            foreach (var d in scene.Definitions)
            {
                w.Write(d.Id);
                WriteString(w, d.Name);
                w.Write((uint)d.VertexCount);
                w.Write((uint)d.TriangleCount);
                // Positions are float32: a float carries ~7 significant
                // digits, so a 10 m assembly resolves to under a micron,
                // finer than any tessellation this could be asked for, at
                // half the bytes.
                foreach (var v in d.Positions) w.Write((float)v);
                if (normals) foreach (var n in d.Normals) w.Write((float)n);
                if (uvs) foreach (var t in d.Uvs) w.Write((float)t);
                foreach (var i in d.Triangles) w.Write(i);
                foreach (var i in d.TriangleMaterials) w.Write(i);
            }

            foreach (var inst in scene.Instances)
            {
                w.Write(inst.DefinitionId);
                WriteString(w, inst.ComponentId);
                WriteString(w, inst.Name);
                WriteString(w, inst.Path);
                WriteTransform(w, inst.Transform);
                // One byte rather than another sixteen doubles on every
                // record: a part that is its own component, which is nearly
                // all of them, carries no place inside anything.
                w.Write((byte)(inst.Local == null ? 0 : 1));
                if (inst.Local != null) WriteTransform(w, inst.Local);
            }

            foreach (var node in scene.Nodes)
            {
                WriteString(w, node.Path);
                WriteString(w, node.Name);
                WriteString(w, node.ComponentId);
                WriteTransform(w, node.Transform);
            }

            WriteBodies(w, scene);
            w.Flush();
        }

        /// <summary>The tag of the section that says where each body of a
        /// definition starts.</summary>
        public static readonly byte[] BodiesTag = Encoding.ASCII.GetBytes("BODY");

        /// <summary>
        /// Sections after the node table: a 4-byte tag, a uint32 byte length,
        /// then the data. A version 3 reader stops after the nodes and never
        /// sees them, so a section adds to the format without a new version,
        /// and a CADder that has not been updated still reads the file.
        ///
        /// BODY: for each definition in file order, a uint32 count and that
        /// many int32 first-vertex indices. It is left out when every
        /// definition is one body, which is the usual case.
        /// </summary>
        private static void WriteBodies(BinaryWriter w, MeshScene scene)
        {
            bool several = false;
            foreach (var d in scene.Definitions)
                if (d.BodyStarts.Count > 1) several = true;
            if (!several) return;

            uint length = 0;
            foreach (var d in scene.Definitions)
                length += 4 + 4 * (uint)d.BodyStarts.Count;
            w.Write(BodiesTag);
            w.Write(length);
            foreach (var d in scene.Definitions)
            {
                w.Write((uint)d.BodyStarts.Count);
                foreach (var start in d.BodyStarts) w.Write(start);
            }
        }

        /// <summary>Transforms stay DOUBLE: a rotation folded into float32
        /// and then into a bone rest pose is exactly the drift the rig spent
        /// three rounds chasing out. A missing one writes as identity.</summary>
        private static void WriteTransform(BinaryWriter w, double[] m)
        {
            for (int i = 0; i < 16; i++)
                w.Write(m != null && i < m.Length ? m[i] : (i % 5 == 0 ? 1.0 : 0.0));
        }

        private static void WriteString(BinaryWriter w, string s)
        {
            var bytes = s == null ? new byte[0] : Encoding.UTF8.GetBytes(s);
            if (bytes.Length > ushort.MaxValue)
                throw new InvalidOperationException("string too long for .swmesh");
            w.Write((ushort)bytes.Length);
            w.Write(bytes);
        }
    }
}
