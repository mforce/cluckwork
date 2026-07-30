# --- Web build: compile the React/Vite SPA to static assets ---
# #267 — base images are pinned to an immutable @sha256 digest (the multi-arch
# index digest) so a rebuild resolves the SAME bytes CI scanned, never a silently
# re-tagged "latest 10.0". Dependabot's `docker` ecosystem opens the bump PR when
# a newer digest ships a security fix; refresh with
#   docker buildx imagetools inspect <image>:<tag> --format '{{.Manifest.Digest}}'
FROM node:26-alpine@sha256:e88a35be04478413b7c71c455cd9865de9b9360e1f43456be5951032d7ac1a66 AS web-build
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
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:ed034a8bf0b24ded0cbbac07e17825d8e9ebfe21e308191d0f7421eaf5ad4664 AS build
WORKDIR /src
COPY Cluckwork.sln ./
COPY src/Cluckwork.Domain/Cluckwork.Domain.csproj src/Cluckwork.Domain/
COPY src/Cluckwork.Application/Cluckwork.Application.csproj src/Cluckwork.Application/
COPY src/Cluckwork.Infrastructure/Cluckwork.Infrastructure.csproj src/Cluckwork.Infrastructure/
COPY src/Cluckwork.Api/Cluckwork.Api.csproj src/Cluckwork.Api/
RUN dotnet restore src/Cluckwork.Api/Cluckwork.Api.csproj
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
FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:1fa23fc4872d95fd71c2833ebe65d7e84a43b2d51a31d119516852f13d9505a7 AS runtime
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
ENTRYPOINT ["dotnet", "Cluckwork.Api.dll"]
