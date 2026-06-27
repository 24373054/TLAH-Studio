using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TLAHStudio.Core.Models;

/// <summary>
/// Per-chat settings overrides. NULL fields inherit from GlobalSettings.
/// Maps 1:1 from ChatSettings in models/settings.py.
/// </summary>
public class ChatSettings
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ChatId { get; set; }

    [MaxLength(50)]
    public string? Provider { get; set; }

    [MaxLength(2048)]
    public string? ApiKey { get; set; }

    [MaxLength(500)]
    public string? BaseUrl { get; set; }

    [MaxLength(100)]
    public string? Model { get; set; }

    public bool? UseLongContext { get; set; }

    [MaxLength(20)]
    public string? ThinkingDepth { get; set; }

    public double? Temperature { get; set; }

    public int? MaxTokens { get; set; }

    [MaxLength(50)]
    public string? UserRole { get; set; }

    // Navigation
    [ForeignKey(nameof(ChatId))]
    public Chat Chat { get; set; } = null!;
}
