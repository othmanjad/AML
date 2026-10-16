# AML Platform API (.NET 8). Build context: repository root.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/AML.Domain/AML.Domain.csproj src/AML.Domain/
COPY src/AML.Application/AML.Application.csproj src/AML.Application/
COPY src/AML.Infrastructure/AML.Infrastructure.csproj src/AML.Infrastructure/
COPY src/AML.Api/AML.Api.csproj src/AML.Api/
RUN dotnet restore src/AML.Api/AML.Api.csproj
COPY src/AML.Domain/ src/AML.Domain/
COPY src/AML.Application/ src/AML.Application/
COPY src/AML.Infrastructure/ src/AML.Infrastructure/
COPY src/AML.Api/ src/AML.Api/
RUN dotnet publish src/AML.Api/AML.Api.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
# Writable data (uploaded evidence, log files) lives on a volume owned by the non-root "app" user.
RUN mkdir -p /data/storage /data/logs && chown -R app:app /data
USER app
ENV ASPNETCORE_URLS=http://+:8080 \
    Storage__Root=/data/storage \
    DOTNET_RUNNING_IN_CONTAINER=true
EXPOSE 8080
ENTRYPOINT ["dotnet", "AML.Api.dll"]
