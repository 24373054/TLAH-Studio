using TLAHStudio.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace TLAHStudio.Core.Helpers;

/// <summary>
/// Builds the complete system prompt by merging chat-level override,
/// global system prompt, and optional AGENT.md file content.
/// Maps from _build_system_prompt() in services/llm_service.py.
/// </summary>
public static class SystemPromptBuilder
{
    /// <summary>
    /// Build the effective system prompt for a chat.
    /// Priority: Chat.system_prompt > GlobalSettings.system_prompt,
    /// with AgentFile content appended if present.
    /// </summary>
    public static async Task<string> BuildAsync(
        IQueryable<Chat> chats,
        IQueryable<GlobalSettings> globalSettings,
        IQueryable<AgentFile> agentFiles,
        IQueryable<ProjectSpace> projectSpaces,
        IQueryable<ConfigProfile> configProfiles,
        Guid chatId,
        CancellationToken ct = default)
    {
        var chat = await chats.FirstOrDefaultAsync(c => c.Id == chatId, ct);
        if (chat == null)
            return "You are a helpful assistant.";

        var gs = await globalSettings.FirstOrDefaultAsync(g => g.Id == 1, ct);
        ProjectSpace? project = null;
        if (chat.ProjectSpaceId != null)
            project = await projectSpaces.FirstOrDefaultAsync(p => p.Id == chat.ProjectSpaceId.Value, ct);

        ConfigProfile? profile = null;
        if (chat.ConfigProfileId != null)
        {
            profile = await configProfiles.FirstOrDefaultAsync(p => p.Id == chat.ConfigProfileId.Value, ct);
        }
        else if (project?.DefaultConfigProfileId != null)
        {
            profile = await configProfiles.FirstOrDefaultAsync(p => p.Id == project.DefaultConfigProfileId.Value, ct);
        }

        // Chat-level system prompt takes priority over global
        string basePrompt = !string.IsNullOrWhiteSpace(chat.SystemPrompt)
            ? chat.SystemPrompt
            : !string.IsNullOrWhiteSpace(profile?.SystemPrompt)
                ? profile.SystemPrompt
            : gs?.SystemPrompt ?? "You are a helpful assistant.";

        if (project != null && !string.IsNullOrWhiteSpace(project.SharedPrompt))
        {
            basePrompt += $"\n\n---\nProject shared prompt:\n\n{project.SharedPrompt}";
        }

        if (project != null && !string.IsNullOrWhiteSpace(project.TeamNorms))
        {
            basePrompt += $"\n\n---\nTeam norms:\n\n{project.TeamNorms}";
        }

        // Append AGENT.md content if present
        var agentFile = await agentFiles.FirstOrDefaultAsync(a => a.ChatId == chatId, ct);
        if (agentFile != null && !string.IsNullOrWhiteSpace(agentFile.Content))
        {
            basePrompt += $"\n\n---\nThe following is additional context/instructions:\n\n{agentFile.Content}";
        }

        return basePrompt;
    }
}
