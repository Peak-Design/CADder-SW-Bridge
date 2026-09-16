using System.IO;
using Peak.SwToBlender.Sw;
using Xunit;

namespace Peak.SwToBlender.Tests
{
    /// <summary>
    /// Where a texture named by an appearance is found: as given, beside
    /// the document (the decal image of the usb_case2 sample was authored
    /// on another machine and ships beside the part), or under the
    /// SolidWorks data folder (library files name their images relative
    /// to it).
    /// </summary>
    public class AppearanceFileTests
    {
        [Fact]
        public void AFileIsFoundAsGivenBesideTheDocumentOrInTheLibrary()
        {
            string root = Path.Combine(Path.GetTempPath(), "cadlink-appearance-" + System.Guid.NewGuid().ToString("N"));
            string doc = Path.Combine(root, "project");
            string data = Path.Combine(root, "data");
            Directory.CreateDirectory(doc);
            Directory.CreateDirectory(Path.Combine(data, "Images", "textures"));
            File.WriteAllText(Path.Combine(doc, "logo.png"), "x");
            File.WriteAllText(Path.Combine(data, "Images", "textures", "checker.jpg"), "x");
            try
            {
                string beside = AppearanceTable.ResolveFile(@"C:\Users\someone\Documents\logo.png", doc, data);
                Assert.Equal(Path.Combine(doc, "logo.png"), beside);

                string library = AppearanceTable.ResolveFile("Images\\textures/checker.jpg", doc, data);
                Assert.Equal(Path.Combine(data, "Images", "textures", "checker.jpg"), library);

                string given = Path.Combine(doc, "logo.png");
                Assert.Equal(given, AppearanceTable.ResolveFile(given, null, null));

                Assert.Equal(@"C:\nowhere\missing.png", AppearanceTable.ResolveFile(@"C:\nowhere\missing.png", doc, data));
                Assert.Null(AppearanceTable.ResolveFile("", doc, data));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }
    }
}
