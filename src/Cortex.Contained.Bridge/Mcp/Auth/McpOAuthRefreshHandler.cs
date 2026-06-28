using System.Net;
using System.Net.Http.Headers;

namespace Cortex.Contained.Bridge.Mcp.Auth;

/// <summary>
/// A <see cref="DelegatingHandler"/> that attaches the host-resolved OAuth bearer to every MCP HTTP
/// request and, on a <c>401</c>, transparently refreshes the token and retries the request once.
/// Keeps OAuth entirely host-side and invisible to the agent. The request body is buffered so a
/// retried POST (e.g. <c>tools/call</c>) carries the same payload.
/// </summary>
public sealed class McpOAuthRefreshHandler : DelegatingHandler
{
    private readonly IMcpBearerSource bearerSource;

    public McpOAuthRefreshHandler(IMcpBearerSource bearerSource)
    {
        this.bearerSource = bearerSource;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Buffer the content up-front so the request can be replayed verbatim after a refresh.
        var bufferedBody = request.Content is not null
            ? await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false)
            : null;

        var token = await this.bearerSource.GetAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        var refreshed = await this.bearerSource.RefreshAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(refreshed))
        {
            return response;
        }

        response.Dispose();
        using var retry = CloneRequest(request, bufferedBody);
        retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshed);
        return await base.SendAsync(retry, cancellationToken).ConfigureAwait(false);
    }

    private static HttpRequestMessage CloneRequest(HttpRequestMessage request, byte[]? bufferedBody)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
        };

        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (bufferedBody is not null)
        {
            clone.Content = new ByteArrayContent(bufferedBody);
            if (request.Content?.Headers is not null)
            {
                foreach (var header in request.Content.Headers)
                {
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }
        }

        return clone;
    }
}
