using Cortex.Contained.Bridge.Connectors;
using Cortex.Contained.Bridge.Connectors.Media;
using Microsoft.AspNetCore.Mvc;

namespace Cortex.Contained.Bridge.Endpoints;

/// <summary>
/// Maps the connector attachment transfer endpoints — the out-of-band channel for media too
/// large to travel inside a 1 MiB connector frame.
/// </summary>
/// <remarks>
/// These endpoints are deliberately NOT part of the <c>/api/connectors/*</c> management surface,
/// which requires the Web UI session. A connector has no session, only the pairing token it was
/// issued, so these are mapped <c>AllowAnonymous</c> and perform their own bearer-token
/// authentication against the DPAPI-backed connector registry.
/// <para>
/// The same loopback guard the WebSocket endpoint applies is applied here: connectors are local
/// processes, and nothing about this surface should be reachable from the network even if the
/// Web UI bind address is widened.
/// </para>
/// </remarks>
internal static class ConnectorAttachmentEndpoints
{
    /// <summary>Route of the upload endpoint.</summary>
    internal const string UploadRoute = "/api/connectors/attachments";

    /// <summary>Route of the fetch endpoint.</summary>
    internal const string FetchRoute = "/api/connectors/attachments/{handle}";

    /// <summary>Maps the attachment upload and fetch endpoints onto <paramref name="app"/>.</summary>
    /// <param name="app">The web application to map onto.</param>
    public static void MapConnectorAttachmentEndpoints(this WebApplication app)
    {
        app.MapPost(UploadRoute, async (
            HttpContext context,
            ConnectorAttachmentService service,
            CancellationToken ct) =>
        {
            if (!ConnectorEndpoint.IsLoopbackPeer(context.Connection.RemoteIpAddress))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var authorization = context.Request.Headers.Authorization.ToString();

            // Refuse an over-long body from the declared length before reading a single byte.
            // A hostile Content-Length is not trusted either — the read below is bounded too.
            if (context.Request.ContentLength > service.MaxUploadBytes)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            byte[] data;
            string? declaredMimeType;
            string? fileName;
            string? caption;

            if (context.Request.HasFormContentType)
            {
                var form = await context.Request.ReadFormAsync(ct).ConfigureAwait(false);
                var file = form.Files.GetFile("file") ?? (form.Files.Count > 0 ? form.Files[0] : null);
                if (file is null)
                {
                    return Results.Json(new { error = "a 'file' part is required" }, statusCode: 400);
                }

                if (file.Length > service.MaxUploadBytes)
                {
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                }

                await using var stream = file.OpenReadStream();
                data = await ReadBoundedAsync(stream, service.MaxUploadBytes, ct).ConfigureAwait(false)
                    ?? [];

                declaredMimeType = file.ContentType;
                fileName = file.FileName;
                caption = form["caption"].ToString();
            }
            else
            {
                var read = await ReadBoundedAsync(context.Request.Body, service.MaxUploadBytes, ct)
                    .ConfigureAwait(false);

                if (read is null)
                {
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                }

                data = read;
                declaredMimeType = context.Request.ContentType;
                fileName = context.Request.Headers["X-Attachment-Filename"].ToString();
                caption = context.Request.Headers["X-Attachment-Caption"].ToString();
            }

            var result = service.Upload(authorization, data, declaredMimeType, fileName, caption);

            return result.Success
                ? Results.Ok(new { handle = result.Handle, expiresAt = result.ExpiresAt })
                : Results.Json(new { error = result.Message }, statusCode: StatusCodeFor(result.Error));
        }).AllowAnonymous();

        app.MapGet(FetchRoute, (
            HttpContext context,
            [FromRoute] string handle,
            ConnectorAttachmentService service) =>
        {
            if (!ConnectorEndpoint.IsLoopbackPeer(context.Connection.RemoteIpAddress))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var result = service.Fetch(context.Request.Headers.Authorization.ToString(), handle);

            if (!result.Success)
            {
                return Results.StatusCode(StatusCodeFor(result.Error));
            }

            // No file name in the Content-Disposition: the response body is the payload, and the
            // connector already knows the metadata from the frame that named the handle.
            return Results.File(result.Content!.Data, result.Content.MimeType);
        }).AllowAnonymous();
    }

    /// <summary>
    /// Maps an access error onto an HTTP status. <see cref="ConnectorAttachmentAccessError.NotFound"/>
    /// is 404 rather than 403 on purpose: a distinct forbidden status would confirm that a handle
    /// exists, letting one connector probe for another's attachments.
    /// </summary>
    /// <param name="error">The access error to translate.</param>
    internal static int StatusCodeFor(ConnectorAttachmentAccessError error) => error switch
    {
        ConnectorAttachmentAccessError.None => StatusCodes.Status200OK,
        ConnectorAttachmentAccessError.Unauthorized => StatusCodes.Status401Unauthorized,
        ConnectorAttachmentAccessError.MediaDisabled => StatusCodes.Status404NotFound,
        ConnectorAttachmentAccessError.RateLimited => StatusCodes.Status429TooManyRequests,
        ConnectorAttachmentAccessError.ContentRejected => StatusCodes.Status415UnsupportedMediaType,
        ConnectorAttachmentAccessError.QuotaExceeded => StatusCodes.Status507InsufficientStorage,
        ConnectorAttachmentAccessError.NotFound => StatusCodes.Status404NotFound,
        _ => StatusCodes.Status400BadRequest,
    };

    /// <summary>
    /// Reads at most <paramref name="maxBytes"/> from <paramref name="stream"/>, returning null
    /// when the stream carries more. Reading one byte past the limit is what distinguishes
    /// "exactly at the cap" from "over the cap" without ever buffering the excess.
    /// </summary>
    /// <param name="stream">The request stream.</param>
    /// <param name="maxBytes">Maximum bytes to accept.</param>
    /// <param name="ct">Cancellation token.</param>
    private static async Task<byte[]?> ReadBoundedAsync(Stream stream, long maxBytes, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        long total = 0;

        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maxBytes)
            {
                return null;
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), ct).ConfigureAwait(false);
        }

        return buffer.ToArray();
    }
}
