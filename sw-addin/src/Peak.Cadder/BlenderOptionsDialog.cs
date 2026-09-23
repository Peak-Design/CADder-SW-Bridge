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
        private readonly NumericUpDown _distance;
        private readonly NumericUpDown _angle;
        private readonly CheckBox _relative;
        private readonly NumericUpDown _relativeDistance;
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
        private readonly CheckBox _trisToQuads;
        private readonly CheckBox _unwrapCompound;
        private readonly CheckBox _matchView;
        private readonly CheckBox _autoLaunch;
        private readonly CheckBox _focus;
#if DEBUG
        private readonly CheckBox _labOps;
#endif
        private readonly TextBox _exportPath;
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
                new Item { Label = "Custom", Value = "CUSTOM" },
            }, settings.QualityPreset);
            // The SolidWorks axis that points up. It becomes Blender's Z.
            _upAxis = Combo(new[]
            {
                new Item { Label = "Y (SolidWorks default)", Value = "YPOS" },
                new Item { Label = "Z (no rotation)", Value = "ZPOS" },
                new Item { Label = "X", Value = "XPOS" },
            }, settings.UpAxis);
            // The group says "Send to Blender", so no label below it has
            // to say "send" again. A heading takes the repeated word out
            // of the labels under it.
            _separateSolids = Check(
                "One object per solid body", settings.SeparateSolids,
                "Split a multibody part into one Blender object per body. "
                + "Off, the part arrives as one object");
            _trisToQuads = Check(
                "Triangles to quads", settings.TrisToQuads,
                "Pair the triangles back into quads in Blender. A flat or a "
                + "lightly curved face is cut into long thin pairs that go "
                + "back together cleanly. Nothing is joined across a "
                + "material, a UV island, a seam or a sharp edge");
            _onlySelected = Check(
                "Only the selected components", settings.OnlySelected,
                "Send the components that are selected in the assembly, and "
                + "leave the rest behind. The rig still describes the whole "
                + "assembly. Refresh Model always sends the whole assembly");
            _appearances = Check(
                "Appearances", settings.ExportAppearances,
                "Send the SolidWorks appearances: colors, finish, textures "
                + "and decals. Off, each face carries its plain color");
            _decals = Check(
                "Decals", settings.ExportDecals,
                "Send the decals laid over an appearance");
            _textureMapping = Check(
                "Texture mapping", settings.ExportTextureMapping,
                "Send how a texture is projected onto the part. Off, the "
                + "image still travels at its own tile size, boxed in the "
                + "axes of the part");
            _unwrapCompound = Check(
                "Unwrap compound surfaces", settings.UnwrapCompound,
                "Let Blender unwrap the faces that no one scale can flatten: "
                + "a sphere, a torus, a blend corner or a spline surface. A "
                + "plane, a cylinder and a cone keep the exact coordinates of "
                + "their own surface either way");
            _matchView = Check(
                "Match the Blender view", settings.MatchView,
                "Turn the Blender viewport to the angle this SolidWorks view "
                + "is at once the parts arrive, and frame the model. Off, "
                + "Blender keeps the view it has");
            _buildRig = Check(
                "Build the rig", settings.BuildRig,
                "Read the mates of an assembly and build an armature in "
                + "Blender that moves the way the mates allow. Parts arrive "
                + "parented to it");
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

            import.Controls.Add(Row("Hierarchy:", _hierarchy,
                "Choose how the parts are arranged in the Blender outliner"));
            // The same five settings, with the same numbers, as the STEP
            // import and Mesh Quality in Blender.
            _distance = Number(settings.QualityDistance * 1000.0, 0.002m, 100m, 3, 0.1m);
            _angle = Number(settings.QualityAngle * 180.0 / Math.PI, 0.1m, 85m, 1, 1m);
            _relative = Check(
                "Relative tessellation", settings.QualityRelative,
                "Cut each part to a share of its own size instead of a "
                + "distance. Small parts keep their detail and large parts do "
                + "not explode the triangle count");
            _relativeDistance = Number(settings.QualityRelativeDistance, 0.00001m, 0.5m, 4, 0.001m);
            import.Controls.Add(Row("Mesh quality:", _quality,
                "Set how finely the parts are cut into triangles. A name cuts "
                + "the same way as that name in Blender, for a STEP import and "
                + "for Rebuild from CAD"));
            import.Controls.Add(Row("Distance (mm):", _distance,
                "Set the largest distance between the mesh and the true "
                + "surface, for Custom. A smaller distance gives more triangles"));
            import.Controls.Add(Row("Angle (degrees):", _angle,
                "Set the largest angle one facet may turn through, for Custom "
                + "and for relative tessellation. A smaller angle gives more "
                + "triangles"));
            import.Controls.Add(_relative);
            import.Controls.Add(Row("Relative distance:", _relativeDistance,
                "Set the largest distance between the mesh and the true "
                + "surface as a share of the size of each body, for relative "
                + "tessellation"));
            // A setting that does not apply stays in place, greyed out, so
            // the dialog does not move.
            EventHandler fineness = (s2, e2) =>
            {
                bool relative = _relative.Checked;
                bool custom = Selected(_quality, "") == "CUSTOM";
                _quality.Enabled = !relative;
                _distance.Enabled = custom && !relative;
                _angle.Enabled = custom || relative;
                _relativeDistance.Enabled = relative;
            };
            _quality.SelectedIndexChanged += fineness;
            _relative.CheckedChanged += fineness;
            fineness(null, EventArgs.Empty);
            import.Controls.Add(Row("Up axis:", _upAxis,
                "Name the SolidWorks axis that points up. It becomes the Z "
                + "axis of Blender"));
            import.Controls.Add(_trisToQuads);
            import.Controls.Add(_separateSolids);
            import.Controls.Add(_onlySelected);
            import.Controls.Add(_appearances);
            import.Controls.Add(_decals);
            import.Controls.Add(_textureMapping);
            import.Controls.Add(_unwrapCompound);
            import.Controls.Add(_matchView);
            _relationStep = Combo(new[]
            {
                new Item { Label = "2 degrees (fine, slower export)", Value = "2" },
                new Item { Label = "5 degrees", Value = "5" },
                new Item { Label = "10 degrees (coarse, faster export)", Value = "10" },
            }, settings.RelationStepDeg.ToString(CultureInfo.InvariantCulture));
            import.Controls.Add(_buildRig);
            import.Controls.Add(Row("Cam and universal joint sampling:", _relationStep,
                "Set the step the exporter drags a cam or a universal joint "
                + "through while it measures the relation. A finer step "
                + "measures better and exports slower"));

            // ── 2. STEP+, behind the advanced commands ──────────────────────
            // Everything here belongs to the STEP file: a direct send has
            // no STEP, no occurrences to de-instance and no free edges.
            var step = Group("Export STEP+ (advanced)");
            _deInstance = Check(
                "De-instance overridden components", settings.DeInstance,
                "Give a component its own STEP geometry when its appearance "
                + "differs from the other instances of the same part");
            _material = Check(
                "Engineering material", settings.EngineeringMaterial,
                "Write the material of each part into the STEP file, where "
                + "Blender reads it as a custom property");
            _hidden = Check(
                "Hidden components", settings.IncludeHidden,
                "Export the components that are hidden in the assembly");
            _importCurves = Check(
                "Import curves", settings.ImportCurves,
                "Bring the free edges of the STEP file into Blender as "
                + "curve objects, in a collection named Cad Curves");
            step.Controls.Add(_deInstance);
            step.Controls.Add(_material);
            step.Controls.Add(_hidden);
            step.Controls.Add(_importCurves);
            step.Visible = settings.AdvancedCommands;

            // ── Application ─────────────────────────────────────────────────
            var appGroup = Group("Blender application");
            _autoLaunch = Check(
                "Launch Blender if needed", settings.AutoLaunchBlender,
                "Start Blender and wait for it when no Blender with the "
                + "CADder bridge is already listening");
            _focus = Check(
                "Bring Blender to the front", settings.FocusBlender,
                "Raise the Blender window once the send has finished");

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
            _tips.SetToolTip(_exe,
                "Choose which Blender a send starts and talks to. The "
                + "newest installed one is picked when nothing is chosen");
            _tips.SetToolTip(browse, "Pick a blender.exe that is not listed");
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
                new Item { Label = "A folder of your own", Value = "custom" },
            }, settings.ExportFolderMode);

            _exportPath = new TextBox
            {
                Text = settings.ExportFolder ?? "",
                Width = 260,
                ReadOnly = true,
            };
            var pickFolder = new Button
            {
                Text = "Browse…",
                AutoSize = true,
                UseCompatibleTextRendering = false,
            };
            pickFolder.Click += (s, e) => PickExportFolder();
            _tips.SetToolTip(_exportPath,
                "Show the folder an export writes into. Each export makes a "
                + "folder of its own in it, named after the document");
            _tips.SetToolTip(pickFolder, "Pick the folder to export into");
            var folderRow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0),
            };
            var folderLabel = Prose("Folder:");
            folderLabel.Margin = new Padding(0, 6, 6, 0);
            folderRow.Controls.Add(folderLabel);
            folderRow.Controls.Add(_exportPath);
            folderRow.Controls.Add(pickFolder);
            // The row stays in place and greys out, rather than appearing
            // and disappearing under the controls below it.
            _exportFolder.SelectedIndexChanged += (s, e) =>
            {
                folderRow.Enabled = Selected(_exportFolder, "temp") == "custom";
                if (folderRow.Enabled && _exportPath.Text.Length == 0)
                    PickExportFolder();
            };
            folderRow.Enabled = settings.ExportFolderMode == "custom";

            appGroup.Controls.Add(_autoLaunch);
            appGroup.Controls.Add(_focus);
            appGroup.Controls.Add(exeRow);
            appGroup.Controls.Add(Row("Export files to:", _exportFolder,
                "Choose where the STEP file and the manifest are written. "
                + "Each export makes a folder of its own, named after the "
                + "document. A direct send writes neither file and this has "
                + "no effect on it"));
            appGroup.Controls.Add(folderRow);

            // ── Lab ─────────────────────────────────────────────────────────
            var ribbonGroup = Group("Ribbon");
            _advanced = Check(
                "Advanced commands", settings.AdvancedCommands,
                "Put Export STEP+ and Export Rig on the ribbon, and show "
                + "the STEP+ options above. Takes effect when SolidWorks "
                + "starts again");
            ribbonGroup.Controls.Add(_advanced);
            _advanced.CheckedChanged += (s, e) => step.Visible = _advanced.Checked;

            // The harness drives SolidWorks from a localhost port. It
            // belongs to development, so a shipped build has no switch for
            // it and the listener refuses the operations outright.
#if DEBUG
            var labGroup = Group("Test harness");
            _labOps = Check(
                "Local test harness", settings.LabOps,
                "Let a test harness on this machine open, close and change "
                + "documents over the link. The add-in never saves a "
                + "document, whatever the harness asks for");
            labGroup.Controls.Add(_labOps);
#endif

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
#if DEBUG
            root.Controls.Add(labGroup);
#endif
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
            settings.QualityDistance = (double)_distance.Value / 1000.0;
            settings.QualityAngle = (double)_angle.Value * Math.PI / 180.0;
            settings.QualityRelative = _relative.Checked;
            settings.QualityRelativeDistance = (double)_relativeDistance.Value;
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
            settings.TrisToQuads = _trisToQuads.Checked;
            settings.UnwrapCompound = _unwrapCompound.Checked;
            settings.MatchView = _matchView.Checked;
            settings.AutoLaunchBlender = _autoLaunch.Checked;
            settings.FocusBlender = _focus.Checked;
#if DEBUG
            settings.LabOps = _labOps.Checked;
#endif
            settings.AdvancedCommands = _advanced.Checked;
            settings.BlenderExe = Selected(_exe, settings.BlenderExe ?? "");
            settings.ExportFolderMode = Selected(_exportFolder, settings.ExportFolderMode);
            settings.ExportFolder = _exportPath.Text.Trim();
            // A folder of your own with no folder behind it writes where the
            // app-data mode writes, so the setting says what happens.
            if (settings.ExportFolderMode == "custom" && settings.ExportFolder.Length == 0)
                settings.ExportFolderMode = "temp";
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

        private void PickExportFolder()
        {
            using (var dlg = new FolderBrowserDialog
            {
                Description = "Where an export writes its files",
                ShowNewFolderButton = true,
            })
            {
                if (_exportPath.Text.Length > 0)
                    dlg.SelectedPath = _exportPath.Text;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    _exportPath.Text = dlg.SelectedPath;
            }
        }

        private Control Row(string label, Control field, string tip)
        {
            var row = Row(label, field);
            if (!string.IsNullOrEmpty(tip))
            {
                _tips.SetToolTip(field, tip);
                foreach (Control child in row.Controls) _tips.SetToolTip(child, tip);
            }
            return row;
        }

        private static NumericUpDown Number(
            double value, decimal min, decimal max, int decimals, decimal step)
        {
            decimal v;
            try { v = (decimal)value; }
            catch (OverflowException) { v = min; }
            return new NumericUpDown
            {
                Minimum = min,
                Maximum = max,
                DecimalPlaces = decimals,
                Increment = step,
                Value = Math.Max(min, Math.Min(max, v)),
                Width = 120,
                Margin = new Padding(0, 2, 0, 2),
            };
        }

        private static Control Row(string label, Control combo)
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

        /// <summary>
        /// Every control in this dialog carries one. A label says what a
        /// setting is, and the tooltip says what it does, which keeps the
        /// sentences out of the labels. The text starts with a verb and
        /// ends without a period, the way Blender writes them.
        /// </summary>
        private readonly ToolTip _tips = new ToolTip
        {
            AutoPopDelay = 20000,
            InitialDelay = 400,
            ReshowDelay = 100,
        };

        private CheckBox Check(string text, bool chequed, string tip = null)
        {
            var box = new CheckBox
            {
                Text = text,
                Checked = chequed,
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 2),
                UseCompatibleTextRendering = false,
            };
            if (!string.IsNullOrEmpty(tip)) _tips.SetToolTip(box, tip);
            return box;
        }

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
