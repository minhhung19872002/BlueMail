#!/bin/sh
# Phase 7 acceptance check: filters, blocking, vacation and forwarding act on mail arriving over SMTP
# (PROMPT section 13, Phase 7). Requires the dev stack with mx and outbound on current code and a migrated,
# seeded database (scripts/dev-run.sh identity once). Rules are set up in the database the way the API stores them.
set -eu

main() {
  compose="docker compose -f deploy/docker-compose.dev.yml --env-file deploy/.env.dev"
  marker="p7-$(date +%s)"
  fail=0

  check() { # name, command
    if (eval "$2") >/dev/null 2>&1; then echo "PASS $1"; else echo "FAIL $1"; fail=1; fi
  }

  sql() {
    MSYS_NO_PATHCONV=1 $compose exec -T postgres psql -U bluemail -d bluemail -v ON_ERROR_STOP=1 -tAc "$1"
  }

  uid() { sql "select id from identity.users where primary_email = '$1'"; }
  mbx() { sql "select m.id from mail.mailboxes m join identity.users u on u.id = m.owner_id where u.primary_email = '$1'"; }

  minh=$(uid minh@bluemail.test); hoa=$(uid hoa@bluemail.test); tuan=$(uid tuan@bluemail.test); ngoc=$(uid ngoc@bluemail.test)
  yen=$(uid yen@bluemail.test)
  tenant=$(sql "select tenant_id from identity.users where primary_email = 'minh@bluemail.test'")

  # Rules: a filter for minh, a block for hoa, a vacation for tuan, a verified active forward for ngoc.
  sql "update mail.filters set deleted_at = now() where user_id = '$minh' and criteria like '%p7-%';
       insert into mail.filters (id, tenant_id, user_id, mailbox_id, criteria, actions, sort_order, is_enabled, created_at, updated_at)
       values (gen_random_uuid(), '$tenant', '$minh', '$(mbx minh@bluemail.test)', 'from:an@doitac.test subject:\"hoa don\" $marker',
               '{\"skipInbox\":true,\"markRead\":true,\"star\":true}', 1, true, now(), now())" >/dev/null
  sql "insert into mail.blocked_senders (id, tenant_id, user_id, address_or_domain, created_at, updated_at)
       values (gen_random_uuid(), '$tenant', '$hoa', 'binh@doitac.test', now(), now()) on conflict do nothing" >/dev/null
  sql "insert into mail.vacation (user_id, tenant_id, enabled, subject, html, only_contacts, only_organization, updated_at)
       values ('$tuan', '$tenant', true, 'Out of office $marker', '<p>Tôi đang nghỉ phép.</p>', false, false, now())
       on conflict (user_id) do update set enabled = true, subject = excluded.subject;
       delete from mail.vacation_replies where user_id = '$tuan'" >/dev/null
  sql "update mail.forwarding set is_active = false where user_id = '$ngoc';
       insert into mail.forwarding (id, tenant_id, user_id, target_email, verified_at, failed_attempts, is_active, keep_copy, created_at, updated_at)
       values (gen_random_uuid(), '$tenant', '$ngoc', 'binh@doitac.test', now(), 0, true, true, now(), now())
       on conflict (tenant_id, user_id, target_email) where deleted_at is null do update set is_active = true, verified_at = now()" >/dev/null
  # yen forwards to an address Postfix refuses: the bounce must reach the original sender through SRS.
  sql "delete from mta.suppressions where address = 'nobody@doitac.test';
       update mail.forwarding set is_active = false where user_id = '$yen';
       insert into mail.forwarding (id, tenant_id, user_id, target_email, verified_at, failed_attempts, is_active, keep_copy, created_at, updated_at)
       values (gen_random_uuid(), '$tenant', '$yen', 'nobody@doitac.test', now(), 0, true, true, now(), now())
       on conflict (tenant_id, user_id, target_email) where deleted_at is null do update set is_active = true, verified_at = now()" >/dev/null

  MSYS_NO_PATHCONV=1 $compose exec -T tools python /work/tests/interop/rules_checks.py "$marker" "" || fail=1

  labels() {
    sql "select string_agg(coalesce(l.system_key, l.name), ',' order by coalesce(l.system_key, l.name)) from mail.messages m
         join mail.message_labels ml on ml.message_id = m.id join mail.labels l on l.id = ml.label_id where m.subject = '$1'"
  }
  check "filter: minh's matching mail skipped Inbox, starred" "test \"\$(labels 'Hoa don thang 11 $marker')\" = 'CATEGORY_PRIMARY,STARRED'"
  check "filter: marked read and flagged" "test \"\$(sql \"select seen::text || flagged::text from mail.messages where subject = 'Hoa don thang 11 $marker'\")\" = 'truetrue'"
  check "filter: mailing-list mail not matched stays in Inbox" "labels 'Newsletter $marker' | grep -q INBOX"
  check "block: hoa's mail from binh goes to Spam" "test \"\$(labels 'Blocked $marker')\" = 'SPAM'"
  check "deliveredto: stored with the envelope recipient" "test \"\$(sql \"select delivered_to[1] from mail.messages where subject = 'Forward $marker'\")\" = 'ngoc@bluemail.test'"

  # SRS: use the return path of the forward that was just queued.
  srs=$(sql "select envelope_from from mta.outbound_messages where envelope_from like 'SRS0=%' order by created_at desc limit 1")
  MSYS_NO_PATHCONV=1 $compose exec -T tools python /work/tests/interop/rules_checks.py "$marker-srs" "$srs" >/tmp/bm-p7-srs.txt 2>&1 || true; cat /tmp/bm-p7-srs.txt | grep -v "^{"
  check "SRS: bounces relayed back, other senders and forged addresses refused" "grep -q '^PASS SRS' /tmp/bm-p7-srs.txt"

  sql "update mail.vacation set enabled = false where user_id = '$tuan'; update mail.forwarding set is_active = false where user_id in ('$ngoc', '$yen');
       update mail.blocked_senders set deleted_at = now() where user_id = '$hoa' and address_or_domain = 'binh@doitac.test'" >/dev/null

  if [ "$fail" -eq 0 ]; then echo "Phase 7 check: all passed"; else echo "Phase 7 check: FAILED"; exit 1; fi
}

main "$@"
