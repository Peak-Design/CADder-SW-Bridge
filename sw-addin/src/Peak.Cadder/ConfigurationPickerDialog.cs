using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Peak.Cadder
{
    /// <summary>
    /// Which configurations a send takes, when the Multiple configurations
    /// option is on.
    ///
    /// Each configuration that the user ticks goes to Blender as a complete
    /// send of its own, so it stands in its own collection with its own
    /// rig. The dialog ticks what the user sent last time from this
    /// document in this SolidWorks session. A user who sends the same five
    /// configurations all day ticks them once.
    /// </summary>
    public sealed class ConfigurationPickerDialog : Form
    {
        /// <summary>What the user sent last, by document path, for as
        /// long as this SolidWorks session runs.</summary>
        private static readonly Dictionary<string, List<string>> LastChoice =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        private readonly ConfigurationChecklist _list;

        private ConfigurationPickerDialog(
            IList<string> names, string active, ICollection<string> ticked, bool rig)
        {
            Text = "Send to Blender";
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
            root.Controls.Add(new Label
            {
                Text = "Select the configurations to send. Each configuration goes "
                    + "to Blender in its own collection"
                    + (rig ? ", with its own rig." : "."),
                AutoSize = true,
                MaximumSize = new Size(380, 0),
                Margin = new Padding(0, 0, 0, 8),
                UseCompatibleTextRendering = false,
            });
            _list = new ConfigurationChecklist(names, active, ticked);
            root.Controls.Add(_list);

            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 10, 0, 0),
            };
            var cancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                AutoSize = true,
                UseCompatibleTextRendering = false,
            };
            var send = new Button
            {
                Text = "Send",
                DialogResult = DialogResult.OK,
                AutoSize = true,
                Margin = new Padding(6, 3, 3, 3),
                UseCompatibleTextRendering = false,
            };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(send);
            root.Controls.Add(buttons);

            // A send of nothing is not a send, so the button waits for a
            // tick.
            send.Enabled = _list.Ticked.Count > 0;
            _list.TickedChanged += (s, e) => send.Enabled = _list.Ticked.Count > 0;

            AcceptButton = send;
            CancelButton = cancel;
            Controls.Add(root);
        }

        /// <summary>
        /// The configurations to send, ticked by the user, in the order of
        /// the document. Null when the user cancels.
        /// <paramref name="rig"/> says whether the send builds a rig, so
        /// the dialog does not promise one for a part.
        /// </summary>
        public static List<string> Choose(
            IWin32Window owner, string documentPath, IList<string> names,
            string active, bool rig)
        {
            List<string> remembered;
            LastChoice.TryGetValue(documentPath ?? "", out remembered);
            var ticked = DefaultTicks(names, active, remembered);
            using (var dialog = new ConfigurationPickerDialog(names, active, ticked, rig))
            {
                var result = owner == null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
                if (result != DialogResult.OK) return null;
                var chosen = dialog._list.Ticked;
                if (chosen.Count == 0) return null;
                LastChoice[documentPath ?? ""] = chosen;
                return chosen;
            }
        }

        /// <summary>
        /// The configurations ticked when the dialog opens: the ones the
        /// user sent last time, when they are still in the document. A
        /// configuration deleted or renamed since then is left out. When
        /// none of them is left, the active configuration only, which is
        /// what a send without the dialog takes.
        /// </summary>
        internal static List<string> DefaultTicks(
            IList<string> names, string active, IList<string> remembered)
        {
            var ticks = new List<string>();
            if (names == null) return ticks;
            if (remembered != null)
                foreach (var name in names)
                    if (remembered.Contains(name)) ticks.Add(name);
            if (ticks.Count == 0 && !string.IsNullOrEmpty(active) && names.Contains(active))
                ticks.Add(active);
            return ticks;
        }
    }

    /// <summary>
    /// The configurations of a document, each with a check box, and the
    /// Select All and Clear buttons under them. Send to Blender asks with
    /// it which configurations to send (ConfigurationPickerDialog), and
    /// Refresh Model which ones to bring up to date (RigUpdateDialog).
    /// </summary>
    internal sealed class ConfigurationChecklist : TableLayoutPanel
    {
        private sealed class Item
        {
            public string Name;
            public string Label;
            public override string ToString() => Label;
        }

        /// <summary>The most rows the list shows before it scrolls.</summary>
        private const int MostRows = 10;

        private readonly CheckedListBox _list;

        /// <summary>Raised after a tick changes.</summary>
        public event EventHandler TickedChanged;

        public ConfigurationChecklist(
            IList<string> names, string active, ICollection<string> ticked)
        {
            ColumnCount = 1;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Margin = new Padding(0);

            _list = new CheckedListBox
            {
                CheckOnClick = true,
                IntegralHeight = true,
                HorizontalScrollbar = true,
                Width = 360,
                Font = SystemFonts.MessageBoxFont,
                Margin = new Padding(0, 0, 0, 4),
                UseCompatibleTextRendering = false,
            };
            foreach (var name in names ?? new List<string>())
            {
                // The active configuration is the one SolidWorks shows now,
                // and the one a send without this list takes.
                var item = new Item
                {
                    Name = name,
                    Label = name == active ? name + " (active)" : name,
                };
                _list.Items.Add(item, ticked != null && ticked.Contains(name));
            }
            int rows = Math.Max(3, Math.Min(_list.Items.Count, MostRows));
            _list.Height = rows * _list.ItemHeight + 4;
            // ItemCheck comes before the tick changes, so the event waits
            // for the list to take the change. A list with no window yet
            // has had no click to wait for.
            _list.ItemCheck += (s, e) =>
            {
                if (_list.IsHandleCreated) _list.BeginInvoke(new Action(Changed));
            };

            var all = Push("Select All", () => TickAll(true));
            var none = Push("Clear", () => TickAll(false));
            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0),
            };
            buttons.Controls.Add(all);
            buttons.Controls.Add(none);

            Controls.Add(_list);
            Controls.Add(buttons);
        }

        /// <summary>The ticked configurations, in the order of the
        /// document.</summary>
        public List<string> Ticked
        {
            get
            {
                var names = new List<string>();
                for (int i = 0; i < _list.Items.Count; i++)
                    if (_list.GetItemChecked(i)) names.Add(((Item)_list.Items[i]).Name);
                return names;
            }
        }

        /// <summary>Ticks every configuration, or none. Internal for the
        /// tests, which have no mouse to click the buttons.</summary>
        internal void TickAll(bool tick)
        {
            for (int i = 0; i < _list.Items.Count; i++) _list.SetItemChecked(i, tick);
            Changed();
        }

        private void Changed()
        {
            var handler = TickedChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private static Button Push(string text, Action click)
        {
            var button = new Button
            {
                Text = text,
                AutoSize = true,
                Margin = new Padding(0, 0, 6, 0),
                UseCompatibleTextRendering = false,
            };
            button.Click += (s, e) => click();
            return button;
        }
    }
}
