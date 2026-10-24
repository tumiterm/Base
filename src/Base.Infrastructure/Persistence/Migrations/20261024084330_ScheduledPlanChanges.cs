using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Base.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ScheduledPlanChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ScheduledBillingCycle",
                table: "TenantSubscriptions",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ScheduledChangeAt",
                table: "TenantSubscriptions",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ScheduledPlanId",
                table: "TenantSubscriptions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsUpgrade",
                table: "Payments",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_TenantSubscriptions_ScheduledPlanId",
                table: "TenantSubscriptions",
                column: "ScheduledPlanId");

            migrationBuilder.AddForeignKey(
                name: "FK_TenantSubscriptions_Plans_ScheduledPlanId",
                table: "TenantSubscriptions",
                column: "ScheduledPlanId",
                principalTable: "Plans",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TenantSubscriptions_Plans_ScheduledPlanId",
                table: "TenantSubscriptions");

            migrationBuilder.DropIndex(
                name: "IX_TenantSubscriptions_ScheduledPlanId",
                table: "TenantSubscriptions");

            migrationBuilder.DropColumn(
                name: "ScheduledBillingCycle",
                table: "TenantSubscriptions");

            migrationBuilder.DropColumn(
                name: "ScheduledChangeAt",
                table: "TenantSubscriptions");

            migrationBuilder.DropColumn(
                name: "ScheduledPlanId",
                table: "TenantSubscriptions");

            migrationBuilder.DropColumn(
                name: "IsUpgrade",
                table: "Payments");
        }
    }
}
