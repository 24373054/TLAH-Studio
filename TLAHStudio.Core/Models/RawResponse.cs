using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TLAHStudio.Core.Models;

/// <summary>
/// Stores the COMPLETE raw HTTP response received from the LLM API.
/// Maps 1:1 from RawResponse in models/debug.py.
/// </summary>
public class RawResponse
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TurnId { get; set; }

    [MaxLength(50)]
    public string Provider { get; set; } = string.Empty;

    /// <summary>Full JSON response as string.</summary>
    public string ResponseJson { get; set; } = string.Empty;

    public int HttpStatusCode { get; set; } = 200;

    public int LatencyMs { get; set; }

    /// <summary>Token usage JSON, nullable.</summary>
    public string? TokenUsageJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    [ForeignKey(nameof(TurnId))]
    public Turn Turn { get; set; } = null!;
}
