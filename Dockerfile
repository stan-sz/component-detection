FROM mcr.microsoft.com/dotnet/sdk:6.0-cbl-mariner2.0@sha256:8192dbbb2c5472df2faf97014ada02935a70bdae0e845d4c382424570c53f450 AS build
WORKDIR /app
COPY . .
RUN dotnet publish -c Release -o out \
    -r linux-x64 \
    -p:MinVerSkip=true \
    --self-contained true \
    -p:PublishReadyToRun=false \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:PublishSingleFile=true \
    ./src/Microsoft.ComponentDetection

FROM mcr.microsoft.com/dotnet/runtime-deps:6.0-cbl-mariner2.0@sha256:3fb96c6d1b7d2f22e9a04798c61819ae8e02dfa3a76c383315f7d3a7252fa11e AS runtime
WORKDIR /app
COPY --from=build /app/out ./

RUN tdnf install -y \
    golang \
    moby-engine \
    maven \
    pnpm \
    poetry \
    python

ENTRYPOINT ["/app/Microsoft.ComponentDetection"]
