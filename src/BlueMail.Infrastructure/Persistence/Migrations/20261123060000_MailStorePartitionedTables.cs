using BlueMail.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlueMail.Infrastructure.Persistence.Migrations;

/// <summary>
/// Hand-written: objects EF Core cannot model.
/// - mail.messages and mail.message_labels, hash-partitioned by tenant_id (PROMPT section 4), 16 partitions.
/// - f_unaccent(): IMMUTABLE wrapper of unaccent() so it can be used in indexes and generated vectors.
/// - Search indexes (GIN on search_vector, trigram on subject / from_addr).
/// - audit.audit_logs is append-only: UPDATE, DELETE and TRUNCATE are refused by triggers.
/// - Schema "logs" for the Serilog PostgreSQL sink.
/// The column list of mail.messages / mail.message_labels must match MessageConfig / MessageLabelConfig.
/// </summary>
[DbContext(typeof(BlueMailDbContext))]
[Migration("20261123060000_MailStorePartitionedTables")]
public partial class MailStorePartitionedTables : Migration
{
    private const int Partitions = 16;

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("CREATE SCHEMA IF NOT EXISTS logs;");

        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION public.f_unaccent(text) RETURNS text
            LANGUAGE sql IMMUTABLE PARALLEL SAFE STRICT
            AS $$ SELECT public.unaccent('public.unaccent'::regdictionary, $1) $$;
            """);

        migrationBuilder.Sql("""
            CREATE TABLE mail.messages (
                tenant_id          uuid        NOT NULL,
                id                 uuid        NOT NULL DEFAULT gen_random_uuid(),
                mailbox_id         uuid        NOT NULL REFERENCES mail.mailboxes(id) ON DELETE CASCADE,
                blob_sha256        bytea       NOT NULL,
                thread_id          uuid        NOT NULL REFERENCES mail.threads(id) ON DELETE CASCADE,
                rfc_message_id     text        NULL,
                in_reply_to        text        NULL,
                "references"       text[]      NOT NULL DEFAULT '{}',
                subject            text        NULL,
                normalized_subject text        NULL,
                from_addr          text        NULL,
                from_name          text        NULL,
                to_addrs           jsonb       NOT NULL DEFAULT '[]',
                cc_addrs           jsonb       NOT NULL DEFAULT '[]',
                bcc_addrs          jsonb       NOT NULL DEFAULT '[]',
                reply_to           jsonb       NOT NULL DEFAULT '[]',
                sent_at            timestamptz NULL,
                received_at        timestamptz NOT NULL,
                size               bigint      NOT NULL,
                snippet            text        NULL,
                has_attachments    boolean     NOT NULL DEFAULT false,
                mime_structure     jsonb       NOT NULL DEFAULT '{}',
                seen               boolean     NOT NULL DEFAULT false,
                flagged            boolean     NOT NULL DEFAULT false,
                answered           boolean     NOT NULL DEFAULT false,
                forwarded          boolean     NOT NULL DEFAULT false,
                draft              boolean     NOT NULL DEFAULT false,
                keywords           text[]      NOT NULL DEFAULT '{}',
                spam_score         double precision NULL,
                spam_report        jsonb       NULL,
                auth_results       jsonb       NULL,
                list_id            text        NULL,
                list_unsubscribe   jsonb       NULL,
                category           text        NULL,
                is_important       boolean     NOT NULL DEFAULT false,
                modseq             bigint      NOT NULL,
                legal_hold_count   integer     NOT NULL DEFAULT 0,
                expunged_at        timestamptz NULL,
                search_vector      tsvector    NULL,
                created_at         timestamptz NOT NULL DEFAULT now(),
                created_by         uuid        NULL,
                updated_at         timestamptz NOT NULL DEFAULT now(),
                updated_by         uuid        NULL,
                deleted_at         timestamptz NULL,
                PRIMARY KEY (tenant_id, id),
                CONSTRAINT fk_messages_blob FOREIGN KEY (tenant_id, blob_sha256) REFERENCES mail.blobs(tenant_id, sha256)
            ) PARTITION BY HASH (tenant_id);
            """);

        migrationBuilder.Sql("""
            CREATE TABLE mail.message_labels (
                tenant_id  uuid   NOT NULL,
                message_id uuid   NOT NULL,
                label_id   uuid   NOT NULL REFERENCES mail.labels(id) ON DELETE CASCADE,
                imap_uid   bigint NOT NULL,
                modseq     bigint NOT NULL,
                PRIMARY KEY (tenant_id, message_id, label_id),
                CONSTRAINT ux_message_labels_label_uid UNIQUE (tenant_id, label_id, imap_uid),
                CONSTRAINT fk_message_labels_message FOREIGN KEY (tenant_id, message_id)
                    REFERENCES mail.messages(tenant_id, id) ON DELETE CASCADE
            ) PARTITION BY HASH (tenant_id);
            """);

        for (var i = 0; i < Partitions; i++)
        {
            migrationBuilder.Sql($"CREATE TABLE mail.messages_p{i:D2} PARTITION OF mail.messages FOR VALUES WITH (MODULUS {Partitions}, REMAINDER {i});");
            migrationBuilder.Sql($"CREATE TABLE mail.message_labels_p{i:D2} PARTITION OF mail.message_labels FOR VALUES WITH (MODULUS {Partitions}, REMAINDER {i});");
        }

        migrationBuilder.Sql("""
            CREATE INDEX ix_messages_mailbox_received ON mail.messages (tenant_id, mailbox_id, received_at DESC, id DESC);
            CREATE INDEX ix_messages_thread ON mail.messages (tenant_id, thread_id, received_at);
            CREATE INDEX ix_messages_mailbox_rfc_id ON mail.messages (tenant_id, mailbox_id, rfc_message_id);
            CREATE INDEX ix_messages_mailbox_modseq ON mail.messages (tenant_id, mailbox_id, modseq);
            CREATE INDEX ix_messages_blob ON mail.messages (tenant_id, blob_sha256);
            CREATE INDEX ix_messages_search ON mail.messages USING gin (search_vector);
            CREATE INDEX ix_messages_subject_trgm ON mail.messages USING gin (public.f_unaccent(lower(subject)) gin_trgm_ops);
            CREATE INDEX ix_messages_from_trgm ON mail.messages USING gin (lower(from_addr) gin_trgm_ops);
            CREATE INDEX ix_message_labels_label_modseq ON mail.message_labels (tenant_id, label_id, modseq);
            CREATE INDEX ix_message_labels_message ON mail.message_labels (tenant_id, message_id);
            """);

        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION audit.refuse_change() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                RAISE EXCEPTION 'audit.audit_logs is append-only' USING ERRCODE = 'insufficient_privilege';
            END $$;
            CREATE TRIGGER trg_audit_logs_no_update BEFORE UPDATE OR DELETE ON audit.audit_logs
                FOR EACH ROW EXECUTE FUNCTION audit.refuse_change();
            CREATE TRIGGER trg_audit_logs_no_truncate BEFORE TRUNCATE ON audit.audit_logs
                FOR EACH STATEMENT EXECUTE FUNCTION audit.refuse_change();
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TRIGGER IF EXISTS trg_audit_logs_no_truncate ON audit.audit_logs;
            DROP TRIGGER IF EXISTS trg_audit_logs_no_update ON audit.audit_logs;
            DROP FUNCTION IF EXISTS audit.refuse_change();
            DROP TABLE IF EXISTS mail.message_labels;
            DROP TABLE IF EXISTS mail.messages;
            DROP FUNCTION IF EXISTS public.f_unaccent(text);
            """);
    }
}
