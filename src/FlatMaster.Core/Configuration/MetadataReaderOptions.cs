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

namespace FlatMaster.Core.Configuration;

/// <summary>
/// Options for high-throughput metadata reads.
/// </summary>
public sealed record MetadataReaderOptions
{
    /// <summary>
    /// Max parallel reads when scanning. Increase to saturate fast storage or networks.
    /// </summary>
    public int MaxParallelism { get; init; } = Math.Max(8, Environment.ProcessorCount * 4);

    /// <summary>
    /// Cache metadata in memory to avoid re-reading headers.
    /// </summary>
    public bool UseMemoryCache { get; init; } = true;

    /// <summary>
    /// Optional cache size limit (entries). Set <= 0 for unbounded.
    /// </summary>
    public int CacheSizeLimitEntries { get; init; } = 0;
}

