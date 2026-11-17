using System.Text.Json;
using System.Text.Json.Serialization;
using Trainer.Contracts.Storage;

namespace Trainer.Contracts;

/// <summary>
/// Source-generated serializer metadata for every wire type and every document the islands keep in <c>localStorage</c>,
/// camelCase like the rest of the web defaults. The server and the trimmed WebAssembly client both serialize through it,
/// so no reflection runs and nothing is trimmed away.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(QuestionDto))]
[JsonSerializable(typeof(OptionDto))]
[JsonSerializable(typeof(LinkDto))]
[JsonSerializable(typeof(PracticeSessionRequest))]
[JsonSerializable(typeof(MockSessionRequest))]
[JsonSerializable(typeof(ReviewSessionRequest))]
[JsonSerializable(typeof(SessionDto))]
[JsonSerializable(typeof(MockSessionDto))]
[JsonSerializable(typeof(AnswerDto))]
[JsonSerializable(typeof(AnswerBatch))]
[JsonSerializable(typeof(AnswerBatchResponse))]
[JsonSerializable(typeof(RejectedAnswerDto))]
[JsonSerializable(typeof(MockSummaryDto))]
[JsonSerializable(typeof(MockTopicResultDto))]
[JsonSerializable(typeof(ProfileDto))]
[JsonSerializable(typeof(TopicProgressDto))]
[JsonSerializable(typeof(LevelProgressDto))]
[JsonSerializable(typeof(MockHistoryDto))]
[JsonSerializable(typeof(AntiforgeryTokenDto))]
[JsonSerializable(typeof(ApiErrorDto))]
[JsonSerializable(typeof(RatingRequest))]
[JsonSerializable(typeof(RatingDto))]
[JsonSerializable(typeof(ReportRequest))]
[JsonSerializable(typeof(ReportResponse))]
[JsonSerializable(typeof(TopicDto))]
[JsonSerializable(typeof(MeDto))]
[JsonSerializable(typeof(PracticeFiltersDto))]
[JsonSerializable(typeof(IslandBootstrapDto))]
[JsonSerializable(typeof(MockProgress))]
[JsonSerializable(typeof(AttemptState))]
[JsonSerializable(typeof(MockCompletionState))]
[JsonSerializable(typeof(MockSummaryList))]
[JsonSerializable(typeof(StoredMockSummary))]
[JsonSerializable(typeof(StoredMockQuestions))]
[JsonSerializable(typeof(StoredPracticeFilters))]
public sealed partial class TrainerJsonContext : JsonSerializerContext;
