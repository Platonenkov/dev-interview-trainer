using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Trainer.Infrastructure.Persistence.Entities;

namespace Trainer.Infrastructure.Persistence.Configurations;

internal sealed class UserConfiguration : IEntityTypeConfiguration<UserEntity>
{
    public void Configure(EntityTypeBuilder<UserEntity> builder)
    {
        builder.ToTable("users");
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).HasColumnType("character(26)");
        builder.Property(u => u.TelegramUserId).HasColumnType("bigint");
        builder.Property(u => u.DisplayName).HasColumnType("text");
        builder.Property(u => u.Username).HasColumnType("text");
        builder.Property(u => u.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(u => u.LastSignInAt).HasColumnType("timestamp with time zone");
        // The defaults matter: the upsert in UserAccountService does not list these columns.
        builder.Property(u => u.IsBlocked).HasDefaultValue(false);
        builder.Property(u => u.SecurityStamp).HasColumnType("character(32)");
        builder.Property(u => u.PreferredCulture).HasColumnType("text");
        builder.Property(u => u.UtcOffsetMinutes).HasColumnType("smallint").HasDefaultValue((short)0);
        builder.HasIndex(u => u.TelegramUserId).IsUnique();
    }
}
