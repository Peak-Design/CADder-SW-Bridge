using Peak.Cadder.Sw;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// A part document sent on its own is named after its file, without the
    /// extension, as the assembly route and the STEP route name it. The
    /// window title shows the extension when Windows shows extensions, so a
    /// name taken from it depended on a setting of the PC.
    /// </summary>
    public class SinglePartNameTests
    {
        [Theory]
        [InlineData(@"C:\Parts\Bracket.SLDPRT", "Bracket.SLDPRT", "Bracket")]
        [InlineData(@"C:\Parts\Bracket.SLDPRT", "Bracket", "Bracket")]
        [InlineData(@"C:\Parts\Bracket.v2.sldprt", "Bracket.v2.sldprt", "Bracket.v2")]
        [InlineData("", "Part1.SLDPRT", "Part1")]
        [InlineData(null, "Part1", "Part1")]
        [InlineData(null, null, "part")]
        public void APartIsNamedAfterItsFile(string path, string title, string name)
        {
            Assert.Equal(name, NativeExport.PartName(path, title));
        }
    }
}
