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
    public static string BuildMasterDarkOutputDirectory(string outputRoot, double exposureSeconds, double? temperatureC)
    {
        return Path.Combine(
            outputRoot,
            "Master",
            "Darks",
            FormatExposureFolder(exposureSeconds),
            FormatTemperatureFolder(temperatureC));
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

    private static string NormalizeOutputExtension(string? extension)
    {
        if (string.Equals(extension, "fits", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, "fit", StringComparison.OrdinalIgnoreCase))
            return "fits";

        return "xisf";
    }
}

