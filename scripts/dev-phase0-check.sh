#!/bin/sh
# Phase 0 acceptance check for the dev test network (PROMPT Z1, Z2).
# Requires: dev stack running. Since Phase 3, mail for bluemail.test is received by BlueMail.Mx (10.77.0.25);
# what the Phase 0 sink checked (delivery + DKIM pass) is now checked by scripts/dev-phase3-check.sh.
#   docker compose -f deploy/docker-compose.dev.yml --env-file deploy/.env.dev --profile phase0 up -d --build
set -eu

main() {
  compose="docker compose -f deploy/docker-compose.dev.yml --env-file deploy/.env.dev"
  tools="$compose exec -T tools"
  fail=0

  check() { # name, command
    if sh -c "$2" >/dev/null 2>&1; then echo "PASS $1"; else echo "FAIL $1"; fail=1; fi
  }

  check "dns: MX bluemail.test" "$tools dig +short MX bluemail.test | grep -q 'mx.bluemail.test'"
  # Ask CoreDNS itself: Docker's embedded DNS answers PTR of container addresses with container names.
  check "dns: PTR 10.77.0.26" "$tools dig +short -x 10.77.0.26 @10.77.0.53 | grep -q 'out1.bluemail.test'"
  check "dns: DMARC doitac.test" "$tools dig +short TXT _dmarc.doitac.test | grep -q 'p=reject'"
  check "dns: null MX" "$tools dig +short MX nullmx.doitac.test | grep -q '^0 \.$'"
  check "dns: big TXT needs TCP" "$tools sh -c 'dig +notcp +ignore +bufsize=1232 TXT bigtxt.bluemail.test | grep -q \"flags:.* tc\"'"
  check "tools: dnspython resolves via CoreDNS" "$tools python -c \"import dns.resolver as r; assert r.resolve('mx.bluemail.test','A')[0].to_text()=='10.77.0.25'\""
  check "tools: checkdmarc reads doitac.test" "$tools sh -c 'checkdmarc doitac.test --skip-tls -n 10.77.0.53 | grep -q reject'"

  marker="phase0-$(date +%s)"
  check "postfix: relay out to mx.bluemail.test" "$tools swaks --server 10.77.0.30 --from an@doitac.test --to lan@bluemail.test --header 'Subject: $marker' --tls"
  check "postfix: inbound to binh@doitac.test" "$tools swaks --server 10.77.0.30 --from probe@bluemail.test --to binh@doitac.test --header 'Subject: in-$marker'"
  sleep 2
  check "postfix: verifies DKIM of inbound mail (dkim=pass)" "MSYS_NO_PATHCONV=1 $tools python /work/tests/interop/postfix_dkim_check.py dkim-$marker"
  check "postfix: 451 for tempfail@" "$tools sh -c 'swaks --server 10.77.0.30 --from x@example.test --to tempfail@doitac.test --quit-after RCPT 2>&1 | grep -q \"451 4.3.0\"'"
  check "postfix: 550 for reject@" "$tools sh -c 'swaks --server 10.77.0.30 --from x@example.test --to reject@doitac.test --quit-after RCPT 2>&1 | grep -q \"550 5.1.1\"'"
  check "corpus: present" "test \$(ls tests/BlueMail.Corpus/messages/*/*.eml | wc -l) -ge 40"

  if [ "$fail" -ne 0 ]; then echo "Phase 0 check: FAILED"; exit 1; fi
  echo "Phase 0 check: all passed"
}

main "$@"
