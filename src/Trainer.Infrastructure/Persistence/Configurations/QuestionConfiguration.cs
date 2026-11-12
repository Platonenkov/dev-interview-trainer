using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Trainer.Infrastructure.Persistence.Entities;

namespace Trainer.Infrastructure.Persistence.Configurations;

internal sealed class QuestionConfiguration : IEntityTypeConfiguration<QuestionEntity>
{
    public void Configure(EntityTypeBuilder<QuestionEntity> builder)
    {
        builder.ToTable("questions");
        builder.HasKey(q => q.Id);
        builder.Property(q => q.Id).HasColumnType("character(26)");
        builder.Property(q => q.Level).HasConversion<short>();
        builder.Property(q => q.Type).HasConversion<short>();
        builder.Property(q => q.Grading).HasConversion<short>();
        builder.Property(q => q.Tags).HasColumnType("text[]");
        builder.Property(q => q.Payload).HasColumnType("jsonb");
        builder.Property(q => q.Verified).HasColumnType("date");
        builder.Property(q => q.RetiredAt).HasColumnType("timestamp with time zone");
        builder.HasIndex(q => new { q.StackKey, q.Slug }).IsUnique();
        builder.HasOne<TopicEntity>().WithMany().HasForeignKey(q => new { q.StackKey, q.TopicKey }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class QuestionLocaleConfiguration : IEntityTypeConfiguration<QuestionLocaleEntity>
{
    public void Configure(EntityTypeBuilder<QuestionLocaleEntity> builder)
    {
        builder.ToTable("question_locales");
        builder.HasKey(l => new { l.QuestionId, l.Culture });
        builder.Property(l => l.QuestionId).HasColumnType("character(26)");
        builder.Property(l => l.Options).HasColumnType("jsonb");
        builder.Property(l => l.Links).HasColumnType("jsonb");
        builder.HasOne<QuestionEntity>().WithMany().HasForeignKey(l => l.QuestionId).OnDelete(DeleteBehavior.Cascade);
    }
}
