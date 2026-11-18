using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Trainer.Domain.Grading;
using Trainer.Domain.Ids;
using Trainer.Domain.Scheduling;
using Trainer.Domain.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Trainer.Web.Client.Api;
using Trainer.Web.Client.Backend;
using Trainer.Web.Client.Components;
using Trainer.Web.Client.Interop;
using Trainer.Web.Client.Islands;
using Trainer.Web.Client.Sessions;
using Trainer.Web.Client.Storage;
using Trainer.Web.Client.Sync;

IslandRoots.Keep();
PageCulture.Apply();
ClientEdition edition = ClientEditionReader.Current();
WebAssemblyHostBuilder builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Services.AddAuthorizationCore();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthenticationStateDeserialization();
// The culture is the page's <html lang>, set once by PageCulture.Apply above (ADR 0013), and never changes at runtime.
builder.Services.AddLocalization();
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
// A struct enum cannot be a DI service; the links carry the edition.
builder.Services.AddSingleton(new IslandLinks(edition));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<UlidGenerator>();
builder.Services.AddSingleton<IAnswerGrader, SelfAssessmentGrader>();
builder.Services.AddSingleton<AnswerFactory>();
builder.Services.AddSingleton<ILocalTimeFormatter, BrowserLocalTimeFormatter>();
// One instance per runtime (one per page): the stores hold this tab's unsaved answers and storage status.
builder.Services.AddScoped<IBrowserStore, BrowserStore>();
builder.Services.AddScoped<OutboxStore>();
builder.Services.AddScoped<SummaryStore>();
// Registered in both editions because StorageNotice reads its status; only the static backend ever writes to it.
builder.Services.AddScoped<LocalMockHistoryStore>();
builder.Services.AddScoped<ActiveMockStore>();
builder.Services.AddScoped<PracticeFiltersStore>();
builder.Services.AddScoped<MockPersistence>();
builder.Services.AddScoped<ServerClock>();
builder.Services.AddScoped<AntiforgeryTokenProvider>();
builder.Services.AddScoped<TrainerApiClient>();
// The seam the islands fetch through. TrainerApiClient stays registered in both editions: the islands bind the page's
// owner on it before they ask the backend for anything, and in the static edition it simply never sends a request.
if (edition == ClientEdition.Static)
{
    // Random.Shared is thread-safe, as the selector and the composer require of a singleton.
    builder.Services.AddSingleton<IReviewScheduler, Sm2ReviewScheduler>();
    builder.Services.AddSingleton(new PracticeSelector(Random.Shared));
    builder.Services.AddSingleton(new MockComposer(Random.Shared));
    builder.Services.AddScoped<StaticContentClient>();
    builder.Services.AddScoped<LocalProgressCache>();
    builder.Services.AddScoped<IIslandBackend, StaticIslandBackend>();
}
else
{
    builder.Services.AddScoped<IIslandBackend, ServerIslandBackend>();
}

builder.Services.AddScoped<OutboxSyncService>();
builder.Services.AddScoped<AccountWatcher>();
builder.Services.AddScoped<AccountNotice>();
builder.Services.AddScoped<AnonymousImport>();
builder.Services.AddScoped<KeyboardShortcuts>();

WebAssemblyHost host = builder.Build();
// host.Services is the scope the components resolve from, so this is the clock every island uses. Loading before the
// first render means an answer given before any /api response still gets the last calibrated offset.
await host.Services.GetRequiredService<ServerClock>().LoadAsync();
await host.RunAsync();
