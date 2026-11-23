#!/bin/sh
# Phase 11 acceptance check (PROMPT section 13): BlueMail.Submission and BlueMail.Imap with independent clients -
# Python's imaplib/smtplib (scripted checks) and Dovecot's imaptest (compliance suite).
# Requires the dev stack with submission, imap, outbound and doitac running on current code
#   docker compose -f deploy/docker-compose.dev.yml --env-file deploy/.env.dev up -d --build mx outbound submission imap
# and the imaptest scripts downloaded once into tests/interop/out/imaptest (see docs/06, Phase 11).
set -eu

main() {
  compose="docker compose -f deploy/docker-compose.dev.yml --env-file deploy/.env.dev"
  sql() {
    MSYS_NO_PATHCONV=1 $compose exec -T postgres psql -U bluemail -d bluemail -v ON_ERROR_STOP=1 -tAc "$1"
  }

  # A known dev app password for lan (hash = SHA-256 of the 16 letters, as MailLoginService stores it).
  app="devappimapcheckx"
  hash=$(printf '%s' "$app" | sha256sum | cut -d' ' -f1)
  lan=$(sql "select id from identity.users where primary_email = 'lan@bluemail.test'")
  tenant=$(sql "select tenant_id from identity.users where id = '$lan'")
  sql "delete from identity.app_passwords where user_id = '$lan' and name = 'phase11-check';
       insert into identity.app_passwords (id, tenant_id, user_id, name, secret_hash, created_at, updated_at)
       values (gen_random_uuid(), '$tenant', '$lan', 'phase11-check', '$hash', now(), now())" >/dev/null

  fail=0
  MSYS_NO_PATHCONV=1 $compose exec -T tools python /work/tests/interop/imap_submission_checks.py lan@bluemail.test "${BM_SEED_USER_PASSWORD:-Sample-User-Dev-1}" "$app" || fail=1

  tests="tests/interop/out/imaptest/imaptest-main/src/tests"
  if [ -d "$tests" ] && [ -z "$(sql "select id from identity.users where primary_email = 'imaptest@bluemail.test'")" ]; then
    echo "FAIL imaptest: create the user imaptest@bluemail.test / Imaptest-Dev-Password-1 first (docs/06, Phase 11)"
    fail=1
  elif [ -d "$tests" ]; then
    root=$(pwd -W 2>/dev/null || pwd)
    out=$(MSYS_NO_PATHCONV=1 $compose --profile imap-compliance run --rm -v "$root/$tests:/tests:ro" imaptest \
      host=imap.bluemail.test port=993 ssl=any-cert user=imaptest@bluemail.test pass=Imaptest-Dev-Password-1 test=/tests 2>&1 | tail -3)
    echo "$out"
    echo "$out" | grep -q "base protocol: 0/" && echo "PASS imaptest base protocol" || { echo "FAIL imaptest base protocol"; fail=1; }
  else
    echo "SKIP imaptest (download the scripts first, docs/06 Phase 11)"
  fi

  [ "$fail" -eq 0 ] && echo "Phase 11 check: all passed" || { echo "Phase 11 check: FAILED"; exit 1; }
}

main "$@"
