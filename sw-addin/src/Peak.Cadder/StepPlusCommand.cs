using System;
using System.IO;
using System.Windows.Forms;
using Peak.Cadder.Core;
using Peak.Cadder.Sw;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Peak.Cadder
{
    /// <summary>
    /// NEXT-STEP's "Export STEP+" command, merged in: STEP export with the
    /// appearance hierarchy repaired, no rig manifest. Works for parts and
    /// assemblies. Assemblies also get the flexible-twin de-instancing:
    /// the walk is cheap and the WYSIWYG poses matter to every consumer, not
    /// only the rig pipeline. Options come from the persistent settings; the
    /// per-export dialog only confirms the three appearance choices.
    /// </summary>
    public static class StepPlusCommand
    {
        public static void Run(ISldWorks app)
        {
            if (app == null) return;
            var model = app.ActiveDoc as IModelDoc2;
            if (model == null)
            {
                app.SendMsgToUser2("Open a part or assembly first.",
                    (int)swMessageBoxIcon_e.swMbInformation,
                    (int)swMessageBoxBtn_e.swMbOk);
                return;
            }
            if (string.IsNullOrEmpty(model.GetPathName()))
            {
                app.SendMsgToUser2("Save the document before exporting.",
                    (int)swMessageBoxIcon_e.swMbWarning,
                    (int)swMessageBoxBtn_e.swMbOk);
                return;
            }

            var settings = AppSettings.Load(AddIn.Log);
            if (!StepPlusOptionsDialog.Show(settings)) return;
            settings.Save(AddIn.Log);

            string suggested = Path.ChangeExtension(model.GetPathName(), ".step");
            string target;
            using (var dlg = new SaveFileDialog
            {
                Title = "Export STEP with appearances",
                Filter = "STEP files (*.step;*.stp)|*.step;*.stp",
                FileName = Path.GetFileName(suggested),
                InitialDirectory = Path.GetDirectoryName(suggested),
                OverwritePrompt = true,
            })
            {
                if (dlg.ShowDialog() != DialogResult.OK) return;
                target = dlg.FileName;
            }

            try
            {
                string report = ExportAppearanceOnly(app, model, target, settings);
                app.SendMsgToUser2(report,
                    (int)swMessageBoxIcon_e.swMbInformation,
                    (int)swMessageBoxBtn_e.swMbOk);
            }
            catch (Exception ex)
            {
                AddIn.Log("STEP+ export failed: " + ex);
                app.SendMsgToUser2("Export failed: " + ex.Message,
                    (int)swMessageBoxIcon_e.swMbStop,
                    (int)swMessageBoxBtn_e.swMbOk);
            }
        }

        /// <summary>The STEP+ export body, shared with Send to Blender's
        /// part-document path. Always AP214: the appearance forms need it.</summary>
        public static string ExportAppearanceOnly(
            ISldWorks app, IModelDoc2 model, string target, AppSettings settings)
        {
            StepExporter.Export(app, model, target, 214, AddIn.Log,
                exportAppearances: true,
                includeHidden: settings.IncludeHidden);

            var assembly = model as IAssemblyDoc;
            var flexRequests = assembly == null
                ? null
                : FlexibleLayoutBuilder.Build(
                    AssemblyWalker.Walk(assembly, AddIn.Log), AddIn.Log);

            var post = Appearance.AppearancePipeline.Run(model, target,
                repairAppearances: true,
                deInstance: settings.DeInstance,
                includeMaterial: settings.EngineeringMaterial,
                includeHidden: settings.IncludeHidden,
                flexRequests: flexRequests,
                log: AddIn.Log);

            string msg = "Exported " + Path.GetFileName(target) + ".\n\n"
                + string.Join("\n", post.Notes.ToArray());
            if (post.ColoursUnmatched > 0)
                msg += "\n\nWARNING: " + post.ColoursUnmatched + " occurrence(s) "
                    + "could not be matched, and keep the SolidWorks colour. "
                    + "See the log: " + AddIn.LogPath;
            return msg;
        }
    }

    /// <summary>The three appearance choices, pre-filled from settings.</summary>
    public sealed class StepPlusOptionsDialog : Form
    {
        private readonly CheckBox _deInstance;
        private readonly CheckBox _material;
        private readonly CheckBox _hidden;

        private const int TextWidth = 430;

        private StepPlusOptionsDialog(AppSettings settings)
        {
            Text = "Export STEP+";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Font;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Font = System.Drawing.SystemFonts.MessageBoxFont;

            var root = new TableLayoutPanel
            {
                ColumnCount = 1,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
                Padding = new Padding(12),
            };

            var intro = Prose(
                "STEP export that keeps the SolidWorks appearance hierarchy: "
                + "occurrence colour overrides survive, and shared flexible "
                + "subassemblies are de-instanced to their true poses.");
            intro.Margin = new Padding(0, 0, 0, 10);

            _deInstance = Check("De-instance components that carry an override",
                settings.DeInstance);
            var deHelp = Help(
                "Occurrences whose colour differs get their own copy of the "
                + "geometry with a plain style every consumer reads. Off keeps "
                + "true instancing via occurrence styling, which Fusion 360 "
                + "and CADder ignore.");
            _material = Check("Include engineering material (name and density)",
                settings.EngineeringMaterial);
            var matHelp = Help(
                "Written as STEP property entities; with de-instancing on, "
                + "each appearance group gets a numbered material name.");
            _hidden = Check("Include hidden components", settings.IncludeHidden);
            var hidHelp = Help(
                "Temporarily shows hidden components for the export and hides "
                + "them again. Suppressed components are never exported.");
            hidHelp.Margin = new Padding(20, 0, 0, 12);

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
            root.Controls.Add(_deInstance);
            root.Controls.Add(deHelp);
            root.Controls.Add(_material);
            root.Controls.Add(matHelp);
            root.Controls.Add(_hidden);
            root.Controls.Add(hidHelp);
            root.Controls.Add(buttons);
            Controls.Add(root);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        private static Label Prose(string text)
            => new Label
            {
                Text = text,
                AutoSize = true,
                MaximumSize = new System.Drawing.Size(TextWidth, 0),
                Margin = new Padding(0),
                UseCompatibleTextRendering = false,
            };

        private static Label Help(string text)
        {
            var label = Prose(text);
            label.ForeColor = System.Drawing.SystemColors.GrayText;
            label.Margin = new Padding(20, 0, 0, 8);
            return label;
        }

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
                MinimumSize = new System.Drawing.Size(84, 26),
                UseCompatibleTextRendering = false,
            };

        public static bool Show(AppSettings settings)
        {
            var owner = ExportOptionsDialog.ActiveOwner();
            using (var dlg = new StepPlusOptionsDialog(settings))
            {
                var result = owner == null ? dlg.ShowDialog() : dlg.ShowDialog(owner);
                if (result != DialogResult.OK) return false;
                settings.DeInstance = dlg._deInstance.Checked;
                settings.EngineeringMaterial = dlg._material.Checked;
                settings.IncludeHidden = dlg._hidden.Checked;
                return true;
            }
        }
    }
}
