using System.Diagnostics.CodeAnalysis;
using Base.Application.Files;

namespace Base.Infrastructure.Files;

internal sealed class FileStorageProviderResolver : IFileStorageProviderResolver
{
    private readonly Dictionary<string, IFileStorageProvider> _providers;

    public FileStorageProviderResolver(IEnumerable<IFileStorageProvider> providers)
    {
        _providers = providers.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        // The database provider is always there but capped for small files, so it doesn't count as "configured".
        if (!_providers.Keys.Any(n => !string.Equals(n, FileStorageProviderNames.Database, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("No file storage provider is configured. Configure FileStorage:LocalDisk, :S3 or :AzureBlob.");
        }

        Names = _providers.Keys.ToList();
    }

    public IReadOnlyCollection<string> Names { get; }

    public IFileStorageProvider Get(string name) =>
        TryGet(name, out var provider)
            ? provider
            : throw new InvalidOperationException($"File storage provider '{name}' is not configured, but files were stored with it.");

    public bool TryGet(string name, [MaybeNullWhen(false)] out IFileStorageProvider provider) =>
        _providers.TryGetValue(name, out provider);
}
