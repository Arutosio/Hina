# Host Guide

Hina.Host is a lightweight ASP.NET Core static file server purpose-built for serving patch manifests and Brotli-compressed chunks to Hina clients. It requires zero application logic on the server side -- all intelligence lives in the client and builder.

---

## What Hina.Host Does

Hina.Host serves two things:

1. **Manifest files** -- JSON documents describing the current release (file list, chunk hashes, version, signature).
2. **Chunk files** -- Brotli-compressed binary blobs stored under a two-character hash-prefix directory structure.

The host also exposes a `/health` endpoint for load balancer and monitoring integrations.

Because Hina.Host is a pure static file server, you can replace it entirely with Nginx, Apache, a CDN, or any HTTP server capable of serving files from disk.

---

## Configuration

### hina.host.json

The primary configuration file. Place it in the working directory alongside the Hina.Host binary.

```json
{
  "root": "patch"
}
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `root` | `string` | `"patch"` | Path to the directory containing `manifest.json` and `chunks/`. Relative paths are resolved from the working directory. |

### Resolution Order

The host resolves its root directory in the following priority order:

| Priority | Source | Example |
|----------|--------|---------|
| 1 | `--config <path>` flag | `--config /etc/hina/host.json` |
| 2 | `hina.host.json` in working directory | Automatic discovery |
| 3 | `Patcher:Root` in appsettings.json | ASP.NET Core configuration |
| 4 | Built-in default | `"patch"` |

### Using the --config Flag

```shell
dotnet Hina.Host.dll --config /etc/hina/production.json
```

The specified file must contain a JSON object with a `root` property.

### Using appsettings.json

You can configure the root through standard ASP.NET Core configuration:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "Patcher": {
    "Root": "/var/www/patch"
  }
}
```

This also means you can set the root via environment variables:

```shell
export Patcher__Root=/var/www/patch
dotnet Hina.Host.dll
```

---

## Endpoints

| Method | Path | Response | Description |
|--------|------|----------|-------------|
| GET | `/manifest.json` | 200 + JSON | Stable channel manifest |
| GET | `/manifest.<channel>.json` | 200 + JSON | Channel-specific manifest (e.g., `manifest.beta.json`) |
| GET | `/chunks/<prefix>/<hash>.chunk.br` | 200 + binary | Brotli-compressed chunk data |
| GET | `/health` | 200 `"ok"` | Health check for load balancers and monitoring |
| GET | `/<any file>` | 200 + file | Any file under the root directory is served as a static file |

### Expected Directory Structure

```
<root>/
  manifest.json
  manifest.beta.json          # optional, per-channel
  chunks/
    a3/
      a3f7e2...chunk.br
    b1/
      b12e84...chunk.br
    ...
```

The two-character prefix directories (`a3/`, `b1/`) are hash-prefix buckets created by Hina.Builder. This prevents any single directory from containing too many files, which would degrade file system performance.

---

## Deployment Options

### Standalone

Run Hina.Host directly. Suitable for development, small deployments, or internal networks.

```shell
# Build and run
dotnet run --project Hina.Host

# Or publish and run
dotnet publish Hina.Host -c Release -o ./publish
dotnet ./publish/Hina.Host.dll
```

By default, ASP.NET Core listens on `http://localhost:5000`. Configure the listen address with standard ASP.NET Core options:

```shell
dotnet Hina.Host.dll --urls "http://0.0.0.0:8080"
```

Or via environment variable:

```shell
export ASPNETCORE_URLS="http://0.0.0.0:8080"
dotnet Hina.Host.dll
```

### Behind Nginx Reverse Proxy

For production deployments, place Hina.Host behind Nginx for TLS termination, caching, and connection management.

```nginx
server {
    listen 443 ssl http2;
    server_name patch.example.com;

    ssl_certificate     /etc/ssl/certs/patch.example.com.pem;
    ssl_certificate_key /etc/ssl/private/patch.example.com.key;

    location / {
        proxy_pass http://127.0.0.1:5000;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_for;
        proxy_set_header X-Forwarded-Proto $scheme;

        # Chunk files are immutable (content-addressed), cache aggressively
        location ~* \.chunk\.br$ {
            proxy_pass http://127.0.0.1:5000;
            expires 1y;
            add_header Cache-Control "public, immutable";
        }

        # Manifests change on each build, use short cache or no-cache
        location ~* manifest.*\.json$ {
            proxy_pass http://127.0.0.1:5000;
            expires 30s;
            add_header Cache-Control "public, must-revalidate";
        }
    }
}
```

### Behind Apache Reverse Proxy

```apache
<VirtualHost *:443>
    ServerName patch.example.com

    SSLEngine on
    SSLCertificateFile    /etc/ssl/certs/patch.example.com.pem
    SSLCertificateKeyFile /etc/ssl/private/patch.example.com.key

    ProxyPreserveHost On
    ProxyPass / http://127.0.0.1:5000/
    ProxyPassReverse / http://127.0.0.1:5000/

    <LocationMatch "\.chunk\.br$">
        Header set Cache-Control "public, immutable, max-age=31536000"
    </LocationMatch>

    <LocationMatch "manifest.*\.json$">
        Header set Cache-Control "public, must-revalidate, max-age=30"
    </LocationMatch>
</VirtualHost>
```

### Serving Patch Files Directly with Nginx (Without Hina.Host)

Since all files are static, you can skip Hina.Host entirely and serve the build output directly from Nginx:

```nginx
server {
    listen 443 ssl http2;
    server_name patch.example.com;

    ssl_certificate     /etc/ssl/certs/patch.example.com.pem;
    ssl_certificate_key /etc/ssl/private/patch.example.com.key;

    root /var/www/patch;

    # Health check (optional, for compatibility)
    location = /health {
        return 200 "ok";
        add_header Content-Type text/plain;
    }

    # Chunk files are content-addressed and immutable
    location /chunks/ {
        expires 1y;
        add_header Cache-Control "public, immutable";

        # Chunks are already Brotli-compressed, serve as-is
        types {
            application/octet-stream br;
        }
    }

    # Manifests should not be cached long
    location ~* manifest.*\.json$ {
        expires 30s;
        add_header Cache-Control "public, must-revalidate";
    }
}
```

### CDN Deployment

Hina's content-addressed chunk design is ideal for CDNs. Upload the builder output to any CDN origin (S3, Azure Blob Storage, GCS) and configure the CDN to serve it.

Key considerations:

- **Chunk files** are immutable and identified by hash. Set `Cache-Control: public, immutable, max-age=31536000` for aggressive caching.
- **Manifest files** change on every build. Set a short TTL (30-60 seconds) or use cache invalidation on deploy.
- The client constructs URLs from the `baseUrl` in its config, so set `baseUrl` to the CDN endpoint.

Example with AWS S3 + CloudFront:

```shell
# Upload build output to S3
aws s3 sync ./build-output/ s3://my-patch-bucket/ \
  --cache-control "public, max-age=31536000, immutable" \
  --exclude "manifest*.json"

aws s3 sync ./build-output/ s3://my-patch-bucket/ \
  --cache-control "public, max-age=30" \
  --include "manifest*.json"
```

### Docker Deployment

```dockerfile
# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Hina.Host/ Hina.Host/
COPY Hina.Core/ Hina.Core/
RUN dotnet publish Hina.Host/Hina.Host.csproj -c Release -o /app

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

# Mount or copy patch files into /app/patch
VOLUME /app/patch

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Hina.Host.dll"]
```

Build and run:

```shell
docker build -t hina-host .
docker run -d -p 8080:8080 -v /path/to/patch:/app/patch hina-host
```

With Docker Compose:

```yaml
services:
  hina-host:
    build: .
    ports:
      - "8080:8080"
    volumes:
      - ./build-output:/app/patch:ro
    environment:
      - ASPNETCORE_URLS=http://+:8080
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:8080/health"]
      interval: 30s
      timeout: 5s
      retries: 3
```

---

## CORS Considerations

If your patcher client runs in a browser (e.g., a web-based game launcher or Electron app), you need to configure CORS headers. Hina.Host does not include CORS middleware by default.

**Option 1: Add CORS via reverse proxy (recommended)**

```nginx
# In your Nginx server block
add_header Access-Control-Allow-Origin "https://launcher.example.com" always;
add_header Access-Control-Allow-Methods "GET, HEAD, OPTIONS" always;
add_header Access-Control-Allow-Headers "Content-Type" always;

if ($request_method = OPTIONS) {
    return 204;
}
```

**Option 2: Modify Hina.Host Program.cs**

Add CORS middleware before the static file middleware:

```csharp
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins("https://launcher.example.com")
              .AllowAnyHeader()
              .WithMethods("GET", "HEAD");
    });
});

// ...after building the app:
app.UseCors();
app.UseStaticFiles(new StaticFileOptions { FileProvider = fileProvider });
```

---

## Performance Tuning Tips

### Chunk Caching

Chunk files are content-addressed (named by their SHA256 hash). Once written, their contents never change. This makes them ideal for aggressive caching at every layer:

- **Reverse proxy**: Set `expires 1y` and `Cache-Control: public, immutable`.
- **CDN**: Maximum TTL. No invalidation needed for chunks -- new versions produce new hashes.
- **Client-side**: Clients only request chunks they do not already have locally.

### Manifest Caching

Manifests change on every build. Use short TTLs (30-60 seconds) or explicit cache invalidation when deploying a new build.

### File System Optimization

- **SSD storage**: Recommended for serving chunks. Random read patterns from many small files benefit from SSD I/O.
- **File system choice**: On Linux, ext4 or XFS handle large numbers of small files well. The hash-prefix bucketing (256 possible two-character directories) keeps directory sizes manageable.
- **OS-level caching**: Ensure the server has enough RAM for the OS page cache to hold frequently accessed chunks.

### Connection Limits

When running Hina.Host behind a reverse proxy, tune connection limits to match expected concurrent clients:

```nginx
# Nginx upstream tuning
upstream hina {
    server 127.0.0.1:5000;
    keepalive 64;
}
```

### Compression

Chunk files are already Brotli-compressed by the builder. Do not enable additional gzip or Brotli compression on the reverse proxy for `.chunk.br` files, as this wastes CPU for no benefit.

Manifest files are small JSON and benefit from on-the-fly gzip compression:

```nginx
gzip on;
gzip_types application/json;
gzip_min_length 256;
```

### Horizontal Scaling

Because Hina.Host is stateless, you can run multiple instances behind a load balancer. All instances serve the same files from a shared volume or synchronized directory.
