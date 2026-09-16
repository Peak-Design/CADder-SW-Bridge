using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// The writing rules the Export Options dialog follows.
    ///
    /// They are the Blender guidelines, read across to a Windows dialog:
    /// a label names the setting and nothing more, a tooltip says what it
    /// does, the tooltip starts with a verb and ends without a period,
    /// and no label repeats a word its group heading already carries.
    ///
    /// The dialog needs a message loop and a single threaded apartment to
    /// build, so these read the source instead. They catch the mistake
    /// that matters, which is a sentence written into a label.
    /// </summary>
    public class OptionsCopyTests
    {
        private static readonly Regex CheckCall = new Regex(
            @"Check\(\s*""(?<label>(?:[^""\\]|\\.)*)""\s*,\s*settings\.\w+\s*(?<rest>,|\))",
            RegexOptions.Compiled);

        private static readonly Regex Tooltip = new Regex(
            @"(?:_tips\.SetToolTip\([^,]+,|,)\s*\r?\n?\s*""(?<tip>(?:[^""\\]|\\.)*)""",
            RegexOptions.Compiled);

        private static readonly string[] WeakOpeners =
            { "Enables", "Activates", "Whether", "If ", "This " };

        /// <summary>
        /// The source with every string concatenation joined, so a
        /// tooltip written over three lines reads as the one sentence the
        /// user sees rather than three fragments.
        /// </summary>
        private static string Dialog()
        {
            var text = File.ReadAllText(Path.Combine(
                RepositoryRoot(), "sw-addin", "src", "Peak.Cadder",
                "BlenderOptionsDialog.cs"));
            return Regex.Replace(text, @"""\s*\+\s*""", "");
        }

        [Fact]
        public void EveryCheckBoxCarriesATooltip()
        {
            var naked = new List<string>();
            foreach (Match m in CheckCall.Matches(Dialog()))
                if (m.Groups["rest"].Value == ")")
                    naked.Add(m.Groups["label"].Value);
            Assert.True(naked.Count == 0,
                "no tooltip on: " + string.Join(", ", naked));
        }

        [Fact]
        public void NoLabelIsASentence()
        {
            // A label names the setting. The sentence that explains it
            // belongs in the tooltip, where it does not widen the dialog.
            var wordy = new List<string>();
            foreach (Match m in CheckCall.Matches(Dialog()))
            {
                var label = m.Groups["label"].Value;
                if (label.Split(' ').Length > 6 || label.EndsWith("."))
                    wordy.Add(label);
            }
            Assert.True(wordy.Count == 0,
                "these labels read as sentences: " + string.Join(" | ", wordy));
        }

        [Fact]
        public void NoLabelRepeatsTheGroupHeading()
        {
            // "Send to Blender" is the heading, so nothing under it says
            // "Send" again.
            var repeats = new List<string>();
            foreach (Match m in CheckCall.Matches(Dialog()))
            {
                var label = m.Groups["label"].Value;
                if (label.StartsWith("Send ", StringComparison.Ordinal))
                    repeats.Add(label);
            }
            Assert.True(repeats.Count == 0,
                "these repeat the heading: " + string.Join(" | ", repeats));
        }

        [Fact]
        public void NoTooltipEndsWithAPeriodOrOpensWeakly()
        {
            var text = Dialog();
            var offences = new List<string>();
            foreach (Match m in Tooltip.Matches(text))
            {
                var tip = m.Groups["tip"].Value.Trim();
                // Only the multi-word strings are tooltips; a one word
                // string here is a control name or a file filter.
                if (tip.Split(' ').Length < 4) continue;
                if (tip.EndsWith(".") && !tip.Contains(". "))
                    offences.Add("period: " + tip);
                foreach (var weak in WeakOpeners)
                    if (tip.StartsWith(weak, StringComparison.Ordinal))
                        offences.Add("weak opener: " + tip);
            }
            Assert.True(offences.Count == 0,
                string.Join(Environment.NewLine, offences));
        }

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
