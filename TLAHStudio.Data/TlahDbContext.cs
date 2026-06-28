using Microsoft.EntityFrameworkCore;
using System.Data.Common;
using TLAHStudio.Core.Models;

namespace TLAHStudio.Data;

/// <summary>
/// EF Core database context. Maps 1:1 from database.py (engine + SessionLocal + Base).
/// </summary>
public class TlahDbContext : DbContext
{
    public DbSet<Chat> Chats => Set<Chat>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<Turn> Turns => Set<Turn>();
    public DbSet<RawRequest> RawRequests => Set<RawRequest>();
    public DbSet<RawResponse> RawResponses => Set<RawResponse>();
    public DbSet<GlobalSettings> GlobalSettings => Set<GlobalSettings>();
    public DbSet<ChatSettings> ChatSettings => Set<ChatSettings>();
    public DbSet<AgentFile> AgentFiles => Set<AgentFile>();
    public DbSet<ProjectSpace> ProjectSpaces => Set<ProjectSpace>();
    public DbSet<ConfigProfile> ConfigProfiles => Set<ConfigProfile>();
    public DbSet<PromptTemplate> PromptTemplates => Set<PromptTemplate>();
    public DbSet<AuditLogEntry> AuditLogEntries => Set<AuditLogEntry>();
    public DbSet<AgentRun> AgentRuns => Set<AgentRun>();
    public DbSet<AgentStep> AgentSteps => Set<AgentStep>();
    public DbSet<ToolInvocation> ToolInvocations => Set<ToolInvocation>();
    public DbSet<AgentEvent> AgentEvents => Set<AgentEvent>();
    public DbSet<AgentCheckpoint> AgentCheckpoints => Set<AgentCheckpoint>();
    public DbSet<AgentArtifact> AgentArtifacts => Set<AgentArtifact>();
    public DbSet<ToolPermission> ToolPermissions => Set<ToolPermission>();
    public DbSet<ToolPlatformSettings> ToolPlatformSettings => Set<ToolPlatformSettings>();
    public DbSet<ToolPolicyRule> ToolPolicyRules => Set<ToolPolicyRule>();
    public DbSet<McpServerConfig> McpServerConfigs => Set<McpServerConfig>();
    public DbSet<CredentialEntry> CredentialEntries => Set<CredentialEntry>();

    private readonly string _dbPath;

    public TlahDbContext()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dataDir = Path.Combine(appData, "TLAH Studio", "data");
        Directory.CreateDirectory(dataDir);
        _dbPath = Path.Combine(dataDir, "tlah.db");
    }

    public TlahDbContext(DbContextOptions<TlahDbContext> options) : base(options)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dataDir = Path.Combine(appData, "TLAH Studio", "data");
        Directory.CreateDirectory(dataDir);
        _dbPath = Path.Combine(dataDir, "tlah.db");
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            optionsBuilder.UseSqlite($"Data Source={_dbPath}");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ── Chat ──────────────────────────────────────────────────
        modelBuilder.Entity<Chat>(entity =>
        {
            entity.HasMany(c => c.Messages)
                  .WithOne(m => m.Chat)
                  .HasForeignKey(m => m.ChatId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(c => c.Turns)
                  .WithOne(t => t.Chat)
                  .HasForeignKey(t => t.ChatId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(c => c.ProjectSpace)
                  .WithMany(p => p.Chats)
                  .HasForeignKey(c => c.ProjectSpaceId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(c => c.ConfigProfile)
                  .WithMany()
                  .HasForeignKey(c => c.ConfigProfileId)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        // ── Message ───────────────────────────────────────────────
        modelBuilder.Entity<Message>(entity =>
        {
            entity.HasIndex(m => m.ChatId);
            entity.HasIndex(m => m.SequenceNum);
        });

        // ── Turn ──────────────────────────────────────────────────
        modelBuilder.Entity<Turn>(entity =>
        {
            entity.HasIndex(t => t.ChatId);

            entity.HasMany(t => t.Messages)
                  .WithOne(m => m.Turn)
                  .HasForeignKey(m => m.TurnId)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        // ── RawRequest (unique TurnId) ────────────────────────────
        modelBuilder.Entity<RawRequest>(entity =>
        {
            entity.HasIndex(r => r.TurnId).IsUnique();

            entity.HasOne(r => r.Turn)
                  .WithOne(t => t.RawRequest)
                  .HasForeignKey<RawRequest>(r => r.TurnId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // ── RawResponse (unique TurnId) ───────────────────────────
        modelBuilder.Entity<RawResponse>(entity =>
        {
            entity.HasIndex(r => r.TurnId).IsUnique();

            entity.HasOne(r => r.Turn)
                  .WithOne(t => t.RawResponse)
                  .HasForeignKey<RawResponse>(r => r.TurnId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // ── GlobalSettings (singleton seed) ───────────────────────
        modelBuilder.Entity<GlobalSettings>(entity =>
        {
            entity.HasData(new GlobalSettings
            {
                Id = 1,
                Provider = "deepseek",
                BaseUrl = "https://api.deepseek.com",
                Model = "deepseek-v4-pro",
                UseLongContext = true,
                ThinkingDepth = "auto",
                Temperature = 0.7,
                MaxTokens = 4096,
                SystemPrompt = "You are a helpful assistant.",
                UserRole = "user",
                UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            });
        });

        // ── ChatSettings (unique ChatId) ──────────────────────────
        modelBuilder.Entity<ChatSettings>(entity =>
        {
            entity.HasIndex(c => c.ChatId).IsUnique();
        });

        // ── AgentFile (unique ChatId) ─────────────────────────────
        modelBuilder.Entity<AgentFile>(entity =>
        {
            entity.HasIndex(a => a.ChatId).IsUnique();
        });

        modelBuilder.Entity<ProjectSpace>(entity =>
        {
            entity.HasIndex(p => p.Name);
        });

        modelBuilder.Entity<ConfigProfile>(entity =>
        {
            entity.HasIndex(p => p.ProjectSpaceId);
            entity.HasOne(p => p.ProjectSpace)
                  .WithMany(p => p.ConfigProfiles)
                  .HasForeignKey(p => p.ProjectSpaceId)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<PromptTemplate>(entity =>
        {
            entity.HasIndex(t => t.ProjectSpaceId);
            entity.HasOne(t => t.ProjectSpace)
                  .WithMany(p => p.PromptTemplates)
                  .HasForeignKey(t => t.ProjectSpaceId)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<AuditLogEntry>(entity =>
        {
            entity.HasIndex(a => a.ProjectSpaceId);
            entity.HasIndex(a => a.ChatId);
            entity.HasIndex(a => a.CreatedAt);
        });

        modelBuilder.Entity<AgentRun>(entity =>
        {
            entity.HasIndex(r => r.ChatId);
            entity.HasIndex(r => r.TurnId).IsUnique();
            entity.HasIndex(r => r.Status);
            entity.HasOne(r => r.Chat)
                  .WithMany(c => c.AgentRuns)
                  .HasForeignKey(r => r.ChatId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(r => r.Turn)
                  .WithOne(t => t.AgentRun)
                  .HasForeignKey<AgentRun>(r => r.TurnId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentStep>(entity =>
        {
            entity.HasIndex(s => new { s.AgentRunId, s.StepNumber }).IsUnique();
            entity.HasOne(s => s.AgentRun)
                  .WithMany(r => r.Steps)
                  .HasForeignKey(s => s.AgentRunId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ToolInvocation>(entity =>
        {
            entity.HasIndex(i => i.AgentRunId);
            entity.HasIndex(i => i.AgentStepId);
            entity.HasIndex(i => i.Status);
            entity.HasOne(i => i.AgentRun)
                  .WithMany(r => r.ToolInvocations)
                  .HasForeignKey(i => i.AgentRunId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(i => i.AgentStep)
                  .WithMany(s => s.ToolInvocations)
                  .HasForeignKey(i => i.AgentStepId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentEvent>(entity =>
        {
            entity.HasIndex(e => new { e.AgentRunId, e.SequenceNumber }).IsUnique();
            entity.HasIndex(e => e.EventType);
            entity.HasIndex(e => e.CreatedAt);
            entity.HasOne(e => e.AgentRun)
                  .WithMany(r => r.Events)
                  .HasForeignKey(e => e.AgentRunId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.AgentStep)
                  .WithMany()
                  .HasForeignKey(e => e.AgentStepId)
                  .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.ToolInvocation)
                  .WithMany()
                  .HasForeignKey(e => e.ToolInvocationId)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<AgentCheckpoint>(entity =>
        {
            entity.HasIndex(c => new { c.AgentRunId, c.StepNumber });
            entity.HasOne(c => c.AgentRun)
                  .WithMany(r => r.Checkpoints)
                  .HasForeignKey(c => c.AgentRunId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentArtifact>(entity =>
        {
            entity.HasIndex(a => new { a.AgentRunId, a.RelativePath }).IsUnique();
            entity.HasOne(a => a.AgentRun)
                  .WithMany(r => r.Artifacts)
                  .HasForeignKey(a => a.AgentRunId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ToolPermission>(entity =>
        {
            entity.HasIndex(p => new { p.ChatId, p.ToolName }).IsUnique();
            entity.HasOne(p => p.Chat)
                  .WithMany(c => c.ToolPermissions)
                  .HasForeignKey(p => p.ChatId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ToolPlatformSettings>(entity =>
        {
            entity.HasData(new ToolPlatformSettings
            {
                Id = 1,
                UpdatedAt = new DateTime(2026, 6, 24, 0, 0, 0, DateTimeKind.Utc)
            });
        });

        modelBuilder.Entity<ToolPolicyRule>(entity =>
        {
            entity.HasIndex(r => new
            {
                r.Scope,
                r.ChatId,
                r.ProjectSpaceId,
                r.SubjectKind,
                r.Pattern
            }).IsUnique();
            entity.HasIndex(r => r.Decision);
            entity.HasIndex(r => r.SubjectKind);
        });

        modelBuilder.Entity<McpServerConfig>(entity =>
        {
            entity.HasIndex(s => s.Name);
            entity.HasIndex(s => s.ProjectSpaceId);
        });

        modelBuilder.Entity<CredentialEntry>(entity =>
        {
            entity.HasIndex(c => c.Name).IsUnique();
        });
    }

    /// <summary>
    /// Ensures the database and tables exist. Called on app startup.
    /// Equivalent to init_db() in database.py.
    /// </summary>
    public void Initialize()
    {
        Database.EnsureCreated();
        ApplyLightweightMigrations();
    }

    private void ApplyLightweightMigrations()
    {
        if (!Database.IsSqlite())
            return;

        using var connection = Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            connection.Open();

        AddColumnIfMissing(connection, "Chats", "IsPinned", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(connection, "Chats", "IsArchived", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(connection, "Chats", "DeletedAt", "TEXT NULL");
        AddColumnIfMissing(connection, "Chats", "ProjectSpaceId", "TEXT NULL");
        AddColumnIfMissing(connection, "Chats", "ConfigProfileId", "TEXT NULL");
        AddColumnIfMissing(connection, "GlobalSettings", "UseLongContext", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(connection, "GlobalSettings", "ThinkingDepth", "TEXT NOT NULL DEFAULT 'auto'");
        AddColumnIfMissing(connection, "ChatSettings", "UseLongContext", "INTEGER NULL");
        AddColumnIfMissing(connection, "ChatSettings", "ThinkingDepth", "TEXT NULL");

        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS "ProjectSpaces" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_ProjectSpaces" PRIMARY KEY,
                "Name" TEXT NOT NULL,
                "Description" TEXT NOT NULL,
                "SharedPrompt" TEXT NOT NULL,
                "TeamNorms" TEXT NOT NULL,
                "CloudSyncEnabled" INTEGER NOT NULL DEFAULT 0,
                "SyncFolderPath" TEXT NULL,
                "DefaultConfigProfileId" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            """);

        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS "ConfigProfiles" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_ConfigProfiles" PRIMARY KEY,
                "ProjectSpaceId" TEXT NULL,
                "Name" TEXT NOT NULL,
                "Provider" TEXT NOT NULL,
                "ApiKey" TEXT NULL,
                "BaseUrl" TEXT NOT NULL,
                "Model" TEXT NOT NULL,
                "UseLongContext" INTEGER NOT NULL DEFAULT 0,
                "ThinkingDepth" TEXT NOT NULL DEFAULT 'auto',
                "Temperature" REAL NOT NULL,
                "MaxTokens" INTEGER NOT NULL,
                "UserRole" TEXT NOT NULL,
                "SystemPrompt" TEXT NOT NULL,
                "IsShared" INTEGER NOT NULL DEFAULT 1,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            """);
        AddColumnIfMissing(connection, "ConfigProfiles", "UseLongContext", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(connection, "ConfigProfiles", "ThinkingDepth", "TEXT NOT NULL DEFAULT 'auto'");

        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS "PromptTemplates" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_PromptTemplates" PRIMARY KEY,
                "ProjectSpaceId" TEXT NULL,
                "Name" TEXT NOT NULL,
                "Category" TEXT NOT NULL,
                "Content" TEXT NOT NULL,
                "IsShared" INTEGER NOT NULL DEFAULT 1,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            """);

        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS "AuditLogEntries" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_AuditLogEntries" PRIMARY KEY,
                "ProjectSpaceId" TEXT NULL,
                "ChatId" TEXT NULL,
                "EventType" TEXT NOT NULL,
                "EntityType" TEXT NOT NULL,
                "EntityId" TEXT NOT NULL,
                "Summary" TEXT NOT NULL,
                "MetadataJson" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL
            );
            """);

        ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS \"IX_ProjectSpaces_Name\" ON \"ProjectSpaces\" (\"Name\");");
        ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS \"IX_ConfigProfiles_ProjectSpaceId\" ON \"ConfigProfiles\" (\"ProjectSpaceId\");");
        ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS \"IX_PromptTemplates_ProjectSpaceId\" ON \"PromptTemplates\" (\"ProjectSpaceId\");");
        ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS \"IX_AuditLogEntries_ProjectSpaceId\" ON \"AuditLogEntries\" (\"ProjectSpaceId\");");
        ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS \"IX_AuditLogEntries_ChatId\" ON \"AuditLogEntries\" (\"ChatId\");");
        ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS \"IX_AuditLogEntries_CreatedAt\" ON \"AuditLogEntries\" (\"CreatedAt\");");

        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS "AgentRuns" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_AgentRuns" PRIMARY KEY,
                "ChatId" TEXT NOT NULL,
                "TurnId" TEXT NOT NULL,
                "Status" TEXT NOT NULL,
                "UserRequest" TEXT NOT NULL,
                "CurrentStep" INTEGER NOT NULL,
                "MaxSteps" INTEGER NOT NULL,
                "ErrorMessage" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                "CompletedAt" TEXT NULL,
                CONSTRAINT "FK_AgentRuns_Chats_ChatId" FOREIGN KEY ("ChatId") REFERENCES "Chats" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_AgentRuns_Turns_TurnId" FOREIGN KEY ("TurnId") REFERENCES "Turns" ("Id") ON DELETE CASCADE
            );
            """);
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS "AgentSteps" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_AgentSteps" PRIMARY KEY,
                "AgentRunId" TEXT NOT NULL,
                "StepNumber" INTEGER NOT NULL,
                "Kind" TEXT NOT NULL,
                "Status" TEXT NOT NULL,
                "Summary" TEXT NOT NULL,
                "InputJson" TEXT NOT NULL,
                "OutputJson" TEXT NOT NULL,
                "StartedAt" TEXT NOT NULL,
                "CompletedAt" TEXT NULL,
                CONSTRAINT "FK_AgentSteps_AgentRuns_AgentRunId" FOREIGN KEY ("AgentRunId") REFERENCES "AgentRuns" ("Id") ON DELETE CASCADE
            );
            """);
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS "ToolInvocations" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_ToolInvocations" PRIMARY KEY,
                "AgentRunId" TEXT NOT NULL,
                "AgentStepId" TEXT NOT NULL,
                "ToolName" TEXT NOT NULL,
                "ProviderCallId" TEXT NOT NULL,
                "ArgumentsJson" TEXT NOT NULL,
                "ResultJson" TEXT NOT NULL,
                "Status" TEXT NOT NULL,
                "RequiresApproval" INTEGER NOT NULL,
                "Approved" INTEGER NULL,
                "CreatedAt" TEXT NOT NULL,
                "ApprovedAt" TEXT NULL,
                "StartedAt" TEXT NULL,
                "CompletedAt" TEXT NULL,
                CONSTRAINT "FK_ToolInvocations_AgentRuns_AgentRunId" FOREIGN KEY ("AgentRunId") REFERENCES "AgentRuns" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_ToolInvocations_AgentSteps_AgentStepId" FOREIGN KEY ("AgentStepId") REFERENCES "AgentSteps" ("Id") ON DELETE CASCADE
            );
            """);
        AddColumnIfMissing(connection, "ToolInvocations", "SafetyLevel", "TEXT NOT NULL DEFAULT 'unknown'");
        AddColumnIfMissing(connection, "ToolInvocations", "SafetySummary", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(connection, "ToolInvocations", "SafetyJson", "TEXT NOT NULL DEFAULT '{}'");
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS "AgentCheckpoints" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_AgentCheckpoints" PRIMARY KEY,
                "AgentRunId" TEXT NOT NULL,
                "StepNumber" INTEGER NOT NULL,
                "StateJson" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                CONSTRAINT "FK_AgentCheckpoints_AgentRuns_AgentRunId" FOREIGN KEY ("AgentRunId") REFERENCES "AgentRuns" ("Id") ON DELETE CASCADE
            );
            """);
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS "AgentArtifacts" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_AgentArtifacts" PRIMARY KEY,
                "AgentRunId" TEXT NOT NULL,
                "RelativePath" TEXT NOT NULL,
                "ContentType" TEXT NOT NULL,
                "Sha256" TEXT NOT NULL,
                "SizeBytes" INTEGER NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                CONSTRAINT "FK_AgentArtifacts_AgentRuns_AgentRunId" FOREIGN KEY ("AgentRunId") REFERENCES "AgentRuns" ("Id") ON DELETE CASCADE
            );
            """);
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS "AgentEvents" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_AgentEvents" PRIMARY KEY,
                "AgentRunId" TEXT NOT NULL,
                "AgentStepId" TEXT NULL,
                "ToolInvocationId" TEXT NULL,
                "SequenceNumber" INTEGER NOT NULL,
                "EventType" TEXT NOT NULL,
                "Severity" TEXT NOT NULL,
                "Summary" TEXT NOT NULL,
                "DataJson" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                CONSTRAINT "FK_AgentEvents_AgentRuns_AgentRunId" FOREIGN KEY ("AgentRunId") REFERENCES "AgentRuns" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_AgentEvents_AgentSteps_AgentStepId" FOREIGN KEY ("AgentStepId") REFERENCES "AgentSteps" ("Id") ON DELETE SET NULL,
                CONSTRAINT "FK_AgentEvents_ToolInvocations_ToolInvocationId" FOREIGN KEY ("ToolInvocationId") REFERENCES "ToolInvocations" ("Id") ON DELETE SET NULL
            );
            """);
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS "ToolPermissions" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_ToolPermissions" PRIMARY KEY,
                "ChatId" TEXT NOT NULL,
                "ToolName" TEXT NOT NULL,
                "Decision" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                CONSTRAINT "FK_ToolPermissions_Chats_ChatId" FOREIGN KEY ("ChatId") REFERENCES "Chats" ("Id") ON DELETE CASCADE
            );
            """);
        ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS \"IX_AgentRuns_ChatId\" ON \"AgentRuns\" (\"ChatId\");");
        ExecuteNonQuery(connection, "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_AgentRuns_TurnId\" ON \"AgentRuns\" (\"TurnId\");");
        ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS \"IX_AgentRuns_Status\" ON \"AgentRuns\" (\"Status\");");
        ExecuteNonQuery(connection, "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_AgentSteps_AgentRunId_StepNumber\" ON \"AgentSteps\" (\"AgentRunId\", \"StepNumber\");");
        ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS \"IX_ToolInvocations_AgentRunId\" ON \"ToolInvocations\" (\"AgentRunId\");");
        ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS \"IX_ToolInvocations_AgentStepId\" ON \"ToolInvocations\" (\"AgentStepId\");");
        ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS \"IX_ToolInvocations_Status\" ON \"ToolInvocations\" (\"Status\");");
        ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS \"IX_AgentCheckpoints_AgentRunId_StepNumber\" ON \"AgentCheckpoints\" (\"AgentRunId\", \"StepNumber\");");
        ExecuteNonQuery(connection, "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_AgentArtifacts_AgentRunId_RelativePath\" ON \"AgentArtifacts\" (\"AgentRunId\", \"RelativePath\");");
        ExecuteNonQuery(connection, "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_AgentEvents_AgentRunId_SequenceNumber\" ON \"AgentEvents\" (\"AgentRunId\", \"SequenceNumber\");");
        ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS \"IX_AgentEvents_EventType\" ON \"AgentEvents\" (\"EventType\");");
        ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS \"IX_AgentEvents_CreatedAt\" ON \"AgentEvents\" (\"CreatedAt\");");
        ExecuteNonQuery(connection, "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_ToolPermissions_ChatId_ToolName\" ON \"ToolPermissions\" (\"ChatId\", \"ToolName\");");
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS "ToolPlatformSettings" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_ToolPlatformSettings" PRIMARY KEY,
                "DefaultBackend" TEXT NOT NULL,
                "NetworkAllowlist" TEXT NOT NULL,
                "MaxRuntimeSeconds" INTEGER NOT NULL,
                "MaxOutputChars" INTEGER NOT NULL,
                "MaxFileBytes" INTEGER NOT NULL,
                "MaxMemoryMb" INTEGER NOT NULL,
                "MaxProcesses" INTEGER NOT NULL,
                "WslDistribution" TEXT NOT NULL,
                "DockerImage" TEXT NOT NULL,
                "RemoteEndpoint" TEXT NOT NULL,
                "RemoteCredentialName" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            INSERT OR IGNORE INTO "ToolPlatformSettings" (
                "Id", "DefaultBackend", "NetworkAllowlist", "MaxRuntimeSeconds",
                "MaxOutputChars", "MaxFileBytes", "MaxMemoryMb", "MaxProcesses",
                "WslDistribution", "DockerImage", "RemoteEndpoint",
                "RemoteCredentialName", "UpdatedAt"
            ) VALUES (
                1, 'restricted_local',
                'api.github.com
            github.com
            raw.githubusercontent.com
            html.duckduckgo.com',
                30, 20000, 10485760, 512, 8, '',
                'mcr.microsoft.com/powershell:latest', '', '', CURRENT_TIMESTAMP
            );
            """);
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS "ToolPolicyRules" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_ToolPolicyRules" PRIMARY KEY,
                "ChatId" TEXT NULL,
                "ProjectSpaceId" TEXT NULL,
                "ToolName" TEXT NOT NULL,
                "SubjectKind" TEXT NOT NULL DEFAULT 'tool',
                "Pattern" TEXT NOT NULL DEFAULT '',
                "Scope" TEXT NOT NULL,
                "Decision" TEXT NOT NULL,
                "Description" TEXT NOT NULL DEFAULT '',
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            """);
        AddColumnIfMissing(connection, "ToolPolicyRules", "SubjectKind", "TEXT NOT NULL DEFAULT 'tool'");
        AddColumnIfMissing(connection, "ToolPolicyRules", "Pattern", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(connection, "ToolPolicyRules", "Description", "TEXT NOT NULL DEFAULT ''");
        ExecuteNonQuery(connection, """
            UPDATE "ToolPolicyRules"
            SET "SubjectKind" = 'tool',
                "Pattern" = CASE WHEN "Pattern" = '' THEN "ToolName" ELSE "Pattern" END
            WHERE "SubjectKind" IS NULL OR "SubjectKind" = '' OR "Pattern" = '';
            """);
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS "McpServerConfigs" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_McpServerConfigs" PRIMARY KEY,
                "ProjectSpaceId" TEXT NULL,
                "Name" TEXT NOT NULL,
                "Transport" TEXT NOT NULL,
                "Command" TEXT NOT NULL,
                "ArgumentsJson" TEXT NOT NULL,
                "Endpoint" TEXT NOT NULL,
                "HeadersJson" TEXT NOT NULL,
                "EnvironmentJson" TEXT NOT NULL,
                "Enabled" INTEGER NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            """);
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS "CredentialEntries" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_CredentialEntries" PRIMARY KEY,
                "Name" TEXT NOT NULL,
                "ProtectedValue" TEXT NOT NULL,
                "AllowedDomains" TEXT NOT NULL,
                "AllowedTools" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            """);
        ExecuteNonQuery(connection, "DROP INDEX IF EXISTS \"IX_ToolPolicyRules_Scope_ChatId_ProjectSpaceId_ToolName\";");
        ExecuteNonQuery(connection, "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_ToolPolicyRules_Scope_ChatId_ProjectSpaceId_SubjectKind_Pattern\" ON \"ToolPolicyRules\" (\"Scope\", \"ChatId\", \"ProjectSpaceId\", \"SubjectKind\", \"Pattern\");");
        ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS \"IX_ToolPolicyRules_Decision\" ON \"ToolPolicyRules\" (\"Decision\");");
        ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS \"IX_ToolPolicyRules_SubjectKind\" ON \"ToolPolicyRules\" (\"SubjectKind\");");
        ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS \"IX_McpServerConfigs_Name\" ON \"McpServerConfigs\" (\"Name\");");
        ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS \"IX_McpServerConfigs_ProjectSpaceId\" ON \"McpServerConfigs\" (\"ProjectSpaceId\");");
        ExecuteNonQuery(connection, "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_CredentialEntries_Name\" ON \"CredentialEntries\" (\"Name\");");
        ExecuteNonQuery(connection, """
            UPDATE "AgentRuns"
            SET "Status" = 'paused',
                "ErrorMessage" = 'The application closed while this run was active. Resume to continue.',
                "UpdatedAt" = CURRENT_TIMESTAMP
            WHERE "Status" = 'running';
            """);
    }

    private static void AddColumnIfMissing(DbConnection connection, string table, string column, string definition)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info('{table.Replace("'", "''")}')";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return;
        }

        reader.Close();
        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition}";
        alter.ExecuteNonQuery();
    }

    private static void ExecuteNonQuery(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
