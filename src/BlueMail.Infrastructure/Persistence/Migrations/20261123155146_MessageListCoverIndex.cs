using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlueMail.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MessageListCoverIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Conversation lists group every message of a label by thread: a narrow covering index makes that an
            // index-only scan instead of reading the wide message rows (PROMPT 8.2, docs/06 Phase 13).
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS ix_messages_list_cover ON mail.messages (tenant_id, mailbox_id)
                    INCLUDE (id, thread_id, received_at, seen) WHERE deleted_at IS NULL AND expunged_at IS NULL;
                """);

            // Trigram indexes on exactly the expressions SearchSql.Contains produces (subject:, from:), so substring
            // search uses an index instead of reading every message (BUG-065).
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS ix_messages_subject_search ON mail.messages USING gin (public.f_unaccent(lower(coalesce(subject, ''))) gin_trgm_ops);
                CREATE INDEX IF NOT EXISTS ix_messages_from_addr_search ON mail.messages USING gin (public.f_unaccent(lower(coalesce(from_addr, ''))) gin_trgm_ops);
                CREATE INDEX IF NOT EXISTS ix_messages_from_name_search ON mail.messages USING gin (public.f_unaccent(lower(coalesce(from_name, ''))) gin_trgm_ops);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS mail.ix_messages_from_name_search;
                DROP INDEX IF EXISTS mail.ix_messages_from_addr_search;
                DROP INDEX IF EXISTS mail.ix_messages_subject_search;
                DROP INDEX IF EXISTS mail.ix_messages_list_cover;
                """);
        }
    }
}
