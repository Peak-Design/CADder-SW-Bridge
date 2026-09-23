using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Xunit;

namespace Peak.Cadder.Tests
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

        // Project files and scripts are read too: their comments are
        // published like any other. The same list is in tools/Check-Copy.py.
        private static readonly string[] Suffixes =
            { ".cs", ".md", ".py", ".ps1", ".iss", ".json", ".yml", ".yaml",
              ".props", ".targets", ".csproj", ".bat", ".cmd", ".toml",
              ".cpp", ".h", ".xml" };

        // .claude holds the internal notes, and the corpus and assets
        // folders hold working files. Git ignores all three, so none of it
        // is published.
        private static readonly string[] SkipDirs =
            { ".git", "bin", "obj", "node_modules", "__pycache__", "packages",
              ".claude", "test-assemblies", "assets" };

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

        /// <summary>
        /// No client name in anything that is published. The names are in
        /// .claude/client-names.txt, which git ignores, so the list is not
        /// published by the test that enforces it. On a machine without the
        /// file the test has nothing to look for.
        /// </summary>
        [Fact]
        public void NoClientNameInAnythingPublished()
        {
            string root = RepositoryRoot();
            string list = Path.Combine(root, ".claude", "client-names.txt");
            if (!File.Exists(list)) return;
            var names = new List<System.Text.RegularExpressions.Regex>();
            foreach (var raw in File.ReadAllLines(list))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                names.Add(new System.Text.RegularExpressions.Regex(
                    line, System.Text.RegularExpressions.RegexOptions.IgnoreCase));
            }
            var offences = new List<string>();
            foreach (var path in Files(root))
            {
                string[] lines;
                try { lines = File.ReadAllLines(path); }
                catch (IOException) { continue; }
                for (int i = 0; i < lines.Length; i++)
                    foreach (var name in names)
                        if (name.IsMatch(lines[i]))
                        {
                            offences.Add(string.Format("{0}:{1}", path, i + 1));
                            break;
                        }
            }
            Assert.True(offences.Count == 0,
                "Replace the client name with a description of the model:\n  "
                + string.Join("\n  ", offences.ToArray()));
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
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "schema")))
                dir = dir.Parent;
            Assert.True(dir != null, "no repository root above " + here);
            return dir.FullName;
        }
    }
}
