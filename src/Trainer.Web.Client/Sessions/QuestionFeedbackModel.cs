using Trainer.Contracts;
using Trainer.Web.Client.Api;

namespace Trainer.Web.Client.Sessions;

/// <summary>What the feedback bar's status line says.</summary>
public enum FeedbackStatus
{
    Idle,
    Saving,
    RatingSaved,
    ReportCreated,
    ReportUpdated,
    Failed,

    /// <summary>The daily report limit is reached: an expected condition, not an error.</summary>
    Limit,
}

/// <summary>
/// The state and calls behind <c>QuestionFeedbackBar</c> for one question, so the rules are testable without a browser.
/// A call while another is running is ignored: a double click must not send two writes. A failed write leaves the
/// rating as it was; the user retries by pressing again.
/// </summary>
public sealed class QuestionFeedbackModel(TrainerApiClient api, long owner, string questionId, string culture, string? initialRating)
{
    public string? Rating { get; private set; } = initialRating;

    public FeedbackStatus Status { get; private set; }

    public bool IsSaving => Status == FeedbackStatus.Saving;

    /// <summary>Raised whenever <see cref="Status"/> or <see cref="Rating"/> changes.</summary>
    public event Action? Changed;

    /// <summary>
    /// Presses <paramref name="value"/>: pressing the rating already stored clears it, anything else sets it.
    /// </summary>
    public async Task RateAsync(string value, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (IsSaving)
        {
            return;
        }

        string? target = string.Equals(Rating, value, StringComparison.Ordinal) ? null : value;
        Update(FeedbackStatus.Saving);
        ApiResult<RatingDto> result = await api.SetRatingAsync(owner, questionId, target, ct);
        if (result.IsSuccess && result.Value is { } stored)
        {
            Rating = stored.Value;
            Update(FeedbackStatus.RatingSaved);
        }
        else
        {
            Update(FeedbackStatus.Failed);
        }
    }

    /// <returns>True when the server stored the report (created or updated), so the form can close and reset.</returns>
    public async Task<bool> ReportAsync(string reason, string? comment, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reason);
        if (IsSaving)
        {
            return false;
        }

        Update(FeedbackStatus.Saving);
        string? text = string.IsNullOrWhiteSpace(comment) ? null : comment;
        ApiResult<ReportResponse> result = await api.ReportAsync(owner, questionId, new ReportRequest(culture, reason, text), ct);
        if (result.IsSuccess && result.Value is { } response)
        {
            Update(string.Equals(response.Outcome, ReportResponse.Updated, StringComparison.Ordinal)
                ? FeedbackStatus.ReportUpdated
                : FeedbackStatus.ReportCreated);
            return true;
        }

        bool limit = result.Status == 429 && string.Equals(result.ErrorCode, ApiErrorCodes.ReportLimit, StringComparison.Ordinal);
        Update(limit ? FeedbackStatus.Limit : FeedbackStatus.Failed);
        return false;
    }

    private void Update(FeedbackStatus status)
    {
        Status = status;
        Changed?.Invoke();
    }
}
