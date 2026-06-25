using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TLAHStudio.Core.Models;

/// <summary>
/// Reusable message/system prompt template stored in a project library.
/// </summary>
public class PromptTemplate
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? ProjectSpaceId { get; set; }

    [MaxLength(160)]
    public string Name { get; set; } = "New Template";

    [MaxLength(80)]
    public string Category { get; set; } = "General";

    public string Content { get; set; } = string.Empty;

    public bool IsShared { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(ProjectSpaceId))]
    public ProjectSpace? ProjectSpace { get; set; }
}
