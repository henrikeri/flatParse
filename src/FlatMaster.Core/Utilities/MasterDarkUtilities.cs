using System.Globalization;
using System.Text.RegularExpressions;

namespace FlatMaster.Core.Utilities;

public static partial class MasterDarkUtilities
{
    /// <summary>
    /// Extracts the first floating number from a folder name as exposure seconds.
    /// Returns null if none found.
    /// </summary>
    public static double? ExtractExposureFromFolderName(string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName)) return null;

        // 1) Prefer explicit exposure tokens like '5s', '5sec', '5 seconds'
        var m = MyRegex().Match(folderName);
        if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v1))
            return v1;

        // 2) Look for DARKS or DARK prefix with exposure value (e.g., DARKS300, DARK_300)
        m = MyRegex1().Match(folderName);
        if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v2))
            return v2;

        // 3) As a last resort, do not pick arbitrary numbers (dates); return null to force metadata-based exposure
        return null;
    }

    public static string ComputeMasterKeyHash(double exposure, string binning, double? gain, int width, int height)
    {
        // Use full precision (round-trip) for exposure and gain to ensure deterministic uniqueness
        var exposureStr = exposure.ToString("R", CultureInfo.InvariantCulture);
        var gainStr = gain.HasValue ? gain.Value.ToString("R", CultureInfo.InvariantCulture) : "NA";
        var keyString = string.Format(CultureInfo.InvariantCulture, "exp{0}_bin{1}_gain{2}_res{3}x{4}", exposureStr, binning, gainStr, width, height);
        var b = System.Text.Encoding.UTF8.GetBytes(keyString);
        var h = System.Security.Cryptography.SHA1.HashData(b);
        return BitConverter.ToString(h).Replace("-", "").ToLowerInvariant();
    }

    [GeneratedRegex("(?i)\\b(\\d+(?:\\.\\d+)?)\\s*(?:s|sec|secs|seconds)\\b", RegexOptions.None, "nb-NO")]
    private static partial Regex MyRegex();
    [GeneratedRegex("(?i)\\bDARKS?[_-]?(\\d+(?:\\.\\d+)?)\\b", RegexOptions.None, "nb-NO")]
    private static partial Regex MyRegex1();
}
