# --- Web build: compile the React/Vite SPA to static assets ---
# #267 — base images are pinned to an immutable @sha256 digest (the multi-arch
# index digest) so a rebuild resolves the SAME bytes CI scanned, never a silently
# re-tagged "latest 10.0". Dependabot's `docker` ecosystem opens the bump PR when
# a newer digest ships a security fix; refresh with
#   docker buildx imagetools inspect <image>:<tag> --format '{{.Manifest.Digest}}'
FROM node:26-alpine@sha256:aadf416b2cdce311a8811ba3f0608a61b77dbf997500e2eafe781b51f6a0b019 AS web-build
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
RUN npm run build
# #217 — sourcemaps are an OPERATOR tool, not a public asset. The build emits
# them "hidden" (no sourceMappingURL comment), but anything left in dist/ ends
# up in wwwroot where a predictable "<bundle>.js.map" URL would serve the full
# original sources to anyone. Park them next to the app instead: resolve a
# reported stack with  docker cp <container>:/app/sourcemaps .
RUN mkdir /web/sourcemaps && \
    find /web/dist -name '*.map' -exec mv {} /web/sourcemaps/ \;

# --- API build: restore + publish the .NET app ---
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c AS build
WORKDIR /src
# #315 — copy EVERY restore input (Directory.Build.props + each project file AND
# its packages.lock.json, for the API project and the projects it references)
# BEFORE the restore layer, then restore with --locked-mode. This makes the image
# resolve the SAME deterministic NuGet graph CI enforces: a stale/hand-edited lock
# fails the build (NU1004) instead of silently floating to a different version.
# Directory.Build.props sets RestorePackagesWithLockFile, so it must precede
# restore. Only the SERVING projects are restored here (not the test projects),
# so only their four lock files are needed. Copying just these inputs (not the
# full source tree) keeps the restore layer cached across source-only changes.
COPY Directory.Build.props ./
COPY Cluckwork.sln ./
COPY src/Cluckwork.Domain/Cluckwork.Domain.csproj src/Cluckwork.Domain/packages.lock.json src/Cluckwork.Domain/
COPY src/Cluckwork.Application/Cluckwork.Application.csproj src/Cluckwork.Application/packages.lock.json src/Cluckwork.Application/
COPY src/Cluckwork.Infrastructure/Cluckwork.Infrastructure.csproj src/Cluckwork.Infrastructure/packages.lock.json src/Cluckwork.Infrastructure/
COPY src/Cluckwork.Api/Cluckwork.Api.csproj src/Cluckwork.Api/packages.lock.json src/Cluckwork.Api/
RUN dotnet restore src/Cluckwork.Api/Cluckwork.Api.csproj --locked-mode
COPY . .
RUN dotnet publish src/Cluckwork.Api/Cluckwork.Api.csproj -c Release -o /out --no-restore

# --- Runtime: API serves both the JSON API and the SPA from wwwroot ---
# #264 — DEPLOY CONSTRAINT: the runtime image MUST carry the tz database. The
# farm clock resolves IANA zones via TimeZoneInfo.FindSystemTimeZoneById for
# every safety-sensitive local-day decision (daily-entry boundary, 7-day
# auto-lock, FIFO availability) and FAILS CLOSED on an unresolvable zone. The
# Ubuntu 24.04 (Noble) aspnet:10.0 image below ships tzdata; do NOT switch to an
# Alpine/chiseled base WITHOUT tzdata — that would break every farm's date logic at once.
# Program.cs asserts a representative IANA id resolves at boot, so this regression
# fails the container start loudly instead of silently.
# Separately, do NOT set InvariantGlobalization=true: it does not break tz lookup
# on Linux (tzdata is read independently of ICU), but it breaks culture-dependent
# currency/locale formatting the app relies on. Keep ICU (the default here).
FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:a4556ed033fa96f984bb7a8d348851cb2d36b1281dd2420070045f664fbb5f94 AS runtime
WORKDIR /app
EXPOSE 8080

FROM runtime AS final
COPY --from=build /out .
COPY --from=web-build /web/dist ./wwwroot
COPY --from=web-build /web/sourcemaps ./sourcemaps
# #267 — drop root for the request-serving process. The aspnet:10.0 base image
# already ships a non-root `app` account (uid/gid 1654, exposed as $APP_UID); we
# reuse it rather than mint our own. The numeric $APP_UID (not the name) satisfies
# a Kubernetes `runAsNonRoot` gate, which can't verify a username maps to non-zero.
# The COPYs above run as root and land world-readable files, so the app can read
# wwwroot + the published DLLs; binding :8080 needs no privilege (>1024). Runtime
# writes nothing under /app — DataProtection keys go to $HOME (/home/app).
USER $APP_UID
# #266 — container readiness probe. The hardened runtime image ships no
# curl/wget (#267), so the probe rides the same binary: `healthcheck` is a
# run-then-exit verb that GETs /health/ready over loopback and exits non-zero
# until dependencies are green (DB reachable + migrations applied). Compose /
# an orchestrator then stops routing to an unready instance. start-period covers
# boot — the app waits on the one-shot migrate job (#263), so readiness flips
# green shortly after start; retries avoid flapping on a transient blip.
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD ["dotnet", "Cluckwork.Api.dll", "healthcheck"]
ENTRYPOINT ["dotnet", "Cluckwork.Api.dll"]
