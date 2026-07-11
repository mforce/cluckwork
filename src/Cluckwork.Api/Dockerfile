FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
EXPOSE 8080

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

FROM runtime AS final
COPY --from=build /out .
ENTRYPOINT ["dotnet", "Cluckwork.Api.dll"]
