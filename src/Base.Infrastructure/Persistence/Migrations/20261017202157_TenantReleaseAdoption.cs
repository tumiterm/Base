using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Base.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantReleaseAdoption : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReleaseAdoptedAt",
                table: "Tenants",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReleaseVersion",
                table: "Tenants",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "0.0.0");

            migrationBuilder.CreateTable(
                name: "TenantFeatureChoices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FeatureCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IsOn = table.Column<bool>(type: "bit", nullable: false),
                    ChosenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
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
                    table.PrimaryKey("PK_TenantFeatureChoices", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantFeatureChoices_TenantId",
                table: "TenantFeatureChoices",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantFeatureChoices_TenantId_FeatureCode",
                table: "TenantFeatureChoices",
                columns: new[] { "TenantId", "FeatureCode" },
                unique: true,
                filter: "[IsDeleted] = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantFeatureChoices");

            migrationBuilder.DropColumn(
                name: "ReleaseAdoptedAt",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "ReleaseVersion",
                table: "Tenants");
        }
    }
}
