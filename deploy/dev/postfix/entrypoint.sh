#!/bin/sh
set -eu
main() {
  # Apply settings idempotently (container restarts must not duplicate entries).
  grep -v '^\s*#' /etc/postfix/main.cf.append | grep -v '^\s*$' | while IFS= read -r line; do
    postconf -e "$line"
  done
  # No chroot: chrooted services would not see the dev resolver.
  postconf -F '*/*/chroot = n'
  postconf -e "smtp_dns_support_level = enabled"
  mkdir -p /etc/postfix/tls /var/mail/vhosts /run/opendkim /etc/opendkim/keys
  if [ ! -f /etc/postfix/tls/cert.pem ]; then
    openssl req -x509 -newkey rsa:2048 -nodes -days 3650 -subj "/CN=mail.doitac.test" \
      -keyout /etc/postfix/tls/key.pem -out /etc/postfix/tls/cert.pem 2>/dev/null
  fi
  cp /dkim/doitac.test.private.pem /etc/opendkim/keys/
  chown -R opendkim:opendkim /etc/opendkim /run/opendkim
  chmod 600 /etc/opendkim/keys/doitac.test.private.pem
  id vmail >/dev/null 2>&1 || { groupadd -g 5000 vmail; useradd -u 5000 -g vmail -d /var/mail/vhosts -s /usr/sbin/nologin vmail; }
  chown -R vmail:vmail /var/mail/vhosts
  postmap /etc/postfix/recipient_access /etc/postfix/virtual_mailboxes
  cp /etc/resolv.conf /var/spool/postfix/etc/resolv.conf 2>/dev/null || true
  opendkim -x /etc/opendkim.conf
  exec postfix start-fg
}
main "$@"
