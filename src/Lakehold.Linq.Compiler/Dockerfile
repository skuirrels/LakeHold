# Optional, credential-free LINQ planner. It receives only source and a catalog schema snapshot.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

COPY Directory.Packages.props Directory.Build.props ./
COPY src/Lakehold.Linq.Compiler/Lakehold.Linq.Compiler.csproj src/Lakehold.Linq.Compiler/
COPY src/Lakehold.Querying/Lakehold.Querying.csproj src/Lakehold.Querying/
RUN RID=$(case "$TARGETARCH" in amd64) echo linux-x64 ;; arm64) echo linux-arm64 ;; *) exit 1 ;; esac) \
    && echo "$RID" > /rid \
    && dotnet restore src/Lakehold.Linq.Compiler/Lakehold.Linq.Compiler.csproj -r "$RID"

COPY src/Lakehold.Linq.Compiler/ src/Lakehold.Linq.Compiler/
COPY src/Lakehold.Querying/ src/Lakehold.Querying/
RUN dotnet publish src/Lakehold.Linq.Compiler/Lakehold.Linq.Compiler.csproj \
    -c Release -o /app -r "$(cat /rid)" --self-contained false --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build --chown=app:app /app ./
USER app
ENV ASPNETCORE_URLS=http://0.0.0.0:5210 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_gcServer=1
EXPOSE 5210
HEALTHCHECK --interval=15s --timeout=5s --start-period=10s --retries=3 \
    CMD curl -fsS http://localhost:5210/ready || exit 1
ENTRYPOINT ["dotnet", "Lakehold.Linq.Compiler.dll"]
