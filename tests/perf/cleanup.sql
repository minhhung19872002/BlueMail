-- Removes the synthetic performance messages of fill.sql (dev/test databases only).
-- Threads are deleted with foreign-key triggers off (session_replication_role = replica, needs a superuser such as the
-- dev database owner): every perf message is gone by then, and checking 300k thread ids one by one against
-- mail.messages (no index on thread_id alone) takes hours. BlueMail itself never deletes threads.
\set ON_ERROR_STOP on
BEGIN;
DELETE FROM mail.message_labels WHERE message_id IN (SELECT id FROM mail.messages WHERE rfc_message_id LIKE 'perf-%@perf.example');
DELETE FROM mail.messages WHERE rfc_message_id LIKE 'perf-%@perf.example';
SET LOCAL session_replication_role = replica;
DELETE FROM mail.threads t WHERE t.normalized_subject LIKE 'perf thread %'
  AND NOT EXISTS (SELECT 1 FROM mail.messages m WHERE m.tenant_id = t.tenant_id AND m.thread_id = t.id);
COMMIT;
VACUUM ANALYZE mail.messages;
VACUUM ANALYZE mail.message_labels;
VACUUM ANALYZE mail.threads;
