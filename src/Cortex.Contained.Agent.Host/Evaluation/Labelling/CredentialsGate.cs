namespace Cortex.Contained.Agent.Host.Evaluation.Labelling;

/// <summary>
/// Waits for the LLM credentials that the Bridge pushes after the agent (re)starts: an evaluation job that resumes
/// right after a restart would otherwise fail with "no provider" although they arrive a few seconds later.
/// </summary>
public static class CredentialsGate
{
    /// <summary>How long a job waits for the credentials.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    /// <summary>How often it looks.</summary>
    public static readonly TimeSpan DefaultPoll = TimeSpan.FromSeconds(2);

    /// <summary>True once <paramref name="ready"/> says so (null means always ready); false when <paramref name="timeout"/> passes first.</summary>
    public static async Task<bool> WaitAsync(Func<bool>? ready, TimeSpan? timeout, TimeSpan? poll, TimeProvider time, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(time);
        if (ready is null)
        {
            return true;
        }

        var limit = time.GetUtcNow() + (timeout ?? DefaultTimeout);
        while (!ready())
        {
            if (time.GetUtcNow() >= limit)
            {
                return false;
            }

            await Task.Delay(poll ?? DefaultPoll, time, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }
}
