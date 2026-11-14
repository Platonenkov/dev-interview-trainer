using Trainer.Web.Client.Api;

namespace Trainer.Web.Security;

/// <summary>The antiforgery cookie and header names, shared by the configuration in <c>Program.cs</c> and the docs tests.</summary>
public static class AntiforgeryNames
{
    /// <summary>Set only on island and admin pages and by <c>GET /api/antiforgery</c>; public pages never carry it.</summary>
    public const string Cookie = ".dit.af";

    /// <summary>The header the islands send the request token in; the WebAssembly client owns the name.</summary>
    public const string Header = TrainerApiClient.TokenHeader;
}
