using System.ComponentModel.DataAnnotations;

namespace TLAHStudio.Core.Models;

/// <summary>
/// Local audit event for workspace, prompt, settings, and chat operations.
/// </summary>
public class AuditLogEntry
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? ProjectSpaceId { get; set; }

    public Guid? ChatId { get; set; }

    [MaxLength(80)]
    public string EventType { get; set; } = string.Empty;

    [MaxLength(80)]
    public string EntityType { get; set; } = string.Empty;

    [MaxLength(80)]
    public string EntityId { get; set; } = string.Empty;

    [MaxLength(512)]
    public string Summary { get; set; } = string.Empty;

    public string MetadataJson { get; set; } = "{}";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
