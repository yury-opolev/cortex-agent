namespace Cortex.Contained.Bridge.Tokens;

/// <summary>
/// Minimal seam over the credential fan-out so the proactive
/// <see cref="TokenRefreshBackgroundService"/> sweep can re-push refreshed credentials
/// without depending on the concrete (and not-easily-mockable) SignalR router used by
/// <c>CredentialsPusher</c>. Implemented by <c>CredentialsPusher</c>, which already
/// exposes <see cref="PushCredentialsAsync"/>.
/// </summary>
internal interface ICredentialReplisher
{
    /// <summary>Re-pushes the current LLM provider credentials to all connected tenants.</summary>
    Task PushCredentialsAsync(CancellationToken cancellationToken);
}
