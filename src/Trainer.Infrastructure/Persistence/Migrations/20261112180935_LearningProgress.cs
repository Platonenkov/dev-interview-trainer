using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trainer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LearningProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "answers",
                columns: table => new
                {
                    id = table.Column<string>(type: "character(26)", nullable: false),
                    client_answer_id = table.Column<string>(type: "character(26)", nullable: false),
                    user_id = table.Column<string>(type: "character(26)", nullable: false),
                    question_id = table.Column<string>(type: "character(26)", nullable: false),
                    grade = table.Column<short>(type: "smallint", nullable: false),
                    used_hint = table.Column<bool>(type: "boolean", nullable: false),
                    mode = table.Column<short>(type: "smallint", nullable: false),
                    mock_session_id = table.Column<string>(type: "character(26)", nullable: true),
                    answered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_answers", x => x.id);
                    table.ForeignKey(
                        name: "fk_answers_questions_question_id",
                        column: x => x.question_id,
                        principalTable: "questions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_answers_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "mock_sessions",
                columns: table => new
                {
                    id = table.Column<string>(type: "character(26)", nullable: false),
                    user_id = table.Column<string>(type: "character(26)", nullable: false),
                    stack_key = table.Column<string>(type: "text", nullable: false),
                    level = table.Column<short>(type: "smallint", nullable: false),
                    question_count = table.Column<int>(type: "integer", nullable: false),
                    timed = table.Column<bool>(type: "boolean", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    summary = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mock_sessions", x => x.id);
                    table.ForeignKey(
                        name: "fk_mock_sessions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "review_states",
                columns: table => new
                {
                    user_id = table.Column<string>(type: "character(26)", nullable: false),
                    question_id = table.Column<string>(type: "character(26)", nullable: false),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    interval_days = table.Column<int>(type: "integer", nullable: false),
                    ease = table.Column<decimal>(type: "numeric(4,2)", nullable: false),
                    repetitions = table.Column<int>(type: "integer", nullable: false),
                    lapses = table.Column<int>(type: "integer", nullable: false),
                    last_grade = table.Column<short>(type: "smallint", nullable: false),
                    last_answered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_review_states", x => new { x.user_id, x.question_id });
                    table.ForeignKey(
                        name: "fk_review_states_questions_question_id",
                        column: x => x.question_id,
                        principalTable: "questions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_review_states_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_answers_client_answer_id",
                table: "answers",
                column: "client_answer_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_answers_question_id",
                table: "answers",
                column: "question_id");

            migrationBuilder.CreateIndex(
                name: "ix_answers_user_id_answered_at",
                table: "answers",
                columns: new[] { "user_id", "answered_at" });

            migrationBuilder.CreateIndex(
                name: "ix_mock_sessions_user_id",
                table: "mock_sessions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_review_states_question_id",
                table: "review_states",
                column: "question_id");

            migrationBuilder.CreateIndex(
                name: "ix_review_states_user_id_due_at",
                table: "review_states",
                columns: new[] { "user_id", "due_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "answers");

            migrationBuilder.DropTable(
                name: "mock_sessions");

            migrationBuilder.DropTable(
                name: "review_states");
        }
    }
}
