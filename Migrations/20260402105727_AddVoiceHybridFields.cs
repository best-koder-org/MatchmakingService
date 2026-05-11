using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatchmakingService.Migrations
{
    /// <inheritdoc />
    public partial class AddVoiceHybridFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AnswerType",
                table: "user_question_answers",
                type: "varchar(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "tap")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "DepthScore",
                table: "user_question_answers",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QualityBreakdown",
                table: "user_question_answers",
                type: "varchar(500)",
                maxLength: 500,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<double>(
                name: "VoiceDurationSeconds",
                table: "user_question_answers",
                type: "double",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VoiceTranscript",
                table: "user_question_answers",
                type: "varchar(2000)",
                maxLength: 2000,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<bool>(
                name: "VoiceEligible",
                table: "compatibility_questions",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "VoicePromptText",
                table: "compatibility_questions",
                type: "varchar(500)",
                maxLength: 500,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "VoicePromptTextSv",
                table: "compatibility_questions",
                type: "varchar(500)",
                maxLength: 500,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AnswerType",
                table: "user_question_answers");

            migrationBuilder.DropColumn(
                name: "DepthScore",
                table: "user_question_answers");

            migrationBuilder.DropColumn(
                name: "QualityBreakdown",
                table: "user_question_answers");

            migrationBuilder.DropColumn(
                name: "VoiceDurationSeconds",
                table: "user_question_answers");

            migrationBuilder.DropColumn(
                name: "VoiceTranscript",
                table: "user_question_answers");

            migrationBuilder.DropColumn(
                name: "VoiceEligible",
                table: "compatibility_questions");

            migrationBuilder.DropColumn(
                name: "VoicePromptText",
                table: "compatibility_questions");

            migrationBuilder.DropColumn(
                name: "VoicePromptTextSv",
                table: "compatibility_questions");
        }
    }
}
