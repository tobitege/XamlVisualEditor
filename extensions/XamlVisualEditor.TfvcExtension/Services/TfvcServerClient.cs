using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.TfvcExtension.Models;

namespace XamlVisualEditor.TfvcExtension.Services;

/// <summary>Server queries of Team Foundation Server / Azure DevOps Server.</summary>
public interface ITfvcServerClient
{
    /// <summary>Lists the direct children of a server folder.</summary>
    Task<IReadOnlyList<TfvcServerItem>> GetChildrenAsync(string collectionUrl, string serverPath, CancellationToken cancellationToken);

    /// <summary>Gets one item, or null when it does not exist.</summary>
    Task<TfvcServerItem?> GetItemAsync(string collectionUrl, string serverPath, CancellationToken cancellationToken);

    /// <summary>Gets a page of the history of an item, newest changeset first.</summary>
    Task<IReadOnlyList<TfvcChangesetInfo>> GetChangesetsAsync(string collectionUrl, string serverPath, int skip, int top, CancellationToken cancellationToken);

    /// <summary>Gets the changed items of a changeset.</summary>
    Task<IReadOnlyList<TfvcChangesetChange>> GetChangesetChangesAsync(string collectionUrl, int changesetId, CancellationToken cancellationToken);

    /// <summary>Gets the labels that contain an item.</summary>
    Task<IReadOnlyList<TfvcLabelInfo>> GetLabelsAsync(string collectionUrl, string serverPath, CancellationToken cancellationToken);

    /// <summary>Gets a work item summary, or null when it does not exist.</summary>
    Task<TfvcWorkItemInfo?> GetWorkItemAsync(string collectionUrl, int id, CancellationToken cancellationToken);

    /// <summary>Links a changeset to a work item.</summary>
    Task LinkChangesetAsync(string collectionUrl, int workItemId, int changesetId, CancellationToken cancellationToken);
}

/// <summary>Raised when a server request fails.</summary>
public sealed class TfvcServerException : Exception
{
    /// <summary>Creates the exception.</summary>
    public TfvcServerException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>Calls the TFVC and work item REST APIs with the credentials of the current Windows user.</summary>
public sealed class TfvcServerClient : ITfvcServerClient, IDisposable
{
    private const string API_VERSION = "5.0";
    private const int MAX_COMMENT_LENGTH = 4000;

    private readonly HttpClient _httpClient;

    /// <summary>Creates a client that authenticates with the current Windows user.</summary>
    public TfvcServerClient()
        : this(new HttpClient(new HttpClientHandler { UseDefaultCredentials = true, PreAuthenticate = true })
        {
            Timeout = TimeSpan.FromSeconds(60)
        })
    {
    }

    /// <summary>Creates a client on top of an existing HTTP client.</summary>
    public TfvcServerClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TfvcServerItem>> GetChildrenAsync(string collectionUrl, string serverPath, CancellationToken cancellationToken)
    {
        string normalized = TfvcWorkspace.NormalizeServerPath(serverPath);
        string url = BuildItemsUrl(collectionUrl, normalized, "OneLevel");
        using JsonDocument? document = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        List<TfvcServerItem> items = new();
        if (document is null)
        {
            return items;
        }

        foreach (TfvcServerItem item in ReadItems(document.RootElement))
        {
            if (!string.Equals(TfvcWorkspace.NormalizeServerPath(item.ServerPath), normalized, StringComparison.OrdinalIgnoreCase))
            {
                items.Add(item);
            }
        }

        items.Sort(CompareItems);
        return items;
    }

    /// <inheritdoc />
    public async Task<TfvcServerItem?> GetItemAsync(string collectionUrl, string serverPath, CancellationToken cancellationToken)
    {
        string url = BuildItemsUrl(collectionUrl, TfvcWorkspace.NormalizeServerPath(serverPath), "None");
        using JsonDocument? document = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return null;
        }

        foreach (TfvcServerItem item in ReadItems(document.RootElement))
        {
            return item;
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TfvcChangesetInfo>> GetChangesetsAsync(
        string collectionUrl,
        string serverPath,
        int skip,
        int top,
        CancellationToken cancellationToken)
    {
        string url = TrimCollectionUrl(collectionUrl) + "/_apis/tfvc/changesets?searchCriteria.itemPath="
            + Uri.EscapeDataString(TfvcWorkspace.NormalizeServerPath(serverPath))
            + "&$skip=" + skip.ToString(CultureInfo.InvariantCulture)
            + "&$top=" + top.ToString(CultureInfo.InvariantCulture)
            + "&maxCommentLength=" + MAX_COMMENT_LENGTH.ToString(CultureInfo.InvariantCulture)
            + "&api-version=" + API_VERSION;
        using JsonDocument? document = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        return document is null ? Array.Empty<TfvcChangesetInfo>() : ReadChangesets(document.RootElement);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TfvcChangesetChange>> GetChangesetChangesAsync(string collectionUrl, int changesetId, CancellationToken cancellationToken)
    {
        string url = TrimCollectionUrl(collectionUrl) + "/_apis/tfvc/changesets/"
            + changesetId.ToString(CultureInfo.InvariantCulture) + "/changes?$top=10000&api-version=" + API_VERSION;
        using JsonDocument? document = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        List<TfvcChangesetChange> changes = new();
        if (document is null || !document.RootElement.TryGetProperty("value", out JsonElement values) || values.ValueKind != JsonValueKind.Array)
        {
            return changes;
        }

        foreach (JsonElement value in values.EnumerateArray())
        {
            string? path = value.TryGetProperty("item", out JsonElement item) ? ReadString(item, "path") : null;
            if (!string.IsNullOrWhiteSpace(path))
            {
                changes.Add(new TfvcChangesetChange(path, ReadString(value, "changeType") ?? string.Empty));
            }
        }

        return changes;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The labels API filters by label scope, not by contained item. Labels are therefore collected for the
    /// item and each of its parent folders; a label scoped to a parent folder usually covers the item.
    /// </remarks>
    public async Task<IReadOnlyList<TfvcLabelInfo>> GetLabelsAsync(string collectionUrl, string serverPath, CancellationToken cancellationToken)
    {
        List<TfvcLabelInfo> labels = new();
        HashSet<int> seen = new();
        string scope = TfvcWorkspace.NormalizeServerPath(serverPath);
        while (scope.Length > 2)
        {
            string url = TrimCollectionUrl(collectionUrl) + "/_apis/tfvc/labels?requestData.labelScope="
                + Uri.EscapeDataString(scope) + "&$top=1000&api-version=" + API_VERSION;
            using JsonDocument? document = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
            if (document is not null && document.RootElement.TryGetProperty("value", out JsonElement values) && values.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement value in values.EnumerateArray())
                {
                    string? name = ReadString(value, "name");
                    int id = value.TryGetProperty("id", out JsonElement idElement) && idElement.TryGetInt32(out int parsedId) ? parsedId : 0;
                    string? labelScope = ReadString(value, "labelScope");
                    if (string.IsNullOrWhiteSpace(name) || !seen.Add(id)
                        || (labelScope is not null && !TfvcWorkspace.IsSameOrChildServerPath(TfvcWorkspace.NormalizeServerPath(serverPath), TfvcWorkspace.NormalizeServerPath(labelScope))))
                    {
                        continue;
                    }

                    labels.Add(new TfvcLabelInfo(
                        name,
                        value.TryGetProperty("owner", out JsonElement owner) ? ReadString(owner, "displayName") : null,
                        ReadDate(value, "modifiedDate"),
                        ReadString(value, "description"),
                        labelScope));
                }
            }

            int separator = scope.LastIndexOf('/');
            scope = separator <= 1 ? string.Empty : scope.Substring(0, separator);
        }

        labels.Sort(static (left, right) => Nullable.Compare(right.ModifiedDate, left.ModifiedDate));
        return labels;
    }

    /// <summary>Reads changesets from a changesets API response.</summary>
    public static IReadOnlyList<TfvcChangesetInfo> ReadChangesets(JsonElement root)
    {
        List<TfvcChangesetInfo> changesets = new();
        if (!root.TryGetProperty("value", out JsonElement values) || values.ValueKind != JsonValueKind.Array)
        {
            return changesets;
        }

        foreach (JsonElement value in values.EnumerateArray())
        {
            if (!value.TryGetProperty("changesetId", out JsonElement idElement) || !idElement.TryGetInt32(out int id))
            {
                continue;
            }

            string? author = value.TryGetProperty("author", out JsonElement authorElement) ? ReadString(authorElement, "displayName") : null;
            changesets.Add(new TfvcChangesetInfo(id, author, ReadDate(value, "createdDate"), ReadString(value, "comment")));
        }

        return changesets;
    }

    /// <inheritdoc />
    public async Task<TfvcWorkItemInfo?> GetWorkItemAsync(string collectionUrl, int id, CancellationToken cancellationToken)
    {
        string url = TrimCollectionUrl(collectionUrl) + "/_apis/wit/workitems/"
            + id.ToString(CultureInfo.InvariantCulture)
            + "?fields=System.Title,System.WorkItemType,System.State&api-version=" + API_VERSION;
        using JsonDocument? document = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        if (document is null || !document.RootElement.TryGetProperty("fields", out JsonElement fields))
        {
            return null;
        }

        return new TfvcWorkItemInfo(
            id,
            ReadString(fields, "System.WorkItemType"),
            ReadString(fields, "System.Title"),
            ReadString(fields, "System.State"));
    }

    /// <inheritdoc />
    public async Task LinkChangesetAsync(string collectionUrl, int workItemId, int changesetId, CancellationToken cancellationToken)
    {
        string url = TrimCollectionUrl(collectionUrl) + "/_apis/wit/workitems/"
            + workItemId.ToString(CultureInfo.InvariantCulture) + "?api-version=" + API_VERSION;
        string body = BuildChangesetLinkPatch(changesetId);
        using HttpRequestMessage request = new(HttpMethod.Patch, url)
        {
            Content = new StringContent(body, Encoding.UTF8)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json-patch+json");
        using HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new TfvcServerException(await DescribeFailureAsync(response, cancellationToken).ConfigureAwait(false));
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _httpClient.Dispose();
    }

    /// <summary>Builds the JSON patch document that adds a changeset artifact link to a work item.</summary>
    public static string BuildChangesetLinkPatch(int changesetId)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("op", "add");
            writer.WriteString("path", "/relations/-");
            writer.WriteStartObject("value");
            writer.WriteString("rel", "ArtifactLink");
            writer.WriteString("url", "vstfs:///VersionControl/Changeset/" + changesetId.ToString(CultureInfo.InvariantCulture));
            writer.WriteStartObject("attributes");
            writer.WriteString("name", "Fixed in Changeset");
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Reads TFVC items from an items API response.</summary>
    public static IReadOnlyList<TfvcServerItem> ReadItems(JsonElement root)
    {
        List<TfvcServerItem> items = new();
        if (!root.TryGetProperty("value", out JsonElement values) || values.ValueKind != JsonValueKind.Array)
        {
            return items;
        }

        foreach (JsonElement value in values.EnumerateArray())
        {
            string? path = ReadString(value, "path");
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            bool isFolder = value.TryGetProperty("isFolder", out JsonElement folderElement) && folderElement.ValueKind == JsonValueKind.True;
            int version = value.TryGetProperty("version", out JsonElement versionElement) && versionElement.TryGetInt32(out int parsedVersion)
                ? parsedVersion
                : 0;
            DateTimeOffset? changeDate = value.TryGetProperty("changeDate", out JsonElement dateElement)
                && dateElement.ValueKind == JsonValueKind.String
                && dateElement.TryGetDateTimeOffset(out DateTimeOffset parsedDate)
                    ? parsedDate
                    : null;
            items.Add(new TfvcServerItem(TfvcWorkspace.NormalizeServerPath(path), isFolder, version, changeDate));
        }

        return items;
    }

    private static string BuildItemsUrl(string collectionUrl, string serverPath, string recursionLevel)
    {
        return TrimCollectionUrl(collectionUrl) + "/_apis/tfvc/items?scopePath=" + Uri.EscapeDataString(serverPath)
            + "&recursionLevel=" + recursionLevel + "&api-version=" + API_VERSION;
    }

    private static string TrimCollectionUrl(string collectionUrl) => collectionUrl.Trim().TrimEnd('/');

    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new TfvcServerException(await DescribeFailureAsync(response, cancellationToken).ConfigureAwait(false));
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new TfvcServerException(ex.Message, ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TfvcServerException(ex.Message, ex);
        }
    }

    private static async Task<string> DescribeFailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string status = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + " " + response.ReasonPhrase;
        try
        {
            string content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(content);
            string? message = ReadString(document.RootElement, "message");
            return string.IsNullOrWhiteSpace(message) ? status : status + ": " + message;
        }
        catch (JsonException)
        {
            return status;
        }
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    private static DateTimeOffset? ReadDate(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            && value.TryGetDateTimeOffset(out DateTimeOffset date)
                ? date
                : null;
    }

    private static int CompareItems(TfvcServerItem left, TfvcServerItem right)
    {
        if (left.IsFolder != right.IsFolder)
        {
            return left.IsFolder ? -1 : 1;
        }

        return string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
    }
}
