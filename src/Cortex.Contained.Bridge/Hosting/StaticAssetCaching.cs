namespace Cortex.Contained.Bridge.Hosting;

/// <summary>
/// Cache policy for the web UI shell and its static files. Script and page names carry no content hash,
/// so browsers must revalidate (ETag / Last-Modified) on every load; otherwise a deployed upgrade can stay
/// hidden behind a heuristically cached, stale script.
/// </summary>
public static class StaticAssetCaching
{
    /// <summary>Revalidate on every use; unchanged files still come back as a cheap 304.</summary>
    public const string CacheControl = "no-cache";

    /// <summary>Applies the policy to a static-file or SPA-shell response.</summary>
    public static void Apply(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.Headers.CacheControl = CacheControl;
    }
}
