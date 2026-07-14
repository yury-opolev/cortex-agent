using Cortex.Contained.Bridge.Mcp;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Cortex.Contained.Bridge.Tests.Mcp;

/// <summary>
/// Proves the <see cref="McpServerConnectionBase.ConnectAsync"/> failure path never leaks a raw
/// <c>ex.Message</c> into the admin-facing <see cref="McpServerConnectionBase.LastError"/> field or
/// the Bridge log — a connect failure (e.g. a misconfigured HTTP/stdio endpoint) can embed a
/// connection-string secret. Mirrors <see cref="McpErrorSanitizerTests.TransportFailure_NeverContainsTheRawExceptionMessage"/>.
/// </summary>
public sealed class McpServerConnectionBaseConnectTests
{
    private sealed class ThrowingTransportConnection : McpServerConnectionBase
    {
        private readonly Exception exceptionToThrow;

        public ThrowingTransportConnection(Exception exceptionToThrow, ILogger logger)
            : base("srv", [], [], logger)
        {
            this.exceptionToThrow = exceptionToThrow;
        }

        protected override IClientTransport CreateTransport() => throw this.exceptionToThrow;
    }

    /// <summary>Captures fully-formatted log messages so redaction assertions can inspect them.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => this.Messages.Add(formatter(state, exception));
    }

    [Fact]
    public async Task ConnectAsync_TransportCreationFails_NeverPutsRawExceptionMessageInLastError()
    {
        // SECURITY: a connect failure whose exception message contains a secret-bearing string
        // (e.g. an HTTP endpoint URL with inline credentials) must never reach LastError, which is
        // surfaced on the admin-facing MCP Servers page (McpServerView.LastError).
        var capturingLogger = new CapturingLogger<ThrowingTransportConnection>();
        var secretLookingMessage = "connection refused at https://user:s3cr3t@internal.example/mcp";
        var connection = new ThrowingTransportConnection(new IOException(secretLookingMessage), capturingLogger);

        await connection.ConnectAsync(CancellationToken.None);

        Assert.Equal(McpServerStatus.Error, connection.Status);
        Assert.NotNull(connection.LastError);
        Assert.DoesNotContain(secretLookingMessage, connection.LastError, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cr3t", connection.LastError, StringComparison.Ordinal);
        Assert.Contains(nameof(IOException), connection.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectAsync_TransportCreationFails_NeverLogsTheRawExceptionMessage()
    {
        var capturingLogger = new CapturingLogger<ThrowingTransportConnection>();
        var secretLookingMessage = "connection refused at https://user:s3cr3t@internal.example/mcp";
        var connection = new ThrowingTransportConnection(new IOException(secretLookingMessage), capturingLogger);

        await connection.ConnectAsync(CancellationToken.None);

        var failureLogs = capturingLogger.Messages
            .Where(m => m.Contains("connect failed", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var failureLog = Assert.Single(failureLogs);
        Assert.Contains(nameof(IOException), failureLog, StringComparison.Ordinal);
        Assert.DoesNotContain(secretLookingMessage, failureLog, StringComparison.Ordinal);
        Assert.DoesNotContain(capturingLogger.Messages, m => m.Contains("s3cr3t", StringComparison.Ordinal));
    }
}
