#!/bin/sh
# Phase 6 acceptance check: spam scoring in BlueMail.Mx (PROMPT section 13, Phase 6).
# Requires the dev stack with mx running on current code and a migrated database (scripts/dev-run.sh identity once).
set -eu

main() {
  compose="docker compose -f deploy/docker-compose.dev.yml --env-file deploy/.env.dev"
  marker="p6-$(date +%s)"
  fail=0

  check() { # name, command
    if (eval "$2") >/dev/null 2>&1; then echo "PASS $1"; else echo "FAIL $1"; fail=1; fi
  }

  sql() {
    MSYS_NO_PATHCONV=1 $compose exec -T postgres psql -U bluemail -d bluemail -tAc "$1"
  }

  label_of() { # subject prefix -> system labels of minh's copy
    sql "select string_agg(l.system_key, ',' order by l.system_key) from mail.messages m
         join mail.message_labels ml on ml.message_id = m.id join mail.labels l on l.id = ml.label_id
         where m.subject like '$1%$marker' and l.system_key in ('INBOX', 'SPAM')"
  }

  MSYS_NO_PATHCONV=1 $compose exec -T tools python /work/tests/interop/spam_checks.py "$marker" || fail=1

  check "ordinary mail lands in INBOX" "test \"\$(label_of 'Lịch giao hàng')\" = 'INBOX'"
  check "phishing lands in SPAM, not INBOX" "test \"\$(label_of 'Your password expires')\" = 'SPAM'"
  check "spam copy stores its score and rule report" "test \"\$(sql \"select (spam_score >= 5)::text || ',' || (jsonb_array_length(spam_report->'spam'->'hits') > 2)::text
     from mail.messages where subject like 'Your password expires%$marker'\")\" = 'true,true'"
  check "look-alike domain rule fired" "sql \"select spam_report::text from mail.messages where subject like 'Your password expires%$marker'\" | grep -q FROM_LOOKALIKE_DOMAIN"
  check "rejected spam is stored nowhere" "test \"\$(sql \"select count(*) from mail.messages where subject like 'URGENT%$marker'\")\" = 0"
  check "mail_log: spam rejection recorded with its score" "test \"\$(sql \"select count(*) from admin.mail_log where status = 'REJECTED'
     and smtp_response like '550%spam%' and spam_score >= 15 and occurred_at > now() - interval '5 minutes'\")\" -ge 1"

  if [ "$fail" -eq 0 ]; then echo "Phase 6 check: all passed"; else echo "Phase 6 check: FAILED"; exit 1; fi
}

main "$@"
