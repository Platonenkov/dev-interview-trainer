using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Trainer.Infrastructure.Persistence.Entities;

namespace Trainer.Infrastructure.Persistence.Configurations;

internal sealed class MockSessionConfiguration : IEntityTypeConfiguration<MockSessionEntity>
{
    public void Configure(EntityTypeBuilder<MockSessionEntity> builder)
    {
        builder.ToTable("mock_sessions");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).HasColumnType("character(26)");
        builder.Property(m => m.UserId).HasColumnType("character(26)");
        builder.Property(m => m.StackKey).HasColumnType("text");
        builder.Property(m => m.Level).HasConversion<short>();
        builder.Property(m => m.StartedAt).HasColumnType("timestamp with time zone");
        builder.Property(m => m.FinishedAt).HasColumnType("timestamp with time zone");
        builder.Property(m => m.Summary).HasColumnType("jsonb");
        builder.HasOne<UserEntity>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
