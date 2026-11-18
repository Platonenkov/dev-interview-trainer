using Trainer.Content.Model;
using Trainer.Content.Reading;
using Trainer.Content.Rendering;
using Trainer.Content.Validation;

namespace Trainer.Content.Tests;

public sealed class ContentValidatorTests
{
    private const string Output = "dotnet/csharp/closure-for-loop.yaml";
    private const string Single = "dotnet/async/result-deadlock-ui-thread.yaml";
    private const string Multi = "dotnet/concurrency/thread-safe-collections.yaml";
    private const string FindBug = "dotnet/concurrency/parallel-counter-race.yaml";
    private const string Open = "dotnet/async/configureawait-false-purpose.yaml";
    private const string Situation = "dotnet/concurrency/latency-spikes-thread-pool-starvation.yaml";

    private static readonly Lazy<Task<ContentSet>> ValidSet = new(() => new ContentReader().ReadAsync(Fixtures.Valid, CancellationToken.None));

    [Fact]
    public async Task ValidFixture_HasNoIssues()
    {
        ContentSet set = await ValidSet.Value;

        Assert.Empty(set.ReadIssues);
        Assert.Empty(Validate(set));
    }

    [Fact]
    public async Task Reports_SchemaVersionUnsupported() =>
        await Expect(s => s with { SchemaVersion = 2 }, IssueCodes.SchemaVersionUnsupported, "schema-version");

    [Fact]
    public async Task Reports_IdInvalid() =>
        await Expect(Edit(Single, d => d with { Id = "not-a-ulid" }), IssueCodes.IdInvalid, Single);

    [Fact]
    public async Task Reports_IdDuplicate()
    {
        ContentSet set = await ValidSet.Value;
        string earlierId = set.Questions.Single(q => q.RelativePath == Single).Document.Id;

        // Questions are visited in path order; the later file is the one reported.
        await Expect(Edit(Output, d => d with { Id = earlierId }), IssueCodes.IdDuplicate, Output);
    }

    [Fact]
    public async Task Reports_SlugInvalid() =>
        await Expect(Edit(Single, d => d with { Slug = "Bad Slug" }), IssueCodes.SlugInvalid, Single);

    [Fact]
    public async Task Reports_SlugDuplicate()
    {
        ContentSet set = await ValidSet.Value;
        string earlierSlug = set.Questions.Single(q => q.RelativePath == Single).Document.Slug;

        await Expect(Edit(Output, d => d with { Slug = earlierSlug }), IssueCodes.SlugDuplicate, Output);
    }

    [Fact]
    public async Task Reports_PathMismatch() =>
        await Expect(Edit(Single, d => d with { Topic = "concurrency" }), IssueCodes.PathMismatch, Single);

    [Fact]
    public async Task Reports_StackUnknown() =>
        await Expect(Edit(Single, d => d with { Stack = "java" }), IssueCodes.StackUnknown, Single);

    [Fact]
    public async Task Reports_TopicUnknown() =>
        await Expect(Edit(Single, d => d with { Topic = "nope" }), IssueCodes.TopicUnknown, Single);

    [Fact]
    public async Task Reports_TaxonomyDuplicate_ForStacks() =>
        await Expect(s => s with { Stacks = [.. s.Stacks, s.Stacks[0]] }, IssueCodes.TaxonomyDuplicate, "stacks.yaml");

    [Fact]
    public async Task Reports_StackKeyReserved_ForQ() =>
        await Expect(s => s with { Stacks = [s.Stacks[0] with { Key = "q" }] }, IssueCodes.StackKeyReserved, "stacks.yaml");

    [Fact]
    public async Task StackKeyReserved_NamesTheKeyAndTheRoute()
    {
        ContentSet set = (await ValidSet.Value) with { Stacks = [(await ValidSet.Value).Stacks[0] with { Key = "q" }] };

        ContentIssue issue = Assert.Single(Validate(set), i => i.Code == IssueCodes.StackKeyReserved);

        Assert.Contains("Stack key 'q' is reserved", issue.Message, StringComparison.Ordinal);
        Assert.Equal("[0].key", issue.JsonPath);
    }

    [Fact]
    public async Task Reports_TaxonomyDuplicate_ForTopics() =>
        await Expect(
            s => s with { TopicsByStack = new Dictionary<string, IReadOnlyList<TopicDocument>> { ["dotnet"] = [.. s.TopicsByStack["dotnet"], s.TopicsByStack["dotnet"][0]] } },
            IssueCodes.TaxonomyDuplicate,
            "dotnet/topics.yaml");

    [Fact]
    public async Task Reports_TaxonomyLocaleIncomplete_ForStack() =>
        await Expect(
            s => s with { Stacks = [s.Stacks[0] with { Locales = new Dictionary<string, LocaleTextDocument> { ["en"] = s.Stacks[0].Locales["en"] } }] },
            IssueCodes.TaxonomyLocaleIncomplete,
            "stacks.yaml");

    [Fact]
    public async Task Reports_TaxonomyLocaleIncomplete_ForTopicWithBlankDescription() =>
        await Expect(
            s => s with
            {
                TopicsByStack = new Dictionary<string, IReadOnlyList<TopicDocument>>
                {
                    ["dotnet"] = [s.TopicsByStack["dotnet"][0] with { Locales = new Dictionary<string, LocaleTextDocument> { ["en"] = new() { Title = "T", Description = " " }, ["ru"] = s.TopicsByStack["dotnet"][0].Locales["ru"] } }],
                },
            },
            IssueCodes.TaxonomyLocaleIncomplete,
            "dotnet/topics.yaml");

    [Fact]
    public async Task Reports_LocaleUnsupported_ForQuestion() =>
        await Expect(
            Edit(Open, d => d with { Locales = new Dictionary<string, QuestionLocaleDocument>(d.Locales) { ["de"] = d.Locales["en"] } }),
            IssueCodes.LocaleUnsupported,
            Open);

    [Fact]
    public async Task Reports_LocaleUnsupported_ForStack() =>
        await Expect(
            s => s with { Stacks = [s.Stacks[0] with { Locales = new Dictionary<string, LocaleTextDocument>(s.Stacks[0].Locales) { ["de"] = s.Stacks[0].Locales["en"] } }] },
            IssueCodes.LocaleUnsupported,
            "stacks.yaml");

    [Theory]
    [InlineData("Middle")]
    [InlineData("expert")]
    public async Task Reports_LevelInvalid(string level) =>
        await Expect(Edit(Open, d => d with { Level = level }), IssueCodes.LevelInvalid, Open);

    [Theory]
    [InlineData("Open")]
    [InlineData("essay")]
    public async Task Reports_TypeInvalid(string type) =>
        await Expect(Edit(Open, d => d with { Type = type }), IssueCodes.TypeInvalid, Open);

    [Theory]
    [InlineData("llm")]
    [InlineData("Self")]
    public async Task Reports_GradingInvalid(string grading) =>
        await Expect(Edit(Open, d => d with { Grading = grading }), IssueCodes.GradingInvalid, Open);

    [Fact]
    public async Task Reports_GradingTypeMismatch_ForOpen() =>
        await Expect(Edit(Open, d => d with { Grading = "auto" }), IssueCodes.GradingTypeMismatch, Open);

    [Fact]
    public async Task Reports_GradingTypeMismatch_ForSingle() =>
        await Expect(Edit(Single, d => d with { Grading = "self" }), IssueCodes.GradingTypeMismatch, Single);

    [Fact]
    public async Task Reports_OptionsRequired() =>
        await Expect(Edit(Single, d => d with { Options = [d.Options![0]] }), IssueCodes.OptionsRequired, Single);

    [Fact]
    public async Task Reports_OptionsForbidden() =>
        await Expect(Edit(Open, d => d with { Options = [new() { Id = "a", Correct = true }, new() { Id = "b", Correct = false }] }), IssueCodes.OptionsForbidden, Open);

    [Fact]
    public async Task Reports_OptionsCorrectCount_ForSingle() =>
        await Expect(Edit(Single, d => d with { Options = [.. d.Options!.Select(o => o with { Correct = true })] }), IssueCodes.OptionsCorrectCount, Single);

    [Fact]
    public async Task Reports_OptionsCorrectCount_ForMulti() =>
        await Expect(Edit(Multi, d => d with { Options = [.. d.Options!.Select(o => o with { Correct = false })] }), IssueCodes.OptionsCorrectCount, Multi);

    [Fact]
    public async Task Reports_OptionsCorrectCount_ForOutputWithOptions() =>
        await Expect(Edit(Output, d => d with { Options = [.. d.Options!.Select(o => o with { Correct = false })] }), IssueCodes.OptionsCorrectCount, Output);

    [Fact]
    public async Task Reports_OptionIdDuplicate() =>
        await Expect(Edit(Single, d => d with { Options = [d.Options![0], d.Options[1] with { Id = d.Options[0].Id }, d.Options[2]] }), IssueCodes.OptionIdDuplicate, Single);

    [Fact]
    public async Task Reports_OptionIdDuplicate_ForEmptyId() =>
        await Expect(Edit(Single, d => d with { Options = [d.Options![0] with { Id = "" }, d.Options[1], d.Options[2]] }), IssueCodes.OptionIdDuplicate, Single);

    [Fact]
    public async Task Reports_OutputExpectRequired() =>
        await Expect(Edit(Output, d => d with { Expect = null }), IssueCodes.OutputExpectRequired, Output);

    [Fact]
    public async Task Reports_OutputExpectRequired_ForOptionWithoutExpect() =>
        await Expect(Edit(Output, d => d with { Options = [d.Options![0] with { Expect = null }, d.Options[1], d.Options[2]] }), IssueCodes.OutputExpectRequired, Output);

    [Fact]
    public async Task Reports_OutputAnswerMode() =>
        await Expect(Edit(Output, d => d with { Options = null, AcceptedOutputs = null }), IssueCodes.OutputAnswerMode, Output);

    [Fact]
    public async Task Reports_AcceptedOutputsMismatch() =>
        await Expect(Edit(Output, d => d with { AcceptedOutputs = ["444"] }), IssueCodes.AcceptedOutputsMismatch, Output);

    [Fact]
    public async Task AcceptedOutputs_AreComparedAfterTrimAndLineEndingNormalisation()
    {
        ContentSet set = Edit(Output, d => d with { Expect = new() { Kind = "output", Value = "a\nb" }, AcceptedOutputs = ["  a\r\nb\r\n"] })(await ValidSet.Value);

        Assert.DoesNotContain(Validate(set), i => i.Code == IssueCodes.AcceptedOutputsMismatch);
    }

    [Fact]
    public async Task Reports_ExpectKindInvalid() =>
        await Expect(Edit(Output, d => d with { Expect = new() { Kind = "stdout", Value = "333" } }), IssueCodes.ExpectKindInvalid, Output);

    [Theory]
    [InlineData("output")]
    [InlineData("exception")]
    public async Task Reports_ExpectValueRequired(string kind) =>
        await Expect(Edit(Output, d => d with { Expect = new() { Kind = kind } }), IssueCodes.ExpectValueRequired, Output);

    [Fact]
    public async Task Reports_ExpectValueRequired_ForFixedExpect() =>
        await Expect(Edit(FindBug, d => d with { FixedExpect = new() { Kind = "exception" } }), IssueCodes.ExpectValueRequired, FindBug);

    [Fact]
    public async Task Reports_ExpectValueRequired_ForOptionExpect() =>
        await Expect(Edit(Output, d => d with { Options = [d.Options![0] with { Expect = new() { Kind = "output" } }, d.Options[1], d.Options[2]] }), IssueCodes.ExpectValueRequired, Output);

    [Fact]
    public async Task ExpectKindsWithoutValue_AreAccepted()
    {
        ContentSet set = Edit(Output, d => d with { FixedExpect = new() { Kind = "compile-error" } })(await ValidSet.Value);

        Assert.DoesNotContain(Validate(set), i => i.Code == IssueCodes.ExpectValueRequired);
    }

    [Theory]
    [InlineData(Output)]
    [InlineData(FindBug)]
    public async Task Reports_CodeRequired(string path) =>
        await Expect(Edit(path, d => d with { Code = null, BugLineSets = null }), IssueCodes.CodeRequired, path);

    [Fact]
    public async Task Reports_HarnessInvalid() =>
        await Expect(Edit(Output, d => d with { Code = d.Code! with { Harness = "script" } }), IssueCodes.HarnessInvalid, Output);

    [Fact]
    public async Task Reports_HarnessNoneForbidden() =>
        await Expect(Edit(Output, d => d with { Code = d.Code! with { Harness = "none" } }), IssueCodes.HarnessNoneForbidden, Output);

    [Fact]
    public async Task Reports_HarnessNoneReason() =>
        await Expect(Edit(Single, d => d with { Meta = d.Meta with { NoRunReason = null } }), IssueCodes.HarnessNoneReason, Single);

    [Fact]
    public async Task Reports_BuglinesOutOfRange()
    {
        await Expect(Edit(FindBug, d => d with { BugLineSets = [[7], [0]] }), IssueCodes.BuglinesOutOfRange, FindBug);
        await Expect(Edit(FindBug, d => d with { BugLineSets = [[10]] }), IssueCodes.BuglinesOutOfRange, FindBug);
    }

    [Fact]
    public async Task BugLines_TrailingNewlineDoesNotAddALine()
    {
        ContentSet set = await ValidSet.Value;
        int lines = set.Questions.Single(q => q.RelativePath == FindBug).Document.Code!.Source.TrimEnd('\n').Split('\n').Length;

        ContentSet last = Edit(FindBug, d => d with { BugLineSets = [[lines]] })(set);
        ContentSet beyond = Edit(FindBug, d => d with { BugLineSets = [[lines + 1]] })(set);

        Assert.Empty(Validate(last));
        Assert.Contains(Validate(beyond), i => i.Code == IssueCodes.BuglinesOutOfRange);
    }

    [Fact]
    public async Task Reports_BugLinesRequired()
    {
        await Expect(Edit(FindBug, d => d with { BugLineSets = null }), IssueCodes.BugLinesRequired, FindBug);
        await Expect(Edit(FindBug, d => d with { BugLineSets = [] }), IssueCodes.BugLinesRequired, FindBug);
        await Expect(Edit(FindBug, d => d with { BugLineSets = [[]] }), IssueCodes.BugLinesRequired, FindBug);
    }

    [Fact]
    public async Task Reports_LinksRequired() =>
        await Expect(Edit(Open, d => d with { Links = [] }), IssueCodes.LinksRequired, Open);

    [Fact]
    public async Task Reports_FixedExpectRequired() =>
        await Expect(Edit(FindBug, d => d with { FixedExpect = null }), IssueCodes.FixedExpectRequired, FindBug);

    [Fact]
    public async Task Reports_LocaleIncomplete_ForMissingOptionText() =>
        await Expect(Edit(Single, InRu(l => l with { Options = new Dictionary<string, string> { ["a"] = "x", ["b"] = "y" } })), IssueCodes.LocaleIncomplete, Single);

    [Fact]
    public async Task Reports_LocaleIncomplete_ForMissingOptionExplanation() =>
        await Expect(Edit(Single, InRu(l => l with { OptionExplanations = new Dictionary<string, string> { ["a"] = "x" } })), IssueCodes.LocaleIncomplete, Single);

    [Fact]
    public async Task Reports_LocaleIncomplete_ForLinkTitleCount() =>
        await Expect(Edit(Single, InRu(l => l with { LinkTitles = [] })), IssueCodes.LocaleIncomplete, Single);

    [Fact]
    public async Task Reports_LocaleIncomplete_ForNoLocales() =>
        await Expect(Edit(Open, d => d with { Locales = new Dictionary<string, QuestionLocaleDocument>() }), IssueCodes.LocaleIncomplete, Open);

    [Fact]
    public async Task QuestionInOneLocale_IsValid()
    {
        ContentSet set = Edit(Single, d => d with { Locales = new Dictionary<string, QuestionLocaleDocument> { ["en"] = d.Locales["en"] } })(await ValidSet.Value);

        Assert.Empty(Validate(set));
    }

    [Fact]
    public async Task Reports_LinkKindInvalid() =>
        await Expect(Edit(Open, d => d with { Links = [d.Links[0] with { Kind = "blog" }] }), IssueCodes.LinkKindInvalid, Open);

    [Fact]
    public async Task Reports_LinkLangInvalid() =>
        await Expect(Edit(Open, d => d with { Links = [d.Links[0] with { Lang = "de" }] }), IssueCodes.LinkLangInvalid, Open);

    [Theory]
    [InlineData("http://example.com/a")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/relative/path")]
    public async Task Reports_LinkScheme(string url) =>
        await Expect(Edit(Open, d => d with { Links = [d.Links[0] with { Url = url }] }), IssueCodes.LinkScheme, Open);

    [Theory]
    [InlineData("[x](javascript:alert(1))")]
    [InlineData("![x](http://example.com/i.png)")]
    public async Task Reports_MarkdownLinkScheme(string markdown) =>
        await Expect(Edit(Open, InRu(l => l with { Explanation = markdown })), IssueCodes.MarkdownLinkScheme, Open);

    [Fact]
    public async Task Reports_MarkdownLinkScheme_InOptionText() =>
        await Expect(
            Edit(Single, InRu(l => l with { Options = new Dictionary<string, string>(l.Options!) { ["a"] = "[x](javascript:alert(1))" } })),
            IssueCodes.MarkdownLinkScheme,
            Single);

    private static Func<QuestionLocaleDocument, QuestionLocaleDocument> SetMarkdown(string field, string markdown) => locale => field switch
    {
        "prompt" => locale with { Prompt = markdown },
        "hint" => locale with { Hint = markdown },
        "answer" => locale with { Answer = markdown },
        "explanation" => locale with { Explanation = markdown },
        "options" => locale with { Options = locale.Options!.ToDictionary(p => p.Key, _ => markdown) },
        "optionExplanations" => locale with { OptionExplanations = locale.OptionExplanations!.ToDictionary(p => p.Key, _ => markdown) },
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
    };

    [Theory]
    [InlineData("prompt")]
    [InlineData("hint")]
    [InlineData("answer")]
    [InlineData("explanation")]
    [InlineData("options")]
    [InlineData("optionExplanations")]
    public async Task EveryMarkdownField_RejectsUnsafeLinkTargets(string field)
    {
        string[] unsafeTargets = ["javascript:alert(1)", "data:text/html;base64,AA", "http://example.com/a", "//example.com/a"];
        foreach (string target in unsafeTargets)
        {
            foreach (string markdown in new[] { $"[x]({target})", $"![x]({target})" })
            {
                await Expect(Edit(Single, InRu(SetMarkdown(field, markdown))), IssueCodes.MarkdownLinkScheme, Single);
            }
        }
    }

    [Theory]
    [InlineData("prompt")]
    [InlineData("hint")]
    [InlineData("answer")]
    [InlineData("explanation")]
    [InlineData("options")]
    [InlineData("optionExplanations")]
    public async Task EveryMarkdownField_AcceptsHttpsAndSiteRelativeTargets(string field)
    {
        const string markdown = "[a](https://example.com/a) [b](/ru/dotnet/questions/x) ![c](https://example.com/i.png)";
        ContentSet set = Edit(Single, InRu(SetMarkdown(field, markdown)))(await ValidSet.Value);

        Assert.DoesNotContain(Validate(set), i => i.Code == IssueCodes.MarkdownLinkScheme);
    }

    [Fact]
    public async Task Reports_TagInvalid() =>
        await Expect(Edit(Open, d => d with { Tags = ["Good", "ok-tag"] }), IssueCodes.TagInvalid, Open);

    [Fact]
    public async Task Reports_RedirectTargetUnknown() =>
        await Expect(WithRedirects([new() { From = "async/old", To = "async/missing" }]), IssueCodes.RedirectTargetUnknown, "dotnet/redirects.yaml");

    [Fact]
    public async Task Reports_RedirectCycle()
    {
        Func<ContentSet, ContentSet> mutate = WithRedirects([new() { From = "async/a", To = "async/b" }, new() { From = "async/b", To = "async/a" }]);

        ContentSet set = mutate(await ValidSet.Value);
        IReadOnlyList<ContentIssue> issues = Validate(set);

        Assert.Equal(2, issues.Count(i => i.Code == IssueCodes.RedirectCycle && i.RelativePath == "dotnet/redirects.yaml"));
    }

    [Fact]
    public async Task Reports_RedirectCycle_AcrossTopicsBySlug() =>
        await Expect(WithRedirects([new() { From = "async/old-a", To = "csharp/old-b" }, new() { From = "concurrency/old-b", To = "async/old-a" }]), IssueCodes.RedirectCycle, "dotnet/redirects.yaml");

    [Fact]
    public async Task Reports_RedirectSelf_ForTopicMoveWithSameSlug() =>
        await Expect(WithRedirects([new() { From = "async/configureawait-false-purpose", To = "concurrency/configureawait-false-purpose" }]), IssueCodes.RedirectSelf, "dotnet/redirects.yaml");

    [Fact]
    public async Task Reports_RedirectDuplicate_ForSameSourceSlugInTwoTopics() =>
        await Expect(
            WithRedirects([new() { From = "async/old-name", To = "async/configureawait-false-purpose" }, new() { From = "csharp/old-name", To = "async/result-deadlock-ui-thread" }]),
            IssueCodes.RedirectDuplicate,
            "dotnet/redirects.yaml");

    [Fact]
    public async Task Reports_RedirectFromLive() =>
        await Expect(WithRedirects([new() { From = "async/result-deadlock-ui-thread", To = "async/configureawait-false-purpose" }]), IssueCodes.RedirectFromLive, "dotnet/redirects.yaml");

    [Fact]
    public async Task Redirect_ToLiveQuestionFromRetiredSlug_IsValid()
    {
        ContentSet set = WithRedirects([new() { From = "async/old-name", To = "async/configureawait-false-purpose" }])(await ValidSet.Value);

        Assert.Empty(Validate(set));
    }

    [Fact]
    public async Task Reports_VerifiedInFuture() =>
        await Expect(Edit(Open, d => d with { Meta = d.Meta with { Verified = new DateOnly(2026, 11, 12) } }), IssueCodes.VerifiedInFuture, Open);

    [Fact]
    public async Task VerifiedToday_IsValid()
    {
        ContentSet set = Edit(Open, d => d with { Meta = d.Meta with { Verified = new DateOnly(2026, 11, 11) } })(await ValidSet.Value);

        Assert.Empty(Validate(set));
    }

    [Fact]
    public async Task OpenAndSituationQuestions_NeedNoCodeOrOptions()
    {
        ContentSet set = await ValidSet.Value;

        Assert.All([Open, Situation], path => Assert.Null(set.Questions.Single(q => q.RelativePath == path).Document.Options));
        Assert.Empty(Validate(set));
    }

    private static IReadOnlyList<ContentIssue> Validate(ContentSet set) =>
        new ContentValidator(new ContentRenderer(), new FixedTimeProvider(new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero))).Validate(set);

    private static async Task Expect(Func<ContentSet, ContentSet> mutate, string code, string path)
    {
        ContentSet set = mutate(await ValidSet.Value);

        IReadOnlyList<ContentIssue> issues = Validate(set);

        Assert.Contains(issues, i => i.Code == code && i.RelativePath == path);
    }

    private static Func<ContentSet, ContentSet> Edit(string path, Func<QuestionDocument, QuestionDocument> edit) =>
        set => set with { Questions = [.. set.Questions.Select(q => q.RelativePath == path ? q with { Document = edit(q.Document) } : q)] };

    private static Func<QuestionDocument, QuestionDocument> InRu(Func<QuestionLocaleDocument, QuestionLocaleDocument> edit) =>
        d => d with { Locales = new Dictionary<string, QuestionLocaleDocument>(d.Locales) { ["ru"] = edit(d.Locales["ru"]) } };

    private static Func<ContentSet, ContentSet> WithRedirects(IReadOnlyList<RedirectDocument> redirects) =>
        set => set with { RedirectsByStack = new Dictionary<string, IReadOnlyList<RedirectDocument>> { ["dotnet"] = redirects } };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
