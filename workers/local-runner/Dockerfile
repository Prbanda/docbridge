# Build context is the repo root. Framework-dependent publish on the Alpine ASP.NET runtime
# (busybox wget serves the compose/ECS health checks). ReadyToRun: precompiled, so a new task's first
# requests don't pay for JIT (measured ~4 s cold on 0.25 vCPU Fargate before this).
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY packages/shared/DocBridge.Shared.csproj packages/shared/
COPY workers/local-runner/DocBridge.Workers.LocalRunner.csproj workers/local-runner/
COPY workers/common/DocBridge.Workers.Common.csproj workers/common/
RUN dotnet restore workers/local-runner/DocBridge.Workers.LocalRunner.csproj -r linux-musl-x64 -p:PublishReadyToRun=true
COPY packages/shared packages/shared
COPY workers/local-runner workers/local-runner
COPY workers/common workers/common
RUN dotnet publish workers/local-runner/DocBridge.Workers.LocalRunner.csproj -c Release -r linux-musl-x64 --self-contained false -p:PublishReadyToRun=true -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
WORKDIR /app
COPY --from=build /out .
USER $APP_UID
EXPOSE 3005
ENTRYPOINT ["dotnet", "DocBridge.Workers.LocalRunner.dll"]
