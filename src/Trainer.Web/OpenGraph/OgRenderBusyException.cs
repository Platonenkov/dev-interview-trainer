namespace Trainer.Web.OpenGraph;

/// <summary>Every render slot stayed busy for the whole wait; the endpoint turns this into a 503 the client may retry.</summary>
public sealed class OgRenderBusyException : Exception
{
    public OgRenderBusyException()
        : base("All Open Graph render slots are busy.")
    {
    }
}
