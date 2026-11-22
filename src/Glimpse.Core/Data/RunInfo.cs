namespace Glimpse.Core.Data;

/// <summary>Run-level metadata for one opened file.</summary>
public sealed record RunInfo
{
    public string FileName { get; init; } = "";

    public string? RunId { get; init; }

    public DateTime? StartTime { get; init; }

    public string? InstrumentModel { get; init; }

    public string? InstrumentSerialNumber { get; init; }

    public string? InstrumentManufacturer { get; init; }

    /// <summary>Ionization / analyzer / detector description, one string per instrument configuration.</summary>
    public IReadOnlyList<string> Configurations { get; init; } = Array.Empty<string>();

    /// <summary>Software that produced the file (acquisition software, converters).</summary>
    public IReadOnlyList<string> Software { get; init; } = Array.Empty<string>();
}
