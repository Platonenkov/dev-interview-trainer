using Telegram.OpenIdConnect.AspNetCore;

namespace Trainer.Web.Auth;

/// <summary>Application-owned reason codes of a sign-in attempt, the only failure detail that reaches the log.</summary>
public static class SignInReasons
{
    public const string Ok = "ok";
    public const string GateDenied = "gate-denied";
    public const string MissingId = "missing-id";
    public const string AccessDenied = "access-denied";
    public const string RemoteFailure = "remote-failure";

    /// <summary>
    /// Maps an audit event to a reason code. The library's <c>Detail</c> is matched only against its own fixed strings;
    /// anything else is a remote error message and becomes <see cref="RemoteFailure"/> without being kept.
    /// </summary>
    public static string For(TelegramSignInAuditEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return e.Outcome switch
        {
            TelegramSignInOutcome.Success => Ok,
            TelegramSignInOutcome.Denied => GateDenied,
            _ => e.Detail switch
            {
                "missing id claim" => MissingId,
                "access_denied" => AccessDenied,
                "rejected by gate" => GateDenied,
                _ => RemoteFailure,
            },
        };
    }
}

/// <summary>
/// The sign-in log. The library's own <c>TelegramOidc</c> log category is filtered out in <c>Program</c> because its
/// messages carry the username and the client IP; this sink logs only the outcome, the Telegram id and a reason code,
/// never the raw failure detail (which can echo a remote error description), names, picture or IP.
/// </summary>
public sealed partial class SignInLogSink(ILogger<SignInLogSink> logger) : ITelegramSignInAuditSink
{
    public ValueTask RecordAsync(TelegramSignInAuditEvent e, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(e);
        LogLevel level = e.Outcome == TelegramSignInOutcome.Success ? LogLevel.Information : LogLevel.Warning;
        string reason = SignInReasons.For(e);
        LogSignIn(level, e.Outcome, e.TelegramUserId, reason);
        return ValueTask.CompletedTask;
    }

    [LoggerMessage(EventId = 5001, Message = "Sign-in {Outcome} for Telegram id {TelegramUserId}: {Reason}")]
    private partial void LogSignIn(LogLevel level, TelegramSignInOutcome outcome, long? telegramUserId, string reason);
}
