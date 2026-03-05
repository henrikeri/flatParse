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
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Threading;

namespace FlatMaster.WPF.ViewModels;

public partial class MainViewModel
{
    private const int MaxUiLogLines = 5000;
    private static readonly TimeSpan ProcessingLogFlushInterval = TimeSpan.FromMilliseconds(120);
    private readonly ConcurrentQueue<string> _queuedProcessingLogMessages = new();
    private readonly Queue<string> _uiLogLines = new();
    private DispatcherTimer? _processingLogDrainTimer;

    private void ParseBatchProgress(string msg)
    {
        if (_nativeProgressActive && TryParseNativeProgress(msg))
            return;

        var headerMatch = FolderHeaderRegex.Match(msg);
        if (headerMatch.Success)
        {
            _lastFolderIdx = int.Parse(headerMatch.Groups[2].Value);
            _totalFolderCount = int.Parse(headerMatch.Groups[3].Value);
            var filesUpcoming = int.Parse(headerMatch.Groups[4].Value);
            _totalFileCount = int.Parse(headerMatch.Groups[5].Value);
            BatchProgressText = $"Folder {_lastFolderIdx}/{_totalFolderCount}  ({filesUpcoming}/{_totalFileCount} files)";
            StatusMessage = $"Processing folders {headerMatch.Groups[1].Value}-{_lastFolderIdx} of {_totalFolderCount}...";
        }

        var doneMatch = FolderDoneRegex.Match(msg);
        if (!doneMatch.Success)
            return;

        _completedBatches++;
        var filesDone = int.Parse(doneMatch.Groups[2].Value);
        var filesTotal = int.Parse(doneMatch.Groups[3].Value);
        _totalFileCount = filesTotal;

        if (filesTotal <= 0)
            return;

        BatchProgressValue = (double)filesDone / filesTotal;
        var elapsed = DateTime.Now - _processingStartTime;
        var avgPerFile = elapsed.TotalSeconds / Math.Max(1, filesDone);
        var remaining = avgPerFile * (filesTotal - filesDone);
        TimeRemainingText = FormatTimeRemaining(TimeSpan.FromSeconds(remaining));
        BatchProgressText = $"Folder {_lastFolderIdx}/{_totalFolderCount}  ({filesDone}/{filesTotal} files, {(int)(BatchProgressValue * 100)}%)";
    }

    private void InitializeNativeProgressTracking(List<FlatMaster.Core.Models.DirectoryJob> selectedJobs)
    {
        _nativeProgressActive = true;
        _nativeTotalGroups = Math.Max(1, selectedJobs.Sum(j => j.ExposureGroups.Count));
        _nativeCompletedGroups = 0;
        _nativeCurrentGroupFrames = 0;
        _nativeCurrentGroupCalibrated = 0;
        _nativeGroupInFlight = false;
        BatchProgressValue = 0;
        BatchProgressText = $"Native groups 0/{_nativeTotalGroups}";
        TimeRemainingText = string.Empty;
    }

    private void ResetNativeProgressTracking()
    {
        _nativeProgressActive = false;
        _nativeTotalGroups = 0;
        _nativeCompletedGroups = 0;
        _nativeCurrentGroupFrames = 0;
        _nativeCurrentGroupCalibrated = 0;
        _nativeGroupInFlight = false;
    }

    private bool TryParseNativeProgress(string msg)
    {
        var loadingMatch = NativeLoadingRegex.Match(msg);
        if (loadingMatch.Success)
        {
            _nativeCurrentGroupFrames = int.Parse(loadingMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            _nativeCurrentGroupCalibrated = 0;
            _nativeGroupInFlight = true;
            UpdateNativeBatchProgress();
            return true;
        }

        var calibratedMatch = NativeCalibratedRegex.Match(msg);
        if (calibratedMatch.Success)
        {
            _nativeCurrentGroupCalibrated = int.Parse(calibratedMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            if (_nativeCurrentGroupFrames <= 0)
                _nativeCurrentGroupFrames = int.Parse(calibratedMatch.Groups[2].Value, CultureInfo.InvariantCulture);
            _nativeGroupInFlight = true;
            UpdateNativeBatchProgress();
            return true;
        }

        if (NativeSkippedRegex.IsMatch(msg))
        {
            _nativeCompletedGroups = Math.Min(_nativeTotalGroups, _nativeCompletedGroups + 1);
            _nativeCurrentGroupFrames = 0;
            _nativeCurrentGroupCalibrated = 0;
            _nativeGroupInFlight = false;
            UpdateNativeBatchProgress();
            return true;
        }

        if (NativeMasterWrittenRegex.IsMatch(msg))
        {
            if (_nativeGroupInFlight || _nativeCurrentGroupFrames > 0)
                _nativeCompletedGroups = Math.Min(_nativeTotalGroups, _nativeCompletedGroups + 1);
            _nativeCurrentGroupFrames = 0;
            _nativeCurrentGroupCalibrated = 0;
            _nativeGroupInFlight = false;
            UpdateNativeBatchProgress();
            return true;
        }

        if (NativeErrorRegex.IsMatch(msg) && _nativeGroupInFlight)
        {
            _nativeCompletedGroups = Math.Min(_nativeTotalGroups, _nativeCompletedGroups + 1);
            _nativeCurrentGroupFrames = 0;
            _nativeCurrentGroupCalibrated = 0;
            _nativeGroupInFlight = false;
            UpdateNativeBatchProgress();
            return true;
        }

        return false;
    }

    private void UpdateNativeBatchProgress()
    {
        if (_nativeTotalGroups <= 0)
            return;

        var groupFraction = 0.0;
        if (_nativeGroupInFlight && _nativeCurrentGroupFrames > 0)
        {
            groupFraction = Math.Clamp(
                (double)_nativeCurrentGroupCalibrated / _nativeCurrentGroupFrames,
                0.0,
                1.0);
        }

        var unitsDone = Math.Min(_nativeTotalGroups, _nativeCompletedGroups + groupFraction);
        BatchProgressValue = unitsDone / _nativeTotalGroups;

        if (_nativeGroupInFlight && _nativeCurrentGroupFrames > 0)
        {
            BatchProgressText = string.Format(
                CultureInfo.InvariantCulture,
                "Native group {0}/{1} (calibrated {2}/{3})",
                _nativeCompletedGroups + 1,
                _nativeTotalGroups,
                _nativeCurrentGroupCalibrated,
                _nativeCurrentGroupFrames);
        }
        else
        {
            BatchProgressText = string.Format(
                CultureInfo.InvariantCulture,
                "Native groups {0}/{1}",
                _nativeCompletedGroups,
                _nativeTotalGroups);
        }

        if (unitsDone > 0.0)
        {
            var elapsed = DateTime.Now - _processingStartTime;
            var remainingUnits = Math.Max(0.0, _nativeTotalGroups - unitsDone);
            var avgPerUnit = elapsed.TotalSeconds / unitsDone;
            TimeRemainingText = FormatTimeRemaining(TimeSpan.FromSeconds(avgPerUnit * remainingUnits));
        }
    }

    private static string FormatTimeRemaining(TimeSpan ts)
    {
        if (ts.TotalSeconds < 1)
            return "Almost done";
        if (ts.TotalHours >= 1)
            return $"~{(int)ts.TotalHours}h {ts.Minutes}m remaining";
        if (ts.TotalMinutes >= 1)
            return $"~{(int)ts.TotalMinutes}m remaining";
        return "< 1 min remaining";
    }

    private void StartProcessingLogDrain()
    {
        while (_queuedProcessingLogMessages.TryDequeue(out _))
        {
            // Drain stale queued messages from previous runs.
        }

        if (_processingLogDrainTimer == null)
        {
            _processingLogDrainTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = ProcessingLogFlushInterval
            };
            _processingLogDrainTimer.Tick += (_, _) => FlushQueuedProcessingLogMessages(maxMessages: 256);
        }

        _processingLogDrainTimer.Start();
    }

    private void StopProcessingLogDrain()
    {
        _processingLogDrainTimer?.Stop();
    }

    private void EnqueueProcessingLogMessage(string message)
    {
        _queuedProcessingLogMessages.Enqueue(message);
    }

    private void FlushQueuedProcessingLogMessages(int maxMessages)
    {
        if (maxMessages <= 0)
            return;

        var drained = new List<string>(Math.Min(maxMessages, 256));
        while (drained.Count < maxMessages && _queuedProcessingLogMessages.TryDequeue(out var message))
            drained.Add(message);

        if (drained.Count == 0)
            return;

        AppendLogMessages(drained, parseProgress: true);
    }

    private void FlushAllQueuedProcessingLogMessages()
    {
        while (!_queuedProcessingLogMessages.IsEmpty)
            FlushQueuedProcessingLogMessages(maxMessages: 2048);
    }

    private void AppendLogMessages(IReadOnlyList<string> messages, bool parseProgress)
    {
        if (messages.Count == 0)
            return;

        var fileBuffer = new StringBuilder(messages.Count * 64);
        foreach (var message in messages)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            var line = $"[{timestamp}] {message}";

            _uiLogLines.Enqueue(line);
            while (_uiLogLines.Count > MaxUiLogLines)
                _uiLogLines.Dequeue();

            fileBuffer.AppendLine(line);

            if (parseProgress)
                ParseBatchProgress(message);
        }

        LogText = string.Join(Environment.NewLine, _uiLogLines) + Environment.NewLine;

        try
        {
            File.AppendAllText(_sessionLogPath, fileBuffer.ToString());
        }
        catch
        {
            // Keep UI logging robust if file write fails.
        }
    }

    private void Log(string message)
    {
        AppendLogMessages([message], parseProgress: false);
    }
}

