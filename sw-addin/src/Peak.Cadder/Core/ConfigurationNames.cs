using System;
using System.Collections.Generic;
using System.Text;

namespace Peak.Cadder.Core
{
    /// <summary>
    /// The names a send gives its files, from the document and the
    /// configuration it sends.
    ///
    /// Every send writes &lt;base&gt;_&lt;cfg&gt;.swmesh, .rig.json and .step,
    /// also a send of one configuration. Before 1.2.0 a send wrote
    /// &lt;base&gt;.swmesh, and Blender replaced the earlier import, so two
    /// configurations of one assembly could not stand side by side. Blender
    /// names the import after the same stem, so the rule here is the
    /// contract between the two halves. Change it on both sides or not at
    /// all.
    ///
    /// No SolidWorks types here on purpose: the caller reads the names, and
    /// the tests can run the rule.
    /// </summary>
    public static class ConfigurationNames
    {
        /// <summary>The characters Windows does not accept in a file name.
        /// SafeName finds the control characters with char.IsControl.
        /// </summary>
        private const string Refused = "\\/:*?\"<>|";

        /// <summary>
        /// The configuration name as a part of a file name. Each character
        /// that Windows refuses, and each control character, becomes an
        /// underscore. Windows removes trailing dots and spaces from a file
        /// name, so they go here too. A name with nothing left becomes one
        /// underscore.
        /// </summary>
        public static string SafeName(string configuration)
        {
            var clean = new StringBuilder((configuration ?? "").Length);
            foreach (char c in configuration ?? "")
                clean.Append(char.IsControl(c) || Refused.IndexOf(c) >= 0 ? '_' : c);
            string name = clean.ToString().TrimEnd('.', ' ');
            return name.Length == 0 ? "_" : name;
        }

        /// <summary>The name of the files of one send, without an
        /// extension: the document name, an underscore and the safe
        /// configuration name.</summary>
        public static string Stem(string baseName, string configuration)
        {
            return (baseName ?? "") + "_" + SafeName(configuration);
        }

        /// <summary>
        /// The configuration of <paramref name="names"/> that a request
        /// means, in the spelling of the document, or null when there is
        /// none. The exact name comes first. A name that differs only in
        /// case comes next, so a configuration that the user renamed from
        /// "default" to "Default" after a send is still found.
        /// </summary>
        public static string Find(IEnumerable<string> names, string wanted)
        {
            if (names == null || string.IsNullOrEmpty(wanted)) return null;
            string other = null;
            foreach (var name in names)
            {
                if (string.Equals(name, wanted, StringComparison.Ordinal)) return name;
                if (other == null && string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase))
                    other = name;
            }
            return other;
        }
    }
}
