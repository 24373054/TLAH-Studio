using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TLAHStudio.Core.Models;

/// <summary>
/// Stores the COMPLETE raw HTTP request payload sent to the LLM API.
/// Maps 1:1 from RawRequest in models/debug.py.
/// </summary>
public class RawRequest
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TurnId { get; set; }

    [MaxLength(50)]
    public string Provider { get; set; } = string.Empty;

    [MaxLength(500)]
    public string EndpointUrl { get; set; } = string.Empty;

    /// <summary>Full JSON payload as string.</summary>
    public string RequestJson { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    [ForeignKey(nameof(TurnId))]
    public Turn Turn { get; set; } = null!;
}
