using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Base.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantBillingDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Address",
                table: "Tenants",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BillingContactName",
                table: "Tenants",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LegalName",
                table: "Tenants",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Phone",
                table: "Tenants",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RegistrationNumber",
                table: "Tenants",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaxNumber",
                table: "Tenants",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsTest",
                table: "Payments",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "BuyerAddress",
                table: "Invoices",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BuyerContact",
                table: "Invoices",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BuyerPhone",
                table: "Invoices",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BuyerRegistrationNumber",
                table: "Invoices",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BuyerTaxNumber",
                table: "Invoices",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Address",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "BillingContactName",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "LegalName",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "Phone",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "RegistrationNumber",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "TaxNumber",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "IsTest",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "BuyerAddress",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "BuyerContact",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "BuyerPhone",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "BuyerRegistrationNumber",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "BuyerTaxNumber",
                table: "Invoices");
        }
    }
}
