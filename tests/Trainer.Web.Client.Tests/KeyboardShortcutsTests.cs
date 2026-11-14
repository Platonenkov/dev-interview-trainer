using Microsoft.Extensions.Time.Testing;
using Microsoft.JSInterop;
using Trainer.Contracts;
using Trainer.Domain.Grading;
using Trainer.Web.Client.Api;
using Trainer.Web.Client.Components;
using Trainer.Web.Client.Sessions;

namespace Trainer.Web.Client.Tests;

public sealed class KeyboardShortcutsTests
{
    private static QuestionAttempt Attempt(QuestionDto question) =>
        new(question, TestQuestions.NewId(), new Random(1), new ServerClock(new FakeTimeProvider(), new FakeBrowserStore()));

    [Fact]
    public void Digits_PickTheNthDisplayedOption()
    {
        QuestionAttempt attempt = Attempt(TestQuestions.Single());

        ShortcutAction action = KeyboardShortcuts.Resolve(ShortcutKey.Option3, attempt, canGoNext: false);

        Assert.Equal(new ShortcutAction(ShortcutCommand.PickOption, 2), action);
        Assert.Equal(ShortcutCommand.None, KeyboardShortcuts.Resolve(ShortcutKey.Option4, Attempt(TestQuestions.OutputChoice()), false).Command);
        Assert.Equal(ShortcutCommand.None, KeyboardShortcuts.Resolve(ShortcutKey.Option1, Attempt(TestQuestions.FindBug()), false).Command);
        Assert.Equal(ShortcutCommand.None, KeyboardShortcuts.Resolve(ShortcutKey.Option1, Attempt(TestQuestions.OutputTyped()), false).Command);

        attempt.Toggle(attempt.Options[0].Id);
        attempt.SubmitAutomatic();
        Assert.Equal(ShortcutCommand.None, KeyboardShortcuts.Resolve(ShortcutKey.Option1, attempt, false).Command);
    }

    [Fact]
    public async Task Digits_GradeSelfAssessmentOnlyAfterReveal()
    {
        QuestionAttempt attempt = Attempt(TestQuestions.Open());

        Assert.Equal(ShortcutCommand.None, KeyboardShortcuts.Resolve(ShortcutKey.Option1, attempt, false).Command);

        attempt.RevealReference();
        Assert.Equal(new ShortcutAction(ShortcutCommand.SelfGrade, Grade: Grade.Knew), KeyboardShortcuts.Resolve(ShortcutKey.Option1, attempt, false));
        Assert.Equal(new ShortcutAction(ShortcutCommand.SelfGrade, Grade: Grade.Partial), KeyboardShortcuts.Resolve(ShortcutKey.Option2, attempt, false));
        Assert.Equal(new ShortcutAction(ShortcutCommand.SelfGrade, Grade: Grade.DidntKnow), KeyboardShortcuts.Resolve(ShortcutKey.Option3, attempt, false));
        Assert.Equal(ShortcutCommand.None, KeyboardShortcuts.Resolve(ShortcutKey.Option4, attempt, false).Command);

        await attempt.SubmitSelfAssessmentAsync(Grade.Knew, new SelfAssessmentGrader(), TestContext.Current.CancellationToken);
        Assert.Equal(ShortcutCommand.None, KeyboardShortcuts.Resolve(ShortcutKey.Option1, attempt, false).Command);
    }

    [Fact]
    public void Enter_SubmitsOnlyWhenSubmittable_AndNGoesNextOnlyWhenAllowed()
    {
        QuestionAttempt attempt = Attempt(TestQuestions.Single());

        Assert.Equal(ShortcutCommand.None, KeyboardShortcuts.Resolve(ShortcutKey.Submit, attempt, false).Command);
        attempt.Toggle("a");
        Assert.Equal(ShortcutCommand.Submit, KeyboardShortcuts.Resolve(ShortcutKey.Submit, attempt, false).Command);

        Assert.Equal(ShortcutCommand.None, KeyboardShortcuts.Resolve(ShortcutKey.Next, attempt, canGoNext: false).Command);
        Assert.Equal(ShortcutCommand.Next, KeyboardShortcuts.Resolve(ShortcutKey.Next, attempt, canGoNext: true).Command);
        Assert.Equal(ShortcutCommand.Next, KeyboardShortcuts.Resolve(ShortcutKey.Next, null, canGoNext: true).Command);
        Assert.Equal(ShortcutCommand.None, KeyboardShortcuts.Resolve(ShortcutKey.Option1, null, canGoNext: true).Command);
    }

    [Theory]
    [InlineData("1", ShortcutKey.Option1)]
    [InlineData("4", ShortcutKey.Option4)]
    [InlineData("enter", ShortcutKey.Submit)]
    [InlineData("next", ShortcutKey.Next)]
    public async Task OnShortcut_RaisesPressedForKnownKeys(string key, ShortcutKey expected)
    {
        FakeJsRuntime js = new();
        await using KeyboardShortcuts shortcuts = new(js);
        List<ShortcutKey> pressed = [];
        shortcuts.Pressed += pressed.Add;

        shortcuts.OnShortcut(key);
        shortcuts.OnShortcut("5");
        shortcuts.OnShortcut("");

        Assert.Equal([expected], pressed);
    }

    [Fact]
    public async Task Start_AttachesOnce_AndDisposeDetaches()
    {
        FakeJsRuntime js = new();
        KeyboardShortcuts shortcuts = new(js);

        await shortcuts.StartAsync();
        await shortcuts.StartAsync();
        await shortcuts.DisposeAsync();

        Assert.Equal(["ditShortcuts.attach", "ditShortcuts.detach"], js.Calls.Select(c => c.Identifier));
        Assert.IsType<DotNetObjectReference<KeyboardShortcuts>>(js.Calls[0].Args![0]);
        Assert.Equal(FakeJsRuntime.WatchHandle, js.Calls[1].Args![0]);
    }

    [Fact]
    public async Task Start_WithoutScript_DoesNotThrow()
    {
        FakeJsRuntime js = new() { Throw = true };
        KeyboardShortcuts shortcuts = new(js);

        await shortcuts.StartAsync();
        await shortcuts.DisposeAsync();

        Assert.Single(js.Calls);
    }
}
