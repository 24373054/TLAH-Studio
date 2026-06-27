using System.ComponentModel.DataAnnotations;

namespace TLAHStudio.Core.Models;

/// <summary>
/// Singleton row (Id always 1) holding global defaults.
/// Maps 1:1 from GlobalSettings in models/settings.py.
/// </summary>
public class GlobalSettings
{
    [Key]
    public int Id { get; set; } = 1;

    [MaxLength(50)]
    public string Provider { get; set; } = "openai";

    [MaxLength(2048)]
    public string ApiKey { get; set; } = string.Empty;

    [MaxLength(500)]
    public string BaseUrl { get; set; } = "https://api.openai.com";

    [MaxLength(100)]
    public string Model { get; set; } = "gpt-4o";

    public bool UseLongContext { get; set; }

    [MaxLength(20)]
    public string ThinkingDepth { get; set; } = "auto";

    public double Temperature { get; set; } = 0.7;

    public int MaxTokens { get; set; } = 4096;

    public string SystemPrompt { get; set; } = "You are a helpful assistant.";

    [MaxLength(50)]
    public string UserRole { get; set; } = "user";

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
