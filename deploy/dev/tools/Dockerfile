# Test-only verification toolbox (PROMPT Z2). Independent implementations used as references
# against BlueMail's own code. Never part of a production image.
FROM python:3.12.12-slim-bookworm
RUN apt-get update \
 && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
      swaks libnet-ssleay-perl libio-socket-ssl-perl libauthen-sasl-perl dnsutils openssl ca-certificates netcat-openbsd \
 && rm -rf /var/lib/apt/lists/*
COPY requirements.txt /tmp/requirements.txt
RUN pip install --no-cache-dir -r /tmp/requirements.txt
WORKDIR /work
CMD ["sleep", "infinity"]
