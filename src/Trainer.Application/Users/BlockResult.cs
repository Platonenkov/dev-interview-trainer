namespace Trainer.Application.Users;

public enum BlockResult
{
    Updated,
    NotFound,

    /// <summary>The user is a configured admin; blocking is refused so the last admin cannot be locked out.</summary>
    ProtectedAdmin,
}
