using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TLAHStudio.Core.Models;

public static class AgentRunStatuses
{
    public const string Running = "running";
    public const string AwaitingApproval = "awaiting_approval";
    public const string Paused = "paused";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
    public const string Failed = "failed";
}

public static class AgentStepStatuses
{
    public const string Running = "running";
    public const string AwaitingApproval = "awaiting_approval";
    public const string Completed = "completed";
    public const string Denied = "denied";
    public const string Failed = "failed";
}

public static class ToolInvocationStatuses
{
    public const string Pending = "pending";
    public const string AwaitingApproval = "awaiting_approval";
    public const string Approved = "approved";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Denied = "denied";
    public const string Failed = "failed";
}

public static class AgentEventTypes
{
    public const string RunStarted = "run_started";
    public const string Resume = "resume";
    public const string ModelRequest = "model_request";
    public const string ModelResponse = "model_response";
    public const string ProtocolRepair = "protocol_repair";
    public const string ContextCompacted = "context_compacted";
    public const string ToolResultPersisted = "tool_result_persisted";
    public const string MemoryLoaded = "memory_loaded";
    public const string ToolRequest = "tool_request";
    public const string ApprovalRequested = "approval_requested";
    public const string ApprovalGranted = "approval_granted";
    public const string ApprovalDenied = "approval_denied";
    public const string ToolStarted = "tool_started";
    public const string ToolResult = "tool_result";
    public const string RuntimeMetrics = "runtime_metrics";
    public const string Error = "error";
    public const string RunCompleted = "run_completed";
    public const string RunPaused = "run_paused";
    public const string RunCancelled = "run_cancelled";
}

public static class AgentEventSeverities
{
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Error = "error";
}

public class AgentRun
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ChatId { get; set; }
    public Guid TurnId { get; set; }

    [MaxLength(40)]
    public string Status { get; set; } = AgentRunStatuses.Running;

    public string UserRequest { get; set; } = string.Empty;
    public int CurrentStep { get; set; }
    public int MaxSteps { get; set; } = 6;
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    [ForeignKey(nameof(ChatId))]
    public Chat Chat { get; set; } = null!;

    [ForeignKey(nameof(TurnId))]
    public Turn Turn { get; set; } = null!;

    public ICollection<AgentStep> Steps { get; set; } = new List<AgentStep>();
    public ICollection<ToolInvocation> ToolInvocations { get; set; } = new List<ToolInvocation>();
    public ICollection<AgentCheckpoint> Checkpoints { get; set; } = new List<AgentCheckpoint>();
    public ICollection<AgentArtifact> Artifacts { get; set; } = new List<AgentArtifact>();
    public ICollection<AgentEvent> Events { get; set; } = new List<AgentEvent>();
}

public class AgentStep
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid AgentRunId { get; set; }
    public int StepNumber { get; set; }

    [MaxLength(40)]
    public string Kind { get; set; } = "model";

    [MaxLength(40)]
    public string Status { get; set; } = AgentStepStatuses.Running;

    public string Summary { get; set; } = string.Empty;
    public string InputJson { get; set; } = "{}";
    public string OutputJson { get; set; } = "{}";
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    [ForeignKey(nameof(AgentRunId))]
    public AgentRun AgentRun { get; set; } = null!;

    public ICollection<ToolInvocation> ToolInvocations { get; set; } = new List<ToolInvocation>();
}

public class ToolInvocation
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid AgentRunId { get; set; }
    public Guid AgentStepId { get; set; }

    [MaxLength(100)]
    public string ToolName { get; set; } = string.Empty;

    [MaxLength(255)]
    public string ProviderCallId { get; set; } = string.Empty;

    public string ArgumentsJson { get; set; } = "{}";
    public string ResultJson { get; set; } = "{}";

    [MaxLength(40)]
    public string SafetyLevel { get; set; } = "unknown";

    public string SafetySummary { get; set; } = string.Empty;
    public string SafetyJson { get; set; } = "{}";

    [MaxLength(40)]
    public string Status { get; set; } = ToolInvocationStatuses.Pending;

    public bool RequiresApproval { get; set; }
    public bool? Approved { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ApprovedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    [ForeignKey(nameof(AgentRunId))]
    public AgentRun AgentRun { get; set; } = null!;

    [ForeignKey(nameof(AgentStepId))]
    public AgentStep AgentStep { get; set; } = null!;
}

public class AgentEvent
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid AgentRunId { get; set; }
    public Guid? AgentStepId { get; set; }
    public Guid? ToolInvocationId { get; set; }

    public int SequenceNumber { get; set; }

    [MaxLength(80)]
    public string EventType { get; set; } = AgentEventTypes.ModelRequest;

    [MaxLength(40)]
    public string Severity { get; set; } = AgentEventSeverities.Info;

    public string Summary { get; set; } = string.Empty;
    public string DataJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(AgentRunId))]
    public AgentRun AgentRun { get; set; } = null!;

    [ForeignKey(nameof(AgentStepId))]
    public AgentStep? AgentStep { get; set; }

    [ForeignKey(nameof(ToolInvocationId))]
    public ToolInvocation? ToolInvocation { get; set; }
}

public class AgentCheckpoint
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid AgentRunId { get; set; }
    public int StepNumber { get; set; }
    public string StateJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(AgentRunId))]
    public AgentRun AgentRun { get; set; } = null!;
}

public class AgentArtifact
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid AgentRunId { get; set; }

    [MaxLength(500)]
    public string RelativePath { get; set; } = string.Empty;

    [MaxLength(80)]
    public string ContentType { get; set; } = "application/octet-stream";

    [MaxLength(128)]
    public string Sha256 { get; set; } = string.Empty;

    public long SizeBytes { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(AgentRunId))]
    public AgentRun AgentRun { get; set; } = null!;
}

public class ToolPermission
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ChatId { get; set; }

    [MaxLength(100)]
    public string ToolName { get; set; } = string.Empty;

    [MaxLength(40)]
    public string Decision { get; set; } = "allow";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(ChatId))]
    public Chat Chat { get; set; } = null!;
}
