using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Peak.SwToBlender.Bridge;
using Peak.SwToBlender.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Peak.SwToBlender
{
    /// <summary>
    /// The persistent bridge options — everything Send to Blender uses so
    /// that the send itself never asks. Combo tags carry the wire values;
    /// the labels stay human.
    /// </summary>
    public sealed class BlenderOptionsDialog : Form
    {
        private sealed class Item
        {
            public string Label;
            public string Value;
            public override string ToString() => Label;
        }

        private readonly ComboBox _hierarchy;
        private readonly ComboBox _quality;
        private readonly ComboBox _upAxis;
        private readonly CheckBox _buildRig;
        private readonly CheckBox _syncPoses;
        private readonly CheckBox _parent;
        private readonly CheckBox _cleanup;
        private readonly CheckBox _deInstance;
        private readonly CheckBox _material;
        private readonly CheckBox _hidden;
        private readonly CheckBox _autoLaunch;
        private readonly CheckBox _focus;
        private readonly ComboBox _exe;
        private readonly ComboBox _exportFolder;

        private const int TextWidth = 460;

        private BlenderOptionsDialog(AppSettings settings)
        {
            Text = "Blender Options";
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

            // ── Import ──────────────────────────────────────────────────────
            var import = Group("Blender import (STEPper NEXT)");
            _hierarchy = Combo(new[]
            {
                new Item { Label = "Parented empties (default)", Value = "EMPTIES" },
                new Item { Label = "Flat collection", Value = "FLAT" },
                new Item { Label = "Tree collections", Value = "TREE" },
                new Item { Label = "Collection instances", Value = "COLLECTION_INSTANCES" },
            }, settings.Hierarchy);
            _quality = Combo(new[]
            {
                new Item { Label = "Draft", Value = "DRAFT" },
                new Item { Label = "Balanced (default)", Value = "BALANCED" },
                new Item { Label = "Fine", Value = "FINE" },
                new Item { Label = "Ultra", Value = "ULTRA" },
            }, settings.QualityPreset);
            _upAxis = Combo(new[]
            {
                new Item { Label = "Z up (keeps the rig frame)", Value = "ZPOS" },
                new Item { Label = "Y up", Value = "YPOS" },
                new Item { Label = "X up", Value = "XPOS" },
            }, settings.UpAxis);
            import.Controls.Add(Row("Hierarchy:", _hierarchy));
            import.Controls.Add(Row("Mesh quality:", _quality));
            import.Controls.Add(Row("Up axis:", _upAxis));

            // ── Pipeline ────────────────────────────────────────────────────
            var pipeline = Group("After import (assemblies)");
            _buildRig = Check("Build the rig", settings.BuildRig);
            _syncPoses = Check("Snap geometry to SolidWorks poses", settings.SyncPoses);
            _parent = Check("Parent geometry to the rig", settings.ParentGeometry);
            _cleanup = Check("Remove leftover import empties", settings.CleanupEmpties);
            pipeline.Controls.Add(_buildRig);
            pipeline.Controls.Add(_syncPoses);
            pipeline.Controls.Add(_parent);
            pipeline.Controls.Add(_cleanup);

            // ── Appearance ──────────────────────────────────────────────────
            var appearance = Group("STEP appearance (STEP+)");
            _deInstance = Check("De-instance components that carry an override",
                settings.DeInstance);
            _material = Check("Include engineering material", settings.EngineeringMaterial);
            _hidden = Check("Include hidden components", settings.IncludeHidden);
            appearance.Controls.Add(_deInstance);
            appearance.Controls.Add(_material);
            appearance.Controls.Add(_hidden);

            // ── Application ─────────────────────────────────────────────────
            var appGroup = Group("Blender application");
            _autoLaunch = Check("Launch Blender when none is running",
                settings.AutoLaunchBlender);
            _focus = Check("Bring Blender to the front when done", settings.FocusBlender);

            var exeItems = new List<Item>
            {
                new Item { Label = "Newest installed Blender", Value = "" },
            };
            foreach (var exe in BlenderBridge.FindInstalledBlenders())
                exeItems.Add(new Item { Label = exe, Value = exe });
            if (!string.IsNullOrEmpty(settings.BlenderExe)
                && exeItems.FindIndex(i => string.Equals(
                    i.Value, settings.BlenderExe, StringComparison.OrdinalIgnoreCase)) < 0)
                exeItems.Add(new Item { Label = settings.BlenderExe, Value = settings.BlenderExe });
            _exe = Combo(exeItems.ToArray(), settings.BlenderExe ?? "");

            var browse = new Button
            {
                Text = "Browse…",
                AutoSize = true,
                UseCompatibleTextRendering = false,
            };
            browse.Click += (s, e) =>
            {
                using (var dlg = new OpenFileDialog
                {
                    Title = "Pick blender.exe",
                    Filter = "Blender (blender.exe)|blender.exe|Programs (*.exe)|*.exe",
                })
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    var item = new Item { Label = dlg.FileName, Value = dlg.FileName };
                    _exe.Items.Add(item);
                    _exe.SelectedItem = item;
                }
            };
            var exeRow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0),
            };
            var exeLabel = Prose("Blender:");
            exeLabel.Margin = new Padding(0, 6, 6, 0);
            exeRow.Controls.Add(exeLabel);
            exeRow.Controls.Add(_exe);
            exeRow.Controls.Add(browse);

            _exportFolder = Combo(new[]
            {
                new Item { Label = "App-data exports folder (keeps projects clean)",
                           Value = "temp" },
                new Item { Label = "Next to the assembly", Value = "beside" },
            }, settings.ExportFolderMode);

            appGroup.Controls.Add(_autoLaunch);
            appGroup.Controls.Add(_focus);
            appGroup.Controls.Add(exeRow);
            appGroup.Controls.Add(Row("Export files to:", _exportFolder));

            // ── Status + buttons ────────────────────────────────────────────
            var running = BlenderBridge.Discover(AddIn.Log);
            var status = Prose(running.Count == 0
                ? "No Blender with the STEPper NEXT bridge is running right now."
                : running.Count + " Blender instance(s) listening: "
                  + string.Join("; ", running.ConvertAll(r => r.Describe()).ToArray()));
            status.ForeColor = SystemColors.GrayText;
            status.Margin = new Padding(0, 8, 0, 10);

            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
            };
            var cancel = Push("Cancel", DialogResult.Cancel);
            var ok = Push("Save", DialogResult.OK);
            ok.Margin = new Padding(6, 3, 3, 3);
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);

            root.Controls.Add(import);
            root.Controls.Add(pipeline);
            root.Controls.Add(appearance);
            root.Controls.Add(appGroup);
            root.Controls.Add(status);
            root.Controls.Add(buttons);
            Controls.Add(root);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        private void ApplyTo(AppSettings settings)
        {
            settings.Hierarchy = Selected(_hierarchy, settings.Hierarchy);
            settings.QualityPreset = Selected(_quality, settings.QualityPreset);
            settings.UpAxis = Selected(_upAxis, settings.UpAxis);
            settings.BuildRig = _buildRig.Checked;
            settings.SyncPoses = _syncPoses.Checked;
            settings.ParentGeometry = _parent.Checked;
            settings.CleanupEmpties = _cleanup.Checked;
            settings.DeInstance = _deInstance.Checked;
            settings.EngineeringMaterial = _material.Checked;
            settings.IncludeHidden = _hidden.Checked;
            settings.AutoLaunchBlender = _autoLaunch.Checked;
            settings.FocusBlender = _focus.Checked;
            settings.BlenderExe = Selected(_exe, settings.BlenderExe ?? "");
            settings.ExportFolderMode = Selected(_exportFolder, settings.ExportFolderMode);
        }

        private static string Selected(ComboBox combo, string fallback)
        {
            var item = combo.SelectedItem as Item;
            return item != null ? item.Value : fallback;
        }

        private static GroupBox Group(string title)
        {
            var box = new GroupBox
            {
                Text = title,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0, 0, 0, 8),
                UseCompatibleTextRendering = false,
            };
            var stack = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
                Padding = new Padding(8, 4, 8, 4),
                WrapContents = false,
            };
            box.Controls.Add(stack);
            // Group() callers add rows to the box; forward to the stack.
            box.ControlAdded += (s, e) =>
            {
                if (e.Control != stack)
                {
                    box.Controls.Remove(e.Control);
                    stack.Controls.Add(e.Control);
                }
            };
            return box;
        }

        private static Control Row(string label, ComboBox combo)
        {
            var row = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0),
            };
            var text = Prose(label);
            text.Margin = new Padding(0, 6, 6, 0);
            row.Controls.Add(text);
            row.Controls.Add(combo);
            return row;
        }

        private static ComboBox Combo(Item[] items, string selectedValue)
        {
            var combo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 280,
                Margin = new Padding(0, 2, 0, 2),
            };
            combo.Items.AddRange(items);
            combo.SelectedIndex = 0;
            for (int i = 0; i < items.Length; i++)
                if (string.Equals(items[i].Value, selectedValue,
                        StringComparison.OrdinalIgnoreCase))
                { combo.SelectedIndex = i; break; }
            return combo;
        }

        private static Label Prose(string text)
            => new Label
            {
                Text = text,
                AutoSize = true,
                MaximumSize = new Size(TextWidth, 0),
                Margin = new Padding(0),
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

        public static void Run(ISldWorks app)
        {
            var settings = AppSettings.Load(AddIn.Log);
            var owner = ExportOptionsDialog.ActiveOwner();
            using (var dlg = new BlenderOptionsDialog(settings))
            {
                var result = owner == null ? dlg.ShowDialog() : dlg.ShowDialog(owner);
                if (result != DialogResult.OK) return;
                dlg.ApplyTo(settings);
                settings.Save(AddIn.Log);
            }
        }
    }
}
