# --- Web build: compile the React/Vite SPA to static assets ---
FROM node:22-alpine AS web-build
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
RUN npm run build

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
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
EXPOSE 8080

FROM runtime AS final
COPY --from=build /out .
COPY --from=web-build /web/dist ./wwwroot
ENTRYPOINT ["dotnet", "Cluckwork.Api.dll"]
