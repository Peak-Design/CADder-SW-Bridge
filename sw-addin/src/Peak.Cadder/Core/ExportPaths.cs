using System;
using System.IO;
using System.Text;

namespace Peak.Cadder.Core
{
    /// <summary>
    /// Where an export writes its files.
    ///
    /// Every export gets a folder of its own, named after the document,
    /// inside the root the user chose. Before 2026-09-16 only the app-data
    /// mode did that, and "next to the assembly" dropped the STEP file, the
    /// manifest and the mesh straight into the project folder, one export
    /// over the last (Oscar).
    ///
    /// No SolidWorks types here on purpose: the caller reads the document's
    /// path, and the rule is then arithmetic on strings that the tests can
    /// run.
    /// </summary>
    public static class ExportPaths
    {
        /// <summary>The folder for one export. A "custom" root that is
        /// empty falls back to app data rather than failing the send: the
        /// export is the work, the folder is a preference.</summary>
        public static string For(AppSettings settings, string modelPath, string baseName)
        {
            string mode = settings == null ? "temp" : settings.ExportFolderMode;
            string root = null;
            if (mode == "beside" && !string.IsNullOrEmpty(modelPath))
                root = Path.GetDirectoryName(modelPath);
            else if (mode == "custom" && settings != null
                     && !string.IsNullOrEmpty((settings.ExportFolder ?? "").Trim()))
                root = settings.ExportFolder.Trim();
            if (string.IsNullOrEmpty(root)) root = AppDataExports;
            return Path.Combine(root, FolderName(baseName));
        }

        /// <summary>System.Environment spelled out: the sldworks interop
        /// also declares an Environment type, and this file is read beside
        /// code that uses it.</summary>
        public static string AppDataExports
        {
            get
            {
                return Path.Combine(
                    System.Environment.GetFolderPath(
                        System.Environment.SpecialFolder.LocalApplicationData),
                    "PeakDesign", "CADder", "exports");
            }
        }

        /// <summary>The document's name as a folder name. It comes from a
        /// file name and is already legal, but a trailing dot or space is
        /// legal in one and not in the other.</summary>
        public static string FolderName(string baseName)
        {
            if (string.IsNullOrEmpty(baseName)) return "export";
            var clean = new StringBuilder(baseName.Length);
            foreach (var c in baseName)
                clean.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0
                    ? '_' : c);
            string name = clean.ToString().TrimEnd('.', ' ');
            return name.Length == 0 ? "export" : name;
        }
    }
}
