using System;
using System.IO;

namespace Peak.SwToBlender.Sw
{
    /// <summary>
    /// Where the STEP file is BUILT, as opposed to where it ends up.
    ///
    /// The export makes five passes over that file: SolidWorks writes it, the
    /// appearance pipeline parses it, the pipeline writes it back, the SHA1
    /// reads it, and the occurrence matcher parses it again. On a production
    /// assembly that is well over half a gigabyte of traffic, and when the
    /// model lives on a server reached through a VPN, every byte of it
    /// crosses the wire (live ClampRig, 2026-08-24: a 130 MB STEP on a
    /// mapped network drive).
    ///
    /// So the file is built on a local disk and copied across ONCE, at the
    /// end, as a single sequential write. A target already on a local drive
    /// is left alone: staging it would only add a copy.
    /// </summary>
    internal sealed class StepStaging : IDisposable
    {
        private readonly string _finalPath;
        private readonly bool _staged;
        private bool _published;
        private readonly Action<string> _log;

        /// <summary>The path every pass of the export should use.</summary>
        public string WorkingPath { get; private set; }

        /// <summary>True when work is happening on a local scratch copy.</summary>
        public bool IsStaged { get { return _staged; } }

        private StepStaging(string finalPath, string workingPath, bool staged,
                            Action<string> log)
        {
            _finalPath = finalPath;
            WorkingPath = workingPath;
            _staged = staged;
            _log = log;
        }

        /// <summary>
        /// Stages a file the export is about to WRITE. Nothing is copied up
        /// front: the working file does not exist yet.
        /// </summary>
        public static StepStaging ForWrite(string finalPath, Action<string> log)
        {
            string scratch = Scratch(finalPath, log);
            if (scratch == null) return new StepStaging(finalPath, finalPath, false, log);
            if (log != null)
                log("STEP staging: building on " + scratch
                    + " because the target is on a network path");
            return new StepStaging(finalPath, scratch, true, log);
        }

        /// <summary>
        /// Stages a file the export only READS (the manifest-only path, which
        /// hashes and parses an existing STEP). One copy down beats two reads
        /// across the wire; on failure the remote file is used directly.
        /// </summary>
        public static StepStaging ForRead(string finalPath, Action<string> log)
        {
            if (!File.Exists(finalPath))
                return new StepStaging(finalPath, finalPath, false, log);
            string scratch = Scratch(finalPath, log);
            if (scratch == null) return new StepStaging(finalPath, finalPath, false, log);
            try
            {
                File.Copy(finalPath, scratch, true);
                if (log != null)
                    log("STEP staging: read a local copy at " + scratch);
                return new StepStaging(finalPath, scratch, true, log);
            }
            catch (Exception ex)
            {
                if (log != null) log("STEP staging: local copy failed (" + ex.Message
                    + "); reading across the network instead");
                return new StepStaging(finalPath, finalPath, false, log);
            }
        }

        /// <summary>
        /// Copies the finished file to where the caller asked for it. A no-op
        /// when nothing was staged. Throws if the copy fails: a missing STEP
        /// beside the manifest is not something to discover later.
        /// </summary>
        public void Publish()
        {
            if (!_staged || _published) return;
            if (!File.Exists(WorkingPath)) return;
            var started = DateTime.UtcNow;
            File.Copy(WorkingPath, _finalPath, true);
            _published = true;
            if (_log != null)
            {
                double mb = new FileInfo(_finalPath).Length / (1024.0 * 1024.0);
                _log("STEP staging: copied " + mb.ToString("F1") + " MB to "
                    + _finalPath + " in "
                    + (DateTime.UtcNow - started).TotalSeconds.ToString("F1") + " s");
            }
        }

        public void Dispose()
        {
            if (!_staged) return;
            try { if (File.Exists(WorkingPath)) File.Delete(WorkingPath); }
            catch (Exception ex)
            {
                if (_log != null)
                    log_delete_failed(ex);
            }
        }

        private void log_delete_failed(Exception ex)
        {
            _log("STEP staging: could not remove the scratch file "
                 + WorkingPath + " (" + ex.Message + ")");
        }

        /// <summary>A local scratch path for this target, or null when the
        /// target is already local (or the scratch directory is unusable, in
        /// which case the export simply works the way it always did).</summary>
        private static string Scratch(string finalPath, Action<string> log)
        {
            if (!IsRemote(finalPath)) return null;
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "SwToBlender");
                Directory.CreateDirectory(dir);
                // The name matters: SolidWorks derives the STEP's own product
                // names from the file it is asked to write, so the scratch
                // file keeps the target's base name and only its folder moves.
                return Path.Combine(
                    dir,
                    Path.GetFileNameWithoutExtension(finalPath)
                        + "." + Guid.NewGuid().ToString("N").Substring(0, 8)
                        + Path.GetExtension(finalPath));
            }
            catch (Exception ex)
            {
                if (log != null)
                    log("STEP staging: no scratch directory (" + ex.Message + ")");
                return null;
            }
        }

        /// <summary>UNC paths and mapped network drives. Anything this cannot
        /// decide counts as local: staging is an optimisation, and guessing
        /// wrong toward "local" only costs what the export cost before.</summary>
        internal static bool IsRemote(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                string full = Path.GetFullPath(path);
                if (full.StartsWith(@"\\", StringComparison.Ordinal)) return true;
                string root = Path.GetPathRoot(full);
                if (string.IsNullOrEmpty(root)) return false;
                return new DriveInfo(root).DriveType == DriveType.Network;
            }
            catch
            {
                return false;
            }
        }
    }
}
