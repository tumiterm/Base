using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Base.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InvoiceNumbersWithoutDefault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<long>(
                name: "Sequence",
                table: "Invoices",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldDefaultValueSql: "NEXT VALUE FOR InvoiceNumbers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<long>(
                name: "Sequence",
                table: "Invoices",
                type: "bigint",
                nullable: false,
                defaultValueSql: "NEXT VALUE FOR InvoiceNumbers",
                oldClrType: typeof(long),
                oldType: "bigint");
        }
    }
}
