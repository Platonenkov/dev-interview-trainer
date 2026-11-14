namespace Trainer.Web.Client.Sync;

/// <summary>
/// The one account notice of a page. Every reason to show it ends here: another tab published another owner
/// (<see cref="AccountWatcher"/>), the server refused this tab's owner (<see cref="OutboxSyncService.AccountMismatch"/>),
/// an island got 409 for a request (<see cref="Show"/>), or the server ended the session while syncing
/// (<see cref="OutboxSyncService.SignedOut"/>). Only the shell's <c>AccountChangedNotice</c> renders it, so the notice
/// appears once whichever reason comes first and whichever component was created first.
/// </summary>
public sealed class AccountNotice : IDisposable
{
    private readonly AccountWatcher _watcher;
    private readonly OutboxSyncService _sync;

    public AccountNotice(AccountWatcher watcher, OutboxSyncService sync)
    {
        ArgumentNullException.ThrowIfNull(watcher);
        ArgumentNullException.ThrowIfNull(sync);

        _watcher = watcher;
        _sync = sync;
        _watcher.Changed += OnAccountChanged;
        _sync.AccountMismatch += Show;
        _sync.SignedOut += ShowSignedOut;

        // The sync service may have stopped before this notice was first resolved: its flags keep what happened.
        if (watcher.ChangedTo is not null || sync.HasAccountMismatch)
        {
            Reason = AccountNoticeReason.AccountChanged;
        }
        else if (sync.IsSignedOut)
        {
            Reason = AccountNoticeReason.SignedOut;
        }
    }

    /// <summary>Raised when the notice first shows and when a signed-out notice becomes an account change.</summary>
    public event Action? Changed;

    public bool IsShown => Reason != AccountNoticeReason.None;

    public AccountNoticeReason Reason { get; private set; }

    /// <summary>Another account is signed in now (or nobody, as seen from another tab): reload to continue.</summary>
    public void Show() => Raise(AccountNoticeReason.AccountChanged);

    /// <summary>
    /// The server ended this session (401 to a sync write): sign in again. An account change outranks it, since the
    /// reload it asks for also shows the sign-in state.
    /// </summary>
    public void ShowSignedOut() => Raise(AccountNoticeReason.SignedOut);

    public void Dispose()
    {
        _watcher.Changed -= OnAccountChanged;
        _sync.AccountMismatch -= Show;
        _sync.SignedOut -= ShowSignedOut;
    }

    private void Raise(AccountNoticeReason reason)
    {
        // Shown once per reason; an account change is final and replaces a signed-out notice.
        if (Reason == reason || Reason == AccountNoticeReason.AccountChanged)
        {
            return;
        }

        Reason = reason;
        Changed?.Invoke();
    }

    private void OnAccountChanged(string owner) => Show();
}

/// <summary>Why the page's account notice shows.</summary>
public enum AccountNoticeReason
{
    None,

    /// <summary>The server ended the session; signing in again continues.</summary>
    SignedOut,

    /// <summary>Another tab signed in or out, or the server refused this tab's owner; only a reload continues.</summary>
    AccountChanged,
}
