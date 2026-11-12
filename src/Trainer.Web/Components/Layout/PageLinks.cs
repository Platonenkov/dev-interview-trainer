using Trainer.Domain.Keys;

namespace Trainer.Web.Components.Layout;

/// <summary>
/// Per-request record of the cultures the current page exists in and its canonical path in each. The language
/// switcher renders before the page body, so it subscribes to <see cref="Changed"/> instead of reading once.
/// Links come from here, never from the raw request: the output cache ignores junk query strings and path casing,
/// so anything copied from the request would be baked into HTML served to every visitor.
/// </summary>
public sealed class PageLinks
{
    private IReadOnlyList<CultureKey> _cultures = [];
    private Func<CultureKey, string>? _pathFor;

    public event Action? Changed;

    public bool IsSet => _pathFor is not null;

    public void Set(IReadOnlyList<CultureKey> cultures, Func<CultureKey, string> pathFor)
    {
        ArgumentNullException.ThrowIfNull(cultures);
        ArgumentNullException.ThrowIfNull(pathFor);

        _cultures = cultures;
        _pathFor = pathFor;
        Changed?.Invoke();
    }

    /// <summary>The canonical path in <paramref name="culture"/>, or <see langword="null"/> when the page does not exist in it.</summary>
    public string? PathFor(CultureKey culture) =>
        _pathFor is not null && _cultures.Contains(culture) ? _pathFor(culture) : null;
}
