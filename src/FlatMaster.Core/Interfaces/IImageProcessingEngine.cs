using FlatMaster.Core.Models;

namespace FlatMaster.Core.Interfaces;

public interface IImageProcessingEngine
{
    Task<ProcessingResult> ExecuteAsync(
        ProcessingPlan plan, 
        IProgress<string> progress, 
        CancellationToken cancellationToken = default);
}
