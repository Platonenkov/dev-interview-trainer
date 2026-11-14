using Microsoft.JSInterop;
using Trainer.Domain.Grading;
using Trainer.Web.Client.Sessions;

namespace Trainer.Web.Client.Components;

/// <summary>A key <c>shortcuts.js</c> passes on (spec 9.1: 1-4 to pick, Enter to submit, N for next).</summary>
public enum ShortcutKey
{
    Option1,
    Option2,
    Option3,
    Option4,
    Submit,
    Next,
}

public enum ShortcutCommand
{
    None,
    PickOption,
    SelfGrade,
    Submit,
    Next,
}

/// <param name="OptionIndex">The zero-based displayed option for <see cref="ShortcutCommand.PickOption"/>.</param>
/// <param name="Grade">The chosen grade for <see cref="ShortcutCommand.SelfGrade"/>.</param>
public readonly record struct ShortcutAction(ShortcutCommand Command, int OptionIndex = -1, Grade Grade = default);

/// <summary>
/// The island side of <c>shortcuts.js</c>. The script already drops what must stay native: keys with Ctrl, Alt or Meta,
/// every key typed into a text field, textarea or contenteditable, and Enter on a button, link or summary (their own
/// activation wins). What arrives here is raised as <see cref="Pressed"/>; <see cref="Resolve"/> decides what it means
/// for the attempt on screen.
/// </summary>
public sealed class KeyboardShortcuts(IJSRuntime js) : IAsyncDisposable
{
    private DotNetObjectReference<KeyboardShortcuts>? _reference;
    private int? _handle;

    public event Action<ShortcutKey>? Pressed;

    public async ValueTask StartAsync()
    {
        if (_reference is not null)
        {
            return;
        }

        _reference = DotNetObjectReference.Create(this);
        try
        {
            _handle = await js.InvokeAsync<int>("ditShortcuts.attach", _reference);
        }
        catch (JSException)
        {
            // Without the script the page still works by pointer and Tab; shortcuts are an addition.
        }
    }

    /// <param name="key"><c>1</c>-<c>4</c>, <c>enter</c> or <c>next</c>; anything else is ignored.</param>
    [JSInvokable]
    public void OnShortcut(string key)
    {
        ShortcutKey? parsed = key switch
        {
            "1" => ShortcutKey.Option1,
            "2" => ShortcutKey.Option2,
            "3" => ShortcutKey.Option3,
            "4" => ShortcutKey.Option4,
            "enter" => ShortcutKey.Submit,
            "next" => ShortcutKey.Next,
            _ => null,
        };

        if (parsed is { } shortcut)
        {
            Pressed?.Invoke(shortcut);
        }
    }

    /// <summary>
    /// 1-4 pick the n-th displayed option of an unfrozen choice question (the attempt toggles for multi); during a
    /// self-assessment that can take a grade (<see cref="QuestionAttempt.CanSelfAssess"/>: the reference is shown and no
    /// grade is with the grader), 1, 2 and 3 choose knew, partially and didn't know. Enter submits when
    /// <see cref="QuestionAttempt.CanSubmit"/>; N goes next when <paramref name="canGoNext"/>.
    /// </summary>
    public static ShortcutAction Resolve(ShortcutKey key, QuestionAttempt? attempt, bool canGoNext)
    {
        if (key == ShortcutKey.Next)
        {
            return canGoNext ? new ShortcutAction(ShortcutCommand.Next) : default;
        }

        if (attempt is null)
        {
            return default;
        }

        if (key == ShortcutKey.Submit)
        {
            return attempt.CanSubmit ? new ShortcutAction(ShortcutCommand.Submit) : default;
        }

        int index = key - ShortcutKey.Option1;
        if (attempt.Kind.IsChoice())
        {
            return !attempt.IsFrozen && index < attempt.Options.Count ? new ShortcutAction(ShortcutCommand.PickOption, index) : default;
        }

        if (attempt.CanSelfAssess)
        {
            return index switch
            {
                0 => new ShortcutAction(ShortcutCommand.SelfGrade, Grade: Grade.Knew),
                1 => new ShortcutAction(ShortcutCommand.SelfGrade, Grade: Grade.Partial),
                2 => new ShortcutAction(ShortcutCommand.SelfGrade, Grade: Grade.DidntKnow),
                _ => default,
            };
        }

        return default;
    }

    public async ValueTask DisposeAsync()
    {
        if (_handle is { } handle)
        {
            try
            {
                await js.InvokeVoidAsync("ditShortcuts.detach", handle);
            }
            catch (JSDisconnectedException)
            {
                // The page is going away; its listener goes with it.
            }
            catch (JSException)
            {
                // Nothing left to remove.
            }
        }

        _reference?.Dispose();
    }
}
