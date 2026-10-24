using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Base.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BillingHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BillingEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActorName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PlanId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FromPlanId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BillingCycle = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    Currency = table.Column<string>(type: "varchar(3)", unicode: false, maxLength: 3, nullable: true),
                    PaymentReference = table.Column<string>(type: "varchar(12)", unicode: false, maxLength: 12, nullable: true),
                    Gateway = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Detail = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TraceId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BillingEvents_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BillingEvents_TenantId",
                table: "BillingEvents",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_BillingEvents_TenantId_OccurredAt",
                table: "BillingEvents",
                columns: new[] { "TenantId", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BillingEvents");
        }
    }
}
