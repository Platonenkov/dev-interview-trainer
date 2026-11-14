namespace Trainer.Infrastructure.Persistence.Entities;

internal sealed class UserEntity
{
    public required string Id { get; set; }
    public required long TelegramUserId { get; set; }
    public string? DisplayName { get; set; }
    public string? Username { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSignInAt { get; set; }
    public bool IsBlocked { get; set; }
    public required string SecurityStamp { get; set; }
    public string? PreferredCulture { get; set; }
    public short UtcOffsetMinutes { get; set; }
}
