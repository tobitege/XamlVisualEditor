using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using XamlVisualEditor.Core;

namespace XamlVisualEditor.CSharp.Language;

/// <summary>
/// Manages Roslyn workspaces and documents for C# language services.
/// </summary>
public sealed class CSharpWorkspaceManager : IDisposable
{
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, DocumentId> _documentIds =
        new(FileSystemPathComparison.Comparer);
    private readonly ILogger<CSharpWorkspaceManager> _logger;
    private readonly ConcurrentDictionary<string, DocumentId> _adhocDocumentIds =
        new(FileSystemPathComparison.Comparer);
    private MSBuildWorkspace? _msbuildWorkspace;
    private AdhocWorkspace? _adhocWorkspace;
    private Solution? _solution;

    // Files outside the loaded projects live in their own workspace, so the project workspace stays in place.
    private Solution? _adhocSolution;
    private ProjectId? _adhocProjectId;
    private string? _workspacePath;
    private Task? _workspaceLoadTask;
    private CancellationTokenSource? _workspaceLoadCancellation;
    private int _workspaceGeneration;

    public CSharpWorkspaceManager(ILogger<CSharpWorkspaceManager>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CSharpWorkspaceManager>.Instance;
    }

    public Task InitializeWorkspaceAsync(string workspacePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return Task.CompletedTask;
        }

        MSBuildWorkspace? oldMsbuildWorkspace;
        AdhocWorkspace? oldAdhocWorkspace;
        CancellationTokenSource? oldLoadCancellation;
        lock (_gate)
        {
            if (FileSystemPathComparison.Equals(_workspacePath, workspacePath))
            {
                return Task.CompletedTask;
            }

            oldMsbuildWorkspace = _msbuildWorkspace;
            oldAdhocWorkspace = _adhocWorkspace;
            oldLoadCancellation = _workspaceLoadCancellation;
            _msbuildWorkspace = null;
            _adhocWorkspace = null;
            _adhocSolution = null;
            _adhocProjectId = null;
            _solution = null;
            _workspacePath = workspacePath;
            _workspaceLoadTask = null;
            _workspaceLoadCancellation = null;
            _workspaceGeneration++;
            _documentIds.Clear();
            _adhocDocumentIds.Clear();
        }

        oldLoadCancellation?.Cancel();
        oldLoadCancellation?.Dispose();
        oldMsbuildWorkspace?.Dispose();
        oldAdhocWorkspace?.Dispose();
        return Task.CompletedTask;
    }

    public Task ClearWorkspaceAsync(CancellationToken ct)
    {
        MSBuildWorkspace? oldMsbuildWorkspace;
        AdhocWorkspace? oldAdhocWorkspace;
        CancellationTokenSource? oldLoadCancellation;
        lock (_gate)
        {
            oldMsbuildWorkspace = _msbuildWorkspace;
            oldAdhocWorkspace = _adhocWorkspace;
            oldLoadCancellation = _workspaceLoadCancellation;
            _msbuildWorkspace = null;
            _adhocWorkspace = null;
            _adhocSolution = null;
            _adhocProjectId = null;
            _workspacePath = null;
            _solution = null;
            _workspaceLoadTask = null;
            _workspaceLoadCancellation = null;
            _workspaceGeneration++;
            _documentIds.Clear();
            _adhocDocumentIds.Clear();
        }

        oldLoadCancellation?.Cancel();
        oldLoadCancellation?.Dispose();
        oldMsbuildWorkspace?.Dispose();
        oldAdhocWorkspace?.Dispose();

        return Task.CompletedTask;
    }

    public async Task<Document?> GetOrAddDocumentAsync(string filePath, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        await EnsureMsbuildWorkspaceAsync(ct).ConfigureAwait(false);

        Solution? solution = _solution;
        if (solution is not null)
        {
            Document? existing = FindDocument(solution, filePath);
            if (existing is not null)
            {
                return await UpdateDocumentTextAsync(existing, text, ct).ConfigureAwait(false);
            }
        }

        EnsureAdhocWorkspace();
        return await AddOrUpdateAdhocDocumentAsync(filePath, text, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Tells whether the document belongs to a loaded project. A file outside the projects has only the runtime
    /// assemblies as references, so its references to other assemblies cannot be checked.
    /// </summary>
    public bool IsProjectDocument(Document document)
    {
        lock (_gate)
        {
            return _adhocProjectId is null || document.Project.Id != _adhocProjectId;
        }
    }

    public Task WarmWorkspaceAsync(CancellationToken ct)
    {
        return EnsureMsbuildWorkspaceAsync(ct);
    }

    /// <summary>Gets an immutable snapshot of the initialized project workspace.</summary>
    public async Task<Solution?> GetSolutionAsync(CancellationToken ct)
    {
        await EnsureMsbuildWorkspaceAsync(ct).ConfigureAwait(false);
        lock (_gate) return _solution;
    }

    private async Task EnsureMsbuildWorkspaceAsync(CancellationToken ct)
    {
        Task loadTask;
        lock (_gate)
        {
            if (_solution is not null || string.IsNullOrWhiteSpace(_workspacePath))
            {
                return;
            }

            if (_workspaceLoadTask is null)
            {
                _workspaceLoadCancellation = new CancellationTokenSource();
                _workspaceLoadTask = LoadMsbuildWorkspaceAsync(
                    _workspacePath,
                    _workspaceGeneration,
                    _workspaceLoadCancellation.Token);
            }

            loadTask = _workspaceLoadTask;
        }

        await loadTask.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task LoadMsbuildWorkspaceAsync(string workspacePath, int generation, CancellationToken ct)
    {
        MSBuildWorkspace? workspace = null;
        try
        {
            AssemblySnapshotSettings? snapshot = AssemblySnapshotSettings.FindForWorkspace(workspacePath);
            workspace = snapshot is null ? MSBuildWorkspace.Create()
                : MSBuildWorkspace.Create(snapshot.CreateMSBuildProperties());
            workspace.RegisterWorkspaceFailedHandler(diagnostic =>
            {
                if (diagnostic.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
                {
                    _logger.LogWarning("MSBuild workspace: {Message}", diagnostic.Diagnostic.Message);
                }
            });

            Solution? solution = null;
            if (workspacePath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                workspacePath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            {
                solution = await workspace.OpenSolutionAsync(workspacePath, cancellationToken: ct)
                    .ConfigureAwait(false);
            }
            else if (workspacePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                Project project = await workspace.OpenProjectAsync(workspacePath, cancellationToken: ct)
                    .ConfigureAwait(false);
                solution = project.Solution;
            }

            if (solution is null)
            {
                return;
            }

            snapshot?.EnsureNoLiveReferences(solution.Projects.SelectMany(project => project.MetadataReferences)
                .OfType<PortableExecutableReference>().Select(reference => reference.FilePath).OfType<string>());

            bool accepted;
            lock (_gate)
            {
                accepted = generation == _workspaceGeneration
                    && FileSystemPathComparison.Equals(_workspacePath, workspacePath);
                if (accepted)
                {
                    _msbuildWorkspace = workspace;
                    _solution = solution;
                    workspace = null;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to load MSBuild workspace: {Message}", ex.Message);
        }
        finally
        {
            workspace?.Dispose();
        }
    }

    private Document? FindDocument(Solution solution, string filePath)
    {
        if (_documentIds.TryGetValue(filePath, out DocumentId? cachedId))
        {
            Document? cachedDoc = solution.GetDocument(cachedId);
            if (cachedDoc is not null)
            {
                return cachedDoc;
            }
        }

        foreach (DocumentId docId in solution.GetDocumentIdsWithFilePath(filePath))
        {
            Document? doc = solution.GetDocument(docId);
            if (doc is not null)
            {
                _documentIds[filePath] = docId;
                return doc;
            }
        }

        return null;
    }

    private async Task<Document> UpdateDocumentTextAsync(Document document, string text, CancellationToken ct)
    {
        SourceText sourceText = SourceText.From(text);
        Solution newSolution = document.Project.Solution.WithDocumentText(
            document.Id,
            sourceText,
            PreservationMode.PreserveValue);

        if (IsProjectDocument(document))
        {
            ApplySolution(newSolution);
        }
        else
        {
            ApplyAdhocSolution(newSolution);
        }

        Document updated = newSolution.GetDocument(document.Id) ?? document;
        await updated.GetTextAsync(ct).ConfigureAwait(false);
        return updated;
    }

    private void EnsureAdhocWorkspace()
    {
        if (_adhocWorkspace is not null)
        {
            return;
        }

        AdhocWorkspace workspace = new();
        ProjectId projectId = ProjectId.CreateNewId("AdhocCSharp");
        ProjectInfo projectInfo = ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "AdhocCSharp",
            "AdhocCSharp",
            LanguageNames.CSharp,
            parseOptions: new CSharpParseOptions(LanguageVersion.Preview),
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: GetDefaultReferences());

        workspace.AddProject(projectInfo);
        lock (_gate)
        {
            _adhocWorkspace = workspace;
            _adhocSolution = workspace.CurrentSolution;
            _adhocProjectId = projectId;
        }
    }

    private async Task<Document?> AddOrUpdateAdhocDocumentAsync(string filePath, string text, CancellationToken ct)
    {
        Solution? adhocSolution = _adhocSolution;
        if (adhocSolution is null)
        {
            return null;
        }

        if (_adhocDocumentIds.TryGetValue(filePath, out DocumentId? existingId))
        {
            Document? existing = adhocSolution.GetDocument(existingId);
            if (existing is not null)
            {
                return await UpdateDocumentTextAsync(existing, text, ct).ConfigureAwait(false);
            }
        }

        Project? project = adhocSolution.Projects.FirstOrDefault();
        if (project is null)
        {
            return null;
        }

        SourceText sourceText = SourceText.From(text);
        DocumentId docId = DocumentId.CreateNewId(project.Id);
        DocumentInfo docInfo = DocumentInfo.Create(
            docId,
            Path.GetFileName(filePath),
            filePath: filePath,
            loader: TextLoader.From(TextAndVersion.Create(sourceText, VersionStamp.Create())));

        Solution newSolution = adhocSolution.AddDocument(docInfo);
        ApplyAdhocSolution(newSolution);
        _adhocDocumentIds[filePath] = docId;
        Document? document = newSolution.GetDocument(docId);
        if (document is not null)
        {
            await document.GetTextAsync(ct).ConfigureAwait(false);
        }

        return document;
    }

    // The project snapshot stays in memory: MSBuildWorkspace.TryApplyChanges would write every changed document
    // to disk, including read-only files and edits the shell applies to the files itself.
    private void ApplySolution(Solution solution)
    {
        _solution = solution;
    }

    private void ApplyAdhocSolution(Solution solution)
    {
        _adhocSolution = solution;
        _adhocWorkspace?.TryApplyChanges(solution);
    }

    public void UpdateSolution(Solution solution)
    {
        if (_adhocWorkspace is not null && ReferenceEquals(solution.Workspace, _adhocWorkspace))
        {
            ApplyAdhocSolution(solution);
            return;
        }

        ApplySolution(solution);
    }

    private static IReadOnlyList<MetadataReference> GetDefaultReferences()
    {
        List<MetadataReference> references = new();
        HashSet<string> locations = new(FileSystemPathComparison.Comparer);

        AddReferenceIfValid(references, locations, typeof(object).Assembly);
        AddReferenceIfValid(references, locations, typeof(System.Console).Assembly);
        AddReferenceIfValid(references, locations, typeof(System.Linq.Enumerable).Assembly);
        AddReferenceIfValid(references, locations, typeof(System.Threading.Tasks.Task).Assembly);

        foreach (System.Reflection.Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            AddReferenceIfValid(references, locations, asm);
        }

        return references;
    }

    private static void AddReferenceIfValid(
        ICollection<MetadataReference> references,
        ISet<string> locations,
        System.Reflection.Assembly assembly)
    {
        if (assembly.IsDynamic)
        {
            return;
        }

        string location = assembly.Location;
        if (string.IsNullOrWhiteSpace(location))
        {
            return;
        }

        if (locations.Add(location))
        {
            references.Add(MetadataReference.CreateFromFile(location));
        }
    }

    public void Dispose()
    {
        ClearWorkspaceAsync(CancellationToken.None).GetAwaiter().GetResult();
    }
}
