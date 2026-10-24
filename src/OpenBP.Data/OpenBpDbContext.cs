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
    public DbSet<VariableEntity> Variables => Set<VariableEntity>();
    public DbSet<CredentialEntity> Credentials => Set<CredentialEntity>();
    public DbSet<McpServerEntity> McpServers => Set<McpServerEntity>();
    public DbSet<ExecutionEntity> Executions => Set<ExecutionEntity>();
    public DbSet<ExecutionStepEntity> ExecutionSteps => Set<ExecutionStepEntity>();
    public DbSet<JobEntity> Jobs => Set<JobEntity>();
    public DbSet<WebhookEntity> Webhooks => Set<WebhookEntity>();
    public DbSet<NodeStateEntity> NodeStates => Set<NodeStateEntity>();
    public DbSet<OAuthStateEntity> OAuthStates => Set<OAuthStateEntity>();

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
        b.Entity<WorkflowVersionEntity>(e =>
        {
            e.ToTable("workflow_versions");
            e.Property(x => x.Definition).HasColumnType("jsonb");
            e.HasIndex(x => new { x.WorkflowId, x.Version }).IsUnique();
        });
        b.Entity<CredentialEntity>().ToTable("credentials");
        b.Entity<McpServerEntity>(e =>
        {
            e.ToTable("mcp_servers");
            e.Property(x => x.Config).HasColumnType("jsonb");
            e.Property(x => x.ToolsCache).HasColumnType("jsonb");
        });
        b.Entity<VariableEntity>(e =>
        {
            e.ToTable("variables");
            e.HasKey(x => x.Key);
        });
        b.Entity<ExecutionEntity>(e =>
        {
            e.ToTable("executions");
            e.HasIndex(x => new { x.WorkflowId, x.StartedAt });
            e.HasIndex(x => new { x.WorkflowId, x.ScheduledAt }).HasFilter("scheduled_at IS NOT NULL");
        });
        b.Entity<ExecutionStepEntity>(e =>
        {
            e.ToTable("execution_steps");
            e.Property(x => x.Input).HasColumnType("jsonb");
            e.Property(x => x.Output).HasColumnType("jsonb");
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
