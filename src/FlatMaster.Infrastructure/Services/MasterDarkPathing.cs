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

using System.Globalization;

namespace FlatMaster.Infrastructure.Services;

public static class MasterDarkPathing
{
    public static string BuildMasterDarkOutputDirectory(
        string outputRoot,
        double exposureSeconds,
        double? temperatureC,
        string? binning = null,
        double? gain = null,
        double? offset = null,
        int width = 0,
        int height = 0,
        int channels = 1)
    {
        var basePath = Path.Combine(
            outputRoot,
            "Master",
            "Darks",
            FormatExposureFolder(exposureSeconds),
            FormatTemperatureFolder(temperatureC));

        var hasGeometry = width > 0 && height > 0;
        if (string.IsNullOrWhiteSpace(binning) && !gain.HasValue && !offset.HasValue && !hasGeometry)
            return basePath;

        var identity = FormatIdentityFolder(binning, gain, offset);
        if (hasGeometry)
            identity += $"_Res{width}x{height}x{Math.Max(1, channels)}";

        return Path.Combine(basePath, identity);
    }

    public static string BuildMasterDarkFileName(double exposureSeconds, double? temperatureC, string outputFileExtension = "xisf")
    {
        return $"MasterDark_{FormatExposureFolder(exposureSeconds)}_{FormatTemperatureFileToken(temperatureC)}.{NormalizeOutputExtension(outputFileExtension)}";
    }

    public static string FormatExposureFolder(double exposureSeconds)
    {
        var roundedInt = Math.Round(exposureSeconds);
        if (Math.Abs(exposureSeconds - roundedInt) < 0.001)
            return roundedInt.ToString(CultureInfo.InvariantCulture) + "s";

        return exposureSeconds.ToString("0.###", CultureInfo.InvariantCulture) + "s";
    }

    public static string FormatTemperatureFolder(double? temperatureC)
    {
        if (!temperatureC.HasValue)
            return "Unknown";

        var rounded = Math.Round(temperatureC.Value, 1, MidpointRounding.AwayFromZero);
        var value = Math.Abs(rounded - Math.Round(rounded)) < 0.001
            ? Math.Round(rounded).ToString(CultureInfo.InvariantCulture)
            : rounded.ToString("0.0", CultureInfo.InvariantCulture);

        return value + "degC";
    }

    public static string FormatTemperatureFileToken(double? temperatureC)
    {
        if (!temperatureC.HasValue)
            return "Unknown";

        var rounded = Math.Round(temperatureC.Value, 1, MidpointRounding.AwayFromZero);
        if (Math.Abs(rounded - Math.Round(rounded)) < 0.001)
            return Math.Round(rounded).ToString(CultureInfo.InvariantCulture) + "degC";

        return rounded.ToString("0.0", CultureInfo.InvariantCulture) + "degC";
    }

    public static string FormatIdentityFolder(string? binning, double? gain, double? offset)
    {
        var bin = SanitizePathToken(string.IsNullOrWhiteSpace(binning) ? "Unknown" : binning.Trim());
        var gainToken = gain.HasValue ? gain.Value.ToString("0.###", CultureInfo.InvariantCulture) : "Unknown";
        var offsetToken = offset.HasValue ? offset.Value.ToString("0.###", CultureInfo.InvariantCulture) : "Unknown";
        return $"Bin{bin}_Gain{gainToken}_Offset{offsetToken}";
    }

    private static string SanitizePathToken(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
    }

    private static string NormalizeOutputExtension(string? extension)
    {
        if (string.Equals(extension, "fits", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, "fit", StringComparison.OrdinalIgnoreCase))
            return "fits";

        return "xisf";
    }
}

