using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Trainer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialContent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "content_imports",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    hash = table.Column<string>(type: "text", nullable: false),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    render_version = table.Column<int>(type: "integer", nullable: false),
                    imported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    question_count = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_content_imports", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "stacks",
                columns: table => new
                {
                    key = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stacks", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "redirects",
                columns: table => new
                {
                    stack_key = table.Column<string>(type: "text", nullable: false),
                    from_slug = table.Column<string>(type: "text", nullable: false),
                    to_slug = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_redirects", x => new { x.stack_key, x.from_slug });
                    table.ForeignKey(
                        name: "fk_redirects_stacks_stack_key",
                        column: x => x.stack_key,
                        principalTable: "stacks",
                        principalColumn: "key",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "stack_locales",
                columns: table => new
                {
                    stack_key = table.Column<string>(type: "text", nullable: false),
                    culture = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stack_locales", x => new { x.stack_key, x.culture });
                    table.ForeignKey(
                        name: "fk_stack_locales_stacks_stack_key",
                        column: x => x.stack_key,
                        principalTable: "stacks",
                        principalColumn: "key",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "topics",
                columns: table => new
                {
                    stack_key = table.Column<string>(type: "text", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_topics", x => new { x.stack_key, x.key });
                    table.ForeignKey(
                        name: "fk_topics_stacks_stack_key",
                        column: x => x.stack_key,
                        principalTable: "stacks",
                        principalColumn: "key",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "questions",
                columns: table => new
                {
                    id = table.Column<string>(type: "character(26)", nullable: false),
                    stack_key = table.Column<string>(type: "text", nullable: false),
                    topic_key = table.Column<string>(type: "text", nullable: false),
                    slug = table.Column<string>(type: "text", nullable: false),
                    level = table.Column<short>(type: "smallint", nullable: false),
                    type = table.Column<short>(type: "smallint", nullable: false),
                    grading = table.Column<short>(type: "smallint", nullable: false),
                    tags = table.Column<string[]>(type: "text[]", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    verified = table.Column<DateOnly>(type: "date", nullable: false),
                    dotnet_version = table.Column<string>(type: "text", nullable: true),
                    retired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_questions", x => x.id);
                    table.ForeignKey(
                        name: "fk_questions_topics_stack_key_topic_key",
                        columns: x => new { x.stack_key, x.topic_key },
                        principalTable: "topics",
                        principalColumns: new[] { "stack_key", "key" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "topic_locales",
                columns: table => new
                {
                    stack_key = table.Column<string>(type: "text", nullable: false),
                    topic_key = table.Column<string>(type: "text", nullable: false),
                    culture = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_topic_locales", x => new { x.stack_key, x.topic_key, x.culture });
                    table.ForeignKey(
                        name: "fk_topic_locales_topics_stack_key_topic_key",
                        columns: x => new { x.stack_key, x.topic_key },
                        principalTable: "topics",
                        principalColumns: new[] { "stack_key", "key" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "question_locales",
                columns: table => new
                {
                    question_id = table.Column<string>(type: "character(26)", nullable: false),
                    culture = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    prompt_html = table.Column<string>(type: "text", nullable: false),
                    answer_html = table.Column<string>(type: "text", nullable: false),
                    explanation_html = table.Column<string>(type: "text", nullable: false),
                    hint_html = table.Column<string>(type: "text", nullable: true),
                    options = table.Column<string>(type: "jsonb", nullable: false),
                    links = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_question_locales", x => new { x.question_id, x.culture });
                    table.ForeignKey(
                        name: "fk_question_locales_questions_question_id",
                        column: x => x.question_id,
                        principalTable: "questions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_content_imports_hash",
                table: "content_imports",
                column: "hash");

            migrationBuilder.CreateIndex(
                name: "ix_content_imports_is_active",
                table: "content_imports",
                column: "is_active",
                unique: true,
                filter: "is_active");

            migrationBuilder.CreateIndex(
                name: "ix_questions_stack_key_slug",
                table: "questions",
                columns: new[] { "stack_key", "slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_questions_stack_key_topic_key",
                table: "questions",
                columns: new[] { "stack_key", "topic_key" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "content_imports");

            migrationBuilder.DropTable(
                name: "question_locales");

            migrationBuilder.DropTable(
                name: "redirects");

            migrationBuilder.DropTable(
                name: "stack_locales");

            migrationBuilder.DropTable(
                name: "topic_locales");

            migrationBuilder.DropTable(
                name: "questions");

            migrationBuilder.DropTable(
                name: "topics");

            migrationBuilder.DropTable(
                name: "stacks");
        }
    }
}
