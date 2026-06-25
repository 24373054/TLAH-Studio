using System.ComponentModel.DataAnnotations;

namespace TLAHStudio.Core.Models;

/// <summary>
/// A local project/team workspace that groups chats, shared prompts, norms, and reusable profiles.
/// </summary>
public class ProjectSpace
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(160)]
    public string Name { get; set; } = "Personal Workspace";

    public string Description { get; set; } = string.Empty;

    public string SharedPrompt { get; set; } = string.Empty;

    public string TeamNorms { get; set; } = string.Empty;

    public bool CloudSyncEnabled { get; set; }

    [MaxLength(2048)]
    public string? SyncFolderPath { get; set; }

    public Guid? DefaultConfigProfileId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Chat> Chats { get; set; } = new List<Chat>();
    public ICollection<ConfigProfile> ConfigProfiles { get; set; } = new List<ConfigProfile>();
    public ICollection<PromptTemplate> PromptTemplates { get; set; } = new List<PromptTemplate>();
}
