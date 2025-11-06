# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# copy csproj and restore first for better layer caching
COPY IncludeDamon/IncludeDamon.csproj IncludeDamon/
RUN dotnet restore IncludeDamon/IncludeDamon.csproj

# copy the rest and publish a single-file, self-contained binary
COPY . .
RUN dotnet publish IncludeDamon/IncludeDamon.csproj \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:PublishTrimmed=true \
    -p:AssemblyName=includedamon \
    -o /app/out

# Runtime stage
FROM mcr.microsoft.com/dotnet/runtime-deps:8.0
COPY --from=build /app/out/includedamon /usr/local/bin/includedamon
RUN chmod +x /usr/local/bin/includedamon

ENV TARGETS=
ENV SLACK_WEBHOOK_URL=
ENV DESTROY_FAULTY_PODS=false
ENV LABEL_SELECTOR_FORMAT=""
ENV RESPONSE_TIMEOUT_SECONDS=5
ENV ISSUE_WINDOW_SECONDS=60
ENV STARTUP_WINDOW_SECONDS=60
ENV RESOURCE_ISSUE_WINDOW_SECONDS=60
ENV RESTART_THRESHOLD=60

CMD ["includedamon"]
