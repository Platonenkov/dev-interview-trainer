namespace Trainer.Contracts;

/// <param name="Value">The rating key: <c>like</c> or <c>dislike</c>. Clearing a rating is a <c>DELETE</c>, not a value.</param>
public sealed record RatingRequest(string Value);

/// <param name="Value">The stored rating key, or null when the question is now unrated.</param>
public sealed record RatingDto(string? Value);

/// <param name="Culture">The culture of the text the user read, so the admin sees which locale was reported.</param>
/// <param name="Reason">A report reason key: <c>wrong-answer</c>, <c>unclear-wording</c>, <c>outdated</c>, <c>broken-link</c> or <c>other</c>.</param>
/// <param name="Comment">Optional free text, at most 1000 characters after normalising.</param>
public sealed record ReportRequest(string Culture, string Reason, string? Comment);

/// <param name="Outcome"><c>created</c> for a new report, <c>updated</c> when an open report of the same reason was edited.</param>
public sealed record ReportResponse(string Outcome)
{
    public const string Created = "created";
    public const string Updated = "updated";
}
