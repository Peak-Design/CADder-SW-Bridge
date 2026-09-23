using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// Register-Addin.bat, the install route without the installer. It
    /// ships in the release zip next to the DLL, and a user runs it with a
    /// double-click.
    ///
    /// The script cannot run here as it is: it asks for administrator
    /// rights and writes to HKLM. So each test runs the script's own lines
    /// with the two outside calls replaced: the administrator check, and
    /// PowerShell, which only prints what it would run. Nothing is
    /// registered.
    /// </summary>
    public class RegisterScriptTests : IDisposable
    {
        private readonly string _dir;

        public RegisterScriptTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "cadder-register-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        private static string Script()
        {
            return File.ReadAllText(Path.Combine(RepositoryRoot(),
                "sw-addin", "src", "Peak.Cadder", "Register-Addin.bat"));
        }

        /// <summary>Runs a batch file in the test folder and returns what
        /// it printed. Input is empty, so a pause does not wait.</summary>
        private string Run(string name, string text, string args = "")
        {
            string path = Path.Combine(_dir, name);
            File.WriteAllText(path, text, Encoding.ASCII);
            var start = new ProcessStartInfo("cmd.exe", "/c \"\"" + path + "\" " + args + "\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
                WorkingDirectory = _dir,
            };
            using (var p = Process.Start(start))
            {
                p.StandardInput.Close();
                string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                Assert.True(p.WaitForExit(30000), "the script did not finish");
                return output;
            }
        }

        /// <summary>The lines before the administrator check: the ones that
        /// decide which DLL is registered.</summary>
        private string WhichDll()
        {
            string script = Script();
            int cut = script.IndexOf("REM Self-elevate", StringComparison.Ordinal);
            Assert.True(cut > 0, "no administrator check in the script");
            string output = Run("probe.bat", script.Substring(0, cut) + "\r\necho DLL=%DLL%\r\n");
            foreach (var line in output.Split('\n'))
                if (line.StartsWith("DLL=", StringComparison.Ordinal)) return line.Substring(4).Trim();
            throw new Xunit.Sdk.XunitException("the probe printed no DLL: " + output);
        }

        [Fact]
        public void TheZipLayoutFindsTheDllNextToTheScript()
        {
            // The release zip: the DLL and the script side by side, no bin
            // folder.
            File.WriteAllText(Path.Combine(_dir, "Peak.Cadder.dll"), "");
            Assert.Equal(Path.Combine(_dir, "Peak.Cadder.dll"), WhichDll(), ignoreCase: true);
        }

        [Fact]
        public void TheBuildLayoutStillFindsTheBuild()
        {
            string bin = Path.Combine(_dir, "bin", "Release");
            Directory.CreateDirectory(bin);
            File.WriteAllText(Path.Combine(bin, "Peak.Cadder.dll"), "");
            Assert.Equal(Path.Combine(bin, "Peak.Cadder.dll"), WhichDll(), ignoreCase: true);
        }

        /// <summary>What the script asks PowerShell to run when it is not
        /// an administrator, with PowerShell replaced by an echo.</summary>
        private string ElevationCommand(string args)
        {
            string script = Script()
                .Replace("net session >nul 2>&1", "cmd /c exit 1");
            var text = new StringBuilder();
            foreach (var raw in script.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw;
                if (line.Contains("Start-Process"))
                    line = line.Replace("powershell", "echo ELEVATE");
                text.Append(line).Append("\r\n");
            }
            string output = Run("Register-Addin.bat", text.ToString(), args);
            foreach (var line in output.Split('\n'))
            {
                int at = line.IndexOf("ELEVATE", StringComparison.Ordinal);
                if (at < 0 || line.TrimStart().StartsWith("echo", StringComparison.Ordinal)) continue;
                int first = line.IndexOf('"');
                int last = line.LastIndexOf('"');
                Assert.True(first >= 0 && last > first, "no command in: " + line);
                return line.Substring(first + 1, last - first - 1);
            }
            throw new Xunit.Sdk.XunitException("the script did not ask for elevation: " + output);
        }

        /// <summary>Runs the command in PowerShell against a stand-in for
        /// Start-Process that has the cmdlet's own parameters. PowerShell
        /// checks the arguments against them, and nothing starts.
        /// (Start-Process takes no -WhatIf together with -Verb.)</summary>
        private static void AssertPowerShellTakes(string command)
        {
            string script =
                "$meta = New-Object System.Management.Automation.CommandMetadata (Get-Command Start-Process -CommandType Cmdlet)\n"
                + "$params = [System.Management.Automation.ProxyCommand]::GetParamBlock($meta)\n"
                + "$binding = [System.Management.Automation.ProxyCommand]::GetCmdletBindingAttribute($meta)\n"
                + "Invoke-Expression (\"function Start-Process { \" + $binding + \" param(\" + $params + \") 'bound' }\")\n"
                + "$ErrorActionPreference = 'Stop'\n"
                + command + "\n";
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var start = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -OutputFormat Text -EncodedCommand " + encoded)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using (var p = Process.Start(start))
            {
                string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                Assert.True(p.WaitForExit(60000), "PowerShell did not finish");
                Assert.True(p.ExitCode == 0, "PowerShell refused: " + command + "\n" + output);
            }
        }

        [Fact]
        public void ADoubleClickAsksForAdministratorRights()
        {
            // No arguments, as from Explorer.
            AssertPowerShellTakes(ElevationCommand(""));
        }

        [Fact]
        public void TheArgumentsGoToTheElevatedScript()
        {
            string command = ElevationCommand("Debug");
            Assert.Contains("Debug", command);
            AssertPowerShellTakes(command);
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
