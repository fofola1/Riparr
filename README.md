# Riparr

**Riparr** is a standalone, independent download client manager designed to act as a **SABnzbd-compatible API endpoint** for the *Arr stack (Sonarr/Radarr). 

Instead of maintaining a shared database with an indexer, it is completely self-contained. It retrieves stateless download instructions from Base64-encoded payloads embedded directly in the incoming download URLs forwarded by Sonarr/Radarr and runs them in the background using `ani-cli` or `yt-dlp`. It works hand-in-hand with [Otakarr](https://github.com/fofola1/Otakarr) to resolve and stream titles automatically.

---

## How It Works

1. **URL Interception**: Sonarr adds a download task using `mode=addurl` by passing a URL as the `name` parameter.
2. **Payload Extraction**: The application extracts the `payload` query parameter from the URL, decodes the Base64 JSON string, and gets the download metadata.
3. **Queueing**: A unique job ID (`nzo_id`) is generated. The task is saved into an isolated, local SQLite database (`downloads.db`) and handed to an asynchronous background worker.
4. **Execution**: 
   - If a direct `StreamUrl` is provided in the payload, the worker runs `yt-dlp` to download the raw stream.
   - If no direct `StreamUrl` is provided (or if the `Site` is `ani-cli`), the worker runs `ani-cli` non-interactively to search and download.
5. **Real-time Tracking**: The worker intercepts process stdout/stderr streams, parses progress percentages and download speeds dynamically, and writes updates to SQLite.
6. **Completed Download Handling**: Files are downloaded to `/downloads/incomplete` and, upon successful completion, moved atomically to `/downloads/completed` using clean names matching Sonarr's parser formatting.

---

## Payload Format

The indexer/scraper must embed the download details in a URL parameter named `payload` containing a Base64-encoded JSON string:

```json
{
  "site": "mock_scraper",
  "id": "scraper-specific-stream-or-episode-id",
  "title": "Anime Title",
  "season": 1,
  "ep": 12,
  "stream_url": "https://example.com/direct-stream.mp4",
  "resolution": "1080p",
  "source": "SubsGroup"
}
```

*If `stream_url` is empty or not provided, the application will fallback to downloading via `ani-cli` using search based on `title` and `ep`.*

---

## Docker Compose Setup

Below is the recommended configuration to deploy **Riparr** and **Otakarr** together in a unified network:

```yaml
services:
  otakarr:
    image: ghcr.io/fofola1/otakarr:latest
    container_name: otakarr
    restart: unless-stopped
    ports:
      - "8000:8000"
    environment:
      - PORT=8000
      - DOWNLOADER_URL=http://riparr:8080/api/sabnzbd
      - API_KEY=your_shared_indexer_token

  riparr:
    image: ghcr.io/fofola1/riparr:latest
    container_name: riparr
    restart: unless-stopped
    ports:
      - "8080:8080"
    volumes:
      - ./downloads:/downloads
    environment:
      - API_KEY=riparr-token
      - TZ=Europe/Bratislava
      - PORT=8080
```

---

## Host Development (Arch Linux / CachyOS)

Ensure native dependencies are installed:
```bash
sudo pacman -S yt-dlp aria2 ffmpeg
# Ensure ani-cli is installed and in PATH
```

Run locally:
```bash
cd src
dotnet run
```

---

## Sonarr / Radarr Configuration

To configure **Riparr** as a download client in Sonarr or Radarr:

1. Navigate to **Settings > Download Clients** and click **Add (+)**.
2. Select **SABnzbd** (under the Usenet category).
3. Set the following parameters:
   - **Name**: `Riparr`
   - **Host**: `localhost` (or the IP address of the container)
   - **Port**: `8080`
   - **Url Base**: *(leave blank)*
   - **API Key**: `riparr-token` (matching the `API_KEY` env variable)
   - **Use SSL**: `No`
   - **Category**: `tv` (must match the category sent from Sonarr)
4. Click **Test** to verify. Sonarr will query `mode=version`, which will return `3.7.2` indicating a successful connection.
5. Click **Save**.
