using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Trainer.Infrastructure.Persistence.Entities;

namespace Trainer.Infrastructure.Persistence.Configurations;

internal sealed class UserRoleConfiguration : IEntityTypeConfiguration<UserRoleEntity>
{
    public void Configure(EntityTypeBuilder<UserRoleEntity> builder)
    {
        builder.ToTable("user_roles");
        builder.HasKey(r => new { r.UserId, r.Role });
        builder.Property(r => r.UserId).HasColumnType("character(26)");
        builder.Property(r => r.Role).HasColumnType("text");
        builder.HasOne<UserEntity>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
