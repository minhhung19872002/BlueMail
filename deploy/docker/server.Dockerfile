# One image for every BlueMail .NET process (docs/00 D1). The process is chosen by the first argument:
#   docker run bluemail/server identity|api
# More processes (mx, submission, outbound, imap, worker) join this image as they are built.
FROM mcr.microsoft.com/dotnet/sdk:10.0.404 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props BlueMail.slnx .editorconfig ./
COPY src/ src/
RUN dotnet publish src/BlueMail.Identity -c Release -o /out/identity --nologo \
 && dotnet publish src/BlueMail.Api -c Release -o /out/api --nologo

FROM mcr.microsoft.com/dotnet/aspnet:10.0.15 AS runtime
# libgssapi-krb5-2: loaded by Npgsql at start (GSS/Kerberos probing); absent it logs an error.
RUN apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 && rm -rf /var/lib/apt/lists/*
RUN groupadd -g 10001 bluemail && useradd -u 10001 -g bluemail -M -s /usr/sbin/nologin bluemail \
 && mkdir -p /var/log/bluemail && chown bluemail:bluemail /var/log/bluemail
COPY --from=build /out/ /app/
COPY deploy/docker/server-entrypoint.sh /usr/local/bin/bluemail
RUN chmod 0755 /usr/local/bin/bluemail
USER bluemail
ENV ASPNETCORE_HTTP_PORTS=8080 BM_LOG_DIR=/var/log/bluemail DOTNET_gcServer=1
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=3s --retries=5 CMD ["/usr/local/bin/bluemail", "healthcheck"]
ENTRYPOINT ["/usr/local/bin/bluemail"]
