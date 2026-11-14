using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Trainer.Infrastructure.Persistence.Entities;

namespace Trainer.Infrastructure.Persistence.Configurations;

internal sealed class AnswerConfiguration : IEntityTypeConfiguration<AnswerEntity>
{
    public void Configure(EntityTypeBuilder<AnswerEntity> builder)
    {
        builder.ToTable("answers");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).HasColumnType("character(26)");
        builder.Property(a => a.ClientAnswerId).HasColumnType("character(26)");
        builder.Property(a => a.UserId).HasColumnType("character(26)");
        builder.Property(a => a.QuestionId).HasColumnType("character(26)");
        builder.Property(a => a.Grade).HasConversion<short>();
        builder.Property(a => a.Mode).HasConversion<short>();
        builder.Property(a => a.MockSessionId).HasColumnType("character(26)");
        builder.Property(a => a.AnsweredAt).HasColumnType("timestamp with time zone");
        builder.Property(a => a.ReceivedAt).HasColumnType("timestamp with time zone");
        builder.HasIndex(a => a.ClientAnswerId).IsUnique();
        builder.HasIndex(a => new { a.UserId, a.AnsweredAt });
        // The admin overview counts answers received in the last 24 hours.
        builder.HasIndex(a => a.ReceivedAt);
        builder.HasOne<UserEntity>().WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
        // Questions are retired, never deleted, so the history keeps a valid reference.
        builder.HasOne<QuestionEntity>().WithMany().HasForeignKey(a => a.QuestionId).OnDelete(DeleteBehavior.Restrict);
    }
}
