using System.Globalization;

namespace FlatMaster.Core.Models;

/// <summary>
/// Detailed diagnostic information about dark frame selection for a specific flat
/// </summary>
public sealed record MatchingDiagnostic
{
    /// <summary>
    /// Path to the flat frame
    /// </summary>
    public required string FlatPath { get; init; }

    /// <summary>
    /// The exposure group this flat belongs to
    /// </summary>
    public required double ExposureTime { get; init; }

    /// <summary>
    /// The dark frame that was selected for this flat
    /// </summary>
    public required DarkFrame SelectedDark { get; init; }

    /// <summary>
    /// Why this dark was selected (matching criteria explanation)
    /// </summary>
    public required string SelectionReason { get; init; }

    /// <summary>
    /// Temperature difference between flat and selected dark (in Celsius)
    /// </summary>
    public double? TemperatureDeltaC { get; init; }

    /// <summary>
    /// Display-friendly temperature delta ("N/A" when unavailable)
    /// </summary>
    public string TemperatureDeltaDisplay => TemperatureDeltaC.HasValue 
        ? TemperatureDeltaC.Value.ToString("F1", CultureInfo.InvariantCulture) + "°C" 
        : "N/A";

    /// <summary>
    /// Display-friendly confidence (percentage)
    /// </summary>
    public string ConfidenceDisplay => (ConfidenceScore * 100).ToString("F0", CultureInfo.InvariantCulture) + "%";

    /// <summary>
    /// Alternative dark candidates that were considered but rejected
    /// </summary>
    public List<DarkMatchingCandidate> RejectedAlternatives { get; init; } = new();

    /// <summary>
    /// Confidence score (0.0-1.0) in the selection
    /// </summary>
    public double ConfidenceScore { get; init; }

    /// <summary>
    /// Warning/info messages about this selection
    /// </summary>
    public List<string> Warnings { get; init; } = new();
}

/// <summary>
/// Information about a dark frame candidate that was rejected
/// </summary>
public sealed record DarkMatchingCandidate
{
    public required DarkFrame Dark { get; init; }
    public required string RejectionReason { get; init; }
}
