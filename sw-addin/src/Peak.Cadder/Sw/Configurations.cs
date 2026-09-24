using System;
using System.Collections.Generic;
using Peak.Cadder.Core;
using SolidWorks.Interop.sldworks;

namespace Peak.Cadder.Sw
{
    /// <summary>
    /// The configurations of a part or an assembly: their names, and the
    /// one that SolidWorks shows now.
    ///
    /// The export reads the document as SolidWorks shows it. To export a
    /// configuration that is not shown, the add-in shows it first, the way
    /// a double click in the ConfigurationManager does
    /// (ConfigurationSwitch).
    /// </summary>
    public static class Configurations
    {
        /// <summary>The names of the configurations, in the order that
        /// SolidWorks gives them. Empty when SolidWorks does not give
        /// them.</summary>
        public static List<string> Names(IModelDoc2 model)
        {
            var names = new List<string>();
            if (model == null) return names;
            object raw = null;
            try { raw = model.GetConfigurationNames(); }
            catch (Exception ex) { AddIn.Log("configurations: names unavailable: " + ex.Message); }
            // The names come back as string[] or as object[], by how the
            // call is bound, so both are read the same way.
            var array = raw as Array;
            if (array == null) return names;
            foreach (var o in array)
            {
                var name = o as string;
                if (!string.IsNullOrEmpty(name) && !names.Contains(name)) names.Add(name);
            }
            return names;
        }

        /// <summary>The name of the configuration that SolidWorks shows
        /// now, or null when it cannot be read.</summary>
        public static string Active(IModelDoc2 model)
        {
            if (model == null) return null;
            try
            {
                var active = model.ConfigurationManager.ActiveConfiguration;
                return active == null ? null : active.Name;
            }
            catch (Exception) { return null; }
        }
    }

    /// <summary>
    /// Shows one configuration after another for an export, and shows the
    /// configuration that was active before again when it is disposed.
    ///
    /// The user did not ask for a different configuration in SolidWorks.
    /// A send of three configurations therefore ends with the document
    /// as it was, also when an export fails or the user stops it. Use it
    /// in a using block. A configuration that is already shown is not
    /// shown again, so a send of the active configuration changes
    /// nothing in the document.
    ///
    /// All calls go to SolidWorks, so all of them run on the command
    /// thread.
    /// </summary>
    public sealed class ConfigurationSwitch : IDisposable
    {
        private readonly IModelDoc2 _model;
        private readonly Action<string> _log;
        private bool _disposed;

        /// <summary>The configuration that was active when the switch was
        /// made. Dispose shows it again.</summary>
        public string Original { get; private set; }

        public ConfigurationSwitch(IModelDoc2 model, Action<string> log)
        {
            _model = model;
            _log = log;
            Original = Configurations.Active(model);
        }

        /// <summary>A switch that shows <paramref name="configuration"/>
        /// now. An empty name shows nothing and keeps the active
        /// configuration. Throws, and shows nothing, when the document has
        /// no configuration of that name.</summary>
        public static ConfigurationSwitch Showing(
            IModelDoc2 model, string configuration, Action<string> log)
        {
            var scope = new ConfigurationSwitch(model, log);
            try { scope.Show(configuration); }
            catch
            {
                scope.Dispose();
                throw;
            }
            return scope;
        }

        /// <summary>
        /// Shows the configuration, when it is not shown now. Returns its
        /// name as the document spells it. An empty name keeps the active
        /// configuration and returns its name. Throws when the document
        /// has no configuration of that name, or when SolidWorks does not
        /// show it.
        /// </summary>
        public string Show(string configuration)
        {
            string now = Configurations.Active(_model);
            if (string.IsNullOrEmpty(configuration)) return now;
            string name = ConfigurationNames.Find(Configurations.Names(_model), configuration);
            if (name == null)
                throw new InvalidOperationException(
                    "The document " + PathOf(_model) + " has no configuration named "
                    + configuration + ".");
            if (string.Equals(name, now, StringComparison.Ordinal)) return name;

            bool clean = !Changed(_model);
            Log("configurations: showing " + name + " (was " + (now ?? "unknown") + ")");
            bool shown;
            try { shown = _model.ShowConfiguration2(name); }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "SolidWorks could not show the configuration " + name + ". " + ex.Message, ex);
            }
            if (!shown)
                throw new InvalidOperationException(
                    "SolidWorks could not show the configuration " + name + ".");
            // The log records when a switch marks the document as changed.
            // That explains a save prompt after a send, which the user did
            // not expect from a send.
            if (clean && Changed(_model))
                Log("configurations: SolidWorks marks the document as changed after it showed "
                    + name);
            return name;
        }

        /// <summary>Shows the original configuration again, when another
        /// one is shown now. Never throws: a failure goes to the log, and
        /// the user can show the configuration by hand.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (string.IsNullOrEmpty(Original)) return;
            string now = Configurations.Active(_model);
            if (string.Equals(now, Original, StringComparison.Ordinal)) return;
            Log("configurations: showing " + Original + " again (was " + (now ?? "unknown") + ")");
            try
            {
                if (!_model.ShowConfiguration2(Original))
                    Log("configurations: SolidWorks did not show " + Original
                        + " again. Show it in the ConfigurationManager.");
            }
            catch (Exception ex)
            {
                Log("configurations: showing " + Original + " again failed: " + ex.Message);
            }
        }

        private void Log(string message)
        {
            if (_log != null) _log(message);
        }

        private static bool Changed(IModelDoc2 model)
        {
            try { return model.GetSaveFlag(); }
            catch (Exception) { return false; }
        }

        private static string PathOf(IModelDoc2 model)
        {
            try { return model.GetPathName(); }
            catch (Exception) { return "(unknown)"; }
        }
    }
}
