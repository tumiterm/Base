using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Base.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Messaging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SourceId",
                table: "NotificationMessages",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WhatsAppTemplate",
                table: "NotificationMessages",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Broadcasts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AudienceKind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    AudienceCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    AudienceLabel = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    AudienceJson = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: true),
                    Channels = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    HtmlBody = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: true),
                    TextBody = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: true),
                    SmsBody = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: true),
                    WhatsAppTemplateName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    WhatsAppLanguage = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    WhatsAppParameters = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ScheduledFor = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IsScheduled = table.Column<bool>(type: "bit", nullable: false),
                    SenderUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SenderName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RecipientCount = table.Column<int>(type: "int", nullable: false),
                    QueuedCount = table.Column<int>(type: "int", nullable: false),
                    SkippedCount = table.Column<int>(type: "int", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Processed = table.Column<int>(type: "int", nullable: false),
                    DateCreated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    DateModified = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    UserCreated = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    UserModified = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DateDeleted = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UserDeleted = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Broadcasts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MessageTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    HtmlBody = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: true),
                    SmsBody = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: true),
                    WhatsAppTemplateName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    WhatsAppLanguage = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    WhatsAppParameters = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: true),
                    DateCreated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    DateModified = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    UserCreated = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    UserModified = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DateDeleted = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UserDeleted = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageTemplates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationMessages_TenantId_SourceId_Status",
                table: "NotificationMessages",
                columns: new[] { "TenantId", "SourceId", "Status" },
                filter: "[SourceId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Broadcasts_Status_ScheduledFor",
                table: "Broadcasts",
                columns: new[] { "Status", "ScheduledFor" },
                filter: "[IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Broadcasts_TenantId",
                table: "Broadcasts",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Broadcasts_TenantId_ScheduledFor",
                table: "Broadcasts",
                columns: new[] { "TenantId", "ScheduledFor" });

            migrationBuilder.CreateIndex(
                name: "IX_MessageTemplates_TenantId",
                table: "MessageTemplates",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_MessageTemplates_TenantId_Name",
                table: "MessageTemplates",
                columns: new[] { "TenantId", "Name" },
                unique: true,
                filter: "[IsDeleted] = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Broadcasts");

            migrationBuilder.DropTable(
                name: "MessageTemplates");

            migrationBuilder.DropIndex(
                name: "IX_NotificationMessages_TenantId_SourceId_Status",
                table: "NotificationMessages");

            migrationBuilder.DropColumn(
                name: "SourceId",
                table: "NotificationMessages");

            migrationBuilder.DropColumn(
                name: "WhatsAppTemplate",
                table: "NotificationMessages");
        }
    }
}
