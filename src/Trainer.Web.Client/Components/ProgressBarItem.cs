namespace Trainer.Web.Client.Components;

/// <summary>One row of <see cref="ProgressBars"/>: a topic or a level.</summary>
/// <param name="Key">The topic or level key (<c>data-key</c>).</param>
/// <param name="Title">What the row is called on screen.</param>
/// <param name="Known">Questions whose last grade was <c>knew</c>.</param>
/// <param name="Live">Questions currently in the catalog.</param>
public sealed record ProgressBarItem(string Key, string Title, int Known, int Live);
