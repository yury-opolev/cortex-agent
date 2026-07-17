using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Channels.WebChat.Tests;

/// <summary>
/// A SignalR client that reconnects while the host is draining pins Kestrel's
/// graceful shutdown open for the full shutdown timeout, because a WebSocket is a
/// long-lived in-flight request that never completes on its own. New connections
/// must therefore be refused once <see cref="IHostApplicationLifetime.ApplicationStopping"/>
/// has fired.
/// </summary>
public class WebChatHubShutdownTests : IAsyncDisposable
{
    private readonly WebChatChannel channel;

    public WebChatHubShutdownTests()
    {
        this.channel = new WebChatChannel(NullLogger<WebChatChannel>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await this.channel.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private WebChatHub CreateHub(HubCallerContext context, CancellationToken applicationStopping)
    {
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Returns(applicationStopping);

        return new WebChatHub(
            this.channel,
            Substitute.For<IWebChatHubProxy>(),
            NullLogger<WebChatHub>.Instance,
            lifetime)
        {
            Context = context,
        };
    }

    [Fact]
    public async Task OnConnectedAsync_WhenApplicationIsStopping_AbortsConnection()
    {
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();
        var context = Substitute.For<HubCallerContext>();
        context.ConnectionId.Returns("conn-during-shutdown");

        var hub = this.CreateHub(context, stopping.Token);

        await hub.OnConnectedAsync();

        context.Received(1).Abort();
    }

    [Fact]
    public async Task OnConnectedAsync_WhenApplicationIsRunning_DoesNotAbortConnection()
    {
        var context = Substitute.For<HubCallerContext>();
        context.ConnectionId.Returns("conn-normal");

        var hub = this.CreateHub(context, CancellationToken.None);

        await hub.OnConnectedAsync();

        context.DidNotReceive().Abort();
    }
}
