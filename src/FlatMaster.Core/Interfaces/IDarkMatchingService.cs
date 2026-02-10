// Copyright (C) 2026 Henrik E. Riise
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using FlatMaster.Core.Models;

namespace FlatMaster.Core.Interfaces;

/// <summary>
/// Service for matching dark frames to flat frames
/// </summary>
public interface IDarkMatchingService
{
    /// <summary>
    /// Find the best matching dark for a given exposure group
    /// </summary>
    DarkMatchResult? FindBestDark(
        ExposureGroup exposureGroup,
        IEnumerable<DarkFrame> darkCatalog,
        DarkMatchingOptions options);
    
    /// <summary>
    /// Find best matching dark with detailed diagnostic information
    /// </summary>
    MatchingDiagnostic? FindBestDarkWithDiagnostics(
        string flatPath,
        ExposureGroup exposureGroup,
        IEnumerable<DarkFrame> darkCatalog,
        DarkMatchingOptions options);

    /// <summary>
    /// Generate diagnostics for all flats in a directory
    /// </summary>
    Task<List<MatchingDiagnostic>> GenerateMatchingDiagnosticsAsync(
        IEnumerable<DirectoryJob> jobs,
        IEnumerable<DarkFrame> darkCatalog,
        DarkMatchingOptions options,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Pre-validate dark catalog for common issues
    /// </summary>
    Task<List<string>> ValidateDarkCatalogAsync(
        IEnumerable<DarkFrame> darkCatalog,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of dark frame matching
/// </summary>
public sealed record DarkMatchResult
{
    public required string FilePath { get; init; }
    public required bool OptimizeRequired { get; init; }
    public required string MatchKind { get; init; }
    public required double MatchScore { get; init; }
}

