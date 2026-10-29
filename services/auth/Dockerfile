# Build context is the repo root. Framework-dependent publish on the Alpine ASP.NET runtime
# (busybox wget serves the compose/ECS health checks). ReadyToRun: precompiled, so a new task's first
# requests don't pay for JIT (measured ~4 s cold on 0.25 vCPU Fargate before this).
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY packages/shared/DocBridge.Shared.csproj packages/shared/
COPY services/auth/DocBridge.Auth.csproj services/auth/

RUN dotnet restore services/auth/DocBridge.Auth.csproj -r linux-musl-x64 -p:PublishReadyToRun=true
COPY packages/shared packages/shared
COPY services/auth services/auth

RUN dotnet publish services/auth/DocBridge.Auth.csproj -c Release -r linux-musl-x64 --self-contained false -p:PublishReadyToRun=true -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
WORKDIR /app
COPY --from=build /out .
# Local compose mounts a volume here for the generated dev signing key (unused in AWS).
RUN mkdir /keys && chown $APP_UID /keys
USER $APP_UID
EXPOSE 3001
ENTRYPOINT ["dotnet", "DocBridge.Auth.dll"]
