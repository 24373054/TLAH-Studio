using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TLAHStudio.Core.Models;

/// <summary>
/// AGENT.md file attached to a chat. Content is appended to the system prompt.
/// Maps 1:1 from AgentFile in models/settings.py.
/// </summary>
public class AgentFile
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ChatId { get; set; }

    [MaxLength(255)]
    public string Filename { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public int SizeBytes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    [ForeignKey(nameof(ChatId))]
    public Chat Chat { get; set; } = null!;
}
