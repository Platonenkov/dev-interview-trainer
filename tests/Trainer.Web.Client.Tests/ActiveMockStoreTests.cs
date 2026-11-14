using System.Text.Json;
using Trainer.Contracts;
using Trainer.Contracts.Storage;
using Trainer.Web.Client.Storage;

namespace Trainer.Web.Client.Tests;

public sealed class ActiveMockStoreTests
{
    private const string Owner = "4242";
    private const string SessionId = "01KC0000000000000000000009";
    private static readonly DateTimeOffset At = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    private static readonly QuestionDto Question = new(
        "01KBZ3Q8N9V2K3M4P5R6S7T8WA", "closure-for-loop", "csharp", "C#", "junior", "single", "Title",
        "<p>Prompt</p>", null, null, null,
        [new OptionDto("a", "<p>A</p>", true, null), new OptionDto("b", "<p>B</p>", false, "<p>No</p>")],
        null, null, "<p>Answer</p>", "<p>Explanation</p>", [new LinkDto("https://learn.microsoft.com/dotnet", "Docs", "en")]);

    private static MockProgress Progress(long revision, int index = 0) => new(
        MockProgress.CurrentVersion, SessionId, Owner, "dotnet", "en", "middle", 1, true, At, At.AddMinutes(90), "answering", index, revision,
        [new AttemptState("01KC0000000000000000000001", ["b", "a"], ["a"], [], "", "", false, At.AddMinutes(1), false, null, null, null, null, false)],
        null);

    private static void AssertEmpty((MockProgress? Progress, IReadOnlyList<QuestionDto>? Questions) loaded)
    {
        Assert.Null(loaded.Progress);
        Assert.Null(loaded.Questions);
    }

    private static string Json(MockProgress progress) => JsonSerializer.Serialize(progress, TrainerJsonContext.Default.MockProgress);

    [Fact]
    public async Task ActiveMock_QuestionsWrittenOnce_ProgressRoundTrips()
    {
        FakeBrowserStore fake = new();
        ActiveMockStore store = new(fake);

        Assert.Equal(SaveResult.Saved, await store.SaveQuestionsAsync(Owner, SessionId, [Question]));
        Assert.Equal(SaveResult.Saved, await store.SaveProgressAsync(Owner, Progress(1)));
        Assert.Equal(SaveResult.Saved, await store.SaveProgressAsync(Owner, Progress(2, index: 1)));
        Assert.Equal(SaveResult.Saved, await store.SaveProgressAsync(Owner, Progress(3, index: 1)));

        (MockProgress? progress, IReadOnlyList<QuestionDto>? questions) = await new ActiveMockStore(fake).LoadAsync(Owner);

        Assert.NotNull(progress);
        Assert.Equal(Json(Progress(3, index: 1)), Json(progress));
        Assert.NotNull(questions);
        Assert.Equal(Question.Id, Assert.Single(questions).Id);
        Assert.Single(fake.Ops, op => op.Key == StorageKeys.MockQuestions(Owner, SessionId) && op.Op == "put");
        Assert.Equal(3, fake.Ops.Count(op => op.Op == "putIfRevision"));
    }

    [Fact]
    public void AttemptState_WrittenBeforeReferenceRevealedExisted_ReadsItAsFalse()
    {
        // A document saved by an older build has no "referenceRevealed" member.
        const string Old =
            """{"clientAnswerId":"01KC0000000000000000000001","optionOrder":["b","a"],"picked":["a"],"lines":[],"typed":"","notes":"","usedHint":false,"lastInputAt":null,"frozen":false,"grade":null,"answeredAt":null,"selfGrade":null,"assessedAt":null,"skipped":false}""";

        AttemptState? state = JsonSerializer.Deserialize(Old, TrainerJsonContext.Default.AttemptState);

        Assert.NotNull(state);
        Assert.False(state.ReferenceRevealed);
        Assert.Equal(["b", "a"], state.OptionOrder);
    }

    [Fact]
    public async Task ActiveMock_StaleRevision_ReportsOtherWriter()
    {
        FakeBrowserStore fake = new();
        ActiveMockStore firstTab = new(fake);
        ActiveMockStore secondTab = new(fake);
        await firstTab.SaveQuestionsAsync(Owner, SessionId, [Question]);
        Assert.Equal(SaveResult.Saved, await firstTab.SaveProgressAsync(Owner, Progress(1)));

        (MockProgress? loaded, _) = await secondTab.LoadAsync(Owner);
        Assert.Equal(SaveResult.Saved, await secondTab.SaveProgressAsync(Owner, loaded! with { Revision = loaded.Revision + 1, Index = 1 }));

        Assert.Equal(SaveResult.OtherWriter, await firstTab.SaveProgressAsync(Owner, Progress(2)));
        (MockProgress? current, _) = await firstTab.LoadAsync(Owner);
        Assert.Equal(1, current!.Index);
    }

    [Fact]
    public async Task ActiveMock_OtherSessionAtMatchingRevision_ReportsOtherWriter()
    {
        FakeBrowserStore fake = new();
        ActiveMockStore liveTab = new(fake);
        ActiveMockStore staleTab = new(fake);
        await liveTab.SaveQuestionsAsync(Owner, SessionId, [Question]);
        Assert.Equal(SaveResult.Saved, await liveTab.SaveProgressAsync(Owner, Progress(1)));

        MockProgress other = Progress(2, index: 1) with { SessionId = "01KC0000000000000000000010" };
        Assert.Equal(SaveResult.OtherWriter, await staleTab.SaveProgressAsync(Owner, other));

        (MockProgress? current, _) = await liveTab.LoadAsync(Owner);
        Assert.Equal(SessionId, current!.SessionId);
        Assert.Equal(1, current.Revision);
        Assert.Equal(SessionId, Assert.Single(fake.Ops, op => op.Op == "putIfRevision" && op.ExpectedRevision == 0).SessionId);
    }

    [Fact]
    public async Task ActiveMock_Clear_ProgressDeleteFails_KeepsQuestionsAndReportsIt()
    {
        HookStore hook = new(new FakeBrowserStore());
        ActiveMockStore store = new(hook);
        await store.SaveQuestionsAsync(Owner, SessionId, [Question]);
        await store.SaveProgressAsync(Owner, Progress(1));
        hook.Intercept = op => ValueTask.FromResult<StoreResult?>(
            op.Op == "delete" && op.Key == StorageKeys.MockProgress(Owner) ? StoreResult.Failed(StoreErrors.WriteFailed) : null);

        Assert.False(await store.ClearAsync(Owner, SessionId));

        Assert.NotNull(hook.Inner.Raw(StorageKeys.MockProgress(Owner)));
        Assert.NotNull(hook.Inner.Raw(StorageKeys.MockQuestions(Owner, SessionId)));
        Assert.DoesNotContain(hook.Inner.Ops, op => op.Op == "delete" && op.Key == StorageKeys.MockQuestions(Owner, SessionId));

        hook.Intercept = null;
        Assert.True(await store.ClearAsync(Owner, SessionId));
        AssertEmpty(await store.LoadAsync(Owner));
    }

    [Fact]
    public async Task ActiveMock_Clear_DeletesBothKeysOfThatSessionOnly()
    {
        FakeBrowserStore fake = new();
        ActiveMockStore store = new(fake);
        await store.SaveQuestionsAsync(Owner, SessionId, [Question]);
        await store.SaveProgressAsync(Owner, Progress(1));

        await store.ClearAsync(Owner, "01KC0000000000000000000010");
        Assert.NotNull((await store.LoadAsync(Owner)).Progress);

        await store.ClearAsync(Owner, SessionId);
        Assert.Null(fake.Raw(StorageKeys.MockProgress(Owner)));
        Assert.Null(fake.Raw(StorageKeys.MockQuestions(Owner, SessionId)));
        AssertEmpty(await store.LoadAsync(Owner));
    }

    [Fact]
    public async Task ActiveMock_UnknownVersion_IsNotLoadedAndNotOverwritten()
    {
        const string newer = "{\"version\":2,\"sessionId\":\"01KC0000000000000000000009\",\"revision\":7}";
        FakeBrowserStore fake = new();
        fake.SetRaw(StorageKeys.MockProgress(Owner), newer);
        ActiveMockStore store = new(fake);

        AssertEmpty(await store.LoadAsync(Owner));
        Assert.True(store.HasNewerVersion);
        Assert.Equal(SaveResult.Failed, await store.SaveProgressAsync(Owner, Progress(1)));
        Assert.Equal(newer, fake.Raw(StorageKeys.MockProgress(Owner)));
    }

    [Fact]
    public async Task ActiveMock_NonIntegerRevision_IsQuarantinedOnceThenSaved()
    {
        const string broken = "{\"version\":1,\"sessionId\":\"01KC0000000000000000000009\",\"revision\":1.5}";
        FakeBrowserStore fake = new();
        fake.SetRaw(StorageKeys.MockProgress(Owner), broken);
        ActiveMockStore store = new(fake);

        Assert.Equal(SaveResult.Saved, await store.SaveProgressAsync(Owner, Progress(1)));

        string copy = Assert.Single(fake.Keys, k => k.StartsWith(StorageKeys.CorruptPrefix, StringComparison.Ordinal));
        Assert.Equal(broken, fake.Raw(copy));
        Assert.Equal(1, (await store.LoadAsync(Owner)).Progress!.Revision);
        Assert.False(store.HasNewerVersion);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"version\":1,\"sessionId\":\"01KC0000000000000000000009\",\"revision\":1.5}")]
    public async Task ActiveMock_CorruptProgressMidMock_IsQuarantinedAndLaterSavesSucceed(string broken)
    {
        FakeBrowserStore fake = new();
        fake.SetRaw(StorageKeys.MockProgress(Owner), broken);
        ActiveMockStore store = new(fake);

        // The tab already saved revisions 1-3 before the value broke: an unreadable value cannot be another writer.
        Assert.Equal(SaveResult.Saved, await store.SaveProgressAsync(Owner, Progress(4)));
        Assert.Equal(SaveResult.Saved, await store.SaveProgressAsync(Owner, Progress(5)));

        Assert.Single(fake.Keys, k => k.StartsWith(StorageKeys.CorruptPrefix, StringComparison.Ordinal));
        Assert.Equal(5, (await store.LoadAsync(Owner)).Progress!.Revision);
    }

    [Fact]
    public async Task ActiveMock_ProgressDeletedByAnotherTab_StaysOtherWriter()
    {
        FakeBrowserStore fake = new();
        ActiveMockStore store = new(fake);

        // An absent key mid-mock means another tab finished the mock; the save must not bring it back.
        Assert.Equal(SaveResult.OtherWriter, await store.SaveProgressAsync(Owner, Progress(4)));
        Assert.Null(fake.Raw(StorageKeys.MockProgress(Owner)));
    }
}
