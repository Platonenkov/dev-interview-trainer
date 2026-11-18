using Microsoft.Extensions.Time.Testing;
using Trainer.Contracts;
using Trainer.Domain.Grading;
using Trainer.Domain.Ids;
using Trainer.Web.Client.Api;
using Trainer.Web.Client.Components;
using Trainer.Web.Client.Sessions;
using Trainer.Web.Client.Storage;

namespace Trainer.Web.Client.Tests;

/// <summary>The answering protocol Practice and Review share: outbox first, bounded by <see cref="SlowStorage"/>.</summary>
public sealed class AnsweringFlowTests
{
    private static readonly DateTimeOffset DeviceStart = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Harness
    {
        public Harness(Func<AnswerDto, Task>? write = null)
        {
            Clock = new ServerClock(Device, new FakeBrowserStore());
            Flow = new AnsweringFlow(
                new SlowStorage(Device),
                new SelfAssessmentGrader(),
                attempt => new AnswerFactory().Create(attempt, AnswerMode.Review, null),
                write ?? (answer =>
                {
                    Written.Add((answer, Flow!.IsSaving));
                    return Task.CompletedTask;
                }),
                () => AfterSave++);
        }

        public FakeTimeProvider Device { get; } = new(DeviceStart);

        public ServerClock Clock { get; }

        public AnsweringFlow Flow { get; }

        public List<(AnswerDto Answer, bool WhileSaving)> Written { get; } = [];

        public int AfterSave { get; set; }

        public QuestionAttempt Attempt(QuestionDto question) => new(question, new UlidGenerator(Device).Next(), new Random(1), Clock);
    }

    [Fact]
    public async Task Submit_WritesTheAnswerWhileSaving_ThenCountsItAndAsksForFocus()
    {
        Harness h = new();
        QuestionAttempt attempt = h.Attempt(TestQuestions.Single(correct: "a"));
        attempt.Toggle("a");

        await h.Flow.SubmitAsync(attempt);

        (AnswerDto answer, bool whileSaving) = Assert.Single(h.Written);
        Assert.True(whileSaving);
        Assert.Equal("review", answer.Mode);
        Assert.Equal("knew", answer.Grade);
        Assert.False(h.Flow.IsSaving);
        Assert.Equal(1, h.Flow.Saved);
        Assert.Equal(1, h.AfterSave);
        Assert.True(h.Flow.TakeFocusNext());
        Assert.False(h.Flow.TakeFocusNext());
    }

    [Fact]
    public async Task Submit_WithoutResponseOrTwice_DoesNothing()
    {
        Harness h = new();
        QuestionAttempt attempt = h.Attempt(TestQuestions.Single(correct: "a"));

        await h.Flow.SubmitAsync(attempt);
        await h.Flow.SubmitAsync(null);
        Assert.Empty(h.Written);

        attempt.Toggle("b");
        await h.Flow.SubmitAsync(attempt);
        await h.Flow.SubmitAsync(attempt);
        Assert.Single(h.Written);
    }

    [Fact]
    public async Task Submit_WriteNeverReturns_CarriesOnAfterTheBound()
    {
        TaskCompletionSource never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Harness h = new(_ => never.Task);
        QuestionAttempt attempt = h.Attempt(TestQuestions.Single(correct: "a"));
        attempt.Toggle("a");

        Task submitting = h.Flow.SubmitAsync(attempt);
        Assert.True(h.Flow.IsSaving);
        h.Device.Advance(SlowStorage.WriteTimeout);
        await submitting.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.False(h.Flow.IsSaving);
        Assert.Equal(1, h.Flow.Saved);
        Assert.True(h.Flow.IsSlow);
        Assert.True(h.Flow.CanGoNext(attempt));
    }

    [Fact]
    public async Task SelfGrade_AfterReveal_WritesTheGrade_SkipWritesNothing()
    {
        Harness h = new();
        QuestionAttempt open = h.Attempt(TestQuestions.Open());
        QuestionAttempt skipped = h.Attempt(TestQuestions.Open());

        await h.Flow.SelfGradeAsync(open, Grade.Partial, Ct);
        Assert.Empty(h.Written);

        open.RevealReference();
        await h.Flow.SelfGradeAsync(open, Grade.Partial, Ct);
        Assert.Equal("partial", Assert.Single(h.Written).Answer.Grade);

        skipped.RevealReference();
        h.Flow.TakeFocusNext();
        h.Flow.Skip(skipped);
        Assert.True(skipped.Skipped);
        Assert.Single(h.Written);
        Assert.True(h.Flow.TakeFocusNext());
    }

    [Fact]
    public async Task Shortcuts_PickSubmitAndNext()
    {
        Harness h = new();
        QuestionAttempt attempt = h.Attempt(TestQuestions.Single(correct: "a"));
        int nexts = 0;

        Assert.False(await h.Flow.HandleShortcutAsync(ShortcutKey.Next, attempt, () => { nexts++; return Task.CompletedTask; }, Ct));
        Assert.True(await h.Flow.HandleShortcutAsync(ShortcutKey.Option1, attempt, () => { nexts++; return Task.CompletedTask; }, Ct));
        Assert.Equal(attempt.Options[0].Id, Assert.Single(attempt.Picked));
        Assert.True(await h.Flow.HandleShortcutAsync(ShortcutKey.Submit, attempt, () => { nexts++; return Task.CompletedTask; }, Ct));
        Assert.Single(h.Written);
        Assert.True(await h.Flow.HandleShortcutAsync(ShortcutKey.Next, attempt, () => { nexts++; return Task.CompletedTask; }, Ct));
        Assert.Equal(1, nexts);
    }

    [Fact]
    public async Task Shortcuts_WhileSaving_AreIgnored()
    {
        TaskCompletionSource write = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Harness h = new(_ => write.Task);
        QuestionAttempt attempt = h.Attempt(TestQuestions.Single(correct: "a"));
        attempt.Toggle("a");
        int nexts = 0;

        Task submitting = h.Flow.SubmitAsync(attempt);
        Assert.False(await h.Flow.HandleShortcutAsync(ShortcutKey.Next, attempt, () => { nexts++; return Task.CompletedTask; }, Ct));
        Assert.False(h.Flow.CanGoNext(attempt));
        write.SetResult();
        await submitting;

        Assert.Equal(0, nexts);
        Assert.True(h.Flow.CanGoNext(attempt));
    }
}
