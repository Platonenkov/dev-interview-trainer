using Trainer.Contracts;
using Trainer.Web.Client.Api;

namespace Trainer.Web.Client.Sessions;

/// <summary>What an island shows instead of (or before) its questions; <c>IslandStateView</c> renders each one.</summary>
public enum IslandStateKind
{
    Loading,
    Ready,
    Empty,
    Offline,
    RateLimited,
    SignedOut,
    AccountChanged,
    ServerError,
}

public static class IslandState
{
    /// <summary>
    /// Maps the result of a session request: no response is <see cref="IslandStateKind.Offline"/>, 429, 401 and a 409 with the
    /// <c>owner-mismatch</c> code have their own states, any other failure is <see cref="IslandStateKind.ServerError"/>. A success is
    /// <see cref="IslandStateKind.Empty"/> when <paramref name="sessionEmpty"/>, unless it carried no readable body (a
    /// proxy's or captive portal's page), which is a server error rather than an empty pool.
    /// </summary>
    public static IslandStateKind FromResult<T>(ApiResult<T> result, bool sessionEmpty)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Status switch
        {
            0 => IslandStateKind.Offline,
            401 => IslandStateKind.SignedOut,
            // Only the owner check's 409 means the account changed; any other 409 is an unexpected failure.
            409 when string.Equals(result.ErrorCode, ApiErrorCodes.OwnerMismatch, StringComparison.Ordinal) => IslandStateKind.AccountChanged,
            429 => IslandStateKind.RateLimited,
            _ when result.IsSuccess && result.Value is not null => sessionEmpty ? IslandStateKind.Empty : IslandStateKind.Ready,
            _ => IslandStateKind.ServerError,
        };
    }
}
