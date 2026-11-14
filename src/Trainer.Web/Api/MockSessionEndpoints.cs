using Microsoft.AspNetCore.Mvc;
using Trainer.Application.Mock;
using Trainer.Contracts;
using Trainer.Domain.Ids;
using Trainer.Web.Auth;
using Trainer.Web.ContentState;

namespace Trainer.Web.Api;

/// <summary><c>PUT /api/mock-sessions/{id}</c>: the summary of a finished mock interview, idempotent by its client id.</summary>
public static class MockSessionEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapPut("/mock-sessions/{id}", PutAsync).AsWrite();
    }

    /// <summary>
    /// 204 when stored or replaced. An id that is not a ULID or belongs to another user is 404, which reveals nothing about another user's session;
    /// a structurally invalid summary or one whose times the time policy rejects is 400 <c>validation</c>.
    /// </summary>
    private static async Task<IResult> PutAsync(
        string id,
        [FromBody] MockSummaryDto? summary,
        HttpContext context,
        ContentSnapshotProvider content,
        IMockSessionService mocks,
        CancellationToken ct)
    {
        if (context.User.GetUserId() is not { } user)
        {
            return ApiEndpoints.SignInRequired(context);
        }

        if (!MockSessionId.TryCreate(id, out MockSessionId sessionId))
        {
            return ApiEndpoints.Error(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound);
        }

        if (!ApiRequestParser.TryParseSummary(summary, content.Current, out MockSummary? parsed, out _))
        {
            return ApiEndpoints.Validation();
        }

        MockSaveResult result = await mocks.SaveAsync(user, sessionId, parsed, ct);
        return result switch
        {
            MockSaveResult.Created or MockSaveResult.Updated => Results.NoContent(),
            MockSaveResult.NotFound => ApiEndpoints.Error(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound),
            MockSaveResult.Invalid => ApiEndpoints.Validation(),
            // A new MockSaveResult must be mapped here on purpose; this becomes a logged 500, not a quiet 400.
            _ => throw new InvalidOperationException($"Unhandled {nameof(MockSaveResult)} value {result}."),
        };
    }
}
