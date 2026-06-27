using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TLAHStudio.Core.Models;

/// <summary>
/// Reusable provider/model settings for a project or individual chat.
/// </summary>
public class ConfigProfile
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? ProjectSpaceId { get; set; }

    [MaxLength(160)]
    public string Name { get; set; } = "Default Profile";

    [MaxLength(50)]
    public string Provider { get; set; } = "openai";

    [MaxLength(2048)]
    public string? ApiKey { get; set; }

    [MaxLength(500)]
    public string BaseUrl { get; set; } = "https://api.openai.com";

    [MaxLength(100)]
    public string Model { get; set; } = "gpt-4o";

    public bool UseLongContext { get; set; }

    [MaxLength(20)]
    public string ThinkingDepth { get; set; } = "auto";

    public double Temperature { get; set; } = 0.7;

    public int MaxTokens { get; set; } = 4096;

    [MaxLength(50)]
    public string UserRole { get; set; } = "user";

    public string SystemPrompt { get; set; } = string.Empty;

    public bool IsShared { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(ProjectSpaceId))]
    public ProjectSpace? ProjectSpace { get; set; }
}
