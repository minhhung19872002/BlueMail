# Web SPA: built with Node, served as static files by Caddy. /config.json is written at start from env,
# so one image serves every installation.
FROM node:22.23.5-alpine3.24 AS build
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY web/ ./
RUN npx vitest run && npx tsc -b --noEmit && npx vite build

FROM caddy:2.10.2-alpine
COPY --from=build /web/dist /srv
COPY deploy/docker/web.Caddyfile /etc/caddy/Caddyfile
COPY deploy/docker/web-entrypoint.sh /usr/local/bin/web-entrypoint
RUN chmod 0755 /usr/local/bin/web-entrypoint
EXPOSE 8080
ENTRYPOINT ["/usr/local/bin/web-entrypoint"]
