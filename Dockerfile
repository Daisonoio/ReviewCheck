# syntax=docker/dockerfile:1
#
# Multi-stage build for the ReviewCheck MCP server. The build stage carries the full .NET SDK;
# the final image carries only the runtime + git, so it stays small and has no build tools.
#
#   docker build -t reviewcheck .
#   docker run -i --rm -v "$PWD:/repo" -e REVIEWCHECK_ANTHROPIC_KEY=sk-ant-... reviewcheck
#
# The server speaks MCP over stdio (-i), and reads the repository mounted at /repo via git.

# ---- build stage: full SDK, discarded from the final image ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy the sources and restore once (a cached layer until the projects change).
COPY ReviewCheck.sln Directory.Build.props ./
COPY src/ src/
COPY eval/ eval/
COPY tests/ tests/
RUN dotnet restore ReviewCheck.sln

# Publish the MCP server (framework-dependent — the runtime image provides the framework).
RUN dotnet publish src/ReviewCheck.Mcp/ReviewCheck.Mcp.csproj -c Release -o /app --no-restore

# ---- runtime stage: minimal, no build tools ----
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime

# The server shells out to git to read the local diff.

RUN apt-get update \
    && apt-get install -y --no-install-recommends git \
    && rm -rf /var/lib/apt/lists/* \
    && git config --system --add safe.directory '/repo'

RUN groupadd 'UtenteBase' \
    && useradd -g 'UtenteBase' 'Utente_1'

COPY --from=build /app /app

# The repository under review is mounted here and read via git.
WORKDIR /repo
ENV REVIEWCHECK_REPO=/repo
USER Utente_1
ENTRYPOINT ["dotnet", "/app/ReviewCheck.Mcp.dll"]
