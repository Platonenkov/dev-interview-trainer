using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Trainer.Infrastructure.Persistence.Entities;

namespace Trainer.Infrastructure.Persistence.Configurations;

internal sealed class ReviewStateConfiguration : IEntityTypeConfiguration<ReviewStateEntity>
{
    public void Configure(EntityTypeBuilder<ReviewStateEntity> builder)
    {
        builder.ToTable("review_states");
        builder.HasKey(r => new { r.UserId, r.QuestionId });
        builder.Property(r => r.UserId).HasColumnType("character(26)");
        builder.Property(r => r.QuestionId).HasColumnType("character(26)");
        builder.Property(r => r.DueAt).HasColumnType("timestamp with time zone");
        builder.Property(r => r.Ease).HasColumnType("numeric(4,2)");
        builder.Property(r => r.LastGrade).HasConversion<short>();
        builder.Property(r => r.LastAnsweredAt).HasColumnType("timestamp with time zone");
        // The system column is named explicitly so the snake_case convention cannot turn it into a real "version" column.
        builder.Property(r => r.Version).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
        builder.HasIndex(r => new { r.UserId, r.DueAt });
        builder.HasOne<UserEntity>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<QuestionEntity>().WithMany().HasForeignKey(r => r.QuestionId).OnDelete(DeleteBehavior.Restrict);
    }
}
