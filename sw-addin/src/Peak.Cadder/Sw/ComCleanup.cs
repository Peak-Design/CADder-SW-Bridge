using System;

namespace Peak.Cadder.Sw
{
    /// <summary>
    /// Releases the COM wrappers an export leaves behind, on the thread that
    /// made them, while SolidWorks waits for the command to finish.
    ///
    /// An export takes a wrapper for every component, mate, face and body
    /// it reads. Left alone, the garbage collector releases them later from
    /// its finalizer thread, and each release is sent to SolidWorks' main
    /// thread, which can run it while SolidWorks is inside something else,
    /// such as closing a document. SolidWorks 2022 died of heap corruption
    /// in a close right after an export (lab corpus sweeps, 2026-09-22).
    /// Collected here, the releases run while nothing else is under way.
    /// </summary>
    internal static class ComCleanup
    {
        public static void Now()
        {
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            catch { }
        }
    }
}
