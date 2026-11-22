namespace Cortex.Contained.Bridge.Security;

/// <summary>A tenant storage key exists but cannot be decrypted; it is never silently replaced.</summary>
public sealed class StorageKeyUnavailableException : Exception
{
    public StorageKeyUnavailableException()
    {
    }

    public StorageKeyUnavailableException(string message)
        : base(message)
    {
    }

    public StorageKeyUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
