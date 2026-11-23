-- Performance data for PROMPT 8.2 measurements (dev/test databases only, never production).
-- Adds :n synthetic messages to the mailbox of :email, numbered from :first, in the shape BlueMail.MailStore writes
-- them (search_vector built with the same weights as MailDelivery, threads of up to 3 messages, INBOX for 60 %,
-- UIDs continuing the label's uid_next). Content blobs reuse one existing blob: list and search never open them.
--   psql -v email=lan@bluemail.test -v first=0 -v n=100000 -f fill.sql
-- Remove everything again with cleanup.sql.
\set ON_ERROR_STOP on
BEGIN;

CREATE TEMP TABLE perf_target ON COMMIT DROP AS
SELECT m.tenant_id, m.id AS mailbox_id,
       (SELECT blob_sha256 FROM mail.messages x WHERE x.tenant_id = m.tenant_id LIMIT 1) AS sha,
       (SELECT l.id FROM mail.labels l WHERE l.mailbox_id = m.id AND l.system_key = 'INBOX') AS inbox,
       (SELECT l.id FROM mail.labels l WHERE l.mailbox_id = m.id AND l.system_key = 'CATEGORY_PRIMARY') AS primary_tab
FROM mail.mailboxes m JOIN identity.users u ON u.id = m.owner_id
WHERE u.primary_email = :'email';

CREATE TEMP TABLE perf_words ON COMMIT DROP AS
SELECT ARRAY['báo','giá','hợp','đồng','dự','án','kế','hoạch','quý','họp','tiến','độ','thanh','toán','hoá','đơn','nhân','sự','tuyển','dụng',
             'invoice','contract','meeting','report','budget','project','schedule','review','proposal','delivery','shipment','order','payment',
             'server','network','backup','license','renewal','training','policy','security','audit','marketing','campaign','sales','customer',
             'support','ticket','release','update','roadmap','design','prototype','feedback','office','travel','visa','hotel','flight','agenda',
             'minutes','deadline','approval','signature','quotation','supplier','warehouse','inventory','logistics','partner','vendor','tender',
             'hà','nội','sài','gòn','đà','nẵng','tokyo','singapore','bangkok','berlin','paris','london','madrid','warsaw','dubai','seoul'] AS w;

INSERT INTO mail.threads (id, tenant_id, mailbox_id, root_message_id, normalized_subject, last_message_at, muted, created_at, updated_at)
SELECT ('00000000-0000-4000-8000-' || lpad(to_hex(g), 12, '0'))::uuid, t.tenant_id, t.mailbox_id, NULL,
       'perf thread ' || g, now() - (g % 1000) * interval '1 day', false, now(), now()
FROM perf_target t, generate_series(:first / 3, (:first + :n - 1) / 3) g
ON CONFLICT (id) DO NOTHING;

INSERT INTO mail.messages (tenant_id, id, mailbox_id, blob_sha256, thread_id, rfc_message_id, "references", subject, normalized_subject,
    from_addr, from_name, to_addrs, cc_addrs, bcc_addrs, reply_to, sent_at, received_at, size, snippet, has_attachments, mime_structure,
    seen, flagged, answered, forwarded, draft, keywords, category, is_important, modseq, legal_hold_count, search_vector, delivered_to,
    created_at, updated_at)
SELECT t.tenant_id, ('00000000-0000-4000-9000-' || lpad(to_hex(:first + i), 12, '0'))::uuid, t.mailbox_id, t.sha,
       ('00000000-0000-4000-8000-' || lpad(to_hex((:first + i) / 3), 12, '0'))::uuid,
       'perf-' || (:first + i) || '@perf.example', '{}', s.subject, lower(s.subject),
       'sender' || ((:first + i) % 2000) || '@partner' || ((:first + i) % 50) || '.example', 'Sender ' || ((:first + i) % 2000),
       jsonb_build_array(jsonb_build_object('name', null, 'address', :'email')), '[]', '[]', '[]',
       r.at, r.at, 3000 + (:first + i) % 50000, s.body, (:first + i) % 7 = 0, '[]', (:first + i) % 4 <> 0, (:first + i) % 23 = 0, false, false, false, '{}',
       'PRIMARY', (:first + i) % 11 = 0, 1, 0,
       setweight(to_tsvector('simple', public.f_unaccent(s.subject)), 'A') ||
       setweight(to_tsvector('simple', public.f_unaccent('sender' || ((:first + i) % 2000) || ' partner' || ((:first + i) % 50))), 'B') ||
       setweight(to_tsvector('simple', public.f_unaccent(s.body)), 'D'),
       ARRAY[:'email'], now(), now()
FROM perf_target t, perf_words pw, generate_series(0, :n - 1) i,
     LATERAL (SELECT now() - ((:first + i) * interval '97 seconds') AS at) r,
     LATERAL (SELECT
         pw.w[1 + (:first + i) % 96] || ' ' || pw.w[1 + ((:first + i) / 7) % 96] || ' ' || pw.w[1 + ((:first + i) / 49) % 96] || ' #' || (:first + i) AS subject,
         array_to_string(ARRAY(SELECT pw.w[1 + (((:first + i) * 31 + k * 17) % 96)] FROM generate_series(1, 40) k), ' ') AS body) s;

-- INBOX (and the Primary tab) for 60 % of the messages, UIDs after the label's current uid_next.
WITH picked AS (
    SELECT t.tenant_id, ('00000000-0000-4000-9000-' || lpad(to_hex(:first + i), 12, '0'))::uuid AS id, row_number() OVER (ORDER BY i) AS rn, t.inbox, t.primary_tab
    FROM perf_target t, generate_series(0, :n - 1) i WHERE (:first + i) % 5 < 3
), base AS (SELECT uid_next AS inbox_next, (SELECT uid_next FROM mail.labels WHERE id = (SELECT primary_tab FROM perf_target)) AS primary_next
            FROM mail.labels WHERE id = (SELECT inbox FROM perf_target))
INSERT INTO mail.message_labels (tenant_id, message_id, label_id, imap_uid, modseq)
SELECT p.tenant_id, p.id, l.label, l.next + p.rn - 1, 1
FROM picked p, base b, LATERAL (VALUES (p.inbox, b.inbox_next), (p.primary_tab, b.primary_next)) l(label, next);

UPDATE mail.labels l SET uid_next = uid_next + (SELECT count(*) FROM generate_series(0, :n - 1) i WHERE (:first + i) % 5 < 3)
WHERE l.id IN ((SELECT inbox FROM perf_target), (SELECT primary_tab FROM perf_target));

COMMIT;
ANALYZE mail.messages;
ANALYZE mail.message_labels;
ANALYZE mail.threads;
