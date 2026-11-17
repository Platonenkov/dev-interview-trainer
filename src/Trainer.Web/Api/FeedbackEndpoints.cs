using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Trainer.Application.Feedback;
using Trainer.Contracts;
using Trainer.Domain.Feedback;
using Trainer.Domain.Keys;
using Trainer.Domain.Questions;
using Trainer.Web.Auth;

namespace Trainer.Web.Api;

/// <summary>
/// Question feedback of signed-in users (ADR 0016): <c>PUT</c> and <c>DELETE /api/questions/{id}/rating</c> and
/// <c>POST /api/questions/{id}/reports</c>. All three are writes, so they carry the antiforgery token and the owner header.
/// </summary>
public static class FeedbackEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        RouteGroupBuilder questions = api.MapGroup("/questions/{id}");
        questions.MapPut("/rating", PutRatingAsync).AsWrite();
        questions.MapDelete("/rating", DeleteRatingAsync).AsWrite();
        questions.MapPost("/reports", PostReportAsync).AsWrite();
    }

    private static async Task<IResult> PutRatingAsync(
        string id,
        [FromBody] RatingRequest? request,
        HttpContext context,
        IQuestionFeedbackService feedback,
        CancellationToken ct)
    {
        if (context.User.GetUserId() is not { } user)
        {
            return ApiEndpoints.SignInRequired(context);
        }

        if (!QuestionId.TryCreate(id, out QuestionId question) || !QuestionRatingKeys.TryParse(request?.Value, out QuestionRating rating))
        {
            return ApiEndpoints.Validation();
        }

        return RatingResponse(await feedback.SetRatingAsync(user, question, rating, ct));
    }

    private static async Task<IResult> DeleteRatingAsync(
        string id,
        HttpContext context,
        IQuestionFeedbackService feedback,
        CancellationToken ct)
    {
        if (context.User.GetUserId() is not { } user)
        {
            return ApiEndpoints.SignInRequired(context);
        }

        if (!QuestionId.TryCreate(id, out QuestionId question))
        {
            return ApiEndpoints.Validation();
        }

        return RatingResponse(await feedback.SetRatingAsync(user, question, null, ct));
    }

    private static IResult RatingResponse(RatingResult result) => result.Outcome switch
    {
        FeedbackOutcome.Saved => Results.Json(
            new RatingDto(result.Rating is { } rating ? QuestionRatingKeys.ToKey(rating) : null), TrainerJsonContext.Default.RatingDto),
        FeedbackOutcome.QuestionNotFound => ApiEndpoints.Error(StatusCodes.Status404NotFound, ApiErrorCodes.QuestionNotFound),
        // A new FeedbackOutcome must be mapped here on purpose; this becomes a logged 500, not a quiet 400.
        _ => throw new InvalidOperationException($"Unhandled {nameof(FeedbackOutcome)} value {result.Outcome}."),
    };

    /// <summary>
    /// 201 for a new report, 200 when an open report of the same reason was edited, 429 <c>report-limit</c> with
    /// <c>Retry-After</c> (whole seconds until the oldest counted report leaves the window) over the daily limit.
    /// </summary>
    private static async Task<IResult> PostReportAsync(
        string id,
        [FromBody] ReportRequest? request,
        HttpContext context,
        IQuestionFeedbackService feedback,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (context.User.GetUserId() is not { } user)
        {
            return ApiEndpoints.SignInRequired(context);
        }

        if (request is null
            || !QuestionId.TryCreate(id, out QuestionId question)
            || !CultureKey.TryCreate(request.Culture, out CultureKey culture)
            || !ReportReasonKeys.TryParse(request.Reason, out ReportReason reason)
            || !ReportComment.TryNormalise(request.Comment, out _))
        {
            return ApiEndpoints.Validation();
        }

        ReportResult result;
        try
        {
            result = await feedback.ReportAsync(user, question, culture, reason, request.Comment, ct);
        }
        catch (ArgumentException)
        {
            // The service normalises the comment itself; the check above makes this a backstop for a changed contract.
            return ApiEndpoints.Validation();
        }

        return result.Outcome switch
        {
            ReportOutcome.Created => Results.Json(
                new ReportResponse(ReportResponse.Created), TrainerJsonContext.Default.ReportResponse, statusCode: StatusCodes.Status201Created),
            ReportOutcome.Updated => Results.Json(new ReportResponse(ReportResponse.Updated), TrainerJsonContext.Default.ReportResponse),
            ReportOutcome.QuestionNotFound => ApiEndpoints.Error(StatusCodes.Status404NotFound, ApiErrorCodes.QuestionNotFound),
            ReportOutcome.LimitReached => LimitReached(context, result.RetryAt, clock),
            _ => throw new InvalidOperationException($"Unhandled {nameof(ReportOutcome)} value {result.Outcome}."),
        };
    }

    private static IResult LimitReached(HttpContext context, DateTimeOffset? retryAt, TimeProvider clock)
    {
        // At least one second, so a client never reads 0 as "retry now".
        int seconds = retryAt is { } at ? Math.Max(1, (int)Math.Ceiling((at - clock.GetUtcNow()).TotalSeconds)) : 1;
        context.Response.Headers[HeaderNames.RetryAfter] = seconds.ToString(CultureInfo.InvariantCulture);
        return ApiEndpoints.Error(StatusCodes.Status429TooManyRequests, ApiErrorCodes.ReportLimit);
    }
}
