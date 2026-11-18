using Microsoft.AspNetCore.Authorization;
using Trainer.Web.Startup;

namespace Trainer.Web.Auth;

/// <summary>Authorization policies of the island pages that show a visitor's own progress.</summary>
public static class IslandPolicies
{
    /// <summary>
    /// The Review and profile pages. The server edition keeps progress in the account, so it needs a signed-in user.
    /// The static edition has no accounts and keeps progress in the browser, so the pages are open to everyone.
    /// </summary>
    public const string Progress = "progress";

    public static AuthorizationBuilder AddProgressPolicy(this AuthorizationBuilder builder, SiteEdition edition)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddPolicy(Progress, policy =>
        {
            if (edition == SiteEdition.Static)
            {
                policy.RequireAssertion(_ => true);
            }
            else
            {
                policy.RequireAuthenticatedUser();
            }
        });
    }
}
