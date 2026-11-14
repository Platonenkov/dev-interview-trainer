using Trainer.Web.Client.Storage;

namespace Trainer.Web.Client.Tests;

/// <summary>Lets a test answer, delay or interleave single ops; everything else goes to the fake.</summary>
internal sealed class HookStore(FakeBrowserStore inner) : IBrowserStore
{
    public FakeBrowserStore Inner => inner;

    /// <summary>Returns a result to answer the op without running it, or null to run it.</summary>
    public Func<StoreOp, ValueTask<StoreResult?>>? Intercept { get; set; }

    public ValueTask<string?> ReadAsync(string key) => inner.ReadAsync(key);

    public async ValueTask<StoreResult> MutateAsync(StoreOp op)
    {
        if (Intercept is { } intercept && await intercept(op) is { } answer)
        {
            return answer;
        }

        return await inner.MutateAsync(op);
    }
}
