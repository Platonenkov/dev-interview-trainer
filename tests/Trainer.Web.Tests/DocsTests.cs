using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Trainer.Application.Feedback;
using Trainer.Web.Analytics;
using Trainer.Web.Api;
using Trainer.Web.Auth;
using Trainer.Web.Caching;
using Trainer.Web.Client.Storage;
using Trainer.Web.Internal;
using Trainer.Web.OpenGraph;
using Trainer.Web.Security;
using Trainer.Web.StaticSite;
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

    [GeneratedRegex(@"^\| `(?<key>(?:App|TelegramOidc|ConnectionStrings|ContentImport):[A-Za-z:]+)`", RegexOptions.Multiline)]
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

    [GeneratedRegex(@"vars\.(?<name>[A-Z][A-Z0-9_]*)")]
    private static partial Regex PagesVariable();

    [GeneratedRegex(@"^(?:required|optional)=\((?<names>[^)]*)\)", RegexOptions.Multiline)]
    private static partial Regex RenderEnvNames();

    [GeneratedRegex(@"(?:Secrets|Variables)\s*=\s*@\((?<names>[^)]*)\)")]
    private static partial Regex ProductionSettingList();

    [GeneratedRegex(@"`(?<name>[a-z][A-Za-z]*): [^`]*`")]
    private static partial Regex InputMention();

    [GeneratedRegex(@"^\s{6}(?<name>[a-z][A-Za-z]*):\s*$")]
    private static partial Regex WorkflowInputName();

    [GeneratedRegex(@"^\s{8}type:\s*(?<type>\w+)\s*$")]
    private static partial Regex WorkflowInputType();

    [GeneratedRegex(@"^\s{10}-\s*(?<option>\S+)\s*$")]
    private static partial Regex WorkflowInputOption();

    [GeneratedRegex(@"restore\.sh""?\s+(?<mode>--[a-z]+(?:-[a-z]+)*)")]
    private static partial Regex RestoreMode();

    [GeneratedRegex(@"(?<![\w-])-(?<name>[A-Z][A-Za-z]+)\b")]
    private static partial Regex PowerShellSwitch();

    [GeneratedRegex(@"`(?<file>\.[a-z]+(?:-[a-z]+)*-(?:pending|recovery|progress))`")]
    private static partial Regex GuardFileMention();

    [GeneratedRegex(@"dit_guard_check ""\$APP_DIR/(?<file>[^""]+)""")]
    private static partial Regex GuardFile();

    [GeneratedRegex(@"\bdit-[a-z]+\.(?:service|timer)\b")]
    private static partial Regex SystemdUnitMention();

    [GeneratedRegex(@"dit_guard_check ""[^""]+"" ""(?<section>[^""]+)""")]
    private static partial Regex GuardSection();

    [GeneratedRegex(@"""[^""|]+\|(?<section>[A-Z][A-Za-z ]+)""")]
    private static partial Regex OpsCheckSection();

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

    private static readonly string[] DeploymentSections =
    [
        "Overview and topology",
        "Prerequisites",
        "First-time setup",
        "First deployment",
        "Updating host assets",
        "Interrupted restore",
        "Interrupted rotation",
        "Umami upgrade",
        "Configuration reference",
        "Changing a setting",
        "Rotating credentials",
        "Backups and restore",
        "Rebuilding a lost server",
        "Rolling back",
        "Daily ops check",
        "Hardening checklist before the repository goes public",
        "Troubleshooting",
    ];

    private static readonly string[] DocumentedWorkflows = ["deploy-app", "deploy-content", "rotate-db-password"];

    private static string Deployment() => Read("docs", "deployment.md");

    private static string[] SectionTitles(string markdown) =>
        [.. SectionHeading().Matches(markdown).Select(m => m.Groups["title"].Value)];

    /// <summary>
    /// Names listed in a shell array or a PowerShell list, so the documentation is checked against the files that define
    /// the settings and not against a copy of them in this test.
    /// </summary>
    private static string[] SettingNames()
    {
        string renderEnv = Read("deploy", "scripts", "render-env.sh");
        string library = Read("deploy", "bootstrap", "BootstrapLib.psm1");

        string[] fromScript = [.. RenderEnvNames().Matches(renderEnv)
            .SelectMany(m => m.Groups["names"].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))];
        string[] fromBootstrap = [.. ProductionSettingList().Matches(library)
            .SelectMany(m => Regex.Matches(m.Groups["names"].Value, @"'(?<name>[A-Z][A-Z0-9_]*)'").Select(n => n.Groups["name"].Value))];

        return [.. fromScript.Concat(fromBootstrap).Distinct(StringComparer.Ordinal)];
    }

    private sealed record InputSpec(string Type, string[] Options);

    /// <summary>The inputs under <c>workflow_dispatch: inputs:</c> of a workflow file, with their type and choice options.</summary>
    private static Dictionary<string, InputSpec> DispatchInputs(string workflow)
    {
        string[] lines = workflow.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        Dictionary<string, InputSpec> inputs = new(StringComparer.Ordinal);
        int dispatch = Array.FindIndex(lines, line => line.StartsWith("  workflow_dispatch:", StringComparison.Ordinal));
        int start = dispatch < 0 ? -1 : Array.FindIndex(lines, dispatch + 1, line => line.StartsWith("    inputs:", StringComparison.Ordinal));
        if (start < 0)
        {
            return inputs;
        }

        string? name = null;
        string type = "string";
        List<string> options = [];
        for (int i = start + 1; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length > 0 && lines[i].Length - lines[i].TrimStart().Length <= 4)
            {
                break;
            }

            Match input = WorkflowInputName().Match(lines[i]);
            if (input.Success)
            {
                if (name is not null)
                {
                    inputs[name] = new InputSpec(type, [.. options]);
                }

                name = input.Groups["name"].Value;
                type = "string";
                options = [];
                continue;
            }

            Match typeMatch = WorkflowInputType().Match(lines[i]);
            if (typeMatch.Success)
            {
                type = typeMatch.Groups["type"].Value;
            }

            Match option = WorkflowInputOption().Match(lines[i]);
            if (option.Success)
            {
                options.Add(option.Groups["option"].Value);
            }
        }

        if (name is not null)
        {
            inputs[name] = new InputSpec(type, [.. options]);
        }

        return inputs;
    }

    /// <summary>
    /// Every <c>`input: value`</c> span of the documentation is checked against the workflow named closest before it in the
    /// same item: the input must exist there and its value must fit its type (a choice option, <c>true</c> or <c>false</c>).
    /// </summary>
    private static (List<string> Problems, Dictionary<string, int> Mentions) InputProblems(string markdown, IReadOnlyDictionary<string, Dictionary<string, InputSpec>> workflows)
    {
        List<string> problems = [];
        Dictionary<string, int> mentions = workflows.Keys.ToDictionary(workflow => workflow, _ => 0, StringComparer.Ordinal);
        foreach (string item in LogicalItems(markdown))
        {
            (int Index, string Workflow)[] named = [.. workflows.Keys
                .Select(workflow => (Index: item.IndexOf($"`{workflow}`", StringComparison.Ordinal), Workflow: workflow))
                .Where(entry => entry.Index >= 0)
                .OrderBy(entry => entry.Index)];
            if (named.Length == 0)
            {
                continue;
            }

            foreach (Match mention in InputMention().Matches(item))
            {
                string workflow = named.LastOrDefault(entry => entry.Index < mention.Index).Workflow ?? named[0].Workflow;
                string input = mention.Groups["name"].Value;
                string value = mention.Value.Trim('`')[(input.Length + 2)..].Trim();
                if (!workflows[workflow].TryGetValue(input, out InputSpec? spec))
                {
                    problems.Add($"`{input}` is not an input of {workflow}: {item.Trim()}");
                    continue;
                }

                mentions[workflow]++;
                bool valueFits = spec.Type switch
                {
                    "choice" => spec.Options.Contains(value, StringComparer.Ordinal) || value.StartsWith('<'),
                    "boolean" => value is "true" or "false",
                    _ => true,
                };
                if (!valueFits)
                {
                    problems.Add($"`{input}: {value}` is not a valid value for {workflow} ({spec.Type} {string.Join('/', spec.Options)}): {item.Trim()}");
                }
            }
        }

        return (problems, mentions);
    }

    /// <summary>
    /// The options in the case labels of restore.sh's argument parser (the first <c>case "${1:-}" in</c> up to its
    /// <c>esac</c>), so a mode that is only mentioned in a comment or a message is not "accepted".
    /// </summary>
    private static string[] RestoreOptionLabels(string script)
    {
        int start = script.IndexOf("case \"${1:-}\" in", StringComparison.Ordinal);
        if (start < 0)
        {
            return [];
        }

        int end = script.IndexOf("esac", start, StringComparison.Ordinal);
        string parser = script[start..(end < 0 ? script.Length : end)];
        return [.. parser.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Select(line => Regex.Match(line, @"^\s*(?<labels>--[a-z-]+(?:\s*\|\s*--[a-z-]+)*)\)"))
            .Where(match => match.Success)
            .SelectMany(match => match.Groups["labels"].Value.Split('|', StringSplitOptions.TrimEntries))];
    }

    private static string[] BootstrapSwitchMentions(string markdown) =>
        [.. markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Where(line => line.Contains("Bootstrap.ps1", StringComparison.Ordinal) || Regex.IsMatch(line, @"^\s*-[A-Z]") || line.Contains("`-", StringComparison.Ordinal))
            .SelectMany(line => PowerShellSwitch().Matches(line).Select(m => m.Groups["name"].Value))
            .Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// Paragraphs, list items and table rows as single strings, so a sentence wrapped over several lines is one unit
    /// when a workflow name and its inputs are looked for in it.
    /// </summary>
    private static string[] LogicalItems(string markdown)
    {
        List<string> items = [];
        System.Text.StringBuilder current = new();
        foreach (string raw in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            bool startsItem = raw.Trim().Length == 0 || Regex.IsMatch(raw, @"^\s*(\d+\.|-|\||#)");
            if (startsItem && current.Length > 0)
            {
                items.Add(current.ToString());
                current.Clear();
            }

            if (raw.Trim().Length > 0)
            {
                current.Append(raw).Append(' ');
            }
        }

        if (current.Length > 0)
        {
            items.Add(current.ToString());
        }

        return [.. items];
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
        // The content job's steps live in content-checks.yml, which ci.yml calls.
        HashSet<string> workflow = WorkflowCommands(Read(".github", "workflows", "ci.yml") + "\n" + Read(".github", "workflows", "content-checks.yml"));
        string[] commands = DotnetCommands(Read("AGENTS.md"), "dotnet build -warnaserror");

        Assert.Equal(5, commands.Length);
        foreach (string command in commands)
        {
            Assert.True(workflow.Contains(Normalise(command)), $"ci.yml or content-checks.yml must run exactly the AGENTS.md command: {command}");
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
            InternalOptions.SectionName + ":" + nameof(InternalOptions.Port),
            InternalOptions.TokenKey,
        ];
        AddProperties(expected, typeof(AppOptions), AppOptions.SectionName);
        AddProperties(expected, typeof(RateLimitOptions), RateLimitOptions.SectionName);
        AddProperties(expected, typeof(FeedbackOptions), FeedbackOptions.SectionName);
        AddProperties(expected, typeof(OpenGraphOptions), OpenGraphOptions.SectionName);
        AddProperties(expected, typeof(AnalyticsOptions), AnalyticsOptions.SectionName);

        Assert.Equal(expected.Order(StringComparer.Ordinal), documented.Order(StringComparer.Ordinal));

        static void AddProperties(HashSet<string> keys, Type type, string prefix)
        {
            // A get-only property is derived from a key (StaticSiteOptions.IssuesNewUrl), not a key itself.
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite))
            {
                string key = prefix + ":" + property.Name;
                bool isSection = property.PropertyType.IsClass && property.PropertyType != typeof(string);
                if (isSection)
                {
                    foreach (PropertyInfo inner in property.PropertyType.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite))
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
            StorageKeys.LanguageChoice,
            StorageKeys.ServerClock,
            StorageKeys.PracticeFilters,
            StorageKeys.AnonymousMockHistory,
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
    public void Deployment_DocumentsEverySecretAndVariable()
    {
        string[] names = SettingNames();
        string reference = Section(Deployment(), "Configuration reference");

        // The lists are parsed from the files that define them; a parser that stops matching must not turn this into a no-op.
        Assert.True(names.Length >= 25, $"Only {names.Length} setting names were read from render-env.sh and BootstrapLib.psm1.");
        foreach (string name in names)
        {
            Assert.True(
                reference.Contains($"| `{name}` |", StringComparison.Ordinal),
                $"docs/deployment.md must have a row for `{name}` under '## Configuration reference'.");
        }
    }

    [Fact]
    public void Deployment_HasRequiredSections()
    {
        string[] titles = SectionTitles(Deployment());

        int previous = -1;
        foreach (string required in DeploymentSections)
        {
            int index = Array.IndexOf(titles, required);
            Assert.True(index >= 0, $"docs/deployment.md is missing the section '## {required}'.");
            Assert.True(index > previous, $"docs/deployment.md has '## {required}' out of order.");
            previous = index;
        }
    }

    [Fact]
    public void Deployment_HasEverySectionTheScriptsPointTo()
    {
        string[] titles = SectionTitles(Deployment());
        string[] referenced =
        [
            .. GuardSection().Matches(Read("deploy", "remote", "lib.sh")).Select(m => m.Groups["section"].Value),
            .. OpsCheckSection().Matches(Read("deploy", "remote", "ops-check.sh")).Select(m => m.Groups["section"].Value),
            "Umami upgrade",
        ];

        Assert.True(referenced.Length >= 8, "The section names of lib.sh and ops-check.sh were not read.");
        foreach (string section in referenced.Distinct(StringComparer.Ordinal))
        {
            Assert.True(titles.Contains(section), $"The server scripts tell the operator to follow '{section}' in docs/deployment.md, which has no such section.");
        }
    }

    [Fact]
    public void Agents_DescribesDeployDirectory()
    {
        string agents = Read("AGENTS.md");

        Assert.DoesNotContain("Not created yet", agents, StringComparison.Ordinal);
        Assert.DoesNotContain("joins them in Plan 3", agents, StringComparison.Ordinal);
        Assert.Matches(@"(?m)^deploy/ ", agents);
        Assert.Contains("docs/deployment.md", agents, StringComparison.Ordinal);
    }

    [Fact]
    public void Deployment_DocumentedWorkflowInputsExist()
    {
        Dictionary<string, Dictionary<string, InputSpec>> workflows = DocumentedWorkflows.ToDictionary(
            workflow => workflow,
            workflow => DispatchInputs(Read(".github", "workflows", workflow + ".yml")),
            StringComparer.Ordinal);
        foreach (string workflow in DocumentedWorkflows)
        {
            Assert.NotEmpty(workflows[workflow]);
        }

        Assert.Equal(["rotate", "finish"], workflows["rotate-db-password"]["mode"].Options);
        (List<string> problems, Dictionary<string, int> mentions) = InputProblems(Deployment(), workflows);

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        foreach (string workflow in DocumentedWorkflows)
        {
            Assert.True(mentions[workflow] > 0, $"docs/deployment.md must show at least one input of {workflow}.");
        }
    }

    private static readonly Dictionary<string, Dictionary<string, InputSpec>> SampleWorkflows = new(StringComparer.Ordinal)
    {
        ["deploy-app"] = new(StringComparer.Ordinal) { ["ref"] = new("string", []), ["rollback"] = new("boolean", []) },
        ["rotate-db-password"] = new(StringComparer.Ordinal)
        {
            ["role"] = new("choice", ["trainer", "umami"]),
            ["mode"] = new("choice", ["rotate", "finish"]),
        },
    };

    [Theory]
    [InlineData("Run `rotate-db-password` with `role: trainer` and `mode: finish`.", 0)]
    [InlineData("Dispatch `rotate-db-password` with `role: <that role>`, then `deploy-app` with `ref: <sha>` and `rollback: true`.", 0)]
    [InlineData("Run `rotate-db-password` with `mode: finnish`.", 1)]
    [InlineData("Run `deploy-app` with `rolback: true`.", 1)]
    [InlineData("Run `deploy-app` with `mode: finish`.", 1)]
    [InlineData("Run `deploy-app` with `rollback: yes`.", 1)]
    [InlineData("Run `deploy-app`, then `rotate-db-password` with `rollback: true`.", 1)]
    public void InputProblems_ChecksNamesValuesAndTheWorkflowTheyFollow(string markdown, int expectedProblems)
    {
        Assert.Equal(expectedProblems, InputProblems(markdown, SampleWorkflows).Problems.Count);
    }

    [Fact]
    public void Deployment_CommandsMatchTheScripts()
    {
        string deployment = Deployment();
        string restore = Read("deploy", "remote", "restore.sh");
        string bootstrap = Read("deploy", "bootstrap", "Bootstrap.ps1");
        string lib = Read("deploy", "remote", "lib.sh");
        string guards = lib + Read("deploy", "remote", "receive-content.sh") + Read("deploy", "remote", "ops-check.sh");

        string[] modes = [.. RestoreMode().Matches(deployment).Select(m => m.Groups["mode"].Value).Distinct(StringComparer.Ordinal)];
        Assert.Contains("--replace", modes);
        string[] accepted = RestoreOptionLabels(restore);
        Assert.Contains("--replace", accepted);
        foreach (string mode in modes)
        {
            Assert.True(accepted.Contains(mode), $"docs/deployment.md uses restore.sh {mode}, which the option parser of restore.sh does not accept.");
        }

        string parameterBlock = bootstrap[bootstrap.IndexOf("param(", StringComparison.Ordinal)..bootstrap.IndexOf("Set-StrictMode", StringComparison.Ordinal)];
        string[] parameters = [.. Regex.Matches(parameterBlock, @"\]\$(?<name>[A-Za-z]+)").Select(m => m.Groups["name"].Value)];
        string[] switches = BootstrapSwitchMentions(deployment);
        Assert.Contains("RotateDeployKey", switches);
        Assert.Contains("RotateAgeIdentity", switches);
        Assert.Contains("RotateSecret", switches);
        foreach (string name in switches)
        {
            Assert.True(parameters.Contains(name), $"docs/deployment.md passes -{name} to Bootstrap.ps1, which has no such parameter.");
        }

        string[] guardFiles = [.. GuardFile().Matches(lib).Select(m => m.Groups["file"].Value), ".import-pending"];
        Assert.Equal(5, guardFiles.Length);
        foreach (string file in guardFiles)
        {
            Assert.True(deployment.Contains($"`{file}`", StringComparison.Ordinal), $"docs/deployment.md must name the guard file `{file}`.");
        }

        foreach (Match mention in GuardFileMention().Matches(deployment))
        {
            Assert.True(guards.Contains(mention.Groups["file"].Value, StringComparison.Ordinal), $"docs/deployment.md names the guard file {mention.Value}, which no script uses.");
        }

        string[] units = [.. Directory.GetFiles(Path.Combine(Root, "deploy", "systemd")).Select(Path.GetFileName)!];
        Assert.NotEmpty(units);
        MatchCollection unitMentions = SystemdUnitMention().Matches(deployment);
        Assert.NotEmpty(unitMentions);
        foreach (Match mention in unitMentions)
        {
            Assert.True(units.Contains(mention.Value), $"docs/deployment.md names the systemd unit {mention.Value}, which deploy/systemd does not have.");
        }
    }

    [Theory]
    [InlineData("case \"${1:-}\" in\n  --replace)\n    ;;\n  --drop-old | --drop-scratch)\n    ;;\nesac\n", "--drop-scratch", true)]
    [InlineData("case \"${1:-}\" in\n  --replace)\n    ;;\nesac\n# --drop-old is mentioned here only\necho \"--drop-old)\"\n", "--drop-old", false)]
    [InlineData("usage() { echo \"--replace\"; }\n", "--replace", false)]
    public void RestoreOptionLabels_ReadsOnlyTheCaseLabelsOfTheParser(string script, string option, bool expected)
    {
        Assert.Equal(expected, RestoreOptionLabels(script).Contains(option));
    }

    [Theory]
    [InlineData("pwsh deploy/bootstrap/Bootstrap.ps1 -HostName h -RotateDeployKee", "RotateDeployKee")]
    [InlineData("  -AdminTelegramIds 1 -LetsEncryptEmail e [-SshPort 22]", "SshPort")]
    [InlineData("Bootstrap with `-RotateSecret CONTENT_IMPORT_TOKEN`.", "RotateSecret")]
    public void BootstrapSwitchMentions_FindsTheSwitchesOfACommand(string markdown, string expected)
    {
        Assert.Contains(expected, BootstrapSwitchMentions(markdown));
    }

    private static readonly string[] StaticEditionSections =
    [
        "Overview",
        "How the site is built",
        "Deployments and cached pages",
        "Preview locally",
        "Configuration",
        "Moving to a custom domain",
        "Progress in the browser",
        "Content updates",
        "Reports from visitors",
        "Troubleshooting",
        "Spike results",
        "Going public",
        "Acceptance log",
    ];

    private static string StaticEdition() => Read("docs", "static-edition.md");

    [Fact]
    public void StaticEdition_HasRequiredSections()
    {
        string[] titles = SectionTitles(StaticEdition());

        int previous = -1;
        foreach (string required in StaticEditionSections)
        {
            int index = Array.IndexOf(titles, required);
            Assert.True(index >= 0, $"docs/static-edition.md is missing the section '## {required}'.");
            Assert.True(index > previous, $"docs/static-edition.md has '## {required}' out of order.");
            previous = index;

            string body = Section(StaticEdition(), required).Trim();
            Assert.True(body.Length >= 40, $"The section '## {required}' of docs/static-edition.md has no content.");
        }
    }

    [GeneratedRegex(@"^(?:\| |- )`(?<name>[^`]+)`", RegexOptions.Multiline)]
    private static partial Regex DocumentedItem();

    [Fact]
    public void StaticEdition_DocumentsEveryStaticSetting()
    {
        string configuration = Section(StaticEdition(), "Configuration");

        // The keys come from the option classes, so a new static key fails here until it is documented.
        List<string> keys = ["App:Edition", "App:PublicBaseUrl", "App:Analytics:" + nameof(AnalyticsOptions.GoatCounterCode)];
        keys.AddRange(typeof(StaticSiteOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .Select(p => "App:StaticSite:" + p.Name));
        Assert.Contains("App:StaticSite:BrotliRuntime", keys);

        // The repository variables are the ones pages.yml reads.
        IEnumerable<string> variables = PagesVariable().Matches(Read(".github", "workflows", "pages.yml"))
            .Select(m => m.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal);
        string[] expectedVariables = ["GOATCOUNTER_CODE", "GOOGLE_SITE_VERIFICATION", "SITE_URL", "YANDEX_VERIFICATION"];
        Assert.Equal(expectedVariables, variables.Order(StringComparer.Ordinal));

        // A name counts only as the first cell of a table row or the lead of a list item, not as a passing mention.
        HashSet<string> documented = [.. DocumentedItem().Matches(configuration).Select(m => m.Groups["name"].Value)];
        foreach (string name in keys.Concat(variables).Concat(["build.json", "immutable.json"]))
        {
            Assert.True(documented.Contains(name), $"The Configuration section of docs/static-edition.md has no table row or list item for `{name}`.");
        }
    }

    [Fact]
    public void StaticEdition_StatesTheOriginChangeConsequence()
    {
        string section = Whitespace().Replace(Section(StaticEdition(), "Moving to a custom domain"), " ");

        Assert.Contains("`SITE_URL`", section, StringComparison.Ordinal);
        Assert.Contains("Settings, Pages", section, StringComparison.Ordinal);
        Assert.Contains("progress does not move", section, StringComparison.Ordinal);
        Assert.Contains("announce the move", section, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Agents_DescribesTheStaticEdition()
    {
        string agents = Read("AGENTS.md");

        Assert.Matches(@"(?m)^tools/Trainer\.StaticSite/ ", agents);
        Assert.Contains("Trainer.StaticSite.Tests covers", agents, StringComparison.Ordinal);
        Assert.Matches(@"(?m)^docs/ .*static-edition\.md", agents);

        // One marker for each rule the static-edition tasks added: URLs through SitePaths, no cookie, the island backend
        // seam, the meta CSP with hashes, and the Pages workflow.
        string[] rules =
        [
            "`PathBaseTests` fails on a root-relative URL",
            "The static edition (ADR 0022) sets no cookie",
            "`IIslandBackend`",
            "a new inline script on a static page needs its hash there",
            "`pages.yml` has no `schedule`",
        ];
        string flat = Whitespace().Replace(agents, " ");
        foreach (string rule in rules)
        {
            Assert.True(flat.Contains(rule, StringComparison.Ordinal), $"AGENTS.md lost the static-edition rule '{rule}'.");
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
            Path.Combine("docs", "deployment.md"),
            Path.Combine("docs", "static-edition.md"),
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
