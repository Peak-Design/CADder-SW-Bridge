using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Peak.SwToBlender.Bridge
{
    /// <summary>
    /// Marquee progress while a worker thread talks to Blender. The COM/SW
    /// calls all happen BEFORE this shows; the worker does pure .NET (HTTP,
    /// process launch), so the thread split is safe. No close box: the only
    /// way out is the worker finishing (or the Cancel returning the thread's
    /// result to the void: the HTTP call cannot be aborted mid-import without
    /// leaving Blender half-imported, so there is deliberately no cancel).
    /// </summary>
    public sealed class ProgressDialog : Form
    {
        private ProgressDialog(string title, string message)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ControlBox = false;
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
                Padding = new Padding(16),
            };
            root.Controls.Add(new Label
            {
                Text = message,
                AutoSize = true,
                MaximumSize = new Size(360, 0),
                Margin = new Padding(0, 0, 0, 10),
                UseCompatibleTextRendering = false,
            });
            root.Controls.Add(new ProgressBar
            {
                Style = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 30,
                Width = 340,
                Height = 18,
                Margin = new Padding(0),
            });
            Controls.Add(root);
        }

        public static T Run<T>(IWin32Window owner, string title, string message,
            Func<T> work)
        {
            Exception error = null;
            T result = default(T);
            using (var dlg = new ProgressDialog(title, message))
            {
                var thread = new System.Threading.Thread(() =>
                {
                    try { result = work(); }
                    catch (Exception ex) { error = ex; }
                    try
                    {
                        dlg.BeginInvoke(new Action(() =>
                        {
                            dlg.DialogResult = DialogResult.OK;
                            dlg.Close();
                        }));
                    }
                    catch (InvalidOperationException) { }
                });
                thread.IsBackground = true;
                dlg.Shown += (s, e) => thread.Start();
                if (owner == null) dlg.ShowDialog(); else dlg.ShowDialog(owner);
                thread.Join();
            }
            if (error != null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo
                    .Capture(error).Throw();
            return result;
        }
    }

    /// <summary>Pick which running Blender receives the export.</summary>
    public sealed class InstanceChooserDialog : Form
    {
        private readonly ListBox _list;
        private readonly List<BlenderInstance> _instances;

        private InstanceChooserDialog(List<BlenderInstance> instances)
        {
            _instances = instances;
            Text = "Send to which Blender?";
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
            _list = new ListBox
            {
                Width = 380,
                Height = 110,
                Margin = new Padding(0, 0, 0, 10),
            };
            foreach (var inst in instances) _list.Items.Add(inst.Describe());
            _list.SelectedIndex = 0;
            _list.DoubleClick += (s, e) => { DialogResult = DialogResult.OK; Close(); };

            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
            };
            var cancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                AutoSize = true,
                UseCompatibleTextRendering = false,
            };
            var ok = new Button
            {
                Text = "Send",
                DialogResult = DialogResult.OK,
                AutoSize = true,
                Margin = new Padding(6, 3, 3, 3),
                UseCompatibleTextRendering = false,
            };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);

            root.Controls.Add(_list);
            root.Controls.Add(buttons);
            Controls.Add(root);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        public static BlenderInstance Choose(
            IWin32Window owner, List<BlenderInstance> instances)
        {
            using (var dlg = new InstanceChooserDialog(instances))
            {
                var result = owner == null ? dlg.ShowDialog() : dlg.ShowDialog(owner);
                if (result != DialogResult.OK) return null;
                int i = dlg._list.SelectedIndex;
                return i >= 0 && i < instances.Count ? instances[i] : null;
            }
        }
    }
}
