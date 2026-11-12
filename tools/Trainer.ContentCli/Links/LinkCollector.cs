using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Trainer.Content.Model;
using Trainer.Content.Reading;
using Trainer.Content.Rendering;

namespace Trainer.ContentCli.Links;

/// <summary>Collects every absolute https URL a question cites, from its <c>links</c> and from the Markdown of both locales.</summary>
internal sealed class LinkCollector
{
    private readonly MarkdownPipeline _pipeline = MarkdownPipelineFactory.Create();

    /// <returns>URL (fragment removed) mapped to the sorted content-relative paths of the questions that cite it.</returns>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Collect(ContentSet set)
    {
        SortedDictionary<string, SortedSet<string>> found = new(StringComparer.Ordinal);
        foreach (SourcedQuestion question in set.Questions)
        {
            foreach (LinkDocument link in question.Document.Links)
            {
                Add(found, link.Url, question.RelativePath);
            }

            foreach (QuestionLocaleDocument locale in question.Document.Locales.Values)
            {
                foreach (string markdown in MarkdownFields(locale))
                {
                    foreach (string url in MarkdownUrls(markdown))
                    {
                        Add(found, url, question.RelativePath);
                    }
                }
            }
        }

        return found.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)[.. pair.Value], StringComparer.Ordinal);
    }

    private static IEnumerable<string> MarkdownFields(QuestionLocaleDocument locale)
    {
        yield return locale.Prompt;
        yield return locale.Answer;
        yield return locale.Explanation;
        if (locale.Hint is not null)
        {
            yield return locale.Hint;
        }

        foreach (string text in locale.Options?.Values ?? [])
        {
            yield return text;
        }

        foreach (string text in locale.OptionExplanations?.Values ?? [])
        {
            yield return text;
        }
    }

    private IEnumerable<string> MarkdownUrls(string markdown)
    {
        MarkdownDocument document = Markdown.Parse(markdown, _pipeline);
        foreach (LinkInline link in document.Descendants<LinkInline>())
        {
            if (link.Url is not null)
            {
                yield return link.Url;
            }
        }

        foreach (AutolinkInline autolink in document.Descendants<AutolinkInline>())
        {
            yield return autolink.Url;
        }
    }

    private static void Add(SortedDictionary<string, SortedSet<string>> found, string url, string path)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return;
        }

        // A fragment never reaches the server, so two links that differ only by it are one request.
        string key = uri.GetLeftPart(UriPartial.Query);
        if (!found.TryGetValue(key, out SortedSet<string>? paths))
        {
            paths = new SortedSet<string>(StringComparer.Ordinal);
            found[key] = paths;
        }

        paths.Add(path);
    }
}
