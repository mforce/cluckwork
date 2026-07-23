namespace Cluckwork.Domain.Media;

// An image that has been through ImageSanitizer: the format is one of the three
// we accept, the structure has been walked end to end, metadata containers have
// been dropped, and anything past the format's own end marker is gone.
//
// `Content` is NOT the bytes the client sent — it is the rewritten copy. The
// distinction matters: the uploaded buffer is what an attacker controls, this
// is what we chose to keep.
public sealed record SanitizedImage(ImageKind Kind, byte[] Content, int Width, int Height)
{
    // Served instead of the client's declared Content-Type, which is an
    // unverified claim. With X-Content-Type-Options: nosniff (#144) the browser
    // is held to this value, so a file that lied about being an image cannot be
    // re-interpreted as a document.
    public string ContentType => Kind switch
    {
        ImageKind.Png => "image/png",
        ImageKind.Jpeg => "image/jpeg",
        ImageKind.Webp => "image/webp",
        _ => throw new InvalidOperationException($"Unmapped image kind '{Kind}'.")
    };

    public string FileExtension => Kind switch
    {
        ImageKind.Png => ".png",
        ImageKind.Jpeg => ".jpg",
        ImageKind.Webp => ".webp",
        _ => throw new InvalidOperationException($"Unmapped image kind '{Kind}'.")
    };
}
