using System.Globalization;

namespace FlatMaster.Core.Models;

/// <summary>
/// Represents metadata extracted from FITS/XISF image headers
/// </summary>
public sealed record ImageMetadata
{
    public required string FilePath { get; init; }
    public required ImageType Type { get; init; }
    public double? ExposureTime { get; init; }
    public string? Binning { get; init; }
    public double? Gain { get; init; }
    public double? Offset { get; init; }
    public double? Temperature { get; init; }
    public string? Filter { get; init; }
    public DateTime? ObservationDate { get; init; }
    
    /// <summary>
    /// Format exposure time for consistent display (3 decimal places, strip trailing zeros)
    /// </summary>
    public string ExposureKey => ExposureTime.HasValue 
        ? Math.Round(ExposureTime.Value, 3).ToString("0.###", CultureInfo.InvariantCulture) + "s"
        : "Unknown";
}

/// <summary>
/// Types of astronomical images
/// </summary>
public enum ImageType
{
    Unknown,
    Light,
    Flat,
    Dark,
    DarkFlat,
    Bias,
    MasterFlat,
    MasterDark,
    MasterDarkFlat,
    MasterBias
}
