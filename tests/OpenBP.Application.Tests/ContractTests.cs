using OpenBP.Application.Auth;
using OpenBP.Application.Errors;
using OpenBP.Application.Executions;
using OpenBP.Application.Jobs;
using OpenBP.Application.Paging;
using OpenBP.Data;
using OpenBP.Engine.Execution;
using OpenBP.Shared.Workflow;

namespace OpenBP.Application.Tests;

/// <summary>Допоміжна логіка контрактів F2-00: ролі, пагінація, стани, payload job'ів.</summary>
public class ContractTests
{
    [Theory]
    [InlineData(UserRole.Viewer, UserRole.Viewer, true)]
    [InlineData(UserRole.Editor, UserRole.Viewer, true)]
    [InlineData(UserRole.Admin, UserRole.Editor, true)]
    [InlineData(UserRole.Viewer, UserRole.Editor, false)]
    [InlineData(UserRole.Editor, UserRole.Admin, false)]
    public void Require_ComparesRolesAsHierarchy(UserRole actual, UserRole minimum, bool allowed)
    {
        var user = new TestUser(actual);

        Assert.Equal(allowed, user.Has(minimum));
        if (allowed)
        {
            user.Require(minimum);
        }
        else
        {
            var ex = Assert.Throws<ForbiddenException>(() => user.Require(minimum));
            Assert.Equal(minimum, ex.Required);
            Assert.Equal(403, ex.StatusCode);
            Assert.Equal("FORBIDDEN", ex.Code);
        }
    }

    [Fact]
    public void Require_Unauthenticated_Throws401()
    {
        var anonymous = new TestUser(null);

        var ex = Assert.Throws<UnauthenticatedException>(() => anonymous.Require(UserRole.Viewer));
        Assert.Equal(401, ex.StatusCode);
        Assert.False(anonymous.Has(UserRole.Viewer));
    }

    [Theory]
    [InlineData(0, 0, 1, 1, 0)]
    [InlineData(1, 50, 1, 50, 0)]
    [InlineData(3, 20, 3, 20, 40)]
    [InlineData(2, 1000, 2, PageRequest.MaxPageSize, PageRequest.MaxPageSize)]
    [InlineData(-5, -1, 1, 1, 0)]
    public void PageRequest_NormalizesBounds(int page, int size, int expectedPage, int expectedSize, int expectedSkip)
    {
        var request = new PageRequest { Page = page, PageSize = size };

        Assert.Equal(new PageRequest { Page = expectedPage, PageSize = expectedSize }, request.Normalized());
        Assert.Equal(expectedSkip, request.Skip);
    }

    [Fact]
    public void ExecutionStates_RoundTripAndMatchDbConstants()
    {
        foreach (var state in Enum.GetValues<ExecutionState>())
        {
            Assert.Equal(state, ExecutionStates.FromDb(ExecutionStates.ToDb(state)));
        }

        Assert.Equal(ExecutionStatuses.Queued, ExecutionStates.ToDb(ExecutionState.Queued));
        Assert.Equal(ExecutionStatuses.Cancelling, ExecutionStates.ToDb(ExecutionState.Cancelling));
        Assert.Equal(ExecutionStatuses.ToDb(ExecutionStatus.TimedOut), ExecutionStates.ToDb(ExecutionState.TimedOut));
        Assert.Throws<ArgumentException>(() => ExecutionStates.FromDb("paused"));
    }

    [Theory]
    [InlineData(ExecutionState.Queued, false)]
    [InlineData(ExecutionState.Running, false)]
    [InlineData(ExecutionState.Cancelling, false)]
    [InlineData(ExecutionState.Success, true)]
    [InlineData(ExecutionState.Error, true)]
    [InlineData(ExecutionState.Cancelled, true)]
    [InlineData(ExecutionState.TimedOut, true)]
    public void ExecutionStates_IsFinal(ExecutionState state, bool final) => Assert.Equal(final, ExecutionStates.IsFinal(state));

    [Fact]
    public void JobJson_ExecutionPayload_RoundTripsCamelCaseWithoutNulls()
    {
        var payload = new ExecutionJobPayload
        {
            ExecutionId = Guid.NewGuid(),
            WorkflowId = Guid.NewGuid(),
            VersionId = Guid.NewGuid(),
            Mode = ExecutionMode.Trigger,
            StartNodeId = "n_webhook",
            StartItems = [new Item { Json = new System.Text.Json.Nodes.JsonObject { ["text"] = "привіт" } }],
        };

        var json = JobJson.Serialize(payload);

        Assert.Contains("\"mode\":\"trigger\"", json, StringComparison.Ordinal);
        Assert.Contains("\"startNodeId\":\"n_webhook\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", json, StringComparison.Ordinal);
        var back = JobJson.Deserialize<ExecutionJobPayload>(json);
        Assert.Equal(payload.ExecutionId, back.ExecutionId);
        Assert.Equal(payload.VersionId, back.VersionId);
        Assert.Equal(ExecutionMode.Trigger, back.Mode);
        Assert.Equal("привіт", back.StartItems?.Single().Json["text"]?.GetValue<string>());

        var minimal = JobJson.Serialize(new PollJobPayload { WorkflowId = payload.WorkflowId, NodeId = "n_poll" });
        Assert.DoesNotContain("null", minimal, StringComparison.Ordinal);
        Assert.Equal("n_poll", JobJson.Deserialize<PollJobPayload>(minimal).NodeId);
    }

    [Fact]
    public void WorkflowValidationException_CarriesErrorsAnd422()
    {
        var ex = new WorkflowValidationException([new("GRAPH_CYCLE", "Цикл n1 → n2 → n1.")]);

        Assert.Equal(422, ex.StatusCode);
        Assert.Equal("WORKFLOW_INVALID", ex.Code);
        Assert.Single(ex.Errors);
    }

    private sealed class TestUser(UserRole? role) : ICurrentUser
    {
        public bool IsAuthenticated => role is not null;

        public Guid? UserId => role is null ? null : Guid.Empty;

        public string? Email => role is null ? null : "test@example.com";

        public UserRole? Role => role;
    }
}
