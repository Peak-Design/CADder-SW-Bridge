using System.Collections.Generic;
using Peak.Cadder.Core;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// What a send tells Blender to do with the geometry it carries.
    /// </summary>
    public class SendPayloadTests
    {
        private static Dictionary<string, object> Options(AppSettings settings)
        {
            var payload = SendToBlenderCommand.BuildPayload(
                settings, null, "part.swmesh", null);
            return (Dictionary<string, object>)payload["import_options"];
        }

        [Fact]
        public void TrianglesArePairedIntoQuadsByDefault()
        {
            // The work is Blender's. This is what asks for it, and Blender
            // keeps the answer, so a send and a rebuild there give one mesh.
            Assert.Equal(true, Options(new AppSettings())["tris_to_quads"]);
        }

        [Fact]
        public void TheOptionTravelsBothWays()
        {
            var settings = new AppSettings { TrisToQuads = false };
            Assert.Equal(false, Options(settings)["tris_to_quads"]);
        }

        [Fact]
        public void CompoundSurfacesAreUnwrappedByDefault()
        {
            // A sphere, a torus and a spline have no flat chart to send,
            // so what Blender does with them is asked for here.
            Assert.Equal(true, Options(new AppSettings())["uv_unwrap_compound"]);
            Assert.Equal(false, Options(new AppSettings
            {
                UnwrapCompound = false,
            })["uv_unwrap_compound"]);
        }
    }
}
