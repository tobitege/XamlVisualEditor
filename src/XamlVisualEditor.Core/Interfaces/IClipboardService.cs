using System.Threading.Tasks;

namespace XamlVisualEditor.Core.Interfaces;

/// <summary>Transfers text through the host's system clipboard.</summary>
public interface IClipboardService
{
    Task<string?> GetTextAsync();
    Task SetTextAsync(string text);
}
