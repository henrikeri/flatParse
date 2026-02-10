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
