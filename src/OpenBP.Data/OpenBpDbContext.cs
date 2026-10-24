using Microsoft.EntityFrameworkCore;

namespace OpenBP.Data;

public sealed class OpenBpDbContext(DbContextOptions<OpenBpDbContext> options) : DbContext(options)
{
    public DbSet<UserEntity> Users => Set<UserEntity>();
    public DbSet<UserLoginEntity> UserLogins => Set<UserLoginEntity>();
    public DbSet<ApiKeyEntity> ApiKeys => Set<ApiKeyEntity>();
    public DbSet<WorkflowEntity> Workflows => Set<WorkflowEntity>();
    public DbSet<WorkflowVersionEntity> WorkflowVersions => Set<WorkflowVersionEntity>();
    public DbSet<WorkflowGroupEntity> WorkflowGroups => Set<WorkflowGroupEntity>();
    public DbSet<ProjectMemberEntity> ProjectMembers => Set<ProjectMemberEntity>();
    public DbSet<EvaluationEntity> Evaluations => Set<EvaluationEntity>();
    public DbSet<EvaluationCaseEntity> EvaluationCases => Set<EvaluationCaseEntity>();
    public DbSet<EvaluationRunEntity> EvaluationRuns => Set<EvaluationRunEntity>();
    public DbSet<VariableEntity> Variables => Set<VariableEntity>();
    public DbSet<AppSettingEntity> AppSettings => Set<AppSettingEntity>();
    public DbSet<DataTableEntity> DataTables => Set<DataTableEntity>();
    public DbSet<DataRowEntity> DataRows => Set<DataRowEntity>();
    public DbSet<CredentialEntity> Credentials => Set<CredentialEntity>();
    public DbSet<McpServerEntity> McpServers => Set<McpServerEntity>();
    public DbSet<ExecutionEntity> Executions => Set<ExecutionEntity>();
    public DbSet<ExecutionStepEntity> ExecutionSteps => Set<ExecutionStepEntity>();
    public DbSet<JobEntity> Jobs => Set<JobEntity>();
    public DbSet<WebhookEntity> Webhooks => Set<WebhookEntity>();
    public DbSet<NodeStateEntity> NodeStates => Set<NodeStateEntity>();
    public DbSet<OAuthStateEntity> OAuthStates => Set<OAuthStateEntity>();
    public DbSet<AuditEntryEntity> AuditLog => Set<AuditEntryEntity>();
    public DbSet<NotificationChannelEntity> NotificationChannels => Set<NotificationChannelEntity>();
    public DbSet<NotificationEntity> Notifications => Set<NotificationEntity>();
    public DbSet<NotificationSendEntity> NotificationSends => Set<NotificationSendEntity>();
    public DbSet<WorkerHeartbeatEntity> WorkerHeartbeats => Set<WorkerHeartbeatEntity>();
    public DbSet<BackupRunEntity> BackupRuns => Set<BackupRunEntity>();
    public DbSet<ExecutionStatEntity> ExecutionStats => Set<ExecutionStatEntity>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<UserEntity>(e =>
        {
            e.ToTable("users");
            e.HasIndex(x => x.Email).IsUnique();
        });
        b.Entity<UserLoginEntity>(e =>
        {
            e.ToTable("user_logins");
            e.HasIndex(x => new { x.Provider, x.Subject }).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ApiKeyEntity>(e =>
        {
            e.ToTable("api_keys");
            e.HasIndex(x => x.Hash).IsUnique();
            // Ключ видаленого проєкту зникає разом із ним (F6-14).
            e.HasOne<WorkflowGroupEntity>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<WorkflowEntity>(e =>
        {
            e.ToTable("workflows");
            e.HasIndex(x => x.GroupId);
            e.HasOne<WorkflowGroupEntity>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<WorkflowGroupEntity>(e =>
        {
            e.ToTable("workflow_groups");
            e.HasIndex(x => x.NormalizedName).IsUnique();
        });
        b.Entity<ProjectMemberEntity>(e =>
        {
            e.ToTable("project_members");
            e.HasKey(x => new { x.ProjectId, x.UserId });
            e.HasIndex(x => x.UserId);
            e.HasOne<WorkflowGroupEntity>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<WorkflowVersionEntity>(e =>
        {
            e.ToTable("workflow_versions");
            e.Property(x => x.Definition).HasColumnType("jsonb");
            e.HasIndex(x => new { x.WorkflowId, x.Version }).IsUnique();
        });
        // Проєкт із credentials чи конекторами не видаляється (сервіс повертає 409), FK лише страхує.
        b.Entity<CredentialEntity>(e =>
        {
            e.ToTable("credentials");
            e.HasIndex(x => x.ProjectId);
            e.HasOne<WorkflowGroupEntity>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<McpServerEntity>(e =>
        {
            e.ToTable("mcp_servers");
            e.HasIndex(x => x.ProjectId);
            e.HasOne<WorkflowGroupEntity>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Config).HasColumnType("jsonb");
            e.Property(x => x.ToolsCache).HasColumnType("jsonb");
        });
        b.Entity<AppSettingEntity>(e =>
        {
            e.ToTable("app_settings");
            e.HasKey(x => x.Key);
            e.Property(x => x.Value).HasColumnType("jsonb");
        });
        b.Entity<VariableEntity>(e =>
        {
            e.ToTable("variables");
            e.HasKey(x => x.Key);
        });
        b.Entity<DataTableEntity>(e =>
        {
            e.ToTable("data_tables");
            e.Property(x => x.Columns).HasColumnType("jsonb");
            e.HasIndex(x => x.NormalizedName).IsUnique();
        });
        b.Entity<DataRowEntity>(e =>
        {
            e.ToTable("data_rows");
            e.HasKey(x => new { x.TableId, x.Key });
            e.Property(x => x.Data).HasColumnType("jsonb");
            e.HasIndex(x => new { x.TableId, x.CreatedAt });
            e.HasOne<DataTableEntity>().WithMany().HasForeignKey(x => x.TableId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<EvaluationEntity>(e =>
        {
            e.ToTable("evaluations");
            e.HasIndex(x => new { x.WorkflowId, x.NodeId });
            e.HasOne<WorkflowEntity>().WithMany().HasForeignKey(x => x.WorkflowId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<EvaluationCaseEntity>(e =>
        {
            e.ToTable("evaluation_cases");
            e.Property(x => x.Input).HasColumnType("jsonb");
            e.Property(x => x.Expected).HasColumnType("jsonb");
            e.Property(x => x.CheckConfig).HasColumnType("jsonb");
            e.HasIndex(x => new { x.EvaluationId, x.SortOrder });
            e.HasOne<EvaluationEntity>().WithMany().HasForeignKey(x => x.EvaluationId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<EvaluationRunEntity>(e =>
        {
            e.ToTable("evaluation_runs");
            e.Property(x => x.Results).HasColumnType("jsonb");
            e.HasIndex(x => new { x.EvaluationId, x.StartedAt });
            e.HasOne<EvaluationEntity>().WithMany().HasForeignKey(x => x.EvaluationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<WorkflowVersionEntity>().WithMany().HasForeignKey(x => x.VersionId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<AuditEntryEntity>(e =>
        {
            e.ToTable("audit_log");
            e.Property(x => x.Details).HasColumnType("jsonb");
            e.HasIndex(x => x.At);
            e.HasIndex(x => new { x.EntityType, x.EntityId });
        });
        b.Entity<NotificationChannelEntity>(e =>
        {
            e.ToTable("notification_channels");
            e.Property(x => x.Config).HasColumnType("jsonb");
            e.Property(x => x.Events).HasColumnType("jsonb");
        });
        b.Entity<NotificationEntity>(e =>
        {
            e.ToTable("notifications");
            e.Property(x => x.Details).HasColumnType("jsonb");
            e.HasIndex(x => x.CreatedAt).HasFilter("processed_at IS NULL");
        });
        b.Entity<NotificationSendEntity>(e =>
        {
            e.ToTable("notification_sends");
            e.HasKey(x => new { x.ChannelId, x.Key });
            e.HasOne<NotificationChannelEntity>().WithMany().HasForeignKey(x => x.ChannelId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<WorkerHeartbeatEntity>(e =>
        {
            e.ToTable("worker_heartbeats");
            e.HasKey(x => x.WorkerId);
        });
        b.Entity<BackupRunEntity>(e =>
        {
            e.ToTable("backup_runs");
            e.Property(x => x.At).HasDefaultValueSql("now()");
            e.HasIndex(x => x.At);
        });
        b.Entity<ExecutionStatEntity>(e =>
        {
            e.ToTable("execution_stats");
            e.HasKey(x => new { x.Hour, x.WorkflowId });
        });
        b.Entity<ExecutionEntity>(e =>
        {
            e.ToTable("executions");
            e.HasIndex(x => x.FinishedAt);
            e.HasIndex(x => new { x.WorkflowId, x.StartedAt });
            e.HasIndex(x => new { x.WorkflowId, x.ScheduledAt }).HasFilter("scheduled_at IS NOT NULL");
        });
        b.Entity<ExecutionStepEntity>(e =>
        {
            e.ToTable("execution_steps");
            e.Property(x => x.Input).HasColumnType("jsonb");
            e.Property(x => x.Output).HasColumnType("jsonb");
            e.Property(x => x.SubSteps).HasColumnType("jsonb");
            e.HasIndex(x => x.ExecutionId);
        });
        b.Entity<JobEntity>(e =>
        {
            e.ToTable("jobs");
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.HasIndex(x => x.RunAt).HasFilter("locked_by IS NULL");
        });
        b.Entity<WebhookEntity>(e =>
        {
            e.ToTable("webhooks");
            e.HasIndex(x => x.Path).IsUnique();
        });
        b.Entity<NodeStateEntity>(e =>
        {
            e.ToTable("node_state");
            e.HasKey(x => new { x.WorkflowId, x.NodeId });
            e.Property(x => x.State).HasColumnType("jsonb");
        });

        b.Entity<OAuthStateEntity>(e =>
        {
            e.ToTable("oauth_states");
            e.HasKey(x => x.State);
            e.HasIndex(x => x.CreatedAt);
        });

        // snake_case для колонок і індексів — відповідає architecture.md §6.
        foreach (var entity in b.Model.GetEntityTypes())
        {
            foreach (var p in entity.GetProperties())
            {
                p.SetColumnName(ToSnake(p.Name));
            }
        }
    }

    private static string ToSnake(string name)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0)
            {
                sb.Append('_');
            }
            sb.Append(char.ToLowerInvariant(name[i]));
        }
        return sb.ToString();
    }
}
