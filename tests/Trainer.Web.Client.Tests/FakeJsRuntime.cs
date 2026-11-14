using System.Diagnostics.CodeAnalysis;
using Microsoft.JSInterop;

namespace Trainer.Web.Client.Tests;

/// <summary>Records interop calls; <c>ditStore.watchAccount</c> returns handle 7. <see cref="Throw"/> makes every call fail.</summary>
internal sealed class FakeJsRuntime : IJSRuntime
{
    public const int WatchHandle = 7;

    public bool Throw { get; set; }

    /// <summary>What <c>ditStore.claimAccount</c> answers: null when the claim succeeded, else the owner another tab published.</summary>
    public string? ClaimResult { get; set; }

    public List<(string Identifier, object?[]? Args)> Calls { get; } = [];

    public ValueTask<TValue> InvokeAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)] TValue>(
        string identifier, object?[]? args)
    {
        Calls.Add((identifier, args));
        if (Throw)
        {
            throw new JSException("ditStore is not defined");
        }

        if (string.Equals(identifier, "ditStore.claimAccount", StringComparison.Ordinal))
        {
            return ValueTask.FromResult((TValue)(object?)ClaimResult!);
        }

        return ValueTask.FromResult(typeof(TValue) == typeof(int) ? (TValue)(object)WatchHandle : default!);
    }

    public ValueTask<TValue> InvokeAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)] TValue>(
        string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<TValue>(identifier, args);
}
