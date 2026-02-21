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
public sealed class PixInsightService : IPixInsightService
{
    private readonly ILogger<PixInsightService> _logger;

    public PixInsightService(ILogger<PixInsightService> logger)
    {
      _logger = logger;
    }

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
                        temp = g.MatchingCriteria?.Temperature
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
                maxTempDeltaC = plan.Configuration.DarkMatching.MaxTempDeltaC
            },
            allowNearestExposureWithOptimize = plan.Configuration.DarkMatching.AllowNearestExposureWithOptimize,
            cacheDirName = plan.Configuration.CacheDirName,
            calibratedSubdirBase = plan.Configuration.CalibratedSubdirBase,
            masterSubdirName = plan.Configuration.MasterSubdirName,
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

        var debugScriptMode = Environment.GetEnvironmentVariable("FM_PI_DEBUG_SCRIPT");
        if (string.Equals(debugScriptMode, "touch", StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeScriptLineEndings($@"var __SENTINEL = ""{sentinelPath}"";
function touch(p, s){{ try{{ var f=new File; f.createForWriting(p); f.outTextLn(s); f.close(); }}catch(e){{}} }}
touch(__SENTINEL, ""OK"");");
        }
        if (string.Equals(debugScriptMode, "cfg", StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeScriptLineEndings($@"var __SENTINEL = ""{sentinelPath}"";
function touch(p, s){{ try{{ var f=new File; f.createForWriting(p); f.outTextLn(s); f.close(); }}catch(e){{}} }}
var CFG = {jsConfigObjectLiteral};
touch(__SENTINEL, ""OK"");");
        }
        if (string.Equals(debugScriptMode, "logic", StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeScriptLineEndings($@"var __SENTINEL = ""{sentinelPath}"";
function touch(p, s){{ try{{ var f=new File; f.createForWriting(p); f.outTextLn(s); f.close(); }}catch(e){{}} }}
var CFG = {jsConfigObjectLiteral};
var jobs = CFG.plan || [];
var n = 0;
for (var j=0; j<jobs.length; j++) {{
  var gs = jobs[j].groups || [];
  for (var g=0; g<gs.length; g++) n += (gs[g].files || []).length;
}}
touch(__SENTINEL, ""OK:files="" + n);");
        }
        if (string.Equals(debugScriptMode, "probe-ii", StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeScriptLineEndings($@"var __SENTINEL = ""{sentinelPath}"";
function touch(p, s){{ try{{ var f=new File; f.createForWriting(p); f.outTextLn(s); f.close(); }}catch(e){{}} }}
try {{
  var ii = new ImageIntegration;
  touch(__SENTINEL, ""OK:ImageIntegration"");
}} catch(e) {{
  touch(__SENTINEL, ""ERROR:"" + e);
}}");
        }
        
        // Build config + template as one script
        // sentinel path is set independently so the catch block works even if CFG parse fails
        var script = GetPJSRTemplate()
            .Replace("%SENTINEL_PATH%", sentinelPath)
            .Replace("%CONFIG_JS_OBJECT_LITERAL%", jsConfigObjectLiteral);
        
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
        {
            return new ProcessingResult
            {
                Success = false, ExitCode = -1, Output = "",
                ErrorMessage = $"PixInsight executable not found: {pixInsightExe}"
            };
        }

        var jobList = plan.SelectedJobs.ToList();
        if (jobList.Count == 0)
        {
            return new ProcessingResult
            {
                Success = true, ExitCode = 0, Output = "No jobs to process."
            };
        }

        logOutput?.Report($"[PixInsight] Executable: {pixInsightExe}");
        logOutput?.Report($"[PixInsight] Jobs: {jobList.Count}, batch size: {batchSize}");

        var runId = Guid.NewGuid().ToString("N");
        var tempRoot = Path.GetTempPath();

        // â”€â”€ Preflight (once) â”€â”€
        KillExistingPixInsightProcesses(logOutput);
        var preflightSentinelPath = Path.Combine(tempRoot, $"flatmaster_preflight_{runId}.txt");
        if (File.Exists(preflightSentinelPath)) try { File.Delete(preflightSentinelPath); } catch { }

        var preflightScriptPath = Path.Combine(tempRoot, $"flatmaster_preflight_{runId}.js");
        await File.WriteAllTextAsync(preflightScriptPath, BuildPreflightScript(preflightSentinelPath), cancellationToken);
        logOutput?.Report($"[PixInsight] Preflight script: {preflightScriptPath}");

        var preflightResult = await ExecuteWithVariantsAsync(
            preflightScriptPath, pixInsightExe, preflightSentinelPath, "PREFLIGHT_OK", logOutput, cancellationToken);
        if (!preflightResult.Success)
        {
            return new ProcessingResult
            {
                Success = false, ExitCode = preflightResult.ExitCode,
                Output = preflightResult.Output,
                ErrorMessage = "PixInsight preflight failed. The script did not execute. " +
                               "Check PixInsight installation and CLI invocation."
            };
        }

        // Kill PI after preflight and wait for full shutdown before batches
        KillExistingPixInsightProcesses(logOutput);
        await Task.Delay(3000, cancellationToken);

        // â”€â”€ Batch loop â”€â”€
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
            logOutput?.Report($"\nâ•â• Folders {firstIdx}-{lastIdx} of {jobList.Count}  ({filesProcessedSoFar + batchFiles}/{totalFiles} files) â•â•");
            foreach (var j in batchJobs)
                logOutput?.Report($"  {j.DirectoryPath}");

            // Build a partial plan for this batch
            var batchPlan = new ProcessingPlan
            {
                Jobs = batchJobs,
                DarkCatalog = plan.DarkCatalog.ToList(),
                Configuration = plan.Configuration
            };

            var scriptPath = Path.Combine(tempRoot, $"flatmaster_script_{runId}_b{b + 1}.js");
            var sentinelPath = Path.Combine(tempRoot, $"flatmaster_sentinel_{runId}_b{b + 1}.txt");
            var script = GeneratePJSRScript(batchPlan, sentinelPath);
            await File.WriteAllTextAsync(scriptPath, script, cancellationToken);
            logOutput?.Report($"  Script: {script.Length / 1024} KB ({scriptPath})");

            if (File.Exists(sentinelPath)) try { File.Delete(sentinelPath); } catch { }
            KillExistingPixInsightProcesses(logOutput);

            var batchResult = await ExecuteWithVariantsAsync(
                scriptPath, pixInsightExe, sentinelPath, "OK", logOutput, cancellationToken);

            allOutput.AppendLine(batchResult.Output);
            if (batchResult.Success)
            {
              succeeded++;
              filesProcessedSoFar += batchFiles;
              logOutput?.Report($"  âœ“ Folders {firstIdx}-{lastIdx} done  ({filesProcessedSoFar}/{totalFiles} files)");
            }
            else
            {
              failed++;
              filesProcessedSoFar += batchFiles; // count them even on failure for progress
              logOutput?.Report($"  âœ— Folders {firstIdx}-{lastIdx} FAILED: {batchResult.ErrorMessage}");
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

    private string[][] BuildArgsVariants(string scriptPath)
    {
        var normalized = NormalizePath(scriptPath);
        return new[]
        {
            // Always force a new PI instance (-n) to avoid IPC/slot reuse issues.
            // Keep to documented run forms and avoid -x/startup-script paths.
            new[] { "--automation-mode", "--no-startup-scripts", "-n", $"--run={normalized}", "--force-exit" },
            new[] { "--automation-mode", "-n", $"--run={normalized}", "--force-exit" },
            new[] { "-n", $"--run={normalized}", "--force-exit" }
        };
    }

      private static string BuildPreflightScript(string sentinelPath)
      {
        var normalized = NormalizePath(sentinelPath);
        return $"var f=new File; f.createForWriting(\"{normalized}\"); f.outTextLn(\"PREFLIGHT_OK\"); f.close();";
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
          var maxAttemptDuration = expectedSentinel == "PREFLIGHT_OK"
              ? TimeSpan.FromMinutes(2)
              : TimeSpan.FromMinutes(20);

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
            using var process = new Process { StartInfo = startInfo };

            process.Start();

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
                    if (line.IndexOf("empty script", StringComparison.OrdinalIgnoreCase) >= 0)
                        sawEmptyScriptError = true;
                    if (line.IndexOf("invalid application instance index", StringComparison.OrdinalIgnoreCase) >= 0)
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
                      if (line.IndexOf("empty script", StringComparison.OrdinalIgnoreCase) >= 0)
                          sawEmptyScriptError = true;
                      if (line.IndexOf("invalid application instance index", StringComparison.OrdinalIgnoreCase) >= 0)
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
                  await process.WaitForExitAsync();
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

    private static string NormalizeScriptLineEndings(string script)
    {
        // PixInsight on Windows behaves more reliably with canonical CRLF line endings.
        return script.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
    }

    private static string ConvertJsonToJsObjectLiteral(string json)
    {
        // Convert JSON object keys to unquoted JavaScript property names.
        // Example: "plan": [...] -> plan: [...]
        return Regex.Replace(
            json,
            @"(^|[\{\[,]\s*)""([A-Za-z_][A-Za-z0-9_]*)""\s*:",
            "$1$2:",
            RegexOptions.Multiline);
    }

    private static string GetPJSRTemplate()
    {
        var assembly = Assembly.GetExecutingAssembly();
        try
        {
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("PixInsightTemplate.pjsr", StringComparison.OrdinalIgnoreCase));
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
        var templateCandidates = new[]
        {
            Path.Combine(baseDir, "Services", "PixInsightTemplate.pjsr"),
            Path.Combine(baseDir, "PixInsightTemplate.pjsr")
        };

        foreach (var templatePath in templateCandidates)
        {
            if (File.Exists(templatePath))
                return File.ReadAllText(templatePath);
        }

        return @"
var __SENTINEL = ""%SENTINEL_PATH%"";
function touch(p, s){ try{ if(!p) return; var f=new File; f.createForWriting(p); if(s) f.outTextLn(s); f.close(); }catch(e){} }
touch(__SENTINEL, ""ERROR: PixInsightTemplate.pjsr not found"");
";
    }
}
