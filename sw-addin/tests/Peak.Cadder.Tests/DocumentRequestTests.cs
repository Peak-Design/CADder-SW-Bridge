using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Peak.Cadder.Bridge;
using Peak.Cadder.Core;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// Which SolidWorks document a request from Blender is about.
    ///
    /// Component ids (c001, c002) are one assembly's numbering, so every
    /// assembly has a c001. A request that the active document answered
    /// put the wrong document's poses and geometry on the scene whenever
    /// the user had a part or another assembly open in front. So every
    /// payload names the document it came from, Blender sends that name
    /// back, and the listener answers for that document or says it is not
    /// open.
    /// </summary>
    public class DocumentRequestTests
    {
        private sealed class Doc
        {
            public string Path;
            public string Title;
        }

        private static readonly Doc Assembly = new Doc
        {
            Path = @"C:\Projects\Lifter\lifter.SLDASM", Title = "lifter.SLDASM",
        };

        private static readonly Doc Part = new Doc
        {
            Path = @"C:\Projects\Lifter\bracket.SLDPRT", Title = "bracket.SLDPRT",
        };

        private static Doc Find(Dictionary<string, object> request, Doc active, out string error)
        {
            return SwCommandHandler.DocumentFor(
                request, () => active, new[] { Assembly, Part },
                d => d.Path, d => d.Title, out error);
        }

        [Fact]
        public void ASendNamesItsDocument()
        {
            var payload = SendToBlenderCommand.BuildPayload(
                new AppSettings(), null, "lifter.swmesh", "lifter.rig.json",
                update: true, sourceDocument: Assembly.Path);
            Assert.Equal(Assembly.Path, payload["source_document"]);
        }

        [Fact]
        public void APayloadWithNoDocumentLeavesTheFieldOut()
        {
            var payload = SendToBlenderCommand.BuildPayload(
                new AppSettings(), null, "lifter.swmesh", null);
            Assert.False(payload.ContainsKey("source_document"));
        }

        [Fact]
        public void APosePushNamesItsDocument()
        {
            var payload = PosePush.PayloadOf(Assembly.Title, Assembly.Path, new List<object>());
            Assert.Equal(Assembly.Path, payload["source_document"]);
        }

        [Fact]
        public void TheNamedDocumentAnswersEvenWhenAnotherIsInFront()
        {
            // The user opened the part in its own window to edit it.
            var request = new Dictionary<string, object>
            {
                { "op", "poses" }, { "document_path", Assembly.Path },
            };
            string error;
            Assert.Same(Assembly, Find(request, Part, out error));
            Assert.Null(error);
        }

        [Fact]
        public void TheNameIsMatchedAsAPathNotAsText()
        {
            var request = new Dictionary<string, object>
            {
                { "document_path", @"c:\projects\LIFTER\sub\..\lifter.sldasm" },
            };
            string error;
            Assert.Same(Assembly, Find(request, Part, out error));
        }

        [Fact]
        public void ANamedDocumentThatIsNotOpenFailsAndSaysWhich()
        {
            const string closed = @"C:\Projects\Press\press.SLDASM";
            var request = new Dictionary<string, object> { { "document_path", closed } };
            string error;
            Assert.Null(Find(request, Assembly, out error));
            Assert.Equal("The document " + closed + " is not open in SolidWorks. "
                + "Open it and try again.", error);
        }

        [Fact]
        public void AnOlderBlenderGetsTheActiveDocument()
        {
            var request = new Dictionary<string, object> { { "op", "poses" } };
            string error;
            Assert.Same(Part, Find(request, Part, out error));
            Assert.Null(error);
        }

        [Fact]
        public void NoActiveDocumentIsStillAnError()
        {
            string error;
            Assert.Null(Find(new Dictionary<string, object>(), null, out error));
            Assert.Equal("no document is open in SolidWorks", error);
        }

        [Fact]
        public void APartAnswersOnlyForItself()
        {
            // A part document is one component, c001, under its own name.
            Assert.Null(SwCommandHandler.PartMismatch(
                new[] { "c001" }, new[] { "bracket" }, "bracket.SLDPRT"));
            Assert.Null(SwCommandHandler.PartMismatch(
                new[] { "c001" }, new string[0], "bracket"));
            Assert.Null(SwCommandHandler.PartMismatch(
                new string[0], new string[0], "bracket"));
        }

        [Fact]
        public void APartDoesNotAnswerForAnAssemblysComponent()
        {
            // The scene is the assembly and asks for its fifth component,
            // or for a placement inside the assembly. The part in front
            // must not come back as that component.
            Assert.NotNull(SwCommandHandler.PartMismatch(
                new[] { "c005" }, new string[0], "bracket.SLDPRT"));
            Assert.NotNull(SwCommandHandler.PartMismatch(
                new[] { "c001" }, new[] { "lifterassy-1/bracket-1" }, "bracket.SLDPRT"));
        }

        /// <summary>Poses, geometry and exports go through the one lookup
        /// that honours document_path. Read off the source, because these
        /// handlers need SolidWorks to run.</summary>
        [Theory]
        [InlineData("Poses")]
        [InlineData("Retessellate")]
        [InlineData("Export")]
        public void TheHandlersDoNotReadTheActiveDocumentThemselves(string handler)
        {
            string body = MethodBody(Source("Bridge", "SwCommandHandler.cs"), handler);
            Assert.DoesNotContain("ActiveDoc", body);
            Assert.Contains("ModelFor(app, request, out error)", body);
        }

        private static string MethodBody(string source, string name)
        {
            var start = Regex.Match(source,
                @"private static Dictionary<string, object> " + name + @"\(");
            Assert.True(start.Success, "no handler " + name);
            int end = source.IndexOf("\n        }\n", start.Index, System.StringComparison.Ordinal);
            Assert.True(end > start.Index, "no end to " + name);
            return source.Substring(start.Index, end - start.Index);
        }

        private static string Source(params string[] parts)
        {
            var path = Path.Combine(RepositoryRoot(), "sw-addin", "src", "Peak.Cadder");
            foreach (var part in parts) path = Path.Combine(path, part);
            return File.ReadAllText(path).Replace("\r\n", "\n");
        }

        private static string RepositoryRoot([CallerFilePath] string here = null)
        {
            var dir = new DirectoryInfo(Path.GetDirectoryName(here));
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "schema")))
                dir = dir.Parent;
            Assert.True(dir != null, "no repository root above " + here);
            return dir.FullName;
        }
    }
}
