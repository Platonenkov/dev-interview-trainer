using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trainer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AnswersReceivedAtIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_answers_received_at",
                table: "answers",
                column: "received_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_answers_received_at",
                table: "answers");
        }
    }
}
