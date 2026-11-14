using System.Globalization;
using Microsoft.Extensions.Primitives;
using Trainer.Application.Answers;
using Trainer.Contracts;
using Trainer.Web.Auth;

namespace Trainer.Web.Api;

/// <summary>
/// ADR 0008: every write names the Telegram user whose outbox it flushes in <see cref="OwnerHeader"/>. A write whose
/// owner is missing, malformed or not the signed-in user is answered 409 <c>owner-mismatch</c> before anything is
/// stored, so answers given while signed in as one account never land on another one that signed in meanwhile.
/// It also reads the device offset from <see cref="UtcOffsetHeader"/> for the answer recorder.
/// </summary>
public sealed class OwnerCheckFilter : IEndpointFilter
{
    public const string OwnerHeader = "X-Outbox-Owner";
    public const string UtcOffsetHeader = "X-Client-Utc-Offset";

    private static readonly object UtcOffsetKey = new();

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        HttpContext http = context.HttpContext;
        if (ParseOwner(http.Request.Headers[OwnerHeader]) is not { } owner || owner != http.User.GetTelegramUserId())
        {
            return ApiEndpoints.Error(StatusCodes.Status409Conflict, ApiErrorCodes.OwnerMismatch);
        }

        if (ParseUtcOffset(http.Request.Headers[UtcOffsetHeader]) is { } offset)
        {
            http.Items[UtcOffsetKey] = offset;
        }

        return await next(context);
    }

    /// <summary>The valid <see cref="UtcOffsetHeader"/> of a request this filter passed, else null.</summary>
    public static short? GetClientUtcOffset(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(UtcOffsetKey, out object? value) && value is short offset ? offset : null;
    }

    /// <summary>Exactly one positive decimal Telegram user id, else null.</summary>
    public static long? ParseOwner(StringValues values) =>
        values.Count == 1 && long.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out long owner) && owner > 0
            ? owner
            : null;

    /// <summary>
    /// Exactly one integer within ±<see cref="IAnswerRecorder.MaxUtcOffsetMinutes"/>, else null: a bad offset is ignored,
    /// it never fails the write (it only feeds streak days).
    /// </summary>
    public static short? ParseUtcOffset(StringValues values) =>
        values.Count == 1
        && int.TryParse(values[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int minutes)
        && minutes is >= -IAnswerRecorder.MaxUtcOffsetMinutes and <= IAnswerRecorder.MaxUtcOffsetMinutes
            ? (short)minutes
            : null;
}
