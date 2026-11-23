using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlueMail.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DomainAdmin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "alias_of_domain_id",
                schema: "tenant",
                table: "domains",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_check",
                schema: "tenant",
                table: "domains",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "alias_of_domain_id",
                schema: "tenant",
                table: "domains");

            migrationBuilder.DropColumn(
                name: "last_check",
                schema: "tenant",
                table: "domains");
        }
    }
}
