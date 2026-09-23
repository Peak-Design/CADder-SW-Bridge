using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// When tools/Make-Release.ps1 may build a version. It deletes and
    /// writes dist\CADder-Bridge-&lt;version&gt; and its zip and setup.exe.
    /// A test build of a later commit that still carried a released version
    /// number overwrote the released files with files that called
    /// themselves the same version. The check ran only with -Tag.
    ///
    /// Each test runs the check in PowerShell on a scratch git repository.
    /// </summary>
    public class ReleaseCheckTests : IDisposable
    {
        private readonly string _repo;

        public ReleaseCheckTests()
        {
            _repo = Path.Combine(Path.GetTempPath(), "cadder-release-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_repo);
            Git("init -q");
            Commit("first");
            Git("tag sw-v1.0.1");
        }

        public void Dispose()
        {
            try
            {
                foreach (var file in Directory.GetFiles(_repo, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(_repo, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private string Run(string exe, string args)
        {
            var start = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = _repo,
            };
            using (var p = Process.Start(start))
            {
                string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                Assert.True(p.WaitForExit(60000), exe + " did not finish");
                Assert.True(p.ExitCode == 0 || exe != "git", "git " + args + ": " + output);
                return output;
            }
        }

        private void Git(string args)
        {
            Run("git", "-c user.email=test@example.com -c user.name=test " + args);
        }

        private void Commit(string text)
        {
            File.WriteAllText(Path.Combine(_repo, "file.txt"), text);
            Git("add file.txt");
            Git("commit -q -m " + text);
        }

        /// <summary>What the check says, or null when the build may go
        /// ahead.</summary>
        private string Check(string version, bool tag = false, bool dirty = false)
        {
            string script = ". '" + ChecksScript() + "'\n"
                + "$r = Test-ReleaseVersion -Repo '" + _repo + "' -Version '" + version + "'"
                + (tag ? " -Tag" : "") + (dirty ? " -Dirty" : "") + "\n"
                + "if ($r) { Write-Output ('BLOCKED ' + $r) } else { Write-Output 'FREE' }\n";
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            string output = Run("powershell.exe",
                "-NoProfile -NonInteractive -OutputFormat Text -EncodedCommand " + encoded);
            foreach (var raw in output.Split('\n'))
            {
                string line = raw.Trim();
                if (line == "FREE") return null;
                if (line.StartsWith("BLOCKED ", StringComparison.Ordinal)) return line.Substring(8);
            }
            throw new Xunit.Sdk.XunitException("the check printed nothing: " + output);
        }

        [Fact]
        public void ALaterCommitCannotBuildAReleasedVersion()
        {
            Commit("second");
            Assert.NotNull(Check("1.0.1"));
        }

        [Fact]
        public void TheReleasedCommitCanBeBuiltAgain()
        {
            Assert.Null(Check("1.0.1"));
        }

        [Fact]
        public void ChangesOnTopOfTheReleasedCommitCannotBuildIt()
        {
            Assert.NotNull(Check("1.0.1", dirty: true));
        }

        [Fact]
        public void ANewVersionBuilds()
        {
            Commit("second");
            Assert.Null(Check("1.0.2"));
            Assert.Null(Check("1.0.2", tag: true));
        }

        [Fact]
        public void AnExistingTagIsNotMadeAgain()
        {
            Assert.NotNull(Check("1.0.1", tag: true));
        }

        private static string ChecksScript([CallerFilePath] string here = null)
        {
            var dir = new DirectoryInfo(Path.GetDirectoryName(here));
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "schema")))
                dir = dir.Parent;
            Assert.True(dir != null, "no repository root above " + here);
            return Path.Combine(dir.FullName, "sw-addin", "tools", "Release-Checks.ps1");
        }
    }
}
