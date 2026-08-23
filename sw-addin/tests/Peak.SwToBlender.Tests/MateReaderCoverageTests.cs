using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Peak.SwToBlender.Tests
{
    /// <summary>
    /// MateReader.MatedEntities is a lookup table over the SolidWorks SDK:
    /// EntitiesToMate is declared separately on every *MateFeatureData
    /// interface, with no common base to call it on, so a mate type missing
    /// from the table returns nothing — and the surface retype passes that
    /// depend on it then silently do not run. Live corpus 15 cone3
    /// (2026-08-23) exported as a free joint for exactly that reason:
    /// TANGENT was absent, so a conical face kept the circle typing it
    /// arrives with and lost the half-angle its decomposition needs.
    ///
    /// No fixture can catch that — the entities come from COM. What can is
    /// asking the SDK which interfaces declare the member and the compiled
    /// method which ones it tests for: the C# `as` operator emits `isinst`
    /// against the interface's metadata token, so the tokens in the method
    /// body ARE the table.
    /// </summary>
    public class MateReaderCoverageTests
    {
        private const byte IsInst = 0x75;

        [Fact]
        public void MatedEntitiesCoversEveryMateTypeThatDeclaresASelectionList()
        {
            // Loaded by name rather than referenced: the SolidWorks interops
            // live behind the add-in project's install-path probe, and the
            // tests are meant to build without SolidWorks in the picture.
            var name = typeof(Peak.SwToBlender.Sw.MateReader).Assembly
                .GetReferencedAssemblies()
                .FirstOrDefault(a => a.Name == "SolidWorks.Interop.sldworks");
            Assert.NotNull(name);
            var declaring = Assembly.Load(name).GetTypes()
                .Where(t => t.IsInterface && t.GetProperty("EntitiesToMate") != null)
                .ToList();
            Assert.True(declaring.Count >= 17,
                "expected the interop to declare EntitiesToMate widely, found "
                + declaring.Count);

            var missing = declaring.Where(t => !TestedTypes().Contains(t))
                .Select(t => t.Name)
                .OrderBy(n => n)
                .ToList();
            Assert.True(missing.Count == 0,
                "MateReader.MatedEntitiesCore ignores " + string.Join(", ", missing)
                + " — those mates reach the retype passes with no selection list");
        }

        /// <summary>Every type MatedEntitiesCore runs an `as` cast against.
        /// Scanning for the opcode can also hit a byte inside another
        /// instruction's operand, but a false hit resolves to a random token
        /// — either an exception or a type nobody is looking for — and the
        /// assertion only asks whether the required types are PRESENT.</summary>
        private static HashSet<Type> TestedTypes()
        {
            var method = typeof(Peak.SwToBlender.Sw.MateReader).GetMethod(
                "MatedEntitiesCore", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);
            var il = method.GetMethodBody().GetILAsByteArray();
            var module = method.Module;
            var found = new HashSet<Type>();
            for (int i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] != IsInst) continue;
                int token = BitConverter.ToInt32(il, i + 1);
                try
                {
                    var t = module.ResolveType(token);
                    if (t != null && t.IsInterface) found.Add(t);
                }
                catch (ArgumentException) { }
                catch (BadImageFormatException) { }
            }
            return found;
        }
    }
}
