using System.Security.Claims;
using Trainer.Application.Users;

namespace Trainer.Web.Auth;

/// <summary>The application claims of a session: user id, security stamp and one role claim per role.</summary>
public sealed class UserClaimsFactory
{
    public IReadOnlyList<Claim> Create(UserAccess access)
    {
        ArgumentNullException.ThrowIfNull(access);

        List<Claim> claims = new(2 + access.Roles.Count)
        {
            new Claim(TrainerClaimTypes.UserId, access.UserId.Value),
            new Claim(TrainerClaimTypes.SecurityStamp, access.SecurityStamp),
        };
        foreach (string role in access.Roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        return claims;
    }
}
