using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TLAHStudio.Core.Models;

/// <summary>
/// Groups a user message and its assistant response into one exchange.
/// Maps 1:1 from Turn in models/chat.py.
/// Each Turn has exactly one RawRequest and one RawResponse for debugging.
/// </summary>
public class Turn
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ChatId { get; set; }

    public int TurnNumber { get; set; } = 1;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    [ForeignKey(nameof(ChatId))]
    public Chat Chat { get; set; } = null!;

    public ICollection<Message> Messages { get; set; } = new List<Message>();

    public RawRequest? RawRequest { get; set; }
    public RawResponse? RawResponse { get; set; }
    public AgentRun? AgentRun { get; set; }
}
