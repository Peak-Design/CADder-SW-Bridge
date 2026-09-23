using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace Peak.Cadder.Bridge
{
    /// <summary>
    /// Which CADder works with this add-in.
    ///
    /// The two halves work together when the first two numbers of their
    /// versions are the same: CADder Bridge 1.1.x with CADder 1.1.x. The
    /// last number is a release of one half on its own, which changes
    /// nothing that the other half reads. CADder Pro has the first two
    /// numbers of the CADder it is built on and a last number of its own,
    /// so CADder Pro 1.1.5 works with CADder Bridge 1.1.0. schema/SCHEMA.md
    /// ("Versions of the two halves") is the rule for a release.
    ///
    /// Oscar, 2026-09-23: "we should add a warning to both side of the SW
    /// bridge addons if there are version mismatches ... SW side, add a
    /// dialoge warning before anything is even sent with a download link to
    /// the new version and give them an option to abort or continue".
    /// CADder (bridge.py) has the same rule and says the same in Blender.
    /// </summary>
    public static class VersionMatch
    {
        public const string CadderReleases =
            "https://github.com/Peak-Design/CADder/releases/latest";
        public const string BridgeReleases =
            "https://github.com/Peak-Design/CADder-SW-Bridge/releases/latest";

        /// <summary>A version as a person reads it: this add-in says
        /// "v1.1.0".</summary>
        public static string Plain(string version)
        {
            return (version ?? "").Trim().TrimStart('v', 'V');
        }

        /// <summary>(major, minor) of "1.1.0", "v1.1.0" or "1.1.0.0", or
        /// null when the version is not known.</summary>
        public static int[] MajorMinor(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return null;
            var parts = Plain(version).Split('.');
            int major, minor;
            if (parts.Length < 2
                || !int.TryParse(parts[0], out major)
                || !int.TryParse(parts[1], out minor))
                return null;
            return new[] { major, minor };
        }

        /// <summary>True when the two work together, false when they do
        /// not, null when a version is not known.</summary>
        public static bool? Match(string a, string b)
        {
            var ma = MajorMinor(a);
            var mb = MajorMinor(b);
            if (ma == null || mb == null) return null;
            return ma[0] == mb[0] && ma[1] == mb[1];
        }

        /// <summary>What to tell the user, or null when the two work
        /// together or a version is not known.</summary>
        public static Mismatch For(string addinVersion, string addonVersion,
                                   string addonName)
        {
            if (Match(addinVersion, addonVersion) != false) return null;
            var addin = MajorMinor(addinVersion);
            var addon = MajorMinor(addonVersion);
            string name = string.IsNullOrEmpty(addonName) ? "CADder" : addonName;
            bool addinOlder = addin[0] < addon[0]
                              || (addin[0] == addon[0] && addin[1] < addon[1]);
            string update = addinOlder ? "CADder Bridge" : name;
            var want = addinOlder ? addon : addin;
            string wanted = want[0] + "." + want[1];
            var m = new Mismatch
            {
                AddinVersion = Plain(addinVersion),
                AddonVersion = Plain(addonVersion),
                AddonName = name,
                Update = update,
                Wanted = wanted,
                Url = addinOlder ? BridgeReleases
                    : update == "CADder Pro" ? "" : CadderReleases,
            };
            m.Advice = update == "CADder Pro"
                ? "Update CADder Pro to " + wanted + " from where you got it."
                : "Update " + update + " to " + wanted + ".";
            m.LinkText = "Download " + update + " " + wanted;
            return m;
        }

        public sealed class Mismatch
        {
            public string AddinVersion;
            public string AddonVersion;
            public string AddonName;
            /// <summary>"CADder Bridge", "CADder" or "CADder Pro".</summary>
            public string Update;
            /// <summary>The first two numbers the older half needs.</summary>
            public string Wanted;
            /// <summary>Empty for CADder Pro, which has no public download.</summary>
            public string Url;
            public string Advice;
            public string LinkText;

            /// <summary>The text of the dialog, the link aside.</summary>
            public string Text
            {
                get
                {
                    return "The CADder in Blender does not match this CADder Bridge.\r\n\r\n"
                        + "CADder Bridge: " + AddinVersion + "\r\n"
                        + AddonName + ": " + AddonVersion + "\r\n\r\n"
                        + "The two work together only when the first two numbers of "
                        + "their versions are the same. " + Advice + "\r\n\r\n"
                        + "Continue sends the model all the same. Parts of the send "
                        + "can fail or come out wrong.";
                }
            }

            /// <summary>One line for the log.</summary>
            public override string ToString()
            {
                return "CADder Bridge " + AddinVersion + " does not match "
                    + AddonName + " " + AddonVersion + ". " + Advice
                    + (string.IsNullOrEmpty(Url) ? "" : " " + Url);
            }

            /// <summary>The pair, for the answer the session keeps.</summary>
            public string Key
            {
                get { return AddinVersion + "|" + AddonName + "|" + AddonVersion; }
            }
        }
    }

    /// <summary>
    /// Asks before a send goes to a Blender whose CADder does not match
    /// this add-in. A Continue holds for that pair of versions until
    /// SolidWorks closes, so each send does not ask again. An Abort asks
    /// again next time.
    /// </summary>
    public static class VersionGate
    {
        private static readonly HashSet<string> Accepted =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly object Lock = new object();

        /// <summary>The dialog. A test puts its own answer here.</summary>
        public static Func<IWin32Window, VersionMatch.Mismatch, bool> Ask =
            VersionMismatchDialog.Ask;

        /// <summary>True to go on with the send: the versions match, or
        /// are not known, or the user chose Continue.</summary>
        public static bool Confirm(IWin32Window owner, BlenderInstance target,
                                   Action<string> log = null)
        {
            if (target == null) return true;
            var m = VersionMatch.For(AddIn.AddInVersion, target.AddonVersion,
                                     target.AddonName);
            if (m == null) return true;
            lock (Lock)
            {
                if (Accepted.Contains(m.Key)) return true;
            }
            if (log != null) log("versions: " + m);
            bool go = Ask(owner, m);
            if (log != null) log("versions: " + (go ? "the user chose Continue" : "the user chose Abort"));
            if (go)
            {
                lock (Lock) { Accepted.Add(m.Key); }
            }
            return go;
        }

        /// <summary>Forgets every Continue. For the tests.</summary>
        public static void Reset()
        {
            lock (Lock) { Accepted.Clear(); }
        }
    }

    /// <summary>
    /// The warning before a send to a CADder that does not match: the two
    /// versions, what to update, a link to the download, and Continue or
    /// Abort. Esc and the close box abort.
    /// </summary>
    public sealed class VersionMismatchDialog : Form
    {
        private VersionMismatchDialog(VersionMatch.Mismatch m)
        {
            Text = "CADder Versions Do Not Match";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Font;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Font = SystemFonts.MessageBoxFont;

            var root = new TableLayoutPanel
            {
                ColumnCount = 2,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
                Padding = new Padding(16),
            };
            root.Controls.Add(new PictureBox
            {
                Image = SystemIcons.Warning.ToBitmap(),
                SizeMode = PictureBoxSizeMode.AutoSize,
                Margin = new Padding(0, 0, 12, 0),
            }, 0, 0);
            var text = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0),
            };
            text.Controls.Add(new Label
            {
                Text = m.Text,
                AutoSize = true,
                MaximumSize = new Size(380, 0),
                Margin = new Padding(0, 0, 0, 10),
            });
            if (!string.IsNullOrEmpty(m.Url))
            {
                var link = new LinkLabel
                {
                    Text = m.LinkText,
                    AutoSize = true,
                    Margin = new Padding(0, 0, 0, 10),
                };
                link.LinkClicked += (s, e) =>
                {
                    try { Process.Start(new ProcessStartInfo(m.Url) { UseShellExecute = true }); }
                    catch (Exception ex) { AddIn.Log("versions: could not open " + m.Url + ": " + ex.Message); }
                };
                text.Controls.Add(link);
            }
            root.Controls.Add(text, 1, 0);

            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 6, 0, 0),
            };
            var abort = new Button { Text = "Abort", DialogResult = DialogResult.Cancel, AutoSize = true };
            var go = new Button { Text = "Continue", DialogResult = DialogResult.OK, AutoSize = true };
            buttons.Controls.Add(abort);
            buttons.Controls.Add(go);
            root.Controls.Add(buttons, 0, 1);
            root.SetColumnSpan(buttons, 2);
            Controls.Add(root);
            // A send to a CADder that does not match is the risk, so the
            // safe answer is the one Enter and Esc give.
            AcceptButton = abort;
            CancelButton = abort;
        }

        /// <summary>True for Continue.</summary>
        public static bool Ask(IWin32Window owner, VersionMatch.Mismatch m)
        {
            using (var dlg = new VersionMismatchDialog(m))
                return dlg.ShowDialog(owner) == DialogResult.OK;
        }

        /// <summary>The dialog drawn into a bitmap, for a check of its
        /// layout. It is shown off the screen for a moment: a form that was
        /// never shown draws without its controls.</summary>
        public static Bitmap Render(VersionMatch.Mismatch m)
        {
            using (var dlg = new VersionMismatchDialog(m))
            {
                dlg.StartPosition = FormStartPosition.Manual;
                dlg.Location = new Point(-20000, -20000);
                dlg.Show();
                Application.DoEvents();
                var bmp = new Bitmap(dlg.Width, dlg.Height);
                dlg.DrawToBitmap(bmp, new Rectangle(Point.Empty, dlg.Size));
                dlg.Close();
                return bmp;
            }
        }
    }
}
