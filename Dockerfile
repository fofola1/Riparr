# Stage 1: Build the C# ASP.NET Core application
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files and restore packages
COPY src/*.csproj ./
RUN dotnet restore

# Copy all source files and build
COPY src/ ./
RUN dotnet publish -c Release -o /app

# Stage 2: Create the runtime environment with native tools
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Install system utilities, ffmpeg, aria2, python3 (needed by yt-dlp), and fzf (needed by ani-cli)
RUN apt-get update && apt-get install -y --no-install-recommends \
    curl \
    ca-certificates \
    ffmpeg \
    aria2 \
    python3 \
    git \
    fzf \
    && rm -rf /var/lib/apt/lists/*

# Install the latest yt-dlp binary
RUN curl -L https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp -o /usr/bin/yt-dlp \
    && chmod a+rx /usr/bin/yt-dlp

# Install the latest ani-cli script
RUN curl -L https://raw.githubusercontent.com/pystardust/ani-cli/master/ani-cli -o /usr/bin/ani-cli \
    && chmod a+rx /usr/bin/ani-cli

# Set application variables
ENV PORT=8080
ENV DATABASE_PATH=/downloads/downloads.db
ENV DOWNLOADS_COMPLETED_DIR=/downloads/completed
ENV DOWNLOADS_INCOMPLETE_DIR=/downloads/incomplete

# Create downloads volume mount point
RUN mkdir -p /downloads/completed /downloads/incomplete

# Expose port
EXPOSE 8080

# Copy the built application from stage 1
COPY --from=build /app .

# Start the application
ENTRYPOINT ["dotnet", "Riparr.dll"]
