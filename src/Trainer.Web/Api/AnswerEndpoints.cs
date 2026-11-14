using Microsoft.AspNetCore.Mvc;
using Trainer.Application.Answers;
using Trainer.Contracts;
using Trainer.Web.Auth;

namespace Trainer.Web.Api;

/// <summary><c>POST /api/answers/batch</c>: the outbox flush (spec 8.8). Idempotent by client answer id.</summary>
public static class AnswerEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapPost("/answers/batch", PostBatchAsync).AsWrite();
    }

    /// <summary>
    /// Every distinct client id of a valid envelope is acknowledged once, accepted or rejected, and the client drops both
    /// from its outbox. Entries the parser rejects never reach the recorder.
    /// </summary>
    private static async Task<IResult> PostBatchAsync(
        [FromBody] AnswerBatch? batch,
        HttpContext context,
        IAnswerRecorder recorder,
        CancellationToken ct)
    {
        if (context.User.GetUserId() is not { } user)
        {
            return ApiEndpoints.SignInRequired(context);
        }

        if (!ApiRequestParser.TryParseBatch(batch, out IReadOnlyList<ReportedAnswer>? answers, out IReadOnlyList<RejectedAnswerDto>? invalid, out string? error))
        {
            return ApiEndpoints.Error(StatusCodes.Status400BadRequest, error);
        }

        AnswerBatchResult result = answers.Count == 0
            ? new AnswerBatchResult([], [])
            : await recorder.RecordAsync(user, answers, OwnerCheckFilter.GetClientUtcOffset(context), ct);
        List<RejectedAnswerDto> rejected = [.. invalid, .. result.Rejected.Select(r => new RejectedAnswerDto(r.ClientAnswerId, r.Reason))];
        return Results.Json(new AnswerBatchResponse(result.Accepted, rejected), TrainerJsonContext.Default.AnswerBatchResponse);
    }
}
