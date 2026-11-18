// The render tree is the only way to find a button without a browser; the harness is test-only and pinned to this framework version.
#pragma warning disable BL0006
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.JSInterop;
using Trainer.Contracts;
using Trainer.Domain.Grading;
using Trainer.Domain.Ids;
using Trainer.Web.Client.Api;
using Trainer.Web.Client.Backend;
using Trainer.Web.Client.Components;
using Trainer.Web.Client.Interop;
using Trainer.Web.Client.Islands;
using Trainer.Web.Client.Sessions;
using Trainer.Web.Client.Storage;
using Trainer.Web.Client.Sync;

namespace Trainer.Web.Client.Tests;

/// <summary>
/// Runs a real island component against a scripted backend and drives it by clicking buttons found in its render tree,
/// so island logic (retry routing, what a queue is filtered by, disposal) is tested without a browser.
/// </summary>
internal sealed class IslandHarness : IAsyncDisposable
{
    private static readonly DateTimeOffset DeviceStart = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    private readonly ServiceProvider _services;
    private readonly HarnessRenderer _renderer;
    private readonly BoundaryErrors _boundary = new();
    private int _rootId;

    private const string DefaultPage = "http://localhost/en/dotnet/practice";

    public IslandHarness(IIslandBackend backend)
        : this(backend, null, DefaultPage)
    {
    }

    /// <summary>
    /// A harness whose services the test adjusts: registrations made by <paramref name="configure"/> replace the defaults,
    /// so a test can bring its own backend, store and HTTP client. <paramref name="page"/> is the address the island sees.
    /// </summary>
    public IslandHarness(Action<IServiceCollection> configure, string page = DefaultPage)
        : this(null, configure, page)
    {
    }

    private IslandHarness(IIslandBackend? backend, Action<IServiceCollection>? configure, string page)
    {
        // Set here, not in an async method: the culture flows to the test's later awaits only from the synchronous caller.
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        ServiceCollection services = new();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddLocalization();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(DeviceStart));
        services.AddSingleton<IJSRuntime, FakeJsRuntime>();
        services.AddSingleton<ILocalTimeFormatter, NoTimeFormatter>();
        services.AddSingleton<NavigationManager>(new HarnessNavigation(page));
        services.AddSingleton(new FakeHttpHandler().Client());
        services.AddSingleton<UlidGenerator>();
        services.AddSingleton<IAnswerGrader, SelfAssessmentGrader>();
        services.AddSingleton<AnswerFactory>();
        services.AddSingleton(new IslandLinks(ClientEdition.Server));
        if (backend is not null)
        {
            services.AddSingleton(backend);
        }

        services.AddSingleton<IErrorBoundaryLogger>(_boundary);
        services.AddScoped<IBrowserStore, FakeBrowserStore>();
        services.AddScoped<OutboxStore>();
        services.AddScoped<SummaryStore>();
        services.AddScoped<LocalMockHistoryStore>();
        services.AddScoped<ActiveMockStore>();
        services.AddScoped<PracticeFiltersStore>();
        services.AddScoped<MockPersistence>();
        services.AddScoped<ServerClock>();
        services.AddScoped<AntiforgeryTokenProvider>();
        services.AddScoped<TrainerApiClient>();
        services.AddScoped<OutboxSyncService>();
        services.AddScoped<AccountWatcher>();
        services.AddScoped<AccountNotice>();
        services.AddScoped<KeyboardShortcuts>();
        configure?.Invoke(services);
        _services = services.BuildServiceProvider();
        _renderer = new HarnessRenderer(_services);
    }

    public IIslandBackend Backend => _services.GetRequiredService<IIslandBackend>();

    public IServiceProvider Services => _services;

    public OutboxStore Outbox => _services.GetRequiredService<OutboxStore>();

    /// <summary>Exceptions the renderer was told about: unhandled errors of event handlers and renders.</summary>
    public IReadOnlyList<Exception> Errors => [.. _renderer.Errors, .. _boundary.Errors];

    public static string BootstrapJson(MeDto? me = null, StaticSiteDto? site = null) =>
        JsonSerializer.Serialize(
            new IslandBootstrapDto(
                "en",
                "dotnet",
                [new TopicDto("closures", "Closures")],
                ["middle"],
                ["open"],
                new PracticeFiltersDto([], [], [], null, null),
                me,
                site),
            TrainerJsonContext.Default.IslandBootstrapDto);

    public async Task RenderAsync<TIsland>(string bootstrapJson)
        where TIsland : IComponent
    {
        await await StartRenderAsync<TIsland>(bootstrapJson);
        if (Errors.Count > 0)
        {
            throw new InvalidOperationException("The island failed to render.", Errors[0]);
        }
    }

    /// <summary>
    /// Adds the island and returns at once with the task that ends when its start-up is done, so a test can look at (and
    /// click in) an island whose start-up it holds back.
    /// </summary>
    public async Task<Task> StartRenderAsync<TIsland>(string bootstrapJson)
        where TIsland : IComponent
    {
        (int id, Task done) = await _renderer.Dispatcher.InvokeAsync(
            () => _renderer.AddRoot(typeof(TIsland), ParameterView.FromDictionary(new Dictionary<string, object?> { ["BootstrapJson"] = bootstrapJson })));
        _rootId = id;
        return done;
    }

    /// <summary>The value of the first attribute called <paramref name="name"/> in the island's render tree, or null.</summary>
    public Task<string?> AttributeAsync(string name) => _renderer.Dispatcher.InvokeAsync(() => _renderer.AttributeOf(_rootId, name));

    public async Task<bool> IsDisabledAsync(string label) =>
        (await _renderer.Dispatcher.InvokeAsync(() => _renderer.Buttons(_rootId))).First(b => b.Text.Contains(label, StringComparison.Ordinal)).Disabled;

    public Task<string> TextAsync() => _renderer.Dispatcher.InvokeAsync(() => _renderer.TextOf(_rootId));

    public async Task<bool> HasButtonAsync(string label) =>
        (await _renderer.Dispatcher.InvokeAsync(() => _renderer.Buttons(_rootId))).Any(b => b.Text.Contains(label, StringComparison.Ordinal));

    /// <summary>Starts a click on the button whose text contains <paramref name="label"/>; the returned task ends when the handler does.</summary>
    public async Task<Task> StartClickAsync(string label)
    {
        List<(string Text, ulong HandlerId, bool Disabled)> buttons = await _renderer.Dispatcher.InvokeAsync(() => _renderer.Buttons(_rootId));
        ulong id = buttons.First(b => b.Text.Contains(label, StringComparison.Ordinal)).HandlerId;
        return _renderer.Dispatcher.InvokeAsync(() => _renderer.DispatchEventAsync(id, null, new MouseEventArgs()));
    }

    public async Task ClickAsync(string label) => await await StartClickAsync(label);

    /// <summary>Shows the reference, skips the open question on screen and presses Next; the Next handler is returned unfinished.</summary>
    public async Task<Task> SkipToNextAsync()
    {
        await ClickAsync("Show reference answer");
        await ClickAsync("Skip");
        return await StartClickAsync("Next question");
    }

    public ValueTask DisposeAsync() => _services.DisposeAsync();

    /// <summary>Disposes the renderer and with it the island, as a page unload does.</summary>
    public Task DisposeIslandAsync() => _renderer.Dispatcher.InvokeAsync(_renderer.Dispose);

    private sealed class HarnessRenderer : Renderer
    {
        public HarnessRenderer(IServiceProvider services)
            : base(services, NullLoggerFactory.Instance) =>
            // Without a context, FocusAsync on an element reference throws.
            ElementReferenceContext = new WebElementReferenceContext(services.GetRequiredService<IJSRuntime>());

        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

        public List<Exception> Errors { get; } = [];

        public (int Id, Task Done) AddRoot(Type type, ParameterView parameters)
        {
            IComponent component = InstantiateComponent(type);
            int id = AssignRootComponentId(component);
            return (id, RenderRootComponentAsync(id, parameters));
        }

        public string? AttributeOf(int componentId, string name) => FindAttribute(componentId, name, []);

        public string TextOf(int componentId)
        {
            StringBuilder text = new();
            Walk(componentId, frame => text.Append(frame.TextContent).Append(' '), []);
            return text.ToString();
        }

        public List<(string Text, ulong HandlerId, bool Disabled)> Buttons(int componentId)
        {
            List<(string, ulong, bool)> buttons = [];
            CollectButtons(componentId, buttons, []);
            return buttons;
        }

        protected override void HandleException(Exception exception) => Errors.Add(exception);

        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;

        private void Walk(int componentId, Action<RenderTreeFrame> onText, HashSet<int> visited)
        {
            if (!visited.Add(componentId))
            {
                return;
            }

            ArrayRange<RenderTreeFrame> frames = GetCurrentRenderTreeFrames(componentId);
            for (int i = 0; i < frames.Count; i++)
            {
                RenderTreeFrame frame = frames.Array[i];
                if (frame.FrameType == RenderTreeFrameType.Component)
                {
                    Walk(frame.ComponentId, onText, visited);
                }
                else if (frame.FrameType == RenderTreeFrameType.Text)
                {
                    onText(frame);
                }
            }
        }

        private string? FindAttribute(int componentId, string name, HashSet<int> visited)
        {
            if (!visited.Add(componentId))
            {
                return null;
            }

            ArrayRange<RenderTreeFrame> frames = GetCurrentRenderTreeFrames(componentId);
            for (int i = 0; i < frames.Count; i++)
            {
                RenderTreeFrame frame = frames.Array[i];
                if (frame.FrameType == RenderTreeFrameType.Attribute && string.Equals(frame.AttributeName, name, StringComparison.Ordinal))
                {
                    return Convert.ToString(frame.AttributeValue, CultureInfo.InvariantCulture) ?? string.Empty;
                }

                if (frame.FrameType == RenderTreeFrameType.Component && FindAttribute(frame.ComponentId, name, visited) is { } found)
                {
                    return found;
                }
            }

            return null;
        }

        private void CollectButtons(int componentId, List<(string, ulong, bool)> buttons, HashSet<int> visited)
        {
            if (!visited.Add(componentId))
            {
                return;
            }

            ArrayRange<RenderTreeFrame> frames = GetCurrentRenderTreeFrames(componentId);
            for (int i = 0; i < frames.Count; i++)
            {
                RenderTreeFrame frame = frames.Array[i];
                if (frame.FrameType == RenderTreeFrameType.Component)
                {
                    CollectButtons(frame.ComponentId, buttons, visited);
                }
                else if (frame.FrameType == RenderTreeFrameType.Element && string.Equals(frame.ElementName, "button", StringComparison.Ordinal))
                {
                    ulong handler = 0;
                    bool disabled = false;
                    StringBuilder text = new();
                    for (int j = i + 1; j < i + frame.ElementSubtreeLength; j++)
                    {
                        RenderTreeFrame inner = frames.Array[j];
                        if (inner.FrameType == RenderTreeFrameType.Attribute && string.Equals(inner.AttributeName, "onclick", StringComparison.Ordinal))
                        {
                            handler = inner.AttributeEventHandlerId;
                        }
                        else if (inner.FrameType == RenderTreeFrameType.Attribute && string.Equals(inner.AttributeName, "disabled", StringComparison.Ordinal))
                        {
                            disabled = inner.AttributeValue is true;
                        }
                        else if (inner.FrameType == RenderTreeFrameType.Text)
                        {
                            text.Append(inner.TextContent);
                        }
                    }

                    if (handler != 0)
                    {
                        buttons.Add((text.ToString().Trim(), handler, disabled));
                    }
                }
            }
        }
    }

    /// <summary>The island shell's error boundary swallows render errors; recording them keeps them visible to the test.</summary>
    private sealed class BoundaryErrors : IErrorBoundaryLogger
    {
        public List<Exception> Errors { get; } = [];

        public ValueTask LogErrorAsync(Exception exception)
        {
            Errors.Add(exception);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class HarnessNavigation : NavigationManager
    {
        public HarnessNavigation(string page) => Initialize("http://localhost/", page);
    }

    private sealed class NoTimeFormatter : ILocalTimeFormatter
    {
        public string Format(string language, DateTimeOffset value, bool withTime) => value.ToString("O", CultureInfo.InvariantCulture);
    }
}

/// <summary>A backend whose answers a test scripts; every call is counted.</summary>
internal sealed class ScriptedBackend : IIslandBackend
{
    public bool Progress { get; set; }

    public bool FiltersPendingAnswers { get; set; } = true;

    public bool SendsAnsweredAsExclude => false;

    public string Key { get; set; } = StorageKeys.UserOutbox(42);

    public Func<ApiResult<SessionPlan>> Practice { get; set; } = () => new ApiResult<SessionPlan>(null, 0, null);

    public Func<ApiResult<SessionPlan>> Review { get; set; } = () => new ApiResult<SessionPlan>(null, 0, null);

    public int PracticeCalls { get; private set; }

    public int ReviewCalls { get; private set; }

    public bool KeepsProgress(IslandBootstrapDto bootstrap) => Progress;

    public string OutboxKey(IslandBootstrapDto bootstrap) => Key;

    public Task<IslandProgress?> GetProgressAsync(IslandBootstrapDto bootstrap, CancellationToken ct) => Task.FromResult<IslandProgress?>(null);

    public Task<ApiResult<SessionPlan>> PracticeAsync(IslandBootstrapDto bootstrap, PracticeSessionRequest request, CancellationToken ct)
    {
        PracticeCalls++;
        return Task.FromResult(Practice());
    }

    public Task<ApiResult<SessionPlan>> ReviewAsync(IslandBootstrapDto bootstrap, ReviewSessionRequest request, CancellationToken ct)
    {
        ReviewCalls++;
        return Task.FromResult(Review());
    }

    public Task<ApiResult<MockSessionDto>> MockAsync(IslandBootstrapDto bootstrap, MockSessionRequest request, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<ApiResult<ProfileDto>> ProfileAsync(IslandBootstrapDto bootstrap, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task OnSessionFinishedAsync(IslandBootstrapDto bootstrap, CancellationToken ct) => Task.CompletedTask;

    public Task OnMockFinishedAsync(IslandBootstrapDto bootstrap, string mockSessionId, MockSummaryDto summary, CancellationToken ct) => Task.CompletedTask;
}
