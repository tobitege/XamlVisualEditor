using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace XamlVisualEditor.Extensions.Hosting;

/// <summary>Stores small extension preference records in the user's data directory.</summary>
public sealed class FileExtensionStorage : IExtensionStorage
{
    private static readonly ConcurrentDictionary<string, object> s_fileLocks = new(StringComparer.Ordinal);
    private readonly string _directory;

    public FileExtensionStorage(string extensionId, string? storageRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionId);
        string root = storageRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "XamlVisualEditor", "extension-state");
        if (!Path.IsPathFullyQualified(root))
            throw new ArgumentException("Extension storage requires an absolute user data directory.", nameof(storageRoot));
        _directory = Path.Combine(Path.GetFullPath(root), Hash(extensionId));
    }

    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken)
    {
        string path = GetPath(key);
        lock (s_fileLocks.GetOrAdd(path, _ => new object()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using JsonDocument document = JsonDocument.Parse(stream);
                T? value = document.RootElement.ValueKind == JsonValueKind.Null
                    ? default : JsonSerializer.Deserialize<T>(document.RootElement.GetRawText());
                return Task.FromResult(value);
            }
            catch (FileNotFoundException) { return Task.FromResult<T?>(default); }
            catch (DirectoryNotFoundException) { return Task.FromResult<T?>(default); }
        }
    }

    public Task SetAsync<T>(string key, T value, CancellationToken cancellationToken)
    {
        string path = GetPath(key);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] content = JsonSerializer.SerializeToUtf8Bytes(value);
        lock (s_fileLocks.GetOrAdd(path, _ => new object()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(_directory);
            string temporaryPath = path + "." + Environment.ProcessId + ".tmp";
            using (FileStream stream = new(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, path, overwrite: true);
        }
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken) =>
        SetAsync<object?>(key, null, cancellationToken);

    private string GetPath(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return Path.Combine(_directory, Hash(key) + ".json");
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
