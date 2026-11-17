using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trainer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class QuestionFeedback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "question_ratings",
                columns: table => new
                {
                    user_id = table.Column<string>(type: "character(26)", nullable: false),
                    question_id = table.Column<string>(type: "character(26)", nullable: false),
                    value = table.Column<short>(type: "smallint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_question_ratings", x => new { x.user_id, x.question_id });
                    table.ForeignKey(
                        name: "fk_question_ratings_questions_question_id",
                        column: x => x.question_id,
                        principalTable: "questions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_question_ratings_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "question_reports",
                columns: table => new
                {
                    id = table.Column<string>(type: "character(26)", nullable: false),
                    user_id = table.Column<string>(type: "character(26)", nullable: false),
                    question_id = table.Column<string>(type: "character(26)", nullable: false),
                    culture = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<short>(type: "smallint", nullable: false),
                    comment = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolved_by = table.Column<string>(type: "character(26)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_question_reports", x => x.id);
                    table.ForeignKey(
                        name: "fk_question_reports_questions_question_id",
                        column: x => x.question_id,
                        principalTable: "questions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_question_reports_users_resolved_by",
                        column: x => x.resolved_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_question_reports_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_question_ratings_question_id",
                table: "question_ratings",
                column: "question_id");

            migrationBuilder.CreateIndex(
                name: "ix_question_reports_question_id",
                table: "question_reports",
                column: "question_id");

            migrationBuilder.CreateIndex(
                name: "ix_question_reports_resolved_at_created_at",
                table: "question_reports",
                columns: new[] { "resolved_at", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_question_reports_resolved_by",
                table: "question_reports",
                column: "resolved_by");

            migrationBuilder.CreateIndex(
                name: "ix_question_reports_user_id_created_at",
                table: "question_reports",
                columns: new[] { "user_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "question_ratings");

            migrationBuilder.DropTable(
                name: "question_reports");
        }
    }
}
