using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Peak.Cadder.Core;

namespace Peak.Cadder
{
    /// <summary>
    /// What a refresh should do with the rig.
    ///
    /// The parts can be brought up to date without asking anybody: a part
    /// is where SolidWorks says it is. The rig cannot, because it may have
    /// been taken over by hand, keyed, or both, and only the user knows
    /// which of those matters more than the new joints.
    ///
    /// The choice is remembered, so a user who refreshes all day answers
    /// once.
    ///
    /// When Blender holds two or more configurations of the document, the
    /// dialog also asks which of them to bring up to date, and ticks the
    /// ones Blender holds. A part has no rig, so for a part the dialog asks
    /// only about the configurations. A part with one configuration in
    /// Blender is not asked at all.
    /// </summary>
    public sealed class RigUpdateDialog : Form
    {
        public const string Keep = "KEEP";
        public const string Append = "APPEND";
        public const string Regenerate = "REGENERATE";

        private readonly RadioButton _keep;
        private readonly RadioButton _append;
        private readonly RadioButton _regenerate;
        private readonly ConfigurationChecklist _configurations;

        /// <summary>What the user chose.</summary>
        public sealed class Answer
        {
            /// <summary>Keep, Append or Regenerate.</summary>
            public string Mode;

            /// <summary>The configurations to bring up to date, in the
            /// order of the document. Null when the dialog did not ask.
            /// </summary>
            public List<string> Configurations;
        }

        private RigUpdateDialog(string chosen, bool hasRig, IList<string> configurations,
                                string active, ICollection<string> ticked)
        {
            Text = "Refresh Model";
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

            if (configurations != null)
            {
                root.Controls.Add(Prose(
                    "Blender holds more than one configuration of this "
                    + "document. Select the configurations to bring up to date."));
                _configurations = new ConfigurationChecklist(configurations, active, ticked);
                _configurations.Margin = new Padding(0, 4, 0, hasRig ? 12 : 0);
                root.Controls.Add(_configurations);
            }

            if (hasRig)
            {
                root.Controls.Add(Prose(
                    "The parts, their places and the assembly tree are brought "
                    + "up to date either way. What should happen to the rig?"));

                _append = Option(root,
                    "Add and remove bones",
                    "Bones for new parts arrive and bones for parts that have "
                    + "gone are removed. A body made of the same parts as before "
                    + "keeps its bone and its name, so keyframes on it still "
                    + "work. Bones you added yourself are left alone.");
                _keep = Option(root,
                    "Keep the rig as it is",
                    "Nothing about the rig changes. New parts arrive without "
                    + "bones, and a part that moved sits where SolidWorks has it "
                    + "rather than where its bone was.");
                _regenerate = Option(root,
                    "Build a new rig",
                    "The rig is thrown away and built again from the new mates. "
                    + "Anything done to it by hand goes with it, keyframes "
                    + "included.");

                switch (chosen)
                {
                    case Keep: _keep.Checked = true; break;
                    case Regenerate: _regenerate.Checked = true; break;
                    default: _append.Checked = true; break;
                }
            }

            var ok = new Button
            {
                Text = "Refresh",
                DialogResult = DialogResult.OK,
                AutoSize = true,
                UseCompatibleTextRendering = false,
            };
            var cancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                AutoSize = true,
                UseCompatibleTextRendering = false,
            };
            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 10, 0, 0),
            };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            root.Controls.Add(buttons);

            // A refresh of nothing is not a refresh, so the button waits
            // for a tick.
            if (_configurations != null)
            {
                ok.Enabled = _configurations.Ticked.Count > 0;
                _configurations.TickedChanged += (s, e) =>
                    ok.Enabled = _configurations.Ticked.Count > 0;
            }

            AcceptButton = ok;
            CancelButton = cancel;
            Controls.Add(root);
        }

        /// <summary>
        /// What the user picked, or null if they cancelled.
        ///
        /// <paramref name="configurations"/> lists every configuration of
        /// the document when Blender holds two or more of them, and is null
        /// otherwise. <paramref name="held"/> are the ones Blender holds,
        /// which the dialog ticks. A part document has no rig to decide
        /// about, so a part is asked only about its configurations, and is
        /// not asked at all when there is no list.
        /// </summary>
        public static Answer Choose(IWin32Window owner, AppSettings settings, bool hasRig,
                                    IList<string> configurations = null, string active = null,
                                    ICollection<string> held = null)
        {
            string chosen = (settings == null ? null : settings.RigUpdateMode) ?? Append;
            if (!hasRig && configurations == null) return new Answer { Mode = chosen };
            using (var dialog = new RigUpdateDialog(chosen, hasRig, configurations, active, held))
            {
                if (dialog.ShowDialog(owner) != DialogResult.OK) return null;
                string mode = chosen;
                if (hasRig)
                    mode = dialog._keep.Checked ? Keep
                         : dialog._regenerate.Checked ? Regenerate : Append;
                var names = dialog._configurations == null ? null : dialog._configurations.Ticked;
                if (names != null && names.Count == 0) return null;
                return new Answer { Mode = mode, Configurations = names };
            }
        }

        /// <summary>One choice: the button, and under it the sentence that
        /// says what it means. The explanation is not in the label, so the
        /// labels stay short enough to compare at a glance.</summary>
        private static RadioButton Option(Control into, string label, string tip)
        {
            var button = new RadioButton
            {
                Text = label,
                AutoSize = true,
                UseCompatibleTextRendering = false,
                Margin = new Padding(0, 8, 0, 0),
            };
            var prose = Prose(tip);
            prose.Margin = new Padding(20, 0, 0, 4);
            prose.ForeColor = SystemColors.GrayText;
            into.Controls.Add(button);
            into.Controls.Add(prose);
            return button;
        }

        private static Label Prose(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                MaximumSize = new Size(380, 0),
                Margin = new Padding(0, 0, 0, 4),
                UseCompatibleTextRendering = false,
            };
        }
    }
}
