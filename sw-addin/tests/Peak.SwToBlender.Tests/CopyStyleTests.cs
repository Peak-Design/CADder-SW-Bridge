using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Xunit;

namespace Peak.SwToBlender.Tests
{
    /// <summary>
    /// The copy in this repository carries no em dash. Readers take one as a
    /// sign that a machine wrote the text, and everything here is read by
    /// someone: comments, documentation and the strings the add-in shows.
    /// The replacement is the punctuation the sentence needs, and never a
    /// hyphen or an en dash in the same role.
    ///
    /// The same check runs from the command line as tools/Check-Copy.py.
    /// </summary>
    public class CopyStyleTests
    {
        private const char EmDash = (char)0x2014;
        private const char EnDash = (char)0x2013;

        private static readonly string[] Suffixes =
            { ".cs", ".md", ".py", ".ps1", ".iss", ".json", ".yml", ".yaml" };

        private static readonly string[] SkipDirs =
            { ".git", "bin", "obj", "node_modules", "__pycache__", "packages" };

        [Fact]
        public void NoEmDashInAnythingSomeoneReads()
        {
            var offences = new List<string>();
            foreach (var path in Files(RepositoryRoot()))
            {
                string[] lines;
                try { lines = File.ReadAllLines(path); }
                catch (IOException) { continue; }
                for (int i = 0; i < lines.Length; i++)
                {
                    bool em = lines[i].IndexOf(EmDash) >= 0;
                    bool en = lines[i].IndexOf(" " + EnDash + " ", StringComparison.Ordinal) >= 0;
                    if (em || en)
                        offences.Add(string.Format("{0}:{1}: {2}", path, i + 1,
                            em ? "em dash" : "en dash as punctuation"));
                }
            }
            Assert.True(offences.Count == 0,
                "Use the punctuation the sentence needs (a colon, a comma, brackets, "
                + "or two sentences):\n  " + string.Join("\n  ", offences.ToArray()));
        }

        private static IEnumerable<string> Files(string root)
        {
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                string rel = path.Substring(root.Length).ToLowerInvariant();
                bool skip = false;
                foreach (var dir in SkipDirs)
                    if (rel.Contains(Path.DirectorySeparatorChar + dir + Path.DirectorySeparatorChar))
                    { skip = true; break; }
                if (skip) continue;
                foreach (var suffix in Suffixes)
                    if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        yield return path;
                        break;
                    }
            }
        }

        /// <summary>The repository root, from this file's own path: the test
        /// runner shadow-copies the assembly, so its location says nothing.</summary>
        private static string RepositoryRoot([CallerFilePath] string here = null)
        {
            var dir = new DirectoryInfo(Path.GetDirectoryName(here));
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CHANGELOG.md")))
                dir = dir.Parent;
            Assert.True(dir != null, "no repository root above " + here);
            return dir.FullName;
        }
    }
}
