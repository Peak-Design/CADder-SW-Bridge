namespace Peak.Cadder.Core
{
    /// <summary>
    /// How much of a SolidWorks appearance a send carries.
    ///
    /// All three are on by default, which is what makes a part arrive
    /// looking as it does in SolidWorks. They exist because each one costs
    /// something and each one can be in the way:
    ///
    ///   * Appearances. Off, a face carries its plain colour and nothing
    ///     else: no texture, no finish values, no decal. That is what a
    ///     user who replaces every material in Blender wants, and it is
    ///     the fastest read.
    ///
    ///   * Decals. Off, decals are not read at all. Reading them opens
    ///     every decal of the part document and asks each face about it.
    ///
    ///   * Texture mapping. Off, a texture still travels, without the
    ///     projection that places it. Blender then boxes it at the
    ///     texture's own size. The mapping is the part most likely to be
    ///     wrong (see the spherical mapping in TODO.md), so it can be
    ///     turned off without losing the image.
    /// </summary>
    public sealed class AppearanceOptions
    {
        public bool Appearances = true;
        public bool Decals = true;
        public bool TextureMapping = true;

        /// <summary>Everything on: what a caller with no settings gets.
        /// </summary>
        public static readonly AppearanceOptions Full = new AppearanceOptions();

        public static AppearanceOptions From(AppSettings settings)
        {
            if (settings == null) return Full;
            return new AppearanceOptions
            {
                Appearances = settings.ExportAppearances,
                Decals = settings.ExportAppearances && settings.ExportDecals,
                TextureMapping = settings.ExportAppearances
                              && settings.ExportTextureMapping,
            };
        }
    }
}
