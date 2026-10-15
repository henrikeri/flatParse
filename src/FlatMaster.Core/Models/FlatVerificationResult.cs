namespace FlatMaster.Core.Models;

public enum FlatVerificationStatus { MasterFound, CopiesPresent, Missing, Failed }

public sealed record FlatVerificationRow(
    string RelativeDirectory, int SourceCount, int MasterCount,
    FlatVerificationStatus Status, string MasterPaths, string Detail)
{
    public string StatusText => Status switch
    {
        FlatVerificationStatus.MasterFound => "Master found",
        FlatVerificationStatus.CopiesPresent => "Copies present",
        _ => Status.ToString()
    };
}

public sealed record FlatVerificationResult(
    string SourceRoot, string OutputRoot, DateTime CheckedUtc,
    IReadOnlyList<FlatVerificationRow> Rows)
{
    public bool AllMastersPresent => Rows.Count > 0 && Rows.All(row => row.Status == FlatVerificationStatus.MasterFound);
    public bool AllOutputsPresent => Rows.Count > 0 && Rows.All(row => row.Status is FlatVerificationStatus.MasterFound or FlatVerificationStatus.CopiesPresent);
    public string Summary => Rows.Count == 0 ? "No unprocessed source images found; nothing checked." :
        $"{Rows.Count} folders · {Rows.Count(row => row.Status == FlatVerificationStatus.MasterFound)} with masters · " +
        $"{Rows.Count(row => row.Status == FlatVerificationStatus.CopiesPresent)} with source copies · " +
        $"{Rows.Count(row => row.Status == FlatVerificationStatus.Missing)} missing · " +
        $"{Rows.Count(row => row.Status == FlatVerificationStatus.Failed)} failed";
}
