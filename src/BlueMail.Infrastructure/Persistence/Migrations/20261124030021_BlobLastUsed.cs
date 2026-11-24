using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlueMail.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BlobLastUsed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "collected_at",
                schema: "mail",
                table: "blobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_used_at",
                schema: "mail",
                table: "blobs",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.CreateIndex(
                name: "ix_blobs_last_used_at",
                schema: "mail",
                table: "blobs",
                column: "last_used_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_blobs_last_used_at",
                schema: "mail",
                table: "blobs");

            migrationBuilder.DropColumn(
                name: "collected_at",
                schema: "mail",
                table: "blobs");

            migrationBuilder.DropColumn(
                name: "last_used_at",
                schema: "mail",
                table: "blobs");
        }
    }
}
