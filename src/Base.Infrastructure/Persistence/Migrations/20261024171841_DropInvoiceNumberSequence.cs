using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Base.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DropInvoiceNumberSequence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropSequence(
                name: "InvoiceNumbers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence(
                name: "InvoiceNumbers");
        }
    }
}
