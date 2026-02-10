using FlatMaster.Core.Models;

namespace FlatMaster.Core.Interfaces;

/// <summary>
/// Service for executing PixInsight processing via PJSR scripts
/// </summary>
public interface IPixInsightService
{
    /// <summary>
    /// Generate PJSR script for a (partial) processing plan.
    /// </summary>
    string GeneratePJSRScript(ProcessingPlan plan);
    
    /// <summary>
    /// Process all jobs in batches, launching PixInsight once per batch.
    /// Runs a preflight test first, then iterates through batches.
    /// </summary>
    Task<ProcessingResult> ProcessJobsInBatchesAsync(
        ProcessingPlan plan,
        string pixInsightExe,
        int batchSize,
        IProgress<string>? logOutput = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of PixInsight processing
/// </summary>
public sealed record ProcessingResult
{
    public required bool Success { get; init; }
    public required int ExitCode { get; init; }
    public required string Output { get; init; }
    public string? ErrorMessage { get; init; }
    public int SucceededBatches { get; init; }
    public int FailedBatches { get; init; }
    public int TotalBatches { get; init; }
}
