using System;
using SolidWorks.Interop.sldworks;

namespace Peak.SwToBlender.Sw
{
    /// <summary>
    /// Persistent component identity, transcribed from SW2URDF's
    /// CommonSwOperations.SaveSWComponent (vendor/sw2urdf/
    /// CommonSwOperations.cs): IModelDocExtension.GetPersistReference3 on the
    /// component gives the byte array SolidWorks will honour across sessions,
    /// rebuilds and releases (API help, IModelDocExtension~
    /// GetPersistReference3.html — the bytes may change, the resolution
    /// stays stable). The manifest stores it base64-encoded; SW2URDF's
    /// ASCII-string round trip corrupts bytes above 0x7F, which is why this
    /// project does not copy that part.
    /// </summary>
    public static class ComponentIdentity
    {
        /// <summary>Null when SolidWorks cannot produce a reference — a
        /// suppressed or failing occurrence. The manifest schema allows null
        /// there, and a missing id must not sink the export.</summary>
        public static string PersistIdBase64(IModelDoc2 topDocument, Component2 component)
        {
            if (topDocument == null || component == null) return null;
            try
            {
                object raw = topDocument.Extension.GetPersistReference3(component);
                byte[] bytes = raw as byte[];
                if (bytes == null)
                {
                    // The VARIANT can come back as a non-byte array on some
                    // interop paths; copy element-wise rather than fail.
                    var array = raw as Array;
                    if (array == null) return null;
                    bytes = new byte[array.Length];
                    for (int i = 0; i < array.Length; i++)
                        bytes[i] = Convert.ToByte(array.GetValue(i));
                }
                return bytes.Length == 0 ? null : Convert.ToBase64String(bytes);
            }
            catch
            {
                return null;
            }
        }
    }
}
