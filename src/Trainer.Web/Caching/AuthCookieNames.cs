namespace Trainer.Web.Caching;

/// <summary>Names of the cookies that make a request personal; the output cache never serves or stores such requests.</summary>
public static class AuthCookieNames
{
    /// <summary>The authentication session cookie issued after sign-in (Plan 2).</summary>
    public const string Session = ".dit.session";
}
