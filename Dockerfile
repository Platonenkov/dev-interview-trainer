# syntax=docker/dockerfile:1

# 10.0.303 is the SDK the wasm-tools workload set is pinned to (ADR 0013), but MCR publishes no 10.0.303 image; 10.0.302
# is in the same 10.0.3xx feature band, which is what the workload install needs.
FROM mcr.microsoft.com/dotnet/sdk:10.0.302@sha256:72dd743782f2ae7e5476fd64f6a460045e3998dc862218b80e6944cba79a01b0 AS build
ARG APP_VERSION=local
WORKDIR /src

# emscripten inside wasm-tools needs python3. Relinking the client runtime drops the ICU code that invariant
# globalization no longer needs; the size check below fails the build if the prebuilt runtime is shipped instead.
RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 \
    && rm -rf /var/lib/apt/lists/* \
    && dotnet workload install wasm-tools --version 10.0.303

# .editorconfig marks the migrations as generated code; without it the analyzers fail the Release build on them.
COPY global.json dotnet-tools.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Trainer.Domain/Trainer.Domain.csproj src/Trainer.Domain/
COPY src/Trainer.Contracts/Trainer.Contracts.csproj src/Trainer.Contracts/
COPY src/Trainer.Application/Trainer.Application.csproj src/Trainer.Application/
COPY src/Trainer.Content/Trainer.Content.csproj src/Trainer.Content/
COPY src/Trainer.Infrastructure/Trainer.Infrastructure.csproj src/Trainer.Infrastructure/
COPY src/Trainer.Web.Client/Trainer.Web.Client.csproj src/Trainer.Web.Client/
COPY src/Trainer.Web/Trainer.Web.csproj src/Trainer.Web/
RUN dotnet restore src/Trainer.Web/Trainer.Web.csproj

COPY src/ src/
RUN dotnet publish src/Trainer.Web -c Release -o /out \
    -p:InformationalVersion=$APP_VERSION -p:IncludeSourceRevisionInInformationalVersion=false

RUN set -e; \
    wasm="$(ls /out/wwwroot/_framework/dotnet.native.*.wasm.br)"; \
    size="$(stat -c %s "$wasm")"; \
    echo "$wasm is $size bytes"; \
    [ "$size" -le 600000 ] || { echo "The WebAssembly runtime was not relinked (prebuilt is about 977 KB, relinked about 468 KB)"; exit 1; }

# The bundle reads TRAINER_DB through TrainerDbContextFactory; the connection string is never an argument.
RUN dotnet tool restore \
    && dotnet ef migrations bundle --project src/Trainer.Infrastructure -r linux-x64 --configuration Release --force -o /out-migrate/efbundle

# Not chiseled: the Compose health check needs curl and SkiaSharp needs the glibc user-space libraries.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble@sha256:bfd912c83295ce66d4b706ebb8541b9f14259b0e76fbb88b2751f58ce2399d2e AS final
ARG APP_VERSION=local
LABEL org.opencontainers.image.revision=$APP_VERSION

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /out ./
COPY --from=build /out-migrate/efbundle ./migrate/efbundle

# Docker copies a mount point's ownership into a new empty named volume, so the ogcache volume is writable by the app.
RUN install -d -o "$APP_UID" -g "$APP_UID" -m 0750 /var/cache/dit/og

ENV App__OpenGraph__CacheDirectory=/var/cache/dit/og
EXPOSE 8080 8081
USER $APP_UID
ENTRYPOINT ["dotnet", "/app/Trainer.Web.dll"]
