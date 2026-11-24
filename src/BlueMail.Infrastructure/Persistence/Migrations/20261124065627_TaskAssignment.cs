using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlueMail.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TaskAssignment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "assigned_by_id",
                schema: "cal",
                table: "tasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "assigned_to_id",
                schema: "cal",
                table: "tasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "origin_task_id",
                schema: "cal",
                table: "tasks",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "assigned_by_id",
                schema: "cal",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "assigned_to_id",
                schema: "cal",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "origin_task_id",
                schema: "cal",
                table: "tasks");
        }
    }
}
