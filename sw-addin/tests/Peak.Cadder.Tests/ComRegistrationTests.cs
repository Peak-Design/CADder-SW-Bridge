using System;
using System.Linq;
using System.Runtime.InteropServices;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// What setup registers as COM classes. Setup runs RegAsm /codebase,
    /// which registers every public class with a public default
    /// constructor of a COM-visible assembly, machine-wide. The csproj
    /// asked for an assembly that is not COM-visible, but the SDK has no
    /// such property, so 70 classes were registered, the dialogs among
    /// them, and a class renamed in a later version left its old entry
    /// behind. SolidWorks needs two: the add-in and the object its ribbon
    /// calls back.
    /// </summary>
    public class ComRegistrationTests
    {
        [Fact]
        public void OnlyTheAddInAndItsCallbacksAreRegistered()
        {
            var types = new RegistrationServices()
                .GetRegistrableTypesInAssembly(typeof(AddIn).Assembly)
                .Select(t => t.FullName).OrderBy(n => n).ToArray();
            Assert.Equal(new[] { "Peak.Cadder.AddIn", "Peak.Cadder.CommandCallbacks" }, types);
        }

        [Fact]
        public void SolidWorksStillReachesTheAddIn()
        {
            // SolidWorks asks the add-in object for ISwAddin, and calls the
            // ribbon's methods by name on the callback object.
            Assert.True(Marshal.IsTypeVisibleFromCom(typeof(AddIn)));
            Assert.True(Marshal.IsTypeVisibleFromCom(typeof(CommandCallbacks)));
            foreach (var face in typeof(AddIn).GetInterfaces())
                Assert.True(Marshal.IsTypeVisibleFromCom(face), face.FullName + " is hidden from COM");
            var callbacks = typeof(CommandCallbacks).GetCustomAttributes(typeof(ClassInterfaceAttribute), false);
            Assert.Equal(ClassInterfaceType.AutoDispatch, ((ClassInterfaceAttribute)callbacks.Single()).Value);
        }
    }
}
