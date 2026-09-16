using System;
using System.Globalization;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Peak.Cadder.Bridge;
using Peak.Cadder.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Peak.Cadder
{
    /// <summary>
    /// The persistent bridge options: everything Send to Blender uses so
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
        private readonly CheckBox _appearances;
        private readonly CheckBox _decals;
        private readonly CheckBox _textureMapping;
        private readonly CheckBox _deInstance;
        private readonly CheckBox _material;
        private readonly CheckBox _hidden;
        private readonly CheckBox _onlySelected;
        private readonly CheckBox _importCurves;
        private readonly CheckBox _separateSolids;
        private readonly CheckBox _autoLaunch;
        private readonly CheckBox _focus;
        private readonly CheckBox _labOps;
        private readonly CheckBox _advanced;
        private readonly ComboBox _relationStep;
        private readonly ComboBox _exe;
        private readonly ComboBox _exportFolder;

        private const int TextWidth = 460;

        private BlenderOptionsDialog(AppSettings settings)
        {
            Text = "Export Options";
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

            // ── 1. The send itself ──────────────────────────────────────────
            // The direct send is the main route, so its options come
            // first. The STEP group below only appears with the advanced
            // commands, which is where the STEP commands live too.
            var import = Group("Send to Blender");
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
            // The SolidWorks axis that points up. It becomes Blender's Z.
            _upAxis = Combo(new[]
            {
                new Item { Label = "Y (SolidWorks default)", Value = "YPOS" },
                new Item { Label = "Z (no rotation)", Value = "ZPOS" },
                new Item { Label = "X", Value = "XPOS" },
            }, settings.UpAxis);
            _separateSolids = Check(
                "One object per body of a multibody part", settings.SeparateSolids);
            _onlySelected = Check("Send only the selected components",
                settings.OnlySelected);
            _appearances = Check("Send the SolidWorks appearances",
                settings.ExportAppearances);
            _decals = Check("Send the decals", settings.ExportDecals);
            _textureMapping = Check("Send the texture mapping",
                settings.ExportTextureMapping);
            _buildRig = Check("Build the rig (assemblies)", settings.BuildRig);
            // A decal and a mapping are parts of an appearance, so they
            // mean nothing on their own.
            EventHandler follow = (s, e) =>
            {
                _decals.Enabled = _appearances.Checked;
                _textureMapping.Enabled = _appearances.Checked;
            };
            _appearances.CheckedChanged += follow;
            follow(null, EventArgs.Empty);
            _decals.Margin = new Padding(16, 0, 0, 0);
            _textureMapping.Margin = new Padding(16, 0, 0, 0);

            import.Controls.Add(Row("Hierarchy:", _hierarchy));
            import.Controls.Add(Row("Mesh quality:", _quality));
            import.Controls.Add(Row("SolidWorks up axis (becomes Blender Z):", _upAxis));
            import.Controls.Add(_separateSolids);
            import.Controls.Add(_onlySelected);
            import.Controls.Add(_appearances);
            import.Controls.Add(_decals);
            import.Controls.Add(_textureMapping);
            _relationStep = Combo(new[]
            {
                new Item { Label = "2 degrees (fine, slower export)", Value = "2" },
                new Item { Label = "5 degrees", Value = "5" },
                new Item { Label = "10 degrees (coarse, faster export)", Value = "10" },
            }, settings.RelationStepDeg.ToString(CultureInfo.InvariantCulture));
            import.Controls.Add(_buildRig);
            import.Controls.Add(Row("Cam and universal joint sampling:", _relationStep));

            // ── 2. STEP+, behind the advanced commands ──────────────────────
            // Everything here belongs to the STEP file: a direct send has
            // no STEP, no occurrences to de-instance and no free edges.
            var step = Group("Export STEP+ (advanced)");
            _deInstance = Check("De-instance components that carry an override",
                settings.DeInstance);
            _material = Check("Include engineering material", settings.EngineeringMaterial);
            _hidden = Check("Include hidden components", settings.IncludeHidden);
            _importCurves = Check(
                "Import curves (free edges, into a \"Cad Curves\" collection)",
                settings.ImportCurves);
            step.Controls.Add(_deInstance);
            step.Controls.Add(_material);
            step.Controls.Add(_hidden);
            step.Controls.Add(_importCurves);
            step.Visible = settings.AdvancedCommands;

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

            // ── Lab ─────────────────────────────────────────────────────────
            var ribbonGroup = Group("Ribbon");
            _advanced = Check(
                "Show the advanced commands (Export STEP+, Export Rig). "
                + "Takes effect when SolidWorks starts again",
                settings.AdvancedCommands);
            ribbonGroup.Controls.Add(_advanced);
            _advanced.CheckedChanged += (s, e) => step.Visible = _advanced.Checked;

            var labGroup = Group("Test harness");
            _labOps = Check(
                "Let a local test harness open, close and change documents "
                + "(the add-in never saves)",
                settings.LabOps);
            labGroup.Controls.Add(_labOps);

            // ── Status + buttons ────────────────────────────────────────────
            var running = BlenderBridge.Discover(AddIn.Log);
            var status = Prose(running.Count == 0
                ? "No Blender with the CADder bridge is running right now."
                : running.Count + " Blender instance(s) listening: "
                  + string.Join("; ", running.ConvertAll(r => r.Describe()).ToArray()));
            status.ForeColor = SystemColors.GrayText;
            status.Margin = new Padding(0, 8, 0, 0);
            var logNote = Prose("Log: " + AddIn.LogPath);
            logNote.ForeColor = SystemColors.GrayText;
            logNote.Margin = new Padding(0, 2, 0, 10);

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
            root.Controls.Add(step);
            root.Controls.Add(appGroup);
            root.Controls.Add(ribbonGroup);
            root.Controls.Add(labGroup);
            root.Controls.Add(status);
            root.Controls.Add(logNote);
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
            settings.ExportAppearances = _appearances.Checked;
            settings.ExportDecals = _decals.Checked;
            settings.ExportTextureMapping = _textureMapping.Checked;
            int stepDeg;
            if (int.TryParse(Selected(_relationStep, "5"), NumberStyles.Integer,
                             CultureInfo.InvariantCulture, out stepDeg))
                settings.RelationStepDeg = stepDeg;
            settings.DeInstance = _deInstance.Checked;
            settings.EngineeringMaterial = _material.Checked;
            settings.IncludeHidden = _hidden.Checked;
            settings.OnlySelected = _onlySelected.Checked;
            settings.ImportCurves = _importCurves.Checked;
            settings.SeparateSolids = _separateSolids.Checked;
            settings.AutoLaunchBlender = _autoLaunch.Checked;
            settings.FocusBlender = _focus.Checked;
            settings.LabOps = _labOps.Checked;
            settings.AdvancedCommands = _advanced.Checked;
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
