using Microsoft.JSInterop;

namespace Trainer.Web.Client.Sync;

/// <summary>
/// Cross-tab account hand-over: an island publishes its owner (the Telegram user id or <see cref="Anonymous"/>) under
/// <c>dit.account</c>, and so do signed-in public pages and the sign-in page (<c>account-publish.js</c>). When another
/// tab publishes a different owner, the cookie this tab relies on now belongs to someone else (or to nobody), so the
/// island stops syncing and asks for a reload. This is a courtesy: the server's owner check (ADR 0008) stays
/// authoritative and catches what this misses, such as storage events that never arrive.
/// </summary>
public sealed class AccountWatcher(IJSRuntime js) : IAsyncDisposable
{
    public const string Anonymous = "anon";

    private string? _owner;
    private DotNetObjectReference<AccountWatcher>? _reference;
    private int? _handle;

    /// <summary>Raised once, with the owner another tab published.</summary>
    public event Action<string>? Changed;

    /// <summary>The owner another tab published, or null while the account is unchanged.</summary>
    public string? ChangedTo { get; private set; }

    /// <param name="owner">The Telegram user id as text, or <see cref="Anonymous"/>.</param>
    public async ValueTask StartAsync(string owner)
    {
        ArgumentException.ThrowIfNullOrEmpty(owner);
        if (_owner is not null)
        {
            return;
        }

        _owner = owner;
        _reference = DotNetObjectReference.Create(this);
        string? publishedSince;
        try
        {
            // Listen first, then claim: a change between the two still arrives as an event.
            _handle = await js.InvokeAsync<int>("ditStore.watchAccount", _reference);
            publishedSince = await js.InvokeAsync<string?>("ditStore.claimAccount", owner);
        }
        catch (JSException)
        {
            // No storage or no script: nothing to publish or watch; the server's owner check still applies.
            return;
        }

        // Another tab published another owner after this page started loading: the page was rendered for an account
        // the cookie no longer holds. The claim then wrote nothing, so that tab is not told about a change that is not one.
        if (publishedSince is not null)
        {
            OnAccountPublished(publishedSince);
        }
    }

    /// <summary>
    /// Called by <c>outbox.js</c> with the owner of every <c>storage</c> event on <c>dit.account</c> from another tab (the
    /// stored value is <c>owner|unix ms</c>; the script passes the owner part).
    /// </summary>
    [JSInvokable]
    public void OnAccountPublished(string owner)
    {
        if (_owner is null || ChangedTo is not null || string.IsNullOrEmpty(owner) || string.Equals(owner, _owner, StringComparison.Ordinal))
        {
            return;
        }

        ChangedTo = owner;
        Changed?.Invoke(owner);
    }

    public async ValueTask DisposeAsync()
    {
        if (_handle is { } handle)
        {
            try
            {
                await js.InvokeVoidAsync("ditStore.unwatchAccount", handle);
            }
            catch (JSDisconnectedException)
            {
                // The page is going away; its listeners go with it.
            }
            catch (JSException)
            {
                // Nothing left to unregister.
            }
        }

        _reference?.Dispose();
    }
}
