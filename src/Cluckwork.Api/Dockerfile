# --- Web build: compile the React/Vite SPA to static assets ---
FROM node:24-alpine AS web-build
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
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
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
# #264 — DEPLOY CONSTRAINT: the runtime image MUST carry the tz database
# (tzdata + ICU). The farm clock resolves IANA zones via
# TimeZoneInfo.FindSystemTimeZoneById for every safety-sensitive local-day
# decision (daily-entry boundary, 7-day auto-lock, FIFO availability) and FAILS
# CLOSED on an unresolvable zone. The Debian aspnet:10.0 image below ships both.
# Do NOT switch to an Alpine/chiseled base without tzdata, and do NOT set
# InvariantGlobalization=true — either would break every farm's date logic at
# once. Program.cs asserts a representative IANA id resolves at boot, so a
# regression here fails the container start loudly instead of silently.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
EXPOSE 8080

FROM runtime AS final
COPY --from=build /out .
COPY --from=web-build /web/dist ./wwwroot
COPY --from=web-build /web/sourcemaps ./sourcemaps
ENTRYPOINT ["dotnet", "Cluckwork.Api.dll"]
