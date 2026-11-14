namespace Trainer.Web.Client.Storage;

/// <summary>Whether answers given in this tab are safe in browser storage; <c>StorageNotice</c> explains every value but the first.</summary>
public enum StorageStatus
{
    /// <summary>Every answer is in <c>localStorage</c>.</summary>
    Persistent,

    /// <summary>No Web Locks, no <c>localStorage</c>, or an unreadable value that could not be set aside: answers live in this tab only.</summary>
    MemoryOnly,

    /// <summary>The storage quota is exhausted: some answers are only in memory until a later write succeeds.</summary>
    Full,

    /// <summary>A newer app version wrote the outbox; it is left untouched and this tab keeps its answers in memory.</summary>
    NewerVersion,

    /// <summary>A removal of acknowledged answers did not reach storage; it is retried, and until then they may be sent again.</summary>
    WriteFailed,
}
