using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using XamlVisualEditor.Core.Interfaces;

namespace XamlVisualEditor.CSharp.Language;

public sealed partial class CSharpLanguageService : IDesignerEventBindingService
{
    public async Task<DesignerEventBindingResult> ResolveEventHandlerAsync(
        DesignerEventBindingRequest request, CancellationToken cancellationToken = default)
    {
        Solution solution = await _workspaceManager.GetSolutionAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Load the form's project before assigning an event handler.");
        foreach (var openDocument in request.OpenDocumentTexts)
        {
            foreach (DocumentId id in solution.GetDocumentIdsWithFilePath(openDocument.Key))
            {
                solution = solution.WithDocumentText(id, SourceText.From(openDocument.Value));
            }
        }

        return await DesignerEventBindingResolver.ResolveAsync(solution, request, cancellationToken).ConfigureAwait(false);
    }
}
