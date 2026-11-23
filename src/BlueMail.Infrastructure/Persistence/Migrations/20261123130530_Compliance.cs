using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BlueMail.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Compliance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "compliance");

            migrationBuilder.EnsureSchema(
                name: "migration");

            migrationBuilder.CreateTable(
                name: "data_exports",
                schema: "compliance",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    query = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    mailboxes = table.Column<string>(type: "jsonb", nullable: false),
                    format = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    total = table.Column<int>(type: "integer", nullable: false),
                    done = table.Column<int>(type: "integer", nullable: false),
                    chunks = table.Column<int>(type: "integer", nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_exports", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "hold_messages",
                schema: "compliance",
                columns: table => new
                {
                    hold_id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_hold_messages", x => new { x.hold_id, x.message_id });
                });

            migrationBuilder.CreateTable(
                name: "import_errors",
                schema: "migration",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    folder = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    uid = table.Column<long>(type: "bigint", nullable: true),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    detail = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_errors", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "import_jobs",
                schema: "migration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    host = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    port = table.Column<int>(type: "integer", nullable: false),
                    tls = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    username = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    password_cipher = table.Column<byte[]>(type: "bytea", nullable: true),
                    upload_id = table.Column<Guid>(type: "uuid", nullable: true),
                    upload_chunks = table.Column<int>(type: "integer", nullable: false),
                    file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    total = table.Column<int>(type: "integer", nullable: false),
                    imported = table.Column<int>(type: "integer", nullable: false),
                    skipped = table.Column<int>(type: "integer", nullable: false),
                    failed = table.Column<int>(type: "integer", nullable: false),
                    current_folder = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_jobs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "imported_messages",
                schema: "migration",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mailbox_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_key = table.Column<string>(type: "character varying(1500)", maxLength: 1500, nullable: false),
                    content_sha256 = table.Column<byte[]>(type: "bytea", nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_imported_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "legal_holds",
                schema: "compliance",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    scope = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    org_unit_id = table.Column<Guid>(type: "uuid", nullable: true),
                    query = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    matched = table.Column<int>(type: "integer", nullable: false),
                    released_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    released_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_legal_holds", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "retention_rules",
                schema: "compliance",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    days = table.Column<int>(type: "integer", nullable: false),
                    query = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    org_unit_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    last_run_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_run_deleted = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_retention_rules", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_data_exports_status_created_at",
                schema: "compliance",
                table: "data_exports",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_data_exports_tenant_id_requested_by",
                schema: "compliance",
                table: "data_exports",
                columns: new[] { "tenant_id", "requested_by" });

            migrationBuilder.CreateIndex(
                name: "ix_hold_messages_tenant_id_message_id",
                schema: "compliance",
                table: "hold_messages",
                columns: new[] { "tenant_id", "message_id" });

            migrationBuilder.CreateIndex(
                name: "ix_import_errors_tenant_id_job_id",
                schema: "migration",
                table: "import_errors",
                columns: new[] { "tenant_id", "job_id" });

            migrationBuilder.CreateIndex(
                name: "ix_import_jobs_status_created_at",
                schema: "migration",
                table: "import_jobs",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_import_jobs_tenant_id_batch_id",
                schema: "migration",
                table: "import_jobs",
                columns: new[] { "tenant_id", "batch_id" });

            migrationBuilder.CreateIndex(
                name: "ix_import_jobs_tenant_id_user_id",
                schema: "migration",
                table: "import_jobs",
                columns: new[] { "tenant_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ux_import_jobs_one_active_per_user",
                schema: "migration",
                table: "import_jobs",
                column: "user_id",
                unique: true,
                filter: "status IN ('QUEUED', 'RUNNING')");

            migrationBuilder.CreateIndex(
                name: "ix_imported_messages_tenant_id_mailbox_id_content_sha256",
                schema: "migration",
                table: "imported_messages",
                columns: new[] { "tenant_id", "mailbox_id", "content_sha256" });

            migrationBuilder.CreateIndex(
                name: "ix_imported_messages_tenant_id_mailbox_id_source_key",
                schema: "migration",
                table: "imported_messages",
                columns: new[] { "tenant_id", "mailbox_id", "source_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_legal_holds_org_unit_id",
                schema: "compliance",
                table: "legal_holds",
                column: "org_unit_id",
                filter: "released_at IS NULL AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_legal_holds_tenant_id_scope",
                schema: "compliance",
                table: "legal_holds",
                columns: new[] { "tenant_id", "scope" },
                filter: "released_at IS NULL AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_legal_holds_user_id",
                schema: "compliance",
                table: "legal_holds",
                column: "user_id",
                filter: "released_at IS NULL AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_retention_rules_tenant_id_action",
                schema: "compliance",
                table: "retention_rules",
                columns: new[] { "tenant_id", "action" },
                filter: "deleted_at IS NULL");

            // Hand-written (docs/00 D22): legal hold counts are recomputed from their source, never added up, and every
            // permanent delete checks them (and KEEP_MIN retention) inside the same UPDATE.
            migrationBuilder.Sql("""
                CREATE FUNCTION compliance.f_mailbox_hold_count(p_tenant uuid, p_mailbox uuid) RETURNS int LANGUAGE sql STABLE AS $$
                  SELECT count(*)::int FROM compliance.legal_holds h
                  JOIN mail.mailboxes mb ON mb.id = p_mailbox
                  LEFT JOIN identity.users u ON u.id = mb.owner_id
                  WHERE h.tenant_id = p_tenant AND h.released_at IS NULL AND h.deleted_at IS NULL
                    AND ((h.scope = 'USER' AND h.user_id = mb.owner_id)
                      OR (h.scope = 'ORG_UNIT' AND u.org_unit_id IS NOT NULL AND h.org_unit_id = u.org_unit_id))
                $$;

                CREATE FUNCTION compliance.f_recompute_hold_counts(p_tenant uuid, p_mailboxes uuid[]) RETURNS int LANGUAGE sql AS $$
                  WITH per_mailbox AS (
                    SELECT id AS mailbox_id, compliance.f_mailbox_hold_count(p_tenant, id) AS c FROM unnest(p_mailboxes) AS id),
                  upd AS (
                    UPDATE mail.messages m
                    SET legal_hold_count = pm.c + (
                      SELECT count(*)::int FROM compliance.hold_messages hm
                      JOIN compliance.legal_holds h ON h.id = hm.hold_id AND h.released_at IS NULL AND h.deleted_at IS NULL
                      WHERE hm.tenant_id = m.tenant_id AND hm.message_id = m.id)
                    FROM per_mailbox pm
                    WHERE m.tenant_id = p_tenant AND m.mailbox_id = pm.mailbox_id AND m.expunged_at IS NULL
                      AND m.legal_hold_count IS DISTINCT FROM pm.c + (
                        SELECT count(*)::int FROM compliance.hold_messages hm
                        JOIN compliance.legal_holds h ON h.id = hm.hold_id AND h.released_at IS NULL AND h.deleted_at IS NULL
                        WHERE hm.tenant_id = m.tenant_id AND hm.message_id = m.id)
                    RETURNING 1)
                  SELECT count(*)::int FROM upd
                $$;

                CREATE FUNCTION compliance.f_keep_min_days(p_tenant uuid, p_mailbox uuid) RETURNS int LANGUAGE sql STABLE AS $$
                  SELECT coalesce(max(r.days), 0) FROM compliance.retention_rules r
                  JOIN mail.mailboxes mb ON mb.id = p_mailbox
                  LEFT JOIN identity.users u ON u.id = mb.owner_id
                  WHERE r.tenant_id = p_tenant AND r.action = 'KEEP_MIN' AND r.is_enabled AND r.deleted_at IS NULL
                    AND (r.org_unit_id IS NULL OR r.org_unit_id = u.org_unit_id)
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION compliance.f_keep_min_days(uuid, uuid); DROP FUNCTION compliance.f_recompute_hold_counts(uuid, uuid[]); DROP FUNCTION compliance.f_mailbox_hold_count(uuid, uuid);");

            migrationBuilder.DropTable(
                name: "data_exports",
                schema: "compliance");

            migrationBuilder.DropTable(
                name: "hold_messages",
                schema: "compliance");

            migrationBuilder.DropTable(
                name: "import_errors",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "import_jobs",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "imported_messages",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "legal_holds",
                schema: "compliance");

            migrationBuilder.DropTable(
                name: "retention_rules",
                schema: "compliance");
        }
    }
}
