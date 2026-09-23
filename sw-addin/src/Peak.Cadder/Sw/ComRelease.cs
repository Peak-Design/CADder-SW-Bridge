using System;

namespace Peak.Cadder.Sw
{
    /// <summary>
    /// Frees the SolidWorks objects a command has finished with, while every
    /// document the command read is still open.
    ///
    /// .NET frees a COM object when its garbage collector finds it unused,
    /// and that can be minutes after the command. The appearances of a direct
    /// send are render materials that belong to a document. When that
    /// document closes first, or a part opens in its own window and closes
    /// again, the late free writes into memory that SolidWorks has already
    /// freed, and the next large operation fails. Live CutterRig
    /// (2026-09-23): an access violation in FixComponent when a part was
    /// opened and closed between two sends. Corpus two-stroke engine: a heap
    /// check in the tessellator when the assembly was closed and opened
    /// again between two sends. Exports without the mesh read no appearances
    /// and never failed. With this call at the end of each command, the
    /// engine sequence ran eight times and the CutterRig sequence four
    /// times without a failure.
    ///
    /// So every command frees what it read before it returns.
    /// </summary>
    internal static class ComRelease
    {
        public static void Flush()
        {
            // Two rounds: an object freed in the first round can hold the
            // last reference to another one.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }
}
