# Test-only "external" mail server for doitac.test: Postfix + OpenDKIM (signs) + dkimpy-milter (verifies, BUG-027).
# Never part of a production image (see PROMPT section 0 / 11).
FROM debian:bookworm-slim
RUN apt-get update \
 && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
      postfix opendkim opendkim-tools dkimpy-milter ca-certificates openssl \
 && rm -rf /var/lib/apt/lists/*
COPY main.cf.append /etc/postfix/main.cf.append
COPY recipient_access virtual_mailboxes /etc/postfix/
COPY opendkim.conf /etc/opendkim.conf
COPY dkimpy-milter.conf /etc/dkimpy-milter/dkimpy-milter.conf
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh
EXPOSE 25
ENTRYPOINT ["/entrypoint.sh"]
