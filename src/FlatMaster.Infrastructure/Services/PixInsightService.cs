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
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Globalization;
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
  private readonly IDarkMatchingService _darkMatchingService;

    public PixInsightService(ILogger<PixInsightService> logger, IDarkMatchingService darkMatchingService)
    {
      _logger = logger;
      _darkMatchingService = darkMatchingService;
    }

    public string GeneratePJSRScript(ProcessingPlan plan)
    {
        var sentinelPath = NormalizePath(Path.Combine(Path.GetTempPath(), "flatmaster_sentinel.txt"));

        // Precompute best darks per exposure using the C# dark-matcher so the PixInsight script
        // can rely on preselected choices instead of repeating matching in JS.
        var preselected = new Dictionary<string, object?>();
        var darkCatalog = plan.SelectedDarks.ToList();
        var matchOptions = plan.Configuration.DarkMatching;

        foreach (var job in plan.SelectedJobs)
        {
          foreach (var g in job.ExposureGroups)
          {
            var k = (Math.Round(g.ExposureTime * 1000) / 1000.0).ToString(CultureInfo.InvariantCulture);
            if (preselected.ContainsKey(k)) continue;
            var best = _darkMatchingService.FindBestDark(g, darkCatalog, matchOptions);
            if (best != null)
            {
              preselected[k] = new
              {
                path = NormalizePath(best.FilePath),
                optimize = best.OptimizeRequired,
                kind = best.MatchKind
              };
            }
          }
        }

        var config = new
        {
            plan = plan.SelectedJobs.Select(j => new
            {
                dirPath = NormalizePath(j.DirectoryPath),
                outRoot = NormalizePath(j.OutputRootPath),
                relDir = j.RelativeDirectory.Replace("\\", "/"),
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
          preselected = preselected,
            deleteCalibrated = plan.Configuration.DeleteCalibratedFlats
        };

        // Use UnsafeRelaxedJsonEscaping so paths come through cleanly as forward slashes
        // JSON is valid JavaScript, so var CFG = {...}; works as direct assignment
        var jsonConfig = JsonSerializer.Serialize(config, new JsonSerializerOptions 
        { 
            WriteIndented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        
        // Build config + template as one script
        // sentinel path is set independently so the catch block works even if CFG parse fails
        var script = GetPJSRTemplate()
            .Replace("%SENTINEL_PATH%", sentinelPath)
            .Replace("%CONFIG_JSON_LITERAL%", jsonConfig);
        
        return script;
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

        // ── Preflight (once) ──
        KillExistingPixInsightProcesses(logOutput);
        var preflightSentinelPath = Path.Combine(Path.GetTempPath(), "flatmaster_preflight.txt");
        if (File.Exists(preflightSentinelPath)) try { File.Delete(preflightSentinelPath); } catch { }

        var preflightScriptPath = Path.Combine(Path.GetTempPath(), "flatmaster_preflight.js");
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

        // ── Batch loop ──
        int totalBatches = (jobList.Count + batchSize - 1) / batchSize;
        int succeeded = 0, failed = 0;
        var allOutput = new StringBuilder();
        var scriptPath = Path.Combine(Path.GetTempPath(), "flatmaster_script.js");
        var sentinelPath = Path.Combine(Path.GetTempPath(), "flatmaster_sentinel.txt");
        int totalFiles = jobList.Sum(j => j.TotalFileCount);
        int filesProcessedSoFar = 0;

        for (int b = 0; b < totalBatches; b++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batchJobs = jobList.Skip(b * batchSize).Take(batchSize).ToList();
            int firstIdx = b * batchSize + 1;
            int lastIdx = firstIdx + batchJobs.Count - 1;
            int batchFiles = batchJobs.Sum(j => j.TotalFileCount);
            logOutput?.Report($"\n══ Folders {firstIdx}-{lastIdx} of {jobList.Count}  ({filesProcessedSoFar + batchFiles}/{totalFiles} files) ══");
            foreach (var j in batchJobs)
                logOutput?.Report($"  {j.DirectoryPath}");

            // Build a partial plan for this batch
            var batchPlan = new ProcessingPlan
            {
                Jobs = batchJobs,
                DarkCatalog = plan.DarkCatalog.ToList(),
                Configuration = plan.Configuration
            };

            var script = GeneratePJSRScript(batchPlan);
            await File.WriteAllTextAsync(scriptPath, script, cancellationToken);
            logOutput?.Report($"  Script: {script.Length / 1024} KB");

            if (File.Exists(sentinelPath)) try { File.Delete(sentinelPath); } catch { }
            KillExistingPixInsightProcesses(logOutput);

            var batchResult = await ExecuteWithVariantsAsync(
                scriptPath, pixInsightExe, sentinelPath, "OK", logOutput, cancellationToken);

            allOutput.AppendLine(batchResult.Output);
            if (batchResult.Success)
            {
                succeeded++;
                filesProcessedSoFar += batchFiles;
                logOutput?.Report($"  ✓ Folders {firstIdx}-{lastIdx} done  ({filesProcessedSoFar}/{totalFiles} files)");
            }
            else
            {
                failed++;
                filesProcessedSoFar += batchFiles; // count them even on failure for progress
                logOutput?.Report($"  ✗ Folders {firstIdx}-{lastIdx} FAILED: {batchResult.ErrorMessage}");
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
            new[] { "--automation-mode", "--no-startup-scripts", "-n", $"--run={normalized}", "--force-exit" },
            new[] { $"--run={normalized}", "--force-exit" },
            new[] { $"-x={normalized}" }
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
              Success = sentinelSuccess && exitCode == 0,
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

    private static string GetPJSRTemplate()
    {
        // Embedded PJSR template — config is injected as a direct JSON object literal
        return @"
// === Flat Master Executor (PixInsight 1.8.9+) ===
// Generated by FlatMaster C# Application

// Sentinel path for completion signaling (set independently of CFG for error catch)
var __SENTINEL = ""%SENTINEL_PATH%"";
function touch(p, s){ try{ if(!p) return; var f=new File; f.createForWriting(p); if(s) f.outTextLn(s); f.close(); }catch(e){} }

// Configuration - JSON is valid JavaScript so direct assignment works
var CFG = %CONFIG_JSON_LITERAL%;

// -------- Helper Functions --------
function log(s){ Console.writeln(s); }
function warn(s){ Console.warningln(s); }
function error(s){ Console.criticalln(s); }
function joinPath(a,b){ if(!a)return b; var slash=(a.indexOf(""\\"")>=0)?""\\"":""/""; if(a.endsWith(""/"")||a.endsWith(""\\""))return a+b; return a+slash+b; }
function parentDir(p){ if (!p) return """"; var i = Math.max(p.lastIndexOf(""/""), p.lastIndexOf(""\\"")); return i>0 ? p.substring(0,i) : """"; }
function ensureDir(p){ if (p && !File.directoryExists(p)) File.createDirectory(p, true); }
function kexp(x){ return (Math.round(x*1000)/1000).toString(); }
function baseName(p){ return p.replace(/^.*[\\/]/,''); }

// Enum resolution with fallbacks
function enumVal(klass, names, defVal){
  for (var i=0;i<names.length;i++){
    var n = names[i];
    try{ if (typeof klass[n] === ""number"") return klass[n]; }catch(e){}
    try{ if (klass.prototype && typeof klass.prototype[n] === ""number"") return klass.prototype[n]; }catch(e){}
  }
  return defVal;
}

var II_ENUM = {
  Comb_Average:  enumVal(ImageIntegration, [""Average""], 0),
  Weight_Dont:   enumVal(ImageIntegration, [""DontCare"",""Weight_DontCare""], 0),
  Norm_None:     enumVal(ImageIntegration, [""NoNormalization"",""NoScale"",""NoScaling""], 0),
  Norm_Mult:     enumVal(ImageIntegration, [""Multiplicative""], enumVal(ImageIntegration, [""NoNormalization""], 0)),
  Rej_None:      enumVal(ImageIntegration, [""NoRejection""], 0),
  Rej_Winsor:    enumVal(ImageIntegration, [""WinsorizedSigmaClipping"",""WinsorizedSigmaClip""], enumVal(ImageIntegration, [""NoRejection""], 0)),
  Rej_PC:        enumVal(ImageIntegration, [""PercentileClip"",""Percentile""], enumVal(ImageIntegration, [""NoRejection""], 0)),
  Rej_LinFit:    enumVal(ImageIntegration, [""LinearFit""], enumVal(ImageIntegration, [""NoRejection""], 0)),
  RejNorm_None:  enumVal(ImageIntegration, [""NoRejectionNormalization"",""NoNormalization""], 0),
  RejNorm_Eq:    enumVal(ImageIntegration, [""EqualizeFluxes"",""RejectionNormalization_EqualizeFluxes""], enumVal(ImageIntegration, [""NoRejectionNormalization""], 0))
};

function assignIIImagesRowsPaths(II, paths){
  var rows = [];
  for (var i=0;i<paths.length;i++) rows.push([ true, String(paths[i]), """", """" ]);
  II.images = rows;
}

function assignICTargets(IC, paths){
  var rows = [];
  for (var i=0;i<paths.length;i++) rows.push([true, String(paths[i])]);
  IC.targetFrames = rows;
}

function saveXISF(win, outPath, hints){
  ensureDir(parentDir(outPath));
  var tried = [];
  function tryCall(label, fn){ 
    try{ 
      fn(); 
      log(""  [saveAs "" + label + ""] "" + outPath); 
      return true; 
    } catch(e){ 
      tried.push(label + "": "" + e); 
      return false; 
    } 
  }
  if (tryCall(""path,format,hints"", function(){ win.saveAs(outPath, ""xisf"", hints); })) return;
  if (tryCall(""path,format,hints,overwrite"", function(){ win.saveAs(outPath, ""xisf"", hints, false); })) return;
  if (tryCall(""path,format"", function(){ win.saveAs(outPath, ""xisf""); })) return;
  if (tryCall(""path,overwrite,format,hints"", function(){ win.saveAs(outPath, false, ""xisf"", hints); })) return;
  if (tryCall(""path,overwrite"", function(){ win.saveAs(outPath, false); })) return;
  if (tryCall(""path-only"", function(){ win.saveAs(outPath); })) return;
  throw new Error(""saveAs failed for "" + outPath + "" ; tried => "" + tried.join("" | ""));
}

function safeNum(x){ return (x===undefined||x===null||isNaN(x)) ? NaN : Number(x); }

function metaScore(c,want,match){
  var s=0;
  var cg=safeNum(c.gain), wg=safeNum(want.gain);
  var co=safeNum(c.offset), wo=safeNum(want.offset);
  var ct=safeNum(c.temp), wt=safeNum(want.temp);
  if(match.enforceBinning && want.binning && c.binning && c.binning===want.binning) s+=3;
  if(match.preferSameGainOffset){
    if(!isNaN(cg) && !isNaN(wg) && Math.abs(cg-wg) < 0.01) s+=2;
    if(!isNaN(co) && !isNaN(wo) && Math.abs(co-wo) < 0.5 ) s+=2;
  }
  if(match.preferClosestTemp && !isNaN(ct) && !isNaN(wt)){
    var dt=Math.abs(ct - wt);
    if (dt <= match.maxTempDeltaC) s += (1.5 - dt*0.2);
  }
  return s;
}

function groupByExp(cats, typeName){
  var m = {};
  for (var i=0;i<cats.length;i++){
    var c=cats[i]; if (c.type!==typeName) continue;
    var k=kexp(c.exposure); (m[k]=m[k]||[]).push(c);
  }
  return m;
}

function integrateToMaster(paths,outPath,forDark,hints,rej){
  if(!paths.length) throw new Error(""No frames for ""+outPath);
  if(paths.length < 3) throw new Error(""ImageIntegration needs >=3 inputs; got ""+paths.length);

  var II=new ImageIntegration;
  assignIIImagesRowsPaths(II, paths);

  II.combination = II_ENUM.Comb_Average;
  II.weightMode  = II_ENUM.Weight_Dont;
  II.evaluateNoise = false;
  II.generate64BitResult = true;
  II.generateRejectionMaps = false;
  II.generateSlopeMaps = false;
  II.generateIntegratedImage = true;

  if(forDark){
    II.normalization = II_ENUM.Norm_None;
    II.rejection = II_ENUM.Rej_Winsor;
    II.rejectionNormalization = II_ENUM.RejNorm_None;
  } else {
    II.normalization = II_ENUM.Norm_Mult;
    II.rejectionNormalization = II_ENUM.RejNorm_Eq;
    
    var n = paths.length;
    if (n < 6) {
      II.rejection = II_ENUM.Rej_PC;
      II.pcClipLow = 0.20;
      II.pcClipHigh = 0.10;
    } else if (n <= 15) {
      II.rejection = II_ENUM.Rej_Winsor;
      II.sigmaLow = 4.0;
      II.sigmaHigh = 3.0;
      II.winsorizationCutoff = 5.0;
      II.clipLow = true;
      II.clipHigh = true;
    } else {
      II.rejection = II_ENUM.Rej_LinFit;
      II.linearFitLow = 5.0;
      II.linearFitHigh = 4.0;
      II.clipLow = true;
      II.clipHigh = true;
    }
    II.largeScaleClipHigh = false;
  }

  if (II.rejection === II_ENUM.Rej_Winsor && rej) {
    if (typeof rej.lowSigma === ""number"") II.sigmaLow = rej.lowSigma;
    if (typeof rej.highSigma === ""number"") II.sigmaHigh = rej.highSigma;
  }

  if(!II.executeGlobal()) throw new Error(""ImageIntegration failed: ""+outPath);

  var id=II.integrationImageId;
  var win=ImageWindow.windowById(id);
  if(!win) throw new Error(""Integration window not found"");
  
  try {
    var imgType = (!forDark) ? ""Master Flat"" : ""Master Dark"";
    var kws = win.keywords || [];
    kws.push(new FITSKeyword(""IMAGETYP"", imgType, ""Type of image""));
    win.keywords = kws;
  } catch (e) { warn(""[metadata] IMAGETYP not set: "" + e); }

  saveXISF(win, outPath, hints);
  win.forceClose();

  var extraIds = [ II.lowRejectionMapImageId, II.highRejectionMapImageId, II.slopeMapImageId ];
  for (var i=0;i<extraIds.length;i++){
    var eid = extraIds[i];
    if (eid && typeof eid === ""string"" && eid.length){
      var ew = ImageWindow.windowById(eid);
      if (ew) try{ ew.forceClose(); }catch(_){}
    }
  }
}

function calibrateFlats(paths,outDir,masterDarkPath,optimize,hints){
  ensureDir(outDir);
  var IC=new ImageCalibration;
  assignICTargets(IC, paths);

  IC.masterBiasEnabled = false;
  IC.masterFlatEnabled = false;
  if (masterDarkPath){
    IC.masterDarkEnabled = true;
    IC.masterDarkPath = masterDarkPath;
    IC.optimizeDarks = !!optimize;
  } else {
    IC.masterDarkEnabled = false;
    IC.masterDarkPath = "";
    IC.optimizeDarks = false;
  }

  IC.outputDirectory=outDir; 
  IC.outputExtension="".xisf""; 
  IC.outputPostfix=""_c""; 
  IC.outputHints=hints;
  
  if(!IC.executeGlobal()) throw new Error(""ImageCalibration failed."");
}

function pickDarkFor(exp, want, cacheDir, cats, rej, hintsCal, match){
  var MDF = groupByExp(cats, ""MASTERDARKFLAT"");
  var MD  = groupByExp(cats, ""MASTERDARK"");
  var DF  = groupByExp(cats, ""DARKFLAT"");
  var D   = groupByExp(cats, ""DARK"");
  var k = kexp(exp);

  // If the C# side precomputed a match for this exposure, prefer that selection
  try {
    if (CFG.preselected && CFG.preselected[k]){
      var p = CFG.preselected[k];
      return { path: p.path, optimize: !!p.optimize, kind: p.kind || ""preselected"" };
    }
  } catch (e) { /* ignore and continue to JS matching fallback */ }

  if (MDF[k] && MDF[k].length){
    var best=MDF[k][0], bestS=-1;
    for (var i=0;i<MDF[k].length;i++){ var s=metaScore(MDF[k][i],want,match); if (s>bestS){bestS=s; best=MDF[k][i];}}
    return {path:best.path, optimize:false, kind:""MasterDarkFlat(exact)""};
  }
  if (DF[k] && DF[k].length >= 3){
    var out=joinPath(cacheDir,""MasterDarkFlat_""+k+""s.xisf"");
    integrateToMaster(DF[k].map(function(x){return x.path;}),out,true,hintsCal,rej);
    return {path:out, optimize:false, kind:""MasterDarkFlat(built)""};
  }
  if (MD[k] && MD[k].length){
    var best2=MD[k][0], s2=-1;
    for (var j=0;j<MD[k].length;j++){ var sc=metaScore(MD[k][j],want,match); if (sc>s2){s2=sc; best2=MD[k][j];}}
    return {path:best2.path, optimize:false, kind:""MasterDark(exact)""};
  }
  if (D[k] && D[k].length >= 3){
    var out2=joinPath(cacheDir,""MasterDark_""+k+""s.xisf"");
    integrateToMaster(D[k].map(function(x){return x.path;}),out2,true,hintsCal,rej);
    return {path:out2, optimize:false, kind:""MasterDark(built)""};
  }
  if (CFG.allowNearestExposureWithOptimize){
    var allMD=[], kk;
    for (kk in MD){ for (var a=0;a<MD[kk].length;a++) allMD.push(MD[kk][a]); }
    if (allMD.length){
      allMD.sort(function(a,b){ return Math.abs(a.exposure-exp)-Math.abs(b.exposure-exp); });
      var best3=allMD[0];
      var delta = Math.abs(best3.exposure - exp);
      if (delta <= 2.0) {
        return {path:best3.path, optimize:false, kind:""MasterDark(nearest)""};
      }
      return {path:best3.path, optimize:true, kind:""MasterDark(nearest+optimize)""};
    }
  }
  return null;
}

function guessFilterFrom(files, dir){
  var rx = /(?:^|[_\-])(?:FILTER|Filter)[_\-]?([A-Za-z0-9]+)/;
  for (var i=0;i<files.length;i++){
    var m = baseName(files[i]).match(rx);
    if (m && m[1]) return String(m[1]).toUpperCase();
  }
  var parts = dir.replace(/\\/g,""/"").split(""/"");
  var last = parts.length ? parts[parts.length-1] : """";
  if (last && !/^\d{4}-\d{2}-\d{2}$/.test(last)) return last.toUpperCase();
  return ""UNKNOWN"";
}

function guessDateFromPath(dir, files){
  var rx = /\b(20\d{2}-\d{2}-\d{2})\b/;
  var m = dir.match(rx);
  if (m) return m[1];
  for (var i=0;i<files.length;i++){
    var mm = files[i].match(rx);
    if (mm) return mm[1];
  }
  return ""UNKNOWNDATE"";
}

// -------- Main Processing --------
function run(){
  Console.show();
  var plan = CFG.plan || [];
  var cats = CFG.darkCatalog || [];
  var rej = CFG.rejection || {lowSigma:5.0, highSigma:5.0};
  var hintsCal = CFG.xisfHintsCal||"""";
  var hintsMaster= CFG.xisfHintsMaster||"""";
  var match = CFG.match || {};

  for (var j=0;j<plan.length;j++){
    var job=plan[j], dir=job.dirPath; 
    log(""\n=== FLAT dir: ""+dir+"" ==="");
    
    var rel = job.relDir || """"; 
    if (rel === ""."") rel = """";
    var outRoot = job.outRoot || dir;
    var outBase = rel ? joinPath(outRoot, rel) : outRoot;
    ensureDir(outBase);
    
    for (var g=0; g<job.groups.length; g++){
      var grp=job.groups[g], exp=grp.exposure, files=grp.files, want=grp.want || {};
      if (want.binning === undefined) want.binning = null;
      if (want.gain === undefined) want.gain = null;
      if (want.offset === undefined) want.offset = null;
      if (want.temp === undefined) want.temp = null;

      log(""  Exposure ""+kexp(exp)+"" s : ""+files.length+"" flats"");

      // Pre-compute master flat path so we can skip if already done
      var dateStr = guessDateFromPath(dir, files);
      var filt = guessFilterFrom(files, dir);
      var masterName = ""MasterFlat_"" + dateStr + ""_"" + filt + ""_"" + kexp(exp) + ""s.xisf"";
      var masterOut = joinPath(outBase, masterName);

      if (File.exists(masterOut)){
        log(""  SKIP (master already exists): "" + masterOut);
        continue;
      }
      
      var cacheDir = joinPath(dir, CFG.cacheDirName||""_DarkMasters"");
      var sel = pickDarkFor(exp, want, cacheDir, cats, rej, hintsCal, match);
      if(!sel){
        warn(""  No suitable dark @ "" + kexp(exp) + "" s in "" + dir + "" - skipping this exposure group."");
        continue;
      }
      log(""  Using [""+sel.kind+""] optimize=""+sel.optimize);

      var calOut = joinPath(outBase, (CFG.calibratedSubdirBase||""_CalibratedFlats"")+""_""+kexp(exp)+""s"");
      calibrateFlats(files, calOut, sel.path, sel.optimize, hintsCal);

      var calFiles=[], ff=new FileFind;
      if(ff.begin(joinPath(calOut,""*.xisf""))){ 
        do{ 
          if(ff.isFile) calFiles.push(joinPath(calOut,ff.name)); 
        } while(ff.next()); 
      }
      ff.end();
      if(!calFiles.length) throw new Error(""No calibrated flats in ""+calOut);
      
      integrateToMaster(calFiles, masterOut, false, hintsMaster, rej);
      log(""  Saved: ""+masterOut);

      if (CFG.deleteCalibrated){
        try{
          var delFF=new FileFind;
          if (delFF.begin(joinPath(calOut,""*""))){
            do{
              var p = joinPath(calOut, delFF.name);
              try { if (delFF.isFile) File.remove(p); } catch(e){}
            } while (delFF.next());
          }
          delFF.end();
          try { File.removeDirectory(calOut, true); } catch(e){}
          log(""  [cleanup] removed ""+calOut);
        } catch(e){
          warn(""  [cleanup] failed ""+calOut+"" : ""+e);
        }
      }
    }
  }
  
  log(""\nAll processing complete."");
  touch(__SENTINEL, ""OK"");
}

try{ 
  if (typeof CFG === ""undefined"") throw new Error(""CFG not loaded - config file may have a syntax error"");
  run(); 
} catch(e){ 
  error(""ERROR: ""+e); 
  touch(__SENTINEL, ""ERROR: ""+e); 
}
";
    }
}

