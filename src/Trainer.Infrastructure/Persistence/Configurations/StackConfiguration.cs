using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Trainer.Infrastructure.Persistence.Entities;

namespace Trainer.Infrastructure.Persistence.Configurations;

internal sealed class StackConfiguration : IEntityTypeConfiguration<StackEntity>
{
    public void Configure(EntityTypeBuilder<StackEntity> builder)
    {
        builder.ToTable("stacks");
        builder.HasKey(s => s.Key);
        builder.Property(s => s.RetiredAt).HasColumnType("timestamp with time zone");
    }
}

internal sealed class StackLocaleConfiguration : IEntityTypeConfiguration<StackLocaleEntity>
{
    public void Configure(EntityTypeBuilder<StackLocaleEntity> builder)
    {
        builder.ToTable("stack_locales");
        builder.HasKey(l => new { l.StackKey, l.Culture });
        builder.HasOne<StackEntity>().WithMany().HasForeignKey(l => l.StackKey).OnDelete(DeleteBehavior.Cascade);
    }
}
