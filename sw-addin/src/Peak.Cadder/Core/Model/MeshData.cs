using System.Collections.Generic;

namespace Peak.Cadder.Core.Model
{
    /// <summary>
    /// One tessellated part, in the PART's own space. Several components can
    /// share it: the same part placed twenty times is one definition and
    /// twenty instances, which is most of why the native path is fast.
    ///
    /// Indexed and welded: Positions holds each point once and Triangles
    /// indexes into it. SolidWorks hands tessellation out per face, so the
    /// naive read repeats every shared corner once per face touching it,
    /// which triples the file and leaves split normals down every edge.
    /// </summary>
    public sealed class MeshDefinition
    {
        public int Id;
        public string Name;

        /// <summary>Metres, part space, three doubles per vertex.</summary>
        public List<double> Positions = new List<double>();

        /// <summary>Unit normals, three per vertex; empty when unavailable.</summary>
        public List<double> Normals = new List<double>();

        /// <summary>Surface parameters as UVs, two per vertex; empty when
        /// unavailable.</summary>
        public List<double> Uvs = new List<double>();

        /// <summary>Vertex indices, three per triangle.</summary>
        public List<int> Triangles = new List<int>();

        /// <summary>One material index per TRIANGLE, into MeshScene.Materials.
        /// SolidWorks appearances live per face, per body, per feature and per
        /// component, so the resolved answer travels per triangle: the only
        /// place all four collapse to one value.</summary>
        public List<int> TriangleMaterials = new List<int>();

        public int VertexCount { get { return Positions.Count / 3; } }
        public int TriangleCount { get { return Triangles.Count / 3; } }
    }

    /// <summary>One placement of a definition: which component it is, and
    /// where.</summary>
    public sealed class MeshInstance
    {
        public int DefinitionId;

        /// <summary>The manifest's c001, c002, ... A rigid subassembly is ONE
        /// component and many instances: every part inside it carries the
        /// subassembly's id, because that is the body the rig moves.</summary>
        public string ComponentId;

        /// <summary>The referenced document's name, which is also the name
        /// SolidWorks gives the STEP product. The two routes then name a part
        /// the same thing in Blender.</summary>
        public string Name;

        /// <summary>The occurrence path from the root, as SolidWorks spells
        /// it ("lifter-1/rod-2"). It is unique, so it survives a re-export as
        /// the key of one placement, and it carries the assembly tree that
        /// the consumer builds its collections from.</summary>
        public string Path;

        public double[] Transform;      // row-major 4x4, metres, global

        /// <summary>Where this placement sits inside its COMPONENT, row-major
        /// 4x4 in metres. Null when the placement is the component's own,
        /// which is every part outside a rigid subassembly. A consumer that
        /// moves a component to a new pose needs it: the parts of a rigid
        /// subassembly all carry the subassembly's id, and without their
        /// places inside it they would all land on its origin.
        ///
        /// It is written rather than worked out from the two transforms
        /// because those two are allowed to disagree: an update of the poses
        /// alone sends new component transforms and no geometry, and the
        /// difference then means something else entirely.</summary>
        public double[] Local;
    }

    /// <summary>One subassembly occurrence: a branch of the assembly tree
    /// that holds instances rather than geometry of its own. The consumer
    /// needs the branches to name and place its collections and empties, and
    /// cannot derive the name from a path because a path segment carries the
    /// instance number and the document name does not.</summary>
    public sealed class MeshNode
    {
        public string Path;
        public string Name;

        /// <summary>The manifest component, where this occurrence is one: a
        /// subassembly INSIDE a rigid subassembly is not walked and has no
        /// component of its own, and this is empty.</summary>
        public string ComponentId;

        public double[] Transform;      // row-major 4x4, metres, global
    }

    public sealed class MeshMaterial
    {
        public string Name;
        public double R = 0.8, G = 0.8, B = 0.8, A = 1.0;

        /// <summary>0..1; SolidWorks appearances carry these directly and
        /// Blender's Principled BSDF takes them as-is.</summary>
        public double Roughness = 0.5;
        public double Metallic;

        /// <summary>Absolute path to a diffuse texture, or null.</summary>
        public string Texture;

        /// <summary>The whole SolidWorks appearance as JSON (AppearanceSpec):
        /// colours, finish, library values, mapping, decals. Null for a
        /// material with nothing to say beyond its colour.</summary>
        public string Appearance;
    }

    /// <summary>Everything one native export produces.</summary>
    public sealed class MeshScene
    {
        public List<MeshDefinition> Definitions = new List<MeshDefinition>();
        public List<MeshInstance> Instances = new List<MeshInstance>();
        public List<MeshMaterial> Materials = new List<MeshMaterial>();

        /// <summary>The subassembly occurrences the instances hang under,
        /// parents before children.</summary>
        public List<MeshNode> Nodes = new List<MeshNode>();

        /// <summary>The chord tolerance the definitions were built at, metres.
        /// The consumer shows it and asks for a tighter one per part.</summary>
        public double Tolerance;
    }
}
