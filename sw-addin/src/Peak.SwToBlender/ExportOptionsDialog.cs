using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Peak.SwToBlender.Core;

namespace Peak.SwToBlender
{
    /// <summary>
    /// The Export Rig + STEP options, backed by the persistent AppSettings:
    /// what the user set last time is what the dialog shows next time, in
    /// this session or the next. Built in the constructor, no designer file.
    ///
    /// The two WinForms-inside-SolidWorks traps, learned in NEXT-STEP:
    ///
    ///   * Text rendering. Application.UseCompatibleTextRendering stays TRUE
    ///     because the native host never calls
    ///     SetCompatibleTextRenderingDefault(false). Every control here turns
    ///     it off for itself, or the text renders in greyscale GDI+ and looks
    ///     soft next to every other dialog.
    ///
    ///   * Layout. Hand-written pixel positions clip on machines with a
    ///     different message-box font. Every control sizes itself.
    /// </summary>
    public sealed class ExportOptionsDialog : Form
    {
        private readonly RadioButton _ap214;
        private readonly RadioButton _ap203;
        private readonly CheckBox _dofProbe;
        private readonly CheckBox _appearances;
        private readonly CheckBox _openFolder;

        /// <summary>Wrap width for prose.</summary>
        private const int TextWidth = 430;

        public ExportOptionsDialog(AppSettings settings)
        {
            Text = "Export Rig + STEP";
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
                ColumnCount = 1,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
                Padding = new Padding(12),
            };

            var intro = Prose(
                "Writes the STEP file and a rig manifest (.rig.json) next to it. "
                + "The Blender add-on reads the pair to build a constrained armature.",
                SystemColors.ControlText);
            intro.Margin = new Padding(0, 0, 0, 10);

            // AP214 is the default because it carries colours and the Blender
            // importers in use read it without complaint; AP203 exists for
            // downstream tools that insist on it.
            _ap214 = Radio("AP214 (default)", settings.Ap != 203);
            _ap203 = Radio("AP203", settings.Ap == 203);
            var apHelp = Prose(
                "The STEP application protocol. Both carry the same geometry "
                + "and occurrence tree; AP214 also carries appearances.",
                SystemColors.GrayText);
            apHelp.Margin = new Padding(20, 0, 0, 12);

            _dofProbe = Check("Let the SolidWorks solver decide each joint",
                settings.RunDofProbe);
            var probeHelp = Prose(
                "Asks the solver for each pair's remaining freedom and uses "
                + "its answer: the mates decide which body hangs off which, "
                + "the solver decides what the connection between them is. "
                + "With this off, the joint type is inferred from the mate "
                + "geometry one pair at a time, which is a guess wherever "
                + "three bodies constrain each other. Temporarily fixes "
                + "components and suppresses limit mates, restoring "
                + "everything.",
                SystemColors.GrayText);
            probeHelp.Margin = new Padding(20, 0, 0, 12);

            _appearances = Check("Repair appearances in the STEP (STEP+)",
                settings.RepairAppearances);
            var appearanceHelp = Prose(
                "The NEXT-STEP engine: occurrence colour overrides SolidWorks "
                + "flattens are restored after export, and a flexed flexible "
                + "subassembly next to a rigid twin is de-instanced so both "
                + "import at their SolidWorks poses. De-instancing and "
                + "engineering-material options live in Export Options.",
                SystemColors.GrayText);
            appearanceHelp.Margin = new Padding(20, 0, 0, 12);

            _openFolder = Check("Open output folder when done", settings.OpenFolder);
            _openFolder.Margin = new Padding(0, 0, 0, 14);

            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
            };
            var cancel = Push("Cancel", DialogResult.Cancel);
            var ok = Push("Export", DialogResult.OK);
            ok.Margin = new Padding(6, 3, 3, 3);
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);

            root.Controls.Add(intro);
            root.Controls.Add(_ap214);
            root.Controls.Add(_ap203);
            root.Controls.Add(apHelp);
            root.Controls.Add(_dofProbe);
            root.Controls.Add(probeHelp);
            root.Controls.Add(_appearances);
            root.Controls.Add(appearanceHelp);
            root.Controls.Add(_openFolder);
            root.Controls.Add(buttons);
            Controls.Add(root);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        private void ApplyTo(AppSettings settings)
        {
            settings.Ap = _ap203.Checked ? 203 : 214;
            settings.RunDofProbe = _dofProbe.Checked;
            settings.RepairAppearances = _appearances.Checked;
            settings.OpenFolder = _openFolder.Checked;
        }

        private static Label Prose(string text, Color colour)
            => new Label
            {
                Text = text,
                ForeColor = colour,
                AutoSize = true,
                MaximumSize = new Size(TextWidth, 0),
                Margin = new Padding(0),
                UseCompatibleTextRendering = false,
            };

        private static RadioButton Radio(string text, bool chequed)
            => new RadioButton
            {
                Text = text,
                Checked = chequed,
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 2),
                UseCompatibleTextRendering = false,
            };

        private static CheckBox Check(string text, bool chequed)
            => new CheckBox
            {
                Text = text,
                Checked = chequed,
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 2),
                UseCompatibleTextRendering = false,
            };

        private static Button Push(string text, DialogResult result)
            => new Button
            {
                Text = text,
                DialogResult = result,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(84, 26),
                UseCompatibleTextRendering = false,
            };

        [DllImport("user32.dll")]
        private static extern IntPtr GetActiveWindow();

        internal static IWin32Window ActiveOwner()
        {
            try
            {
                var hwnd = GetActiveWindow();
                if (hwnd != IntPtr.Zero) return new HostWindow(hwnd);
            }
            catch (Exception ex) { AddIn.Log("owner window unavailable: " + ex.Message); }
            return null;
        }

        /// <summary>
        /// Shows the dialog owned by the SolidWorks window that started the
        /// command. On OK the dialog's choices are written back into settings
        /// (the caller persists them). Returns false on cancel.
        /// </summary>
        public static bool Show(AppSettings settings)
        {
            var owner = ActiveOwner();
            using (var dlg = new ExportOptionsDialog(settings))
            {
                var result = owner == null ? dlg.ShowDialog() : dlg.ShowDialog(owner);
                if (result != DialogResult.OK) return false;
                dlg.ApplyTo(settings);
                return true;
            }
        }

        internal sealed class HostWindow : IWin32Window
        {
            public HostWindow(IntPtr handle) { Handle = handle; }
            public IntPtr Handle { get; }
        }
    }
}
