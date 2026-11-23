#!/bin/sh
# Phase 12 acceptance check (PROMPT section 13): DANE against the DNSSEC-signed dev zone doitac.test, VERP return path,
# late bounce and ARF complaint through MX, TLS-RPT report. Requires the dev stack with mx, outbound, submission and
# doitac on current code and a migrated database (scripts/dev-run.sh identity once), see docs/06 Phase 12.
set -eu

main() {
  compose="docker compose -f deploy/docker-compose.dev.yml --env-file deploy/.env.dev"
  fail=0
  check() { # name, command
    if (eval "$2") >/dev/null 2>&1; then echo "PASS $1"; else echo "FAIL $1"; fail=1; fi
  }
  sql() {
    MSYS_NO_PATHCONV=1 $compose exec -T postgres psql -U bluemail -d bluemail -v ON_ERROR_STOP=1 -tAc "$1"
  }
  py() { MSYS_NO_PATHCONV=1 $compose exec -T tools python /work/tests/interop/phase12_mail.py "$@"; }
  wait_for() { # sql returning a non-empty value when done, seconds
    i=0
    while [ $i -lt "$2" ]; do
      v=$(sql "$1"); [ -n "$v" ] && { echo "$v"; return 0; }
      sleep 2; i=$((i + 2))
    done
    return 1
  }

  user=lan@bluemail.test
  pass="${BM_SEED_USER_PASSWORD:-Sample-User-Dev-1}"
  tag=$(date +%s)

  # 1. DANE: doitac.test is signed and mail.doitac.test publishes TLSA 3 1 1 of the Postfix key.
  good=$(py send "$user" "$pass" "an@doitac.test" "p12 dane $tag")
  row=$(wait_for "select r.status || '|' || coalesce(r.last_tls::text,'') from mta.outbound_recipients r join mta.outbound_messages m on m.id = r.outbound_message_id
                  where m.rfc_message_id = '$good' and r.status in ('DELIVERED','BOUNCED','DEFERRED')" 60 || true)
  check "DANE host verified, delivered over TLS" "[ '$row' = 'DELIVERED|true' ]"
  check "TLS result recorded as tlsa success" "[ -n \"\$(sql \"select 1 from mta.tls_results where policy_domain = 'doitac.test' and policy_type = 'tlsa' and failure_type is null and day = current_date\")\" ]"

  # 2. A TLSA record that matches nothing: never delivered, deferred.
  bad=$(py send "$user" "$pass" "x@dane-bad.doitac.test" "p12 dane-bad $tag")
  row=$(wait_for "select r.status || '|' || r.last_response from mta.outbound_recipients r join mta.outbound_messages m on m.id = r.outbound_message_id
                  where m.rfc_message_id = '$bad' and r.attempts > 0" 60 || true)
  check "TLSA mismatch is deferred, not sent" "echo '$row' | grep -q '^DEFERRED|'"
  check "TLS result recorded as tlsa-invalid" "[ -n \"\$(sql \"select 1 from mta.tls_results where policy_domain = 'dane-bad.doitac.test' and failure_type = 'tlsa-invalid'\")\" ]"

  # 3. VERP: the delivered message left with bounces+<id>.<mac>@bluemail.test (Postfix logged the return path).
  msg=$(sql "select id from mta.outbound_messages where rfc_message_id = '$good'")
  verp=$(docker logs bluemail-dev-doitac-1 2>&1 | grep -o 'from=<bounces+[^>]*>' | grep "$(echo "$msg" | tr -d '-')" | head -1 | sed 's/from=<//; s/>//')
  check "VERP return path used" "[ -n '$verp' ]"

  # 4. A late bounce to that return path: recipient bounced, address suppressed, event recorded.
  if [ -n "$verp" ]; then
    py dsn "$verp" "an@doitac.test" "$good" >/dev/null
    check "late bounce recorded" "wait_for \"select 1 from mta.feedback_events where outbound_message_id = '$msg' and kind = 'ASYNC_BOUNCE'\" 20"
    check "recipient marked bounced" "[ \"\$(sql \"select status from mta.outbound_recipients where outbound_message_id = '$msg'\")\" = BOUNCED ]"
    sql "delete from mta.suppressions where address = 'an@doitac.test'" >/dev/null
  fi

  # 5. An ARF complaint to the feedback-loop address, matched by Message-ID.
  py arf "fbl@bluemail.test" "an@doitac.test" "$good" >/dev/null
  check "complaint recorded" "wait_for \"select 1 from mta.feedback_events where outbound_message_id = '$msg' and kind = 'COMPLAINT'\" 20"
  sql "delete from mta.suppressions where address = 'an@doitac.test';
       update identity.users set sending_locked_at = null, sending_lock_reason = null where primary_email = '$user'" >/dev/null

  # 6. TLS-RPT: yesterday's results for doitac.test go to its rua (tlsrpt@doitac.test) as a gzip JSON report.
  sql "delete from mta.suppressions where address = 'tlsrpt@doitac.test';
       delete from mta.tls_results where policy_domain = 'doitac.test' and day = current_date - 1;
       update mta.tls_results set day = current_date - 1, reported_at = null where policy_domain = 'doitac.test'" >/dev/null
  $compose restart outbound >/dev/null 2>&1
  check "TLS report mailed to the rua" "wait_for \"select 1 from admin.mail_log where rcpt = 'tlsrpt@doitac.test' and status = 'DELIVERED' and occurred_at > now() - interval '5 minutes'\" 150"

  [ "$fail" -eq 0 ] && echo "Phase 12 check: all passed" || { echo "Phase 12 check: FAILED"; exit 1; }
}

main "$@"
