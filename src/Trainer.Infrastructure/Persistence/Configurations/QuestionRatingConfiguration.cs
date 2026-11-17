using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Trainer.Infrastructure.Persistence.Entities;

namespace Trainer.Infrastructure.Persistence.Configurations;

internal sealed class QuestionRatingConfiguration : IEntityTypeConfiguration<QuestionRatingEntity>
{
    public void Configure(EntityTypeBuilder<QuestionRatingEntity> builder)
    {
        builder.ToTable("question_ratings");
        builder.HasKey(r => new { r.UserId, r.QuestionId });
        builder.Property(r => r.UserId).HasColumnType("character(26)");
        builder.Property(r => r.QuestionId).HasColumnType("character(26)");
        builder.Property(r => r.Value).HasConversion<short>();
        builder.Property(r => r.UpdatedAt).HasColumnType("timestamp with time zone");
        // Per-question totals for the admin page; the primary key already serves lookups by user.
        builder.HasIndex(r => r.QuestionId);
        builder.HasOne<UserEntity>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
        // Questions are retired, never deleted, so a rating keeps a valid reference.
        builder.HasOne<QuestionEntity>().WithMany().HasForeignKey(r => r.QuestionId).OnDelete(DeleteBehavior.Restrict);
    }
}
