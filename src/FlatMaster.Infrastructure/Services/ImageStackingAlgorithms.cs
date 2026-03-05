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

using System.Collections.Concurrent;

namespace FlatMaster.Infrastructure.Services;

internal static class ImageStackingAlgorithms
{
    public static void AverageStack(FitsImageIO.ImageData[] frames, double[] output, long pixelCount)
    {
        var n = frames.Length;
        Parallel.ForEach(Partitioner.Create(0L, pixelCount), range =>
        {
            for (var p = range.Item1; p < range.Item2; p++)
            {
                double sum = 0;
                for (var i = 0; i < n; i++)
                    sum += frames[i].Pixels[p];
                output[p] = sum / n;
            }
        });
    }

    public static void PercentileClipStack(
        FitsImageIO.ImageData[] frames,
        double[] output,
        long pixelCount,
        double lowClip,
        double highClip,
        double[]? eqFactors)
    {
        var n = frames.Length;
        Parallel.ForEach(Partitioner.Create(0L, pixelCount), range =>
        {
            var col = new double[n];
            var indices = new int[n];
            for (var p = range.Item1; p < range.Item2; p++)
            {
                for (var i = 0; i < n; i++)
                {
                    col[i] = eqFactors != null ? frames[i].Pixels[p] * eqFactors[i] : frames[i].Pixels[p];
                    indices[i] = i;
                }

                Array.Sort(col, indices);
                var lo = (int)Math.Floor(lowClip * n);
                var hi = (int)Math.Ceiling((1.0 - highClip) * n) - 1;
                lo = Math.Clamp(lo, 0, n - 1);
                hi = Math.Clamp(hi, 0, n - 1);
                double sum = 0;
                var count = 0;
                for (var i = lo; i <= hi; i++)
                {
                    sum += frames[indices[i]].Pixels[p];
                    count++;
                }

                output[p] = count > 0 ? sum / count : frames[indices[n / 2]].Pixels[p];
            }
        });
    }

    public static void WinsorizedSigmaClipStack(
        FitsImageIO.ImageData[] frames,
        double[] output,
        long pixelCount,
        double sigmaLow,
        double sigmaHigh,
        double winsorizationCutoff,
        int maxIterations,
        double[]? eqFactors)
    {
        var n = frames.Length;
        Parallel.ForEach(Partitioner.Create(0L, pixelCount), range =>
        {
            var eqCol = new double[n];
            var origCol = new double[n];
            var included = new bool[n];
            var winsorized = new double[n];

            for (var p = range.Item1; p < range.Item2; p++)
            {
                for (var i = 0; i < n; i++)
                {
                    origCol[i] = frames[i].Pixels[p];
                    eqCol[i] = eqFactors != null ? origCol[i] * eqFactors[i] : origCol[i];
                    included[i] = true;
                }

                var count = n;
                for (var iter = 0; iter < maxIterations && count >= 3; iter++)
                {
                    double sum = 0;
                    for (var i = 0; i < n; i++)
                        if (included[i])
                            sum += eqCol[i];
                    var mean = sum / count;

                    double ssq = 0;
                    for (var i = 0; i < n; i++)
                    {
                        if (!included[i])
                            continue;
                        var d = eqCol[i] - mean;
                        ssq += d * d;
                    }

                    var sigma = Math.Sqrt(ssq / Math.Max(1, count - 1));
                    if (sigma < 1e-15)
                        break;

                    var loClip = mean - winsorizationCutoff * sigma;
                    var hiClip = mean + winsorizationCutoff * sigma;
                    for (var i = 0; i < n; i++)
                    {
                        if (included[i])
                            winsorized[i] = Math.Clamp(eqCol[i], loClip, hiClip);
                    }

                    double wSum = 0;
                    for (var i = 0; i < n; i++)
                        if (included[i])
                            wSum += winsorized[i];
                    var wMean = wSum / count;

                    double wSsq = 0;
                    for (var i = 0; i < n; i++)
                    {
                        if (!included[i])
                            continue;
                        var d = winsorized[i] - wMean;
                        wSsq += d * d;
                    }

                    var wSigma = Math.Sqrt(wSsq / Math.Max(1, count - 1));
                    if (wSigma < 1e-15)
                        break;

                    var rejLo = mean - sigmaLow * wSigma;
                    var rejHi = mean + sigmaHigh * wSigma;
                    var anyRejected = false;
                    for (var i = 0; i < n; i++)
                    {
                        if (!included[i])
                            continue;
                        if (eqCol[i] >= rejLo && eqCol[i] <= rejHi)
                            continue;

                        included[i] = false;
                        count--;
                        anyRejected = true;
                    }

                    if (!anyRejected)
                        break;
                }

                if (count > 0)
                {
                    double sum2 = 0;
                    for (var i = 0; i < n; i++)
                        if (included[i])
                            sum2 += origCol[i];
                    output[p] = sum2 / count;
                }
                else
                {
                    Array.Sort(origCol);
                    output[p] = origCol[n / 2];
                }
            }
        });
    }

    public static void LinearFitSigmaClipStack(
        FitsImageIO.ImageData[] frames,
        double[] output,
        long pixelCount,
        double sigmaLow,
        double sigmaHigh,
        int maxIterations,
        double[]? eqFactors,
        double[] fitSlopes,
        double[] fitIntercepts)
    {
        var n = frames.Length;
        Parallel.ForEach(Partitioner.Create(0L, pixelCount), range =>
        {
            var rejCol = new double[n];
            var origCol = new double[n];
            var included = new bool[n];

            for (var p = range.Item1; p < range.Item2; p++)
            {
                for (var i = 0; i < n; i++)
                {
                    origCol[i] = frames[i].Pixels[p];
                    var eq = eqFactors != null ? origCol[i] * eqFactors[i] : origCol[i];
                    rejCol[i] = fitSlopes[i] * eq + fitIntercepts[i];
                    included[i] = true;
                }

                var count = n;
                for (var iter = 0; iter < maxIterations && count >= 3; iter++)
                {
                    double sum = 0;
                    for (var i = 0; i < n; i++)
                        if (included[i])
                            sum += rejCol[i];
                    var mean = sum / count;

                    double ssq = 0;
                    for (var i = 0; i < n; i++)
                    {
                        if (!included[i])
                            continue;
                        var d = rejCol[i] - mean;
                        ssq += d * d;
                    }

                    var sigma = Math.Sqrt(ssq / Math.Max(1, count - 1));
                    if (sigma < 1e-15)
                        break;

                    var rejLo = mean - sigmaLow * sigma;
                    var rejHi = mean + sigmaHigh * sigma;
                    var anyRejected = false;
                    for (var i = 0; i < n; i++)
                    {
                        if (!included[i])
                            continue;
                        if (rejCol[i] >= rejLo && rejCol[i] <= rejHi)
                            continue;

                        included[i] = false;
                        count--;
                        anyRejected = true;
                    }

                    if (!anyRejected)
                        break;
                }

                if (count > 0)
                {
                    double sum2 = 0;
                    for (var i = 0; i < n; i++)
                        if (included[i])
                            sum2 += origCol[i];
                    output[p] = sum2 / count;
                }
                else
                {
                    Array.Sort(origCol);
                    output[p] = origCol[n / 2];
                }
            }
        });
    }
}

