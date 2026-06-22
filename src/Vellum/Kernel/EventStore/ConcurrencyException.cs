namespace Vellum.Kernel.EventStore;

public sealed class ConcurrencyException : Exception
{
    public Guid StreamId { get; }
    public int ExpectedVersion { get; }
    public int ActualVersion { get; }

    public ConcurrencyException(Guid streamId, int expectedVersion, int actualVersion)
        : base($"Stream {streamId}: expected version {expectedVersion}, actual {actualVersion}")
    {
        StreamId = streamId;
        ExpectedVersion = expectedVersion;
        ActualVersion = actualVersion;
    }
}
