namespace Trainer.Web.Startup;

public enum ReadinessStatus
{
    /// <summary>The startup import has not finished.</summary>
    Pending,

    /// <summary>The content is imported (or was already current).</summary>
    Ready,

    /// <summary>The startup import was rejected or threw; the previously stored content keeps serving.</summary>
    Failed,
}

public sealed class ReadinessState
{
    private int _status = (int)ReadinessStatus.Pending;

    public ReadinessStatus Status => (ReadinessStatus)Volatile.Read(ref _status);

    public void MarkReady() => Volatile.Write(ref _status, (int)ReadinessStatus.Ready);

    public void MarkFailed() => Volatile.Write(ref _status, (int)ReadinessStatus.Failed);
}
