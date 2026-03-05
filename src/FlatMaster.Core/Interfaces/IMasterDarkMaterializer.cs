using FlatMaster.Core.Models;

namespace FlatMaster.Core.Interfaces;

public interface IMasterDarkMaterializer
{
    /// <summary>
    /// Ensure that all required master darks for the given plan exist under the output root.
    /// Returns a list of generated master file paths (may be empty). Throws on fatal errors.
    /// </summary>
    Task<List<string>> MaterializeMastersAsync(
        ProcessingPlan plan,
        IEnumerable<string> darkRoots,
        bool preferNative,
        string? pixInsightExecutable,
        CancellationToken cancellationToken = default,
        IProgress<string>? progress = null);
    Task<List<MaterializationCandidate>> PreviewMaterializationAsync(ProcessingPlan plan, IEnumerable<string> darkRoots, CancellationToken cancellationToken = default);
    Task<List<MaterializationCandidate>> PreviewDarksOnlyMaterializationAsync(ProcessingPlan plan, double temperatureToleranceC = 1.0, CancellationToken cancellationToken = default);
    Task<List<string>> MaterializeDarksOnlyAsync(
        ProcessingPlan plan,
        bool preferNative,
        string? pixInsightExecutable,
        double temperatureToleranceC = 1.0,
        CancellationToken cancellationToken = default,
        IProgress<string>? progress = null);
}
