namespace Trainer.Contracts;

/// <summary>The token a client sends back in the <paramref name="HeaderName"/> header on every write.</summary>
public sealed record AntiforgeryTokenDto(string RequestToken, string HeaderName);

/// <summary>The body of every non-success <c>/api</c> response; <paramref name="Code"/> is one of <see cref="ApiErrorCodes"/>.</summary>
public sealed record ApiErrorDto(string Code, string? Detail);

/// <summary>Stable machine-readable codes of <see cref="ApiErrorDto.Code"/>.</summary>
public static class ApiErrorCodes
{
    public const string Antiforgery = "antiforgery";
    public const string Validation = "validation";
    public const string BatchTooLarge = "batch-too-large";
    public const string RateLimited = "rate-limited";
    public const string SignInRequired = "sign-in-required";
    public const string Forbidden = "forbidden";
    public const string NotFound = "not-found";
    public const string MethodNotAllowed = "method-not-allowed";
    public const string OwnerMismatch = "owner-mismatch";
    public const string UnsupportedMediaType = "unsupported-media-type";
    public const string PayloadTooLarge = "payload-too-large";
    public const string ServerError = "server-error";
    public const string QuestionNotFound = "question-not-found";
    public const string ReportLimit = "report-limit";

    /// <summary>A static site's content shard is gone: the site was redeployed under the open page.</summary>
    public const string SiteUpdated = "site-updated";
}
