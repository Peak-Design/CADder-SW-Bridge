using System;
using Peak.Cadder.Core;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// A question asked during an export. SolidWorks draws a message box
    /// behind its live progress bar, so the box the user has to answer is
    /// hidden and SolidWorks looks frozen. The bar closes first.
    /// </summary>
    public class ProgressPromptTests
    {
        private sealed class Bar : ExportProgress, IDisposable
        {
            public bool Closed;
            public void Dispose() { Closed = true; }
        }

        [Fact]
        public void TheBarIsClosedBeforeTheQuestion()
        {
            var bar = new Bar();
            bool closedWhenAsked = ExportCommand.WithoutBar(bar, () => bar.Closed);
            Assert.True(closedWhenAsked, "the question was asked under the live bar");
        }

        [Fact]
        public void TheAnswerComesBack()
        {
            Assert.Equal(7, ExportCommand.WithoutBar(new Bar(), () => 7));
        }

        [Fact]
        public void NoBarIsNoProblem()
        {
            Assert.True(ExportCommand.WithoutBar(ExportProgress.None, () => true));
            Assert.True(ExportCommand.WithoutBar(null, () => true));
        }
    }
}
