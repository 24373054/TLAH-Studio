using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TLAHStudio.Core.Models;

/// <summary>
/// Represents a conversation session. Maps 1:1 from Chat in models/chat.py.
/// </summary>
public class Chat
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(255)]
    public string Title { get; set; } = "New Chat";

    public string SystemPrompt { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public bool IsPinned { get; set; }

    public bool IsArchived { get; set; }

    public DateTime? DeletedAt { get; set; }

    public Guid? ProjectSpaceId { get; set; }

    public Guid? ConfigProfileId { get; set; }

    // Navigation properties
    public ICollection<Message> Messages { get; set; } = new List<Message>();
    public ICollection<Turn> Turns { get; set; } = new List<Turn>();
    public ICollection<AgentRun> AgentRuns { get; set; } = new List<AgentRun>();
    public ICollection<ToolPermission> ToolPermissions { get; set; } = new List<ToolPermission>();
    public ProjectSpace? ProjectSpace { get; set; }
    public ConfigProfile? ConfigProfile { get; set; }
}
