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

using System.Diagnostics;
using System.Text;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Text.Encodings.Web;
using System.Text.Json;
using FlatMaster.Core.Interfaces;
using FlatMaster.Core.Models;
using Microsoft.Extensions.Logging;

namespace FlatMaster.Infrastructure.Services;

/// <summary>
/// Generates and executes PixInsight PJSR scripts
/// </summary>
public sealed partial class PixInsightService(ILogger<PixInsightService> logger) : IPixInsightService
{
    private readonly ILogger<PixInsightService> _logger = logger;

    public string GeneratePJSRScript(ProcessingPlan plan)
    {
        var sentinelPath = NormalizePath(Path.Combine(Path.GetTempPath(), "flatmaster_sentinel.txt"));
        return GeneratePJSRScript(plan, sentinelPath);
    }

    private string GeneratePJSRScript(ProcessingPlan plan, string sentinelPath)
    {
        sentinelPath = NormalizePath(sentinelPath);

        var config = new
        {
            plan = plan.SelectedJobs.Select(j => new
            {
                dirPath = NormalizePath(j.DirectoryPath),
                outRoot = NormalizePath(j.OutputRootPath),
                relDir = (j.RelativeDirectory ?? string.Empty).Replace("\\", "/"),
                groups = j.ExposureGroups.Select(g => new
                {
                    exposure = g.ExposureTime,
                    files = g.FilePaths.Select(NormalizePath).ToArray(),
                    want = new
                    {
                        binning = g.MatchingCriteria?.Binning,
                        gain = g.MatchingCriteria?.Gain,
                        offset = g.MatchingCriteria?.Offset,
                        temp = g.MatchingCriteria?.Temperature,
                        manualDarkPath = string.IsNullOrWhiteSpace(g.MatchingCriteria?.ManualDarkPath)
                            ? null
                            : NormalizePath(g.MatchingCriteria!.ManualDarkPath!)
                    }
                }).ToArray()
            }).ToArray(),
            darkCatalog = plan.SelectedDarks.Select(d => new
            {
                path = NormalizePath(d.FilePath),
                type = d.Type.ToString().ToUpperInvariant(),
                exposure = d.ExposureTime,
                binning = d.Binning,
                gain = d.Gain,
                offset = d.Offset,
                temp = d.Temperature
            }).ToArray(),
            match = new
            {
                enforceBinning = plan.Configuration.DarkMatching.EnforceBinning,
                preferSameGainOffset = plan.Configuration.DarkMatching.PreferSameGainOffset,
                preferClosestTemp = plan.Configuration.DarkMatching.PreferClosestTemp,
                maxTempDeltaC = plan.Configuration.DarkMatching.MaxTempDeltaC,
                darkOverBiasTempDeltaC = plan.Configuration.DarkMatching.DarkOverBiasTempDeltaC,
                darkOverBiasExposureDeltaSeconds = plan.Configuration.DarkMatching.DarkOverBiasExposureDeltaSeconds
            },
            allowNearestExposureWithOptimize = plan.Configuration.DarkMatching.AllowNearestExposureWithOptimize,
            cacheDirName = plan.Configuration.CacheDirName,
            calibratedSubdirBase = plan.Configuration.CalibratedSubdirBase,
            masterSubdirName = plan.Configuration.MasterSubdirName,
            outputExtension = NormalizeOutputExtension(plan.Configuration.OutputFileExtension),
            xisfHintsCal = plan.Configuration.XisfHintsCal,
            xisfHintsMaster = plan.Configuration.XisfHintsMaster,
            rejection = new
            {
                lowSigma = plan.Configuration.Rejection.LowSigma,
                highSigma = plan.Configuration.Rejection.HighSigma
            },
            sentinelPath,
            deleteCalibrated = plan.Configuration.DeleteCalibratedFlats
        };

        // Use UnsafeRelaxedJsonEscaping so paths come through cleanly as forward slashes
        // JSON is valid JavaScript, so var CFG = {...}; works as direct assignment
        var jsonConfig = JsonSerializer.Serialize(config, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        var jsConfigObjectLiteral = ConvertJsonToJsObjectLiteral(jsonConfig);

        // Debug script injection via environment variable FM_PI_DEBUG_SCRIPT has been removed.
        // Scripts are now always generated from the template and configuration object.

        var useDarkTemplate = UsesDarkTemplate(plan);
        var templateName = ResolveTemplateName(plan);
        _logger.LogInformation("GeneratePJSRScript: using template {TemplateName} (jobs={JobCount}, darkMaterialize={IsDarkMaterialize})", templateName, plan.SelectedJobs.Count(), useDarkTemplate);

        // Build config + chosen template as one script
        var script = GetPJSRTemplate(templateName)
            .Replace("%SENTINEL_PATH%", sentinelPath)
            .Replace("%CONFIG_JS_OBJECT_LITERAL%", jsConfigObjectLiteral);

        if (useDarkTemplate)
            ValidateDarkTemplateScript(script, "template");

        return NormalizeScriptLineEndings(script);
    }

    public async Task<ProcessingResult> ProcessJobsInBatchesAsync(
        ProcessingPlan plan,
        string pixInsightExe,
        int batchSize,
        IProgress<string>? logOutput = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(pixInsightExe))
            return MissingPixInsightExecutableResult(pixInsightExe);

        var jobList = BuildPixInsightEligibleJobs(plan.SelectedJobs, logOutput);
        if (jobList.Count == 0)
            return NoJobsResult();

        // Force one folder per PixInsight invocation to avoid large multi-folder batches.
        if (batchSize != 1)
        {
            logOutput?.Report("[PixInsight] Overriding batch size to 1: processing one folder per PixInsight invocation.");
            batchSize = 1;
        }

        logOutput?.Report($"[PixInsight] Executable: {pixInsightExe}");
        logOutput?.Report($"[PixInsight] Jobs: {jobList.Count}, batch size: {batchSize}");

        var runId = Guid.NewGuid().ToString("N");
        var tempRoot = Path.GetTempPath();

        // Batch loop
        int totalBatches = (jobList.Count + batchSize - 1) / batchSize;
        int succeeded = 0, failed = 0;
        var allOutput = new StringBuilder();
        int totalFiles = jobList.Sum(j => j.TotalFileCount);
        int filesProcessedSoFar = 0;

        for (int b = 0; b < totalBatches; b++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batchJobs = jobList.Skip(b * batchSize).Take(batchSize).ToList();
            int firstIdx = b * batchSize + 1;
            int lastIdx = firstIdx + batchJobs.Count - 1;
            int batchFiles = batchJobs.Sum(j => j.TotalFileCount);
            logOutput?.Report($"\n== Folders {firstIdx}-{lastIdx} of {jobList.Count} ({filesProcessedSoFar + batchFiles}/{totalFiles} files) ==");
            foreach (var j in batchJobs)
                logOutput?.Report($"  {j.DirectoryPath}");

            var (scriptPath, sentinelPath) = await BuildBatchScriptAsync(
                plan,
                batchJobs,
                tempRoot,
                runId,
                b + 1,
                logOutput,
                cancellationToken);

            // Generated script created (not persisted for debug in normal mode)

            TryDeleteFile(sentinelPath);
            KillExistingPixInsightProcesses(logOutput);

            var batchResult = await ExecuteWithVariantsAsync(
                scriptPath, pixInsightExe, sentinelPath, "OK", logOutput, cancellationToken);
            allOutput.AppendLine(batchResult.Output);
            if (!batchResult.Success)
            {
                // Failure reported; not persisting scripts/output in normal mode.
            }
            if (batchResult.Success)
            {
                succeeded++;
                filesProcessedSoFar += batchFiles;
                logOutput?.Report($"  [OK] Folders {firstIdx}-{lastIdx} done ({filesProcessedSoFar}/{totalFiles} files)");
            }
            else
            {
                failed++;
                filesProcessedSoFar += batchFiles; // count them even on failure for progress
                logOutput?.Report($"  [FAILED] Folders {firstIdx}-{lastIdx} FAILED: {batchResult.ErrorMessage}");
            }
        }

        KillExistingPixInsightProcesses(null); // clean up

        bool allOk = failed == 0;
        return new ProcessingResult
        {
            Success = allOk,
            ExitCode = allOk ? 0 : -1,
            Output = allOutput.ToString(),
            ErrorMessage = allOk ? null : $"{failed}/{totalBatches} batches failed.",
            SucceededBatches = succeeded,
            FailedBatches = failed,
            TotalBatches = totalBatches
        };
    }

    private async Task<(string ScriptPath, string SentinelPath)> BuildBatchScriptAsync(
        ProcessingPlan plan,
        List<DirectoryJob> batchJobs,
        string tempRoot,
        string runId,
        int batchNumber,
        IProgress<string>? logOutput,
        CancellationToken cancellationToken)
    {
        var batchPlan = new ProcessingPlan
        {
            Jobs = batchJobs,
            DarkCatalog = [.. plan.DarkCatalog],
            Configuration = plan.Configuration
        };

        var batchUsesDarkTemplate = UsesDarkTemplate(batchPlan);
        var batchTemplateName = ResolveTemplateName(batchPlan);
        logOutput?.Report($"  Template: {batchTemplateName}");

        var scriptPath = Path.Combine(tempRoot, $"flatmaster_script_{runId}_b{batchNumber}.js");
        var sentinelPath = Path.Combine(tempRoot, $"flatmaster_sentinel_{runId}_b{batchNumber}.txt");
        var script = GeneratePJSRScript(batchPlan, sentinelPath);
        if (batchUsesDarkTemplate)
        {
            ValidateDarkTemplateScript(script, "generated script");
            logOutput?.Report("  Script mode: Dark integration-only");
        }

        await File.WriteAllTextAsync(scriptPath, script, cancellationToken);
        logOutput?.Report($"  Script: {script.Length / 1024} KB ({scriptPath})");
        return (scriptPath, sentinelPath);
    }

    private static List<DirectoryJob> BuildPixInsightEligibleJobs(
        IEnumerable<DirectoryJob> jobs,
        IProgress<string>? logOutput)
    {
        var filtered = new List<DirectoryJob>();
        int skippedGroupCount = 0;
        int skippedFileCount = 0;

        foreach (var job in jobs)
        {
            var eligibleGroups = new List<ExposureGroup>();
            foreach (var group in job.ExposureGroups)
            {
                if (group.FilePaths.Count < 3)
                {
                    skippedGroupCount++;
                    skippedFileCount += group.FilePaths.Count;
                    continue;
                }

                eligibleGroups.Add(new ExposureGroup
                {
                    ExposureTime = group.ExposureTime,
                    FilePaths = [.. group.FilePaths],
                    RepresentativeMetadata = group.RepresentativeMetadata,
                    AverageTemperatureC = group.AverageTemperatureC,
                    MatchingCriteria = group.MatchingCriteria
                });
            }

            if (eligibleGroups.Count == 0)
                continue;

            filtered.Add(new DirectoryJob
            {
                DirectoryPath = job.DirectoryPath,
                BaseRootPath = job.BaseRootPath,
                OutputRootPath = job.OutputRootPath,
                RelativeDirectory = job.RelativeDirectory,
                ExposureGroups = eligibleGroups,
                IsSelected = job.IsSelected
            });
        }

        if (skippedGroupCount > 0)
        {
            logOutput?.Report(
                $"[PixInsight] Skipping {skippedGroupCount} exposure group(s) with fewer than 3 flats ({skippedFileCount} file(s)).");
        }

        return filtered;
    }

    private static void KillExistingPixInsightProcesses(IProgress<string>? logOutput)
    {
        try
        {
            var piProcesses = Process.GetProcessesByName("PixInsight");
            if (piProcesses.Length > 0)
            {
                logOutput?.Report($"[PixInsight] Killing {piProcesses.Length} existing PixInsight process(es)...");
                foreach (var p in piProcesses)
                {
                    try { p.Kill(true); p.WaitForExit(5000); } catch { }
                    p.Dispose();
                }
                Thread.Sleep(3000); // Give OS time to fully release resources
                logOutput?.Report("[PixInsight] Existing processes terminated.");
            }
            else
            {
                // PI may have just exited via --force-exit; wait for OS cleanup
                Thread.Sleep(3000);
            }
        }
        catch { /* best effort */ }
    }

    private static string[][] BuildArgsVariants(string scriptPath)
    {
        var normalized = NormalizePath(scriptPath);
        return
        [
            // Always force a new PI instance (-n) to avoid IPC/slot reuse issues.
            // Keep to documented run forms and avoid -x/startup-script paths.
            ["--automation-mode", "--no-startup-scripts", "-n", $"--run={normalized}", "--force-exit"],
            ["--automation-mode", "-n", $"--run={normalized}", "--force-exit"],
            ["-n", $"--run={normalized}", "--force-exit"]
        ];
    }

    private static ProcessingResult MissingPixInsightExecutableResult(string pixInsightExe)
    {
        return new ProcessingResult
        {
            Success = false,
            ExitCode = -1,
            Output = "",
            ErrorMessage = $"PixInsight executable not found: {pixInsightExe}"
        };
    }

    private static ProcessingResult NoJobsResult()
    {
        return new ProcessingResult
        {
            Success = true,
            ExitCode = 0,
            Output = "No jobs to process."
        };
    }

    private static void TryDeleteFile(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    private async Task<ProcessingResult> ExecuteWithVariantsAsync(
      string scriptPath,
      string pixInsightExe,
      string sentinelPath,
      string expectedSentinel,
      IProgress<string>? logOutput,
      CancellationToken cancellationToken)
    {
        var argsVariants = BuildArgsVariants(scriptPath);
        var output = new StringBuilder();
        int exitCode = -1;

        for (int attempt = 0; attempt < argsVariants.Length; attempt++)
        {
            var variant = argsVariants[attempt];
            output.Clear();
            var sawEmptyScriptError = false;
            var sawInvalidInstanceIndex = false;
            var attemptStartUtc = DateTime.UtcNow;
            var maxAttemptDuration = TimeSpan.FromMinutes(20);

            logOutput?.Report($"[PixInsight attempt {attempt + 1}/{argsVariants.Length}] args=[{string.Join(", ", variant)}]");
            _logger.LogInformation("Starting PixInsight: {Exe} args={@Args}", pixInsightExe, variant);

            var startInfo = new ProcessStartInfo
            {
                FileName = pixInsightExe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };

            foreach (var arg in variant)
                startInfo.ArgumentList.Add(arg);

            logOutput?.Report($"[PixInsight] Launching (stdout/stderr redirected)...");
            _logger.LogInformation("ExecuteScriptAsync: Redirecting stdout/stderr");

            try
            {
                var baselinePids = GetPixInsightPids();
                logOutput?.Report($"[PixInsight] Baseline PixInsight PIDs: {string.Join(',', baselinePids)}");
                using var process = new Process { StartInfo = startInfo };

                process.Start();
                try
                {
                    logOutput?.Report($"[PixInsight] Launched launcher PID={process.Id}");
                }
                catch { }

                // Snapshot PIDs after start
                try
                {
                    var after = GetPixInsightPids();
                    logOutput?.Report($"[PixInsight] Current PixInsight PIDs after start: {string.Join(',', after)}");
                }
                catch { }

                var outputTask = Task.Run(async () =>
                {
                    try
                    {
                        while (!process.StandardOutput.EndOfStream)
                        {
                            var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
                            if (line != null)
                            {
                                output.AppendLine(line);
                                logOutput?.Report(line);
                                _logger.LogInformation("[PI stdout] {Line}", line);
                                if (line.Contains("empty script", StringComparison.OrdinalIgnoreCase))
                                    sawEmptyScriptError = true;
                                if (line.Contains("invalid application instance index", StringComparison.OrdinalIgnoreCase))
                                    sawInvalidInstanceIndex = true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error reading stdout");
                    }
                }, cancellationToken);

                var errorTask = Task.Run(async () =>
                {
                    try
                    {
                        while (!process.StandardError.EndOfStream)
                        {
                            var line = await process.StandardError.ReadLineAsync(cancellationToken);
                            if (line != null)
                            {
                                if (IsHarmlessStderrNoise(line))
                                {
                                    _logger.LogDebug("[PI stderr/gpu] {Line}", line);
                                }
                                else
                                {
                                    output.AppendLine("[ERROR] " + line);
                                    logOutput?.Report("[ERROR] " + line);
                                    _logger.LogError("[PI stderr] {Line}", line);
                                    if (line.Contains("empty script", StringComparison.OrdinalIgnoreCase))
                                        sawEmptyScriptError = true;
                                    if (line.Contains("invalid application instance index", StringComparison.OrdinalIgnoreCase))
                                        sawInvalidInstanceIndex = true;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error reading stderr");
                    }
                }, cancellationToken);

                try
                {
                    var sentinelSeen = false;
                    var sawDetachedPixInsight = false;
                    var loggedDetached = false;
                    while (true)
                    {
                        if (File.Exists(sentinelPath))
                        {
                            sentinelSeen = true;
                            break;
                        }
                        var piRunning = HasDetachedPixInsightProcess(baselinePids);
                        if (piRunning)
                        {
                            sawDetachedPixInsight = true;
                            if (!loggedDetached)
                            {
                                loggedDetached = true;
                                logOutput?.Report("[PixInsight] Detached script execution detected; waiting for sentinel...");
                            }
                        }

                        // The launcher process returns quickly while script execution may continue
                        // in a detached PixInsight instance. Wait until detached PI stops too.
                        if (process.HasExited && !piRunning)
                        {
                            if (sawDetachedPixInsight || DateTime.UtcNow - attemptStartUtc > TimeSpan.FromSeconds(3))
                            {
                                logOutput?.Report($"[PixInsight] Launcher exited and no detached PI is running (detachedSeen={sawDetachedPixInsight}).");
                                break;
                            }
                        }

                        if (sawEmptyScriptError)
                        {
                            logOutput?.Report("[PixInsight] Detected 'Empty script' output. Terminating process.");
                            KillExistingPixInsightProcesses(logOutput);
                            break;
                        }

                        if (sawInvalidInstanceIndex)
                        {
                            logOutput?.Report("[PixInsight] Detected 'Invalid application instance index'. Terminating process and retrying with fresh instance arguments.");
                            KillExistingPixInsightProcesses(logOutput);
                            break;
                        }

                        if (DateTime.UtcNow - attemptStartUtc > maxAttemptDuration)
                        {
                            logOutput?.Report("[PixInsight] Attempt timed out waiting for sentinel. Terminating process.");
                            KillExistingPixInsightProcesses(logOutput);
                            break;
                        }

                        await Task.Delay(500, cancellationToken);
                    }

                    if (sentinelSeen)
                    {
                        // Scripts can complete in detached PI instances; always clean up after success.
                        KillExistingPixInsightProcesses(null);
                    }

                    if (!process.HasExited)
                        await process.WaitForExitAsync(cancellationToken);

                    await Task.WhenAll(outputTask, errorTask);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("PixInsight execution cancelled - terminating process");
                    logOutput?.Report("[ABORT] Terminating PixInsight process...");

                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(true);
                            await process.WaitForExitAsync(cancellationToken);
                        }
                    }
                    catch (Exception killEx)
                    {
                        _logger.LogWarning(killEx, "Error killing PixInsight process");
                    }

                    throw;
                }

                exitCode = process.ExitCode;
                logOutput?.Report($"[PI exit code={exitCode}]");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logOutput?.Report($"[spawn error] {ex.Message}");
                _logger.LogWarning(ex, "PixInsight launch attempt failed");
                continue;
            }

            if (File.Exists(sentinelPath))
            {
                var sentinelContent = await File.ReadAllTextAsync(sentinelPath, cancellationToken);
                var trimmed = sentinelContent.Trim();
                logOutput?.Report($"[sentinel] {trimmed}");
                var sentinelSuccess = trimmed == expectedSentinel;

                return new ProcessingResult
                {
                    // Launcher exit code is not reliable for detached script execution.
                    Success = sentinelSuccess,
                    ExitCode = exitCode,
                    Output = output.ToString(),
                    ErrorMessage = sentinelSuccess ? null : $"PixInsight reported error: {trimmed}"
                };
            }

            logOutput?.Report("[PixInsight] No sentinel file found, trying next argument variant...");
            // Kill PI before retrying with a different variant to avoid "Invalid application instance index"
            KillExistingPixInsightProcesses(logOutput);
        }

        return new ProcessingResult
        {
            Success = false,
            ExitCode = exitCode,
            Output = output.ToString(),
            ErrorMessage = "PixInsight did not produce a sentinel file after all attempts. " +
                   "Verify that PixInsight is installed and the executable path is correct."
        };
    }

    private static HashSet<int> GetPixInsightPids()
    {
        var pids = new HashSet<int>();
        try
        {
            var procs = Process.GetProcessesByName("PixInsight");
            foreach (var p in procs)
            {
                try
                {
                    pids.Add(p.Id);
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
        catch
        {
            // Best effort
        }

        return pids;
    }

    private static bool HasDetachedPixInsightProcess(HashSet<int> baselinePids)
    {
        try
        {
            var procs = Process.GetProcessesByName("PixInsight");
            foreach (var p in procs)
            {
                try
                {
                    if (!baselinePids.Contains(p.Id))
                        return true;
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
        catch
        {
            // Best effort
        }

        return false;
    }

    private static bool IsHarmlessStderrNoise(string line)
    {
        return line.Contains("gpu_channel_manager")
            || line.Contains("Failed to create GLES")
            || line.Contains("Failed to create shared context for virtualization")
            || line.Contains("gpu_init")
            || line.Contains("GpuChannelManager");
    }

    private static string NormalizePath(string path)
        => path.Replace("\\", "/");

    private static string NormalizeOutputExtension(string? extension)
    {
        if (string.Equals(extension, "fits", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, "fit", StringComparison.OrdinalIgnoreCase))
            return "fits";

        return "xisf";
    }

    private static bool UsesDarkTemplate(ProcessingPlan plan)
        => plan.SelectedJobs.Any(j => IsDarkMaterializeRelativeDirectory(j.RelativeDirectory));

    private static string ResolveTemplateName(ProcessingPlan plan)
        => UsesDarkTemplate(plan) ? "PixInsightTemplate.DARKS.pjsr" : "PixInsightTemplate.pjsr";

    private static void ValidateDarkTemplateScript(string script, string context)
    {
        if (script.Contains("ImageCalibration", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Dark materialization script validation failed: {context} contains ImageCalibration.");
    }

    private static bool IsDarkMaterializeRelativeDirectory(string? relativeDirectory)
    {
        if (string.IsNullOrWhiteSpace(relativeDirectory))
            return false;

        var rel = relativeDirectory.Replace('\\', '/').Trim();
        return rel.Equals("__DARKMATERIALIZE__", StringComparison.OrdinalIgnoreCase)
            || rel.StartsWith("__DARKMATERIALIZE__/", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeScriptLineEndings(string script)
    {
        // PixInsight on Windows behaves more reliably with canonical CRLF line endings.
        return script.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
    }

    private static string ConvertJsonToJsObjectLiteral(string json)
    {
        // Safely convert JSON to a JavaScript object literal with unquoted keys
        // for valid JS identifiers. This avoids brittle regex-based replacements
        // that can break object/array structure for large configs.
        using var doc = JsonDocument.Parse(json);
        var sb = new StringBuilder();

        void WriteElement(JsonElement el)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Object:
                    sb.Append('{');
                    bool firstProp = true;
                    foreach (var prop in el.EnumerateObject())
                    {
                        if (!firstProp) sb.Append(',');
                        firstProp = false;

                        // Emit unquoted identifier when it's a valid JS identifier;
                        // otherwise fall back to a quoted JSON string for the key.
                        var name = prop.Name;
                        if (MyRegex().IsMatch(name))
                            sb.Append(name);
                        else
                            sb.Append(JsonSerializer.Serialize(name));

                        sb.Append(':');
                        WriteElement(prop.Value);
                    }
                    sb.Append('}');
                    break;

                case JsonValueKind.Array:
                    sb.Append('[');
                    bool firstItem = true;
                    foreach (var item in el.EnumerateArray())
                    {
                        if (!firstItem) sb.Append(',');
                        firstItem = false;
                        WriteElement(item);
                    }
                    sb.Append(']');
                    break;

                case JsonValueKind.String:
                    // Use JsonSerializer to ensure proper escaping of string contents
                    sb.Append(JsonSerializer.Serialize(el.GetString()));
                    break;

                case JsonValueKind.Number:
                    sb.Append(el.GetRawText());
                    break;

                case JsonValueKind.True:
                    sb.Append("true");
                    break;

                case JsonValueKind.False:
                    sb.Append("false");
                    break;

                case JsonValueKind.Null:
                default:
                    sb.Append("null");
                    break;
            }
        }

        WriteElement(doc.RootElement);
        return sb.ToString();
    }
    private static string GetPJSRTemplate(string templateFileName = "PixInsightTemplate.pjsr")
    {
        var assembly = Assembly.GetExecutingAssembly();
        try
        {
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(templateFileName, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(resourceName))
            {
                using var resourceStream = assembly.GetManifestResourceStream(resourceName);
                if (resourceStream != null)
                {
                    using var reader = new StreamReader(resourceStream);
                    var embedded = reader.ReadToEnd();
                    if (!string.IsNullOrWhiteSpace(embedded))
                        return embedded;
                }
            }
        }
        catch
        {
            // Fall through to disk candidates.
        }

        var baseDir = AppContext.BaseDirectory;
        // Also consider common source locations when running from the workspace during development
        var workspaceSrc = Path.Combine(Directory.GetCurrentDirectory(), "src", "FlatMaster.Infrastructure", "Services", templateFileName);
        var templateCandidates = new[]
        {
            Path.Combine(baseDir, "Services", templateFileName),
            Path.Combine(baseDir, templateFileName),
            workspaceSrc
        };

        foreach (var templatePath in templateCandidates)
        {
            if (File.Exists(templatePath))
                return File.ReadAllText(templatePath);
        }

        return "var __SENTINEL = \"%SENTINEL_PATH%\";\n"
             + "function touch(p, s){ try{ if(!p) return; var f=new File; f.createForWriting(p); if(s) f.outTextLn(s); f.close(); }catch(e){} }\n"
             + $"touch(__SENTINEL, \"ERROR: {templateFileName} not found\");\n";
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex MyRegex();
}


