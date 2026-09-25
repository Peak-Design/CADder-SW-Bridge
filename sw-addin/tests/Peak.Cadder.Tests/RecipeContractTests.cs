using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// The SW-MCP recipes in tools\recipes call the add-in by name, through
    /// the helper class Cadder (cadder-bridge.cs): Cadder.Call("Sw.MateReader",
    /// "Read", ...) and so on. A rename in the add-in breaks such a call only
    /// at run time, in a lab SolidWorks. So this test reads every name in the
    /// recipe code files and finds it in the add-in.
    /// </summary>
    public class RecipeContractTests
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Static | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

        private static readonly Assembly Bridge = typeof(AddIn).Assembly;

        private static readonly Regex StaticCall = new Regex(@"Cadder\.Call\(\s*""(?<type>[^""]+)""\s*,\s*""(?<member>[^""]+)""");
        private static readonly Regex StaticRead = new Regex(@"Cadder\.Static\(\s*""(?<type>[^""]+)""\s*,\s*""(?<member>[^""]+)""");
        private static readonly Regex NewOrType = new Regex(@"Cadder\.(New|Type)\(\s*""(?<type>[^""]+)""");
        private static readonly Regex InstanceUse = new Regex(@"Cadder\.(CallOn|Get|Set)\([^,()]+(\([^()]*\))?[^,()]*,\s*""(?<member>[^""]+)""");

        public static IEnumerable<object[]> RecipeFiles()
        {
            return Directory.GetFiles(RecipesDir(), "*.cs").Select(f => new object[] { Path.GetFileName(f) });
        }

        [Fact]
        public void TheRecipeFolderHasTheCadderRecipes()
        {
            var names = Directory.GetFiles(RecipesDir(), "*.md").Select(Path.GetFileNameWithoutExtension).ToList();
            Assert.Contains("cadder-bridge", names);
            Assert.Contains("cadder-export", names);
            Assert.Contains("cadder-send", names);
            // SW-MCP takes each .md file of the folder as a recipe.
            Assert.DoesNotContain("README", names, StringComparer.OrdinalIgnoreCase);
        }

        [Theory]
        [MemberData(nameof(RecipeFiles))]
        public void EachStaticNameIsInTheAddIn(string file)
        {
            string code = File.ReadAllText(Path.Combine(RecipesDir(), file));
            var missing = new List<string>();
            foreach (Match m in StaticCall.Matches(code))
            {
                var type = Find(m.Groups["type"].Value, missing);
                if (type != null && !type.GetMethods(Any).Any(x => x.Name == m.Groups["member"].Value))
                    missing.Add(type.FullName + "." + m.Groups["member"].Value + "()");
            }
            foreach (Match m in StaticRead.Matches(code))
            {
                var type = Find(m.Groups["type"].Value, missing);
                string member = m.Groups["member"].Value;
                if (type != null && type.GetField(member, Any) == null && type.GetProperty(member, Any) == null)
                    missing.Add(type.FullName + "." + member);
            }
            foreach (Match m in NewOrType.Matches(code)) Find(m.Groups["type"].Value, missing);
            Assert.True(missing.Count == 0, file + " calls names that the add-in does not have: " + string.Join(", ", missing));
        }

        [Theory]
        [MemberData(nameof(RecipeFiles))]
        public void EachInstanceMemberIsInTheAddIn(string file)
        {
            // The type of the object is not in the text, so the member must be in some type of the add-in.
            string code = File.ReadAllText(Path.Combine(RecipesDir(), file));
            var all = new HashSet<string>(Bridge.GetTypes().SelectMany(t => t.GetMembers(Any)).Select(x => x.Name));
            var missing = InstanceUse.Matches(code).Cast<Match>().Select(m => m.Groups["member"].Value)
                .Where(name => !all.Contains(name)).Distinct().ToList();
            Assert.True(missing.Count == 0, file + " uses members that no type of the add-in has: " + string.Join(", ", missing));
        }

        [Fact]
        public void TheHelperFindsTheAddInByItsClassId()
        {
            string code = File.ReadAllText(Path.Combine(RecipesDir(), "cadder-bridge.cs"));
            var guid = typeof(AddIn).GetCustomAttribute<GuidAttribute>().Value;
            Assert.Contains("AddInClsid = \"{" + guid.ToUpperInvariant() + "}\"", code);
        }

        [Fact]
        public void TheListenerAnswersOnlyWhatBlenderAsks()
        {
            // The ops that Blender sends (rig\cad_link.py and Rebuild from CAD).
            string source = File.ReadAllText(Path.Combine(RepositoryRoot(), "sw-addin", "src", "Peak.Cadder", "Bridge", "SwCommandHandler.cs"));
            var ops = Regex.Matches(source, @"case ""(?<op>[a-z_]+)"":").Cast<Match>().Select(m => m.Groups["op"].Value).OrderBy(s => s).ToList();
            Assert.Equal(new[] { "export", "poses", "retessellate", "status" }, ops);
        }

        private static Type Find(string name, List<string> missing)
        {
            var type = Bridge.GetType("Peak.Cadder." + name, false);
            if (type == null) missing.Add("type Peak.Cadder." + name);
            return type;
        }

        private static string RecipesDir()
        {
            return Path.Combine(RepositoryRoot(), "sw-addin", "tools", "recipes");
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
