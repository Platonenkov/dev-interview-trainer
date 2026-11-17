using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Trainer.Infrastructure.Persistence.Entities;

namespace Trainer.Infrastructure.Persistence.Configurations;

internal sealed class QuestionReportConfiguration : IEntityTypeConfiguration<QuestionReportEntity>
{
    public void Configure(EntityTypeBuilder<QuestionReportEntity> builder)
    {
        builder.ToTable("question_reports");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnType("character(26)");
        builder.Property(r => r.UserId).HasColumnType("character(26)");
        builder.Property(r => r.QuestionId).HasColumnType("character(26)");
        builder.Property(r => r.Culture).HasColumnType("text");
        builder.Property(r => r.Reason).HasConversion<short>();
        builder.Property(r => r.Comment).HasColumnType("text");
        builder.Property(r => r.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(r => r.ResolvedAt).HasColumnType("timestamp with time zone");
        builder.Property(r => r.ResolvedBy).HasColumnType("character(26)");
        // The per-user quota counts the reports of the last 24 hours.
        builder.HasIndex(r => new { r.UserId, r.CreatedAt });
        // The admin page lists open or resolved reports, newest first.
        builder.HasIndex(r => new { r.ResolvedAt, r.CreatedAt }).IsDescending(false, true);
        builder.HasOne<UserEntity>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<QuestionEntity>().WithMany().HasForeignKey(r => r.QuestionId).OnDelete(DeleteBehavior.Restrict);
        // Deleting an admin account keeps the report and forgets who resolved it.
        builder.HasOne<UserEntity>().WithMany().HasForeignKey(r => r.ResolvedBy).OnDelete(DeleteBehavior.SetNull);
    }
}
