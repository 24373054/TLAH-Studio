using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TLAHStudio.Core.Models;

/// <summary>
/// A single message in a conversation. Maps 1:1 from Message in models/chat.py.
/// Roles: user, assistant, system.
/// </summary>
public class Message
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ChatId { get; set; }

    [MaxLength(20)]
    public string Role { get; set; } = "user";

    public string Content { get; set; } = string.Empty;

    public Guid? TurnId { get; set; }

    public int SequenceNum { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    [ForeignKey(nameof(ChatId))]
    public Chat Chat { get; set; } = null!;

    [ForeignKey(nameof(TurnId))]
    public Turn? Turn { get; set; }
}
