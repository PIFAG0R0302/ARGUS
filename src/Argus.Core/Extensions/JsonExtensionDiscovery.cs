using System.Text;
using System.Text.Json;
using Argus.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace Argus.Core.Extensions;

public sealed class JsonExtensionDiscovery : IArgusExtensionDiscovery
{
    private static readonly JsonSerializerOptions JsonOptions = ArgusPaths.CreateJsonOptions();
    private readonly ILogger<JsonExtensionDiscovery> _logger;

    public JsonExtensionDiscovery(ILogger<JsonExtensionDiscovery> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public async Task<IReadOnlyList<ArgusExtensionManifest>> DiscoverAsync(
        string extensionsDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionsDirectory);

        if (!Directory.Exists(extensionsDirectory))
        {
            return [];
        }

        var manifests = new List<ArgusExtensionManifest>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var files = Directory.EnumerateFiles(extensionsDirectory, "argus-extension.json", SearchOption.AllDirectories)
            .OrderBy(static f => f, StringComparer.OrdinalIgnoreCase);

        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                string json = await File.ReadAllTextAsync(file, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                var manifest = JsonSerializer.Deserialize<ArgusExtensionManifest>(json, JsonOptions);
                if (manifest is null ||
                    string.IsNullOrWhiteSpace(manifest.Id) ||
                    string.IsNullOrWhiteSpace(manifest.Name) ||
                    string.IsNullOrWhiteSpace(manifest.EntryPoint))
                {
                    _logger.LogWarning("Skipped invalid extension manifest at {Path}.", file);
                    continue;
                }

                if (manifest.EntryPoint.Contains("..", StringComparison.Ordinal))
                {
                    _logger.LogWarning("Blocked extension manifest with path traversal entry point at {Path}.", file);
                    continue;
                }

                if (seenIds.Add(manifest.Id.Trim()))
                {
                    manifests.Add(manifest with
                    {
                        Id = manifest.Id.Trim(),
                        Name = manifest.Name.Trim(),
                        EntryPoint = manifest.EntryPoint.Trim()
                    });
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                _logger.LogWarning(ex, "Failed to parse extension manifest at {Path}.", file);
            }
        }

        return manifests;
    }
}
