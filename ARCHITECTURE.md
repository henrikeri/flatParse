# Architecture & Design Decisions

## Overview

FlatMaster is architected as a layered, modular application following SOLID principles and modern .NET best practices.

## Layer Architecture

```
┌─────────────────────────────┐
│     Presentation Layer      │  WPF UI with MVVM
│      (FlatMaster.WPF)       │  ViewModels, Views, Converters
└─────────────┬───────────────┘
              │
┌─────────────▼───────────────┐
│   Application Services      │  Orchestration logic
│   (FlatMaster.Services)     │  (Future: if complex workflows)
└─────────────┬───────────────┘
              │
┌─────────────▼───────────────┐
│      Domain Layer           │  Core business logic
│     (FlatMaster.Core)       │  Models, Interfaces
└─────────────┬───────────────┘
              │
┌─────────────▼───────────────┐
│   Infrastructure Layer      │  External concerns
│ (FlatMaster.Infrastructure) │  File I/O, PixInsight
└─────────────────────────────┘
```

## Design Patterns

### 1. **MVVM (Model-View-ViewModel)**
- **Why**: Clean separation between UI and business logic
- **How**: CommunityToolkit.Mvvm for ViewModels with INotifyPropertyChanged
- **Benefit**: Testable ViewModels, no code-behind in XAML

### 2. **Dependency Injection**
- **Why**: Loose coupling, testability, lifetime management
- **How**: Microsoft.Extensions.DependencyInjection
- **Benefit**: Easy mocking in tests, swappable implementations

### 3. **Repository Pattern** (implicit)
- **Why**: Abstract data access from business logic
- **How**: Services encapsulate file system operations
- **Benefit**: Could swap to database or cloud storage

### 4. **Strategy Pattern**
- **Where**: Dark matching algorithms
- **Why**: Different matching strategies (binning vs. gain vs. temp)
- **Benefit**: Extensible scoring system

### 5. **Template Method**
- **Where**: PJSR script generation
- **Why**: Common structure, variable details
- **Benefit**: Consistent PixInsight interactions

## Key Design Decisions

### Records vs Classes

**Decision**: Use `record` for immutable data transfer objects (DTOs)

```csharp
public sealed record ImageMetadata { ... }
public sealed record MatchingCriteria { ... }
```

**Rationale**:
- Value semantics (structural equality)
- Immutability by default (init-only properties)
- Concise syntax with positional records
- Thread-safe (no shared mutable state)

### Async/Await Throughout

**Decision**: All I/O operations are async

```csharp
Task<List<DirectoryJob>> ScanFlatDirectoriesAsync(...)
Task<ImageMetadata?> ReadMetadataAsync(...)
```

**Rationale**:
- Responsive UI (WPF Dispatcher not blocked)
- Efficient I/O (no thread blocking)
- Scalable (handles thousands of files)

### Nullable Reference Types

**Decision**: Enable `<Nullable>enable</Nullable>` project-wide

```csharp
public string? Binning { get; init; }
public double? ExposureTime { get; init; }
```

**Rationale**:
- Compile-time null safety
- Explicit intent (nullable vs non-nullable)
- Fewer runtime NullReferenceExceptions
- Modern C# best practice

### Separation of Concerns

**Decision**: Services layer segregated by responsibility

- `IMetadataReaderService`: Only reads headers
- `IFileScannerService`: Only scans directories
- `IDarkMatchingService`: Only matches darks
- `IPixInsightService`: Only generates/runs scripts

**Rationale**:
- Single Responsibility Principle
- Easy to test in isolation
- Clear boundaries
- Reusable components

## Technology Choices

### WPF over WinForms/UWP/Avalonia

**Choice**: Windows Presentation Foundation (WPF)

**Rationale**:
- Mature, stable framework
- Excellent MVVM support
- Rich data binding
- Cross-platform not required (PixInsight is Windows-only in practice)
- Better than WinForms for modern UI
- More stable than WinUI 3 (as of 2024)

### CommunityToolkit.Mvvm

**Choice**: Use CommunityToolkit.Mvvm for ViewModels

**Rationale**:
- Source generators reduce boilerplate
- `[ObservableProperty]` and `[RelayCommand]` attributes
- Performance (no reflection at runtime)
- Official Microsoft toolkit

### .NET 8

**Choice**: Target .NET 8 (LTS)

**Rationale**:
- Long-term support (Nov 2024 - Nov 2027)
- Improved performance over .NET 6
- Latest C# language features
- Native AOT compatible (future option)

## Error Handling Strategy

### Defensive Programming

```csharp
if (!File.Exists(filePath))
{
    _logger.LogWarning("File not found: {FilePath}", filePath);
    return null;
}
```

**Approach**:
- Guard clauses at method entry
- Null checks with early returns
- Log warnings for recoverable issues
- Throw exceptions for truly exceptional cases

### Exception Propagation

```csharp
try { ... }
catch (Exception ex)
{
    _logger.LogError(ex, "Error scanning flats");
    throw; // Let UI layer handle presentation
}
```

**Approach**:
- Catch at boundaries (UI layer)
- Log at source
- Wrap or rethrow with context
- User-friendly messages in ViewModels

## Performance Considerations

### Parallel File Scanning

```csharp
public async Task<Dictionary<string, ImageMetadata>> ReadMetadataBatchAsync(...)
{
    var tasks = filePaths.Select(async path => ...);
    var results = await Task.WhenAll(tasks);
    return results.ToDictionary(...);
}
```

**Optimization**:
- Parallel I/O with Task.WhenAll
- Configurable thread pool size
- No blocking waits

### Memory Management

**Strategy**:
- Streaming file reads (FITS/XISF headers)
- Early disposal of streams
- Yield return for directory enumeration
- Avoid loading all data at once

### Lazy Evaluation

```csharp
private IEnumerable<string> EnumerateDirectories(string root)
{
    var queue = new Queue<string>();
    // ...
    yield return current;
}
```

**Benefit**:
- Process directories as found
- Lower memory footprint
- Cancellable mid-stream

## Testing Strategy

### Unit Tests

**Approach**:
- Test domain logic (Core models)
- Mock interfaces for services
- xUnit + FluentAssertions + Moq

### Integration Tests

**Future**: Test actual file system operations
- Real FITS/XISF files
- Validate metadata parsing
- End-to-end scenarios

### UI Tests

**Future**: Automated UI testing
- WPF UI Automation
- Test ViewModel interactions
- Verify data binding

## Extensibility Points

### Adding New Image Formats

1. Update `MetadataReaderService`
2. Add format detection in `IsSupportedFormat`
3. Implement parser (FITS/XISF patterns as templates)

### Custom Dark Matching Algorithms

1. Extend `DarkMatchingOptions`
2. Update `CalculateMatchScore` logic
3. Add configuration options

### Alternative Processing Backends

1. Implement `IPixInsightService` interface
2. Could target Siril, ASTAP, etc.
3. Same workflow, different executor

## Security Considerations

### File System Access

- **Validation**: Paths sanitized before use
- **Permissions**: Handle UnauthorizedAccessException gracefully
- **Injection**: No shell command injection (use Process.Start properly)

### Configuration

- **Secrets**: No credentials stored (PixInsight = local exe)
- **Validation**: Validate all config at load time
- **Defaults**: Sensible fallbacks for missing values

## Future Enhancements

### Potential Improvements

1. **Database**: Cache metadata for faster re-scans
2. **Web API**: Expose services for remote access
3. **Batch Queue**: Process multiple sessions sequentially
4. **Preview**: Show master flat previews before processing
5. **Statistics**: Detailed integration reports (RMS, noise, rejections)
6. **Profiles**: Save/load processing configurations
7. **Cloud Storage**: Support Azure Blob, AWS S3 for inputs/outputs

### Refactoring Opportunities

1. **Services Project**: Extract orchestration if complexity grows
2. **Specification Pattern**: For complex dark matching rules
3. **Event Sourcing**: Track processing history
4. **CQRS**: Separate read/write models if needed

## Conclusion

This architecture prioritizes:
- **Maintainability**: Clear structure, separation of concerns
- **Testability**: DI, interfaces, minimal coupling
- **Performance**: Async I/O, parallel processing
- **Extensibility**: Open for extension, closed for modification (SOLID)
- **User Experience**: Responsive UI, clear feedback

The design can scale from hobby use (100s of files) to professional workflows (10,000+ files) without major rework.
