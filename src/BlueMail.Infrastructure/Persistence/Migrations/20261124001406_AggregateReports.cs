using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BlueMail.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AggregateReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "aggregate_reports",
                schema: "mta",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    domain = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    org_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    report_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    begin_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    end_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_aggregate_reports", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "aggregate_report_rows",
                schema: "mta",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    report_id = table.Column<long>(type: "bigint", nullable: false),
                    source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    count = table.Column<long>(type: "bigint", nullable: false),
                    failed = table.Column<long>(type: "bigint", nullable: false),
                    disposition = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    dkim_pass = table.Column<bool>(type: "boolean", nullable: true),
                    spf_pass = table.Column<bool>(type: "boolean", nullable: true),
                    header_from = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    detail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_aggregate_report_rows", x => x.id);
                    table.ForeignKey(
                        name: "fk_aggregate_report_rows_aggregate_reports_report_id",
                        column: x => x.report_id,
                        principalSchema: "mta",
                        principalTable: "aggregate_reports",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_aggregate_report_rows_report_id",
                schema: "mta",
                table: "aggregate_report_rows",
                column: "report_id");

            migrationBuilder.CreateIndex(
                name: "ix_aggregate_reports_kind_org_name_report_id_domain",
                schema: "mta",
                table: "aggregate_reports",
                columns: new[] { "kind", "org_name", "report_id", "domain" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_aggregate_reports_tenant_id_kind_begin_at",
                schema: "mta",
                table: "aggregate_reports",
                columns: new[] { "tenant_id", "kind", "begin_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "aggregate_report_rows",
                schema: "mta");

            migrationBuilder.DropTable(
                name: "aggregate_reports",
                schema: "mta");
        }
    }
}
