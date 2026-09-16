using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Peak.SwToBlender.Core.Model;

namespace Peak.SwToBlender.Core
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
        /// <summary>2 (2026-09-15): every material record ends with the
        /// appearance JSON, length-prefixed with a uint32 (it outgrows a
        /// uint16 once a decal and a library file are in it).</summary>
        public const uint Version = 2;

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
                // Transforms stay DOUBLE: a rotation folded into float32 and
                // then into a bone rest pose is exactly the drift the rig
                // spent three rounds chasing out.
                var m = inst.Transform;
                for (int i = 0; i < 16; i++) w.Write(m != null && i < m.Length ? m[i] : (i % 5 == 0 ? 1.0 : 0.0));
            }
            w.Flush();
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
