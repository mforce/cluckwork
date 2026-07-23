namespace Cluckwork.Domain.Media;

// The raster formats a farm logo may be stored in (#123). Deliberately closed:
// SVG is absent because it is a script-bearing document, not a raster image,
// and rendering one from a tenant upload is a stored-XSS vector.
public enum ImageKind
{
    Png,
    Jpeg,
    Webp
}
