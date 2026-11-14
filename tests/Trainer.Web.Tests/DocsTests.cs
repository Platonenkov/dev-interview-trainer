using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Trainer.Web.Api;
using Trainer.Web.Auth;
using Trainer.Web.Caching;
using Trainer.Web.Client.Storage;
using Trainer.Web.Security;
using Trainer.Web.Startup;

namespace Trainer.Web.Tests;

/// <summary>
/// Keeps README.md, AGENTS.md, docs/architecture.md and docs/adr/ in step with the code, CI and each other: a name, a
/// route, a key or a command that changes in one place and not in the other fails here instead of misleading a reader.
/// </summary>
public sealed partial class DocsTests(WebSiteFixture site) : IClassFixture<WebSiteFixture>
{
    private static readonly string Root = FindRepositoryRoot();

    [GeneratedRegex(@"^\| `(?<method>GET|POST|PUT|DELETE|PATCH) (?<path>/api/[^?`\s]*)", RegexOptions.Multiline)]
    private static partial Regex ApiTableRow();

    [GeneratedRegex(@"^# ADR (?<number>\d{4}): \S", RegexOptions.Multiline)]
    private static partial Regex AdrTitle();

    [GeneratedRegex(@"^\| `(?<key>(?:App|TelegramOidc|ConnectionStrings):[A-Za-z:]+)`", RegexOptions.Multiline)]
    private static partial Regex ConfigurationRow();

    [GeneratedRegex(@"^```bash\r?\n(?<body>.*?)^```", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex BashBlock();

    [GeneratedRegex(@"^## (?<title>.+?)\r?$", RegexOptions.Multiline)]
    private static partial Regex SectionHeading();

    [GeneratedRegex(@"^\d+\. \[(?<title>[^\]]+)\]\(#(?<anchor>[^)]+)\)\r?$", RegexOptions.Multiline)]
    private static partial Regex ContentsEntry();

    [GeneratedRegex(@"^  (?<name>[a-z][a-z-]*):\s*$", RegexOptions.Multiline)]
    private static partial Regex WorkflowJob();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DevInterviewTrainer.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("DevInterviewTrainer.slnx was not found above the test output directory.");
    }

    private static string Read(params string[] relative) =>
        File.ReadAllText(Path.Combine([Root, .. relative]));

    private static string Architecture() => Read("docs", "architecture.md");

    private static string[] AdrFiles() =>
        [.. Directory.GetFiles(Path.Combine(Root, "docs", "adr"), "*.md").Select(Path.GetFileName).Order(StringComparer.Ordinal)!];

    // CI adds --no-build and --no-restore after its own build and restore; they do not change what runs.
    private static string Normalise(string command) => string.Join(
        ' ',
        command.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token is not ("--no-build" or "--no-restore")));

    /// <summary>The text between a section heading (<c>## Title</c>) and the next one.</summary>
    private static string Section(string markdown, string title)
    {
        Match heading = Regex.Match(markdown, "^## " + Regex.Escape(title) + @"\r?$", RegexOptions.Multiline);
        Assert.True(heading.Success, $"Missing section '## {title}'.");
        int start = heading.Index + heading.Length;
        Match next = SectionHeading().Match(markdown, start);
        return markdown[start..(next.Success ? next.Index : markdown.Length)];
    }

    /// <summary>
    /// Every command a workflow runs, one per line: single-line <c>run:</c> values and the lines of <c>run: |</c> blocks,
    /// without comments, so a step that is commented out or carries extra flags does not count as the documented one.
    /// </summary>
    private static HashSet<string> WorkflowCommands(string workflow)
    {
        HashSet<string> commands = new(StringComparer.Ordinal);
        string[] lines = workflow.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                trimmed = trimmed[2..].TrimStart();
            }

            if (!trimmed.StartsWith("run:", StringComparison.Ordinal))
            {
                continue;
            }

            string value = trimmed["run:".Length..].Trim();
            if (value is not ("|" or ">"))
            {
                commands.Add(Normalise(value));
                continue;
            }

            int indent = lines[i].Length - lines[i].TrimStart().Length;
            for (i++; i < lines.Length && (lines[i].Trim().Length == 0 || lines[i].Length - lines[i].TrimStart().Length > indent); i++)
            {
                string line = lines[i].Trim();
                if (line.Length > 0 && !line.StartsWith('#'))
                {
                    commands.Add(Normalise(line));
                }
            }

            i--;
        }

        return commands;
    }

    private static string[] DotnetLines(string markdown) =>
        [.. markdown.Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith("dotnet ", StringComparison.Ordinal)).Select(Normalise)];

    private static string[] WorkflowFiles() =>
        [.. Directory.GetFiles(Path.Combine(Root, ".github", "workflows"), "*.yml").Select(Path.GetFileName).Order(StringComparer.Ordinal)!];

    /// <summary>The <c>dotnet</c> commands of the first fenced bash block of a document that contains <paramref name="marker"/>.</summary>
    private static string[] DotnetCommands(string markdown, string marker)
    {
        foreach (Match block in BashBlock().Matches(markdown))
        {
            if (!block.Groups["body"].Value.Contains(marker, StringComparison.Ordinal))
            {
                continue;
            }

            return [.. block.Groups["body"].Value
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => line.StartsWith("dotnet ", StringComparison.Ordinal))];
        }

        throw new InvalidOperationException($"No bash block with '{marker}' was found.");
    }

    private (string Method, string Path)[] ApiEndpoints()
    {
        List<(string, string)> endpoints = [];
        foreach (EndpointDataSource source in site.Factory.Services.GetServices<EndpointDataSource>())
        {
            foreach (RouteEndpoint endpoint in source.Endpoints.OfType<RouteEndpoint>())
            {
                string? pattern = endpoint.RoutePattern.RawText;
                if (pattern is null || !pattern.StartsWith("/api/", StringComparison.Ordinal))
                {
                    continue;
                }

                IReadOnlyList<string>? methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
                foreach (string method in methods ?? [])
                {
                    endpoints.Add((method, pattern));
                }
            }
        }

        return [.. endpoints.Distinct()];
    }

    [Fact]
    public void Architecture_ListsEveryApiEndpoint()
    {
        HashSet<(string, string)> rows = [.. ApiTableRow().Matches(Architecture()).Select(m => (m.Groups["method"].Value, m.Groups["path"].Value))];
        (string Method, string Path)[] endpoints = ApiEndpoints();

        Assert.NotEmpty(endpoints);
        foreach ((string method, string path) in endpoints)
        {
            // A row of the API table, not a mention in passing elsewhere in the file.
            Assert.True(rows.Contains((method, path)), $"docs/architecture.md must list `{method} {path}` in the API table.");
        }
    }

    [Fact]
    public void Architecture_ContentsListsEverySectionInOrder()
    {
        string architecture = Architecture();
        string[] sections = [.. SectionHeading().Matches(architecture).Select(m => m.Groups["title"].Value).Where(t => t != "Contents")];
        Match[] entries = [.. ContentsEntry().Matches(Section(architecture, "Contents")).Cast<Match>()];

        Assert.NotEmpty(sections);
        Assert.Equal(sections, entries.Select(e => e.Groups["title"].Value));
        foreach (Match entry in entries)
        {
            Assert.Equal(Anchor(entry.Groups["title"].Value), entry.Groups["anchor"].Value);
        }

        // GitHub's heading anchors: lower case, punctuation other than hyphens dropped, spaces to hyphens.
        static string Anchor(string title) =>
            new([.. title.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-').Select(c => c == ' ' ? '-' : c)]);
    }

    [Fact]
    public void Architecture_ApiTableListsNoEndpointThatDoesNotExist()
    {
        HashSet<(string, string)> real = [.. ApiEndpoints()];
        MatchCollection rows = ApiTableRow().Matches(Architecture());

        Assert.NotEmpty(rows);
        foreach (Match row in rows)
        {
            string method = row.Groups["method"].Value;
            string path = row.Groups["path"].Value;
            Assert.True(real.Contains((method, path)), $"docs/architecture.md lists `{method} {path}`, which the application does not map.");
        }
    }

    [Fact]
    public void Architecture_ReferencesEveryAdr()
    {
        string architecture = Architecture();
        string[] files = AdrFiles();

        Assert.NotEmpty(files);
        foreach (string file in files)
        {
            Assert.True(
                architecture.Contains($"(adr/{file})", StringComparison.Ordinal),
                $"docs/architecture.md must link docs/adr/{file} from its decision records section.");
        }

        foreach (Match link in Regex.Matches(architecture, @"\(adr/(?<file>[^)]+\.md)\)"))
        {
            Assert.True(files.Contains(link.Groups["file"].Value), $"docs/architecture.md links a missing ADR: {link.Groups["file"].Value}.");
        }
    }

    [Fact]
    public void Adrs_AreNumberedWithoutGapsAndMatchTheirTitles()
    {
        string[] files = AdrFiles();

        for (int i = 0; i < files.Length; i++)
        {
            string expected = (i + 1).ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
            Assert.StartsWith(expected + "-", files[i], StringComparison.Ordinal);

            Match title = AdrTitle().Match(Read("docs", "adr", files[i]));
            Assert.True(title.Success, $"{files[i]} must start with '# ADR {expected}: <title>'.");
            Assert.Equal(expected, title.Groups["number"].Value);
        }
    }

    [Fact]
    public void Adrs_HaveStatusContextDecisionConsequences()
    {
        string[] headings = ["## Status", "## Context", "## Decision", "## Consequences"];
        foreach (string file in AdrFiles())
        {
            string text = Read("docs", "adr", file);
            int previous = -1;
            foreach (string heading in headings)
            {
                int index = text.IndexOf("\n" + heading + "\n", StringComparison.Ordinal);
                if (index < 0)
                {
                    index = text.IndexOf("\n" + heading + "\r\n", StringComparison.Ordinal);
                }

                Assert.True(index > previous, $"{file} must contain '{heading}' after the previous section.");
                previous = index;
            }
        }
    }

    [Fact]
    public void Agents_TestCommandsMatchCi()
    {
        HashSet<string> workflow = WorkflowCommands(Read(".github", "workflows", "ci.yml"));
        string[] commands = DotnetCommands(Read("AGENTS.md"), "dotnet build -warnaserror");

        Assert.Equal(5, commands.Length);
        foreach (string command in commands)
        {
            Assert.True(workflow.Contains(Normalise(command)), $"ci.yml must run exactly the AGENTS.md command: {command}");
        }

        string release = "dotnet test --filter-trait \"Category=Release\" --ignore-exit-code 8";
        Assert.Contains(release, DotnetLines(Read("AGENTS.md")));
        Assert.Contains(release, workflow);
    }

    [Theory]
    [InlineData("      - run: dotnet build -warnaserror --no-restore", "dotnet build -warnaserror")]
    [InlineData("        run: |\n          dotnet restore\n          # dotnet test\n\n          dotnet build -warnaserror\n      - name: next", "dotnet build -warnaserror")]
    public void WorkflowCommands_ReadsRunLinesAndBlocks(string yaml, string expected)
    {
        HashSet<string> commands = WorkflowCommands(yaml);

        Assert.Contains(expected, commands);
        Assert.DoesNotContain("# dotnet test", commands);
        Assert.DoesNotContain("dotnet test", commands);
        Assert.DoesNotContain("- name: next", commands);
    }

    [Theory]
    [InlineData("        run: dotnet build -warnaserror -p:Foo=1")]
    [InlineData("        # run: dotnet build -warnaserror")]
    public void WorkflowCommands_ExtraFlagsOrACommentAreNotTheCommand(string yaml)
    {
        Assert.DoesNotContain("dotnet build -warnaserror", WorkflowCommands(yaml));
    }

    [Fact]
    public void Readme_TestCommandsMatchAgents()
    {
        string[] fromAgents = DotnetCommands(Read("AGENTS.md"), "dotnet build -warnaserror");
        string[] fromReadme = DotnetCommands(Read("README.md"), "dotnet build -warnaserror");

        Assert.Equal(fromAgents.Select(Normalise), fromReadme.Select(Normalise));
    }

    [Fact]
    public void Readme_DocumentsDevBotSetup()
    {
        // Line breaks inside a sentence are formatting, not content.
        string readme = Whitespace().Replace(Read("README.md"), " ");
        string launchSettings = Read("src", "Trainer.Web", "Properties", "launchSettings.json");

        string[] required =
        [
            "@BotFather",
            "Client ID",
            "Client Secret",
            "https://localhost:7180" + TelegramAuth.CallbackPath,
            "dotnet user-secrets set \"TelegramOidc:ClientId\"",
            "dotnet user-secrets set \"TelegramOidc:ClientSecret\"",
            "dotnet user-secrets set \"App:AdminTelegramIds\"",
            "dotnet dev-certs https --trust",
            "--launch-profile https",
            "Never reuse the production bot",
        ];
        foreach (string text in required)
        {
            Assert.True(readme.Contains(text, StringComparison.Ordinal), $"README.md must document the dev bot setup: missing '{text}'.");
        }

        Assert.Contains("https://localhost:7180", launchSettings, StringComparison.Ordinal);
    }

    [Fact]
    public void Architecture_ConfigurationTableMatchesTheOptionClasses()
    {
        HashSet<string> documented = [.. ConfigurationRow().Matches(Architecture()).Select(m => m.Groups["key"].Value)];

        HashSet<string> expected =
        [
            "ConnectionStrings:Trainer",
            "TelegramOidc:ClientId",
            "TelegramOidc:ClientSecret",
            "TelegramOidc:MetadataAddress",
            AdminTelegramIds.Key,
        ];
        AddProperties(expected, typeof(AppOptions), AppOptions.SectionName);
        AddProperties(expected, typeof(RateLimitOptions), RateLimitOptions.SectionName);

        Assert.Equal(expected.Order(StringComparer.Ordinal), documented.Order(StringComparer.Ordinal));

        static void AddProperties(HashSet<string> keys, Type type, string prefix)
        {
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                string key = prefix + ":" + property.Name;
                bool isSection = property.PropertyType.IsClass && property.PropertyType != typeof(string);
                if (isSection)
                {
                    foreach (PropertyInfo inner in property.PropertyType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        keys.Add(key + ":" + inner.Name);
                    }
                }
                else
                {
                    keys.Add(key);
                }
            }
        }
    }

    [Fact]
    public void Architecture_NamesEveryHeaderAndCookie()
    {
        string architecture = Architecture();
        string[] names =
        [
            OwnerCheckFilter.OwnerHeader,
            OwnerCheckFilter.UtcOffsetHeader,
            AntiforgeryNames.Header,
            AuthCookieNames.Session,
            AntiforgeryNames.Cookie,
        ];

        foreach (string name in names)
        {
            Assert.True(architecture.Contains(name, StringComparison.Ordinal), $"docs/architecture.md must mention {name}.");
        }
    }

    [Fact]
    public void Architecture_ListsEveryStorageKey()
    {
        string architecture = Section(Architecture(), "Browser storage");
        string[] keys =
        [
            StorageKeys.AnonymousOutbox,
            StorageKeys.CorruptPrefix,
            StorageKeys.Account,
            StorageKeys.ServerClock,
            StorageKeys.PracticeFilters,
            StorageKeys.UserOutbox(123456).Replace("123456", "{telegramUserId}", StringComparison.Ordinal),
            StorageKeys.PendingSummaries(123456).Replace("123456", "{telegramUserId}", StringComparison.Ordinal),
            StorageKeys.FailedSummaries(123456).Replace("123456", "{telegramUserId}", StringComparison.Ordinal),
            StorageKeys.MockQuestions("{owner}", "{sessionId}"),
            StorageKeys.MockProgress("{owner}"),
        ];

        foreach (string key in keys)
        {
            Assert.True(
                architecture.Contains(key, StringComparison.Ordinal),
                $"docs/architecture.md must list the storage key {key} under '## Browser storage'.");
        }
    }

    [Fact]
    public void Architecture_ListsEveryCiJob()
    {
        string tests = Section(Architecture(), "Tests and CI");
        string[] files = WorkflowFiles();

        Assert.Contains("ci.yml", files);
        Assert.Contains("link-check.yml", files);
        foreach (string file in files)
        {
            string marker = $"`.github/workflows/{file}`";
            int start = tests.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(start >= 0, $"docs/architecture.md must describe {marker} under '## Tests and CI'.");
            int next = tests.IndexOf("`.github/workflows/", start + marker.Length, StringComparison.Ordinal);
            string description = tests[start..(next < 0 ? tests.Length : next)];

            string workflow = Read(".github", "workflows", file);
            string[] jobs = [.. WorkflowJob().Matches(workflow[workflow.IndexOf("\njobs:", StringComparison.Ordinal)..]).Select(m => m.Groups["name"].Value)];
            Assert.NotEmpty(jobs);
            foreach (string job in jobs)
            {
                // ci.yml has a job table; a one-job workflow names its job in its paragraph.
                string expected = jobs.Length > 1 ? $"| `{job}` |" : $"job `{job}`";
                Assert.True(
                    description.Contains(expected, StringComparison.Ordinal),
                    $"docs/architecture.md must describe the job `{job}` of {file} ({expected}).");
            }
        }
    }

    [Fact]
    public void Documentation_UsesNoLongDashes()
    {
        string[] files =
        [
            "README.md",
            "AGENTS.md",
            Path.Combine("docs", "architecture.md"),
            Path.Combine("docs", "performance.md"),
            Path.Combine("docs", "content-guide.md"),
            .. AdrFiles().Select(file => Path.Combine("docs", "adr", file)),
        ];

        foreach (string file in files)
        {
            string text = Read(file);
            Assert.False(text.Contains('—', StringComparison.Ordinal), $"{file} contains an em dash; use a plain hyphen.");
            Assert.False(text.Contains('–', StringComparison.Ordinal), $"{file} contains an en dash; use a plain hyphen.");
        }
    }
}
