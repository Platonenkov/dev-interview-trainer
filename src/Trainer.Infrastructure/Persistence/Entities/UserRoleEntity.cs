namespace Trainer.Infrastructure.Persistence.Entities;

internal sealed class UserRoleEntity
{
    public required string UserId { get; set; }
    public required string Role { get; set; }
}
