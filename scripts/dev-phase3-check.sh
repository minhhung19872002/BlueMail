#!/bin/sh
# Phase 3 acceptance check: BlueMail.Mx receives mail (PROMPT section 13, Phase 3).
# Requires: dev stack with mx running and the database migrated + seeded (scripts/dev-run.sh identity once):
#   docker compose -f deploy/docker-compose.dev.yml --env-file deploy/.env.dev up -d --build
set -eu

main() {
  compose="docker compose -f deploy/docker-compose.dev.yml --env-file deploy/.env.dev"
  tools="$compose exec -T tools"
  marker="p3-$(date +%s)"
  fail=0

  check() { # name, command
    if (eval "$2") >/dev/null 2>&1; then echo "PASS $1"; else echo "FAIL $1"; fail=1; fi
  }

  sql() { # query -> trimmed single value
    MSYS_NO_PATHCONV=1 $compose exec -T postgres psql -U bluemail -d bluemail -tAc "$1"
  }

  # 1. SMTP behaviour (python smtplib / raw sockets as the reference client)
  MSYS_NO_PATHCONV=1 $tools python /work/tests/interop/mx_checks.py "$marker" || fail=1

  # 2. Real MTA path: Postfix of doitac.test (DKIM-signing, SPF-listed) relays to mx.bluemail.test
  $tools swaks --server 10.77.0.30 --from an@doitac.test --to minh@bluemail.test --header "Subject: postfix $marker" \
    --body "Xin chào từ doitac.test" >/dev/null 2>&1 || fail=1
  i=0
  while [ "$(sql "select count(*) from mail.messages where subject = 'postfix $marker'")" != "1" ] && [ $i -lt 30 ]; do sleep 1; i=$((i + 1)); done

  q="select auth_results->>'spf' || ',' || (auth_results->'dkim'->0->>'result') || ',' || (auth_results->>'dmarc')
     from mail.messages where subject = 'postfix $marker'"
  check "postfix -> mx: stored once with spf=pass, dkim=pass, dmarc=pass" "test \"\$(sql \"$q\")\" = 'pass,pass,pass'"
  check "postfix -> mx: in minh's INBOX with an IMAP UID" "test \"\$(sql \"select count(*) from mail.messages m
     join mail.message_labels ml on ml.message_id = m.id join mail.labels l on l.id = ml.label_id
     join mail.mailboxes b on b.id = m.mailbox_id join identity.users u on u.id = b.owner_id
     where m.subject = 'postfix $marker' and l.system_key = 'INBOX' and u.primary_email = 'minh@bluemail.test' and ml.imap_uid > 0\")\" = 1"

  # 3. What mx_checks sent: stored exactly where it belongs, refused mail stored nowhere
  check "BDAT message stored, dot line intact" "sql \"select snippet from mail.messages where subject = 'bdat $marker'\" | grep -q '^\.line starting with a dot'"
  check "pipelined message stored for the valid recipient only" "test \"\$(sql \"select count(*) from mail.messages where subject = 'pipelined $marker'\")\" = 1"
  check "UTF-8 subject stored" "test \"\$(sql \"select count(*) from mail.messages where subject = 'Tiếng Việt có dấu $marker-utf8'\")\" = 1"
  check "group + member + alias: one copy per mailbox (minh once, 4 members)" "test \"\$(sql \"select count(*) || ',' || count(distinct mailbox_id) from mail.messages where subject = 'group $marker'\")\" = '4,4'"
  check "oversized, spoofed and dangerous messages stored nowhere" "test \"\$(sql \"select count(*) from mail.messages where subject in ('big $marker', 'spoof $marker', 'malware $marker')\")\" = 0"
  check "mail_log: rejections recorded with SMTP response" "sql \"select smtp_response from admin.mail_log where status = 'REJECTED' and occurred_at > now() - interval '10 minutes'\" | grep -q '550 5.1.1'"
  check "mail_log: DMARC rejection recorded" "sql \"select smtp_response from admin.mail_log where status = 'REJECTED' and occurred_at > now() - interval '10 minutes'\" | grep -q DMARC"
  check "message blob stored once per tenant (dedup by SHA-256)" "test \"\$(sql \"select count(*) from mail.messages m where m.subject = 'group $marker' and m.blob_sha256 = (select blob_sha256 from mail.messages where subject = 'group $marker' limit 1)\")\" = 4"

  if [ "$fail" -ne 0 ]; then echo "Phase 3 check: FAILED"; exit 1; fi
  echo "Phase 3 check: all passed"
}

main "$@"
