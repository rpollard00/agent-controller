namespace AgentController.Domain.Tests;

public class DomainSmokeTests
{
    [Fact]
    public void RunLifecycleState_EnumeratesExpectedStates()
    {
        var states = Enum.GetValues<RunLifecycleState>();
        Assert.Contains(RunLifecycleState.Queued, states);
        Assert.Contains(RunLifecycleState.Claimed, states);
        Assert.Contains(RunLifecycleState.EnvironmentProvisioning, states);
        Assert.Contains(RunLifecycleState.EnvironmentReady, states);
        Assert.Contains(RunLifecycleState.RepositoryCloning, states);
        Assert.Contains(RunLifecycleState.RepositoryReady, states);
        Assert.Contains(RunLifecycleState.ContextInjected, states);
        Assert.Contains(RunLifecycleState.AgentStarting, states);
        Assert.Contains(RunLifecycleState.AgentRunning, states);
        Assert.Contains(RunLifecycleState.AwaitingResult, states);
        Assert.Contains(RunLifecycleState.ResultReceived, states);
        Assert.Contains(RunLifecycleState.PrOpened, states);
        Assert.Contains(RunLifecycleState.BranchPushed, states);
        Assert.Contains(RunLifecycleState.NeedsHuman, states);
        Assert.Contains(RunLifecycleState.Completed, states);
        Assert.Contains(RunLifecycleState.Failed, states);
        Assert.Contains(RunLifecycleState.Cancelled, states);
        Assert.Contains(RunLifecycleState.CleanupPending, states);
        Assert.Contains(RunLifecycleState.CleanedUp, states);
    }

    [Fact]
    public void EventSeverity_HasExpectedValues()
    {
        Assert.Equal(0, (int)EventSeverity.Info);
        Assert.Equal(1, (int)EventSeverity.Warning);
        Assert.Equal(2, (int)EventSeverity.Error);
        Assert.Equal(3, (int)EventSeverity.Critical);
    }

    [Fact]
    public void RuntimeEventTypes_HaveExpectedConstants()
    {
        Assert.Equal("runtime.accepted", RuntimeEventTypes.Accepted);
        Assert.Equal("runtime.heartbeat", RuntimeEventTypes.Heartbeat);
        Assert.Equal("runtime.status", RuntimeEventTypes.Status);
        Assert.Equal("runtime.branch_created", RuntimeEventTypes.BranchCreated);
        Assert.Equal("runtime.pr_created", RuntimeEventTypes.PrCreated);
        Assert.Equal("runtime.needs_human", RuntimeEventTypes.NeedsHuman);
        Assert.Equal("runtime.completed", RuntimeEventTypes.Completed);
        Assert.Equal("runtime.failed", RuntimeEventTypes.Failed);
        Assert.Equal("runtime.cancelled", RuntimeEventTypes.Cancelled);
    }

    [Fact]
    public void CompletionOutcomes_HaveExpectedConstants()
    {
        Assert.Equal("pull_request_opened", CompletionOutcomes.PullRequestOpened);
        Assert.Equal("branch_pushed", CompletionOutcomes.BranchPushed);
        Assert.Equal("patch_created", CompletionOutcomes.PatchCreated);
        Assert.Equal("no_changes_needed", CompletionOutcomes.NoChangesNeeded);
        Assert.Equal("needs_human", CompletionOutcomes.NeedsHuman);
        Assert.Equal("failed", CompletionOutcomes.Failed);
    }

    [Fact]
    public void WorkCandidate_CanBeConstructed()
    {
        var candidate = new WorkCandidate
        {
            Id = "cand-1",
            ExternalId = "12345",
            ExternalUrl = "https://dev.azure.com/org/project/_workitems/edit/12345",
            RepoKey = "example-service",
            Title = "Add retry handling",
            Description = "Implement retry for transient 5xx errors.",
            AcceptanceCriteria = new Dictionary<string, string>
            {
                ["AC1"] = "429 responses are retried up to 3 times",
                ["AC2"] = "5xx responses are retried with exponential backoff",
            },
            Priority = 2,
            Status = "New",
            Tags = new List<string> { "agent-ready", "backend" },
            AssignedTo = null,
            Source = "AzureDevOpsBoards",
        };

        Assert.Equal("cand-1", candidate.Id);
        Assert.Equal("12345", candidate.ExternalId);
        Assert.Equal("example-service", candidate.RepoKey);
        Assert.Equal(2, candidate.Tags.Count);
        Assert.Contains("agent-ready", candidate.Tags);
    }

    [Fact]
    public void WorkCandidate_IsImmutable()
    {
        var candidate = new WorkCandidate
        {
            Id = "cand-1",
            ExternalId = "1",
            RepoKey = "repo",
            Title = "Test",
            Source = "fake",
        };

        // Records use init-only setters; with-expressions produce new instances
        var modified = candidate with
        {
            Title = "Modified",
        };
        Assert.Equal("Test", candidate.Title);
        Assert.Equal("Modified", modified.Title);
        Assert.NotSame(candidate, modified);
    }

    [Fact]
    public void ExternalWorkRef_CanBeConstructed()
    {
        var workRef = new ExternalWorkRef
        {
            Source = "AzureDevOpsBoards",
            ExternalId = "12345",
            Url = "https://dev.azure.com/org/project/_workitems/edit/12345",
        };

        Assert.Equal("AzureDevOpsBoards", workRef.Source);
        Assert.Equal("12345", workRef.ExternalId);
    }

    [Fact]
    public void ExternalWorkStatus_SupportsOptionalFields()
    {
        var status = new ExternalWorkStatus
        {
            Status = "Active",
            Tags = new List<string> { "agent-active" },
            Comment = "Controller started work.",
        };

        Assert.Equal("Active", status.Status);
        Assert.Single(status.Tags!);
        Assert.Contains("agent-active", status.Tags!);
    }

    [Fact]
    public void WorkQuery_DefaultMaxResultsIs50()
    {
        var query = new WorkQuery();
        Assert.Equal(50, query.MaxResults);
    }

    [Fact]
    public void ClaimRequest_HasSensibleDefaults()
    {
        var request = new ClaimRequest { WorkerId = "worker-1" };
        Assert.Equal("worker-1", request.WorkerId);
        Assert.Equal(TimeSpan.FromMinutes(30), request.LeaseTimeout);
        Assert.True(request.ClaimedAt <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public void ClaimResult_Successful()
    {
        var result = new ClaimResult
        {
            Success = true,
            WorkRef = new ExternalWorkRef { Source = "AzureDevOpsBoards", ExternalId = "42" },
            LeaseToken = "lease-token-abc",
        };

        Assert.True(result.Success);
        Assert.NotNull(result.WorkRef);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public void ClaimResult_Failed()
    {
        var result = new ClaimResult
        {
            Success = false,
            FailureReason = "Already claimed by another controller.",
        };

        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
        Assert.Null(result.WorkRef);
        Assert.Null(result.LeaseToken);
    }

    [Fact]
    public void RepositorySpec_CanBeConstructed()
    {
        var spec = new RepositorySpec
        {
            RepoKey = "example-service",
            CloneUrl = "https://dev.azure.com/org/project/_git/example-service",
            DefaultBranch = "main",
            Transport = CloneTransport.HttpsPat,
            Profile = new RepositoryProfile
            {
                Key = "example-service",
                CloneUrl = "https://dev.azure.com/org/project/_git/example-service",
                DefaultBranch = "main",
                Transport = CloneTransport.HttpsPat,
                EnvironmentProfile = "local-default",
                RuntimeProfile = "pi-materia-default",
            },
        };

        Assert.Equal("example-service", spec.RepoKey);
        Assert.NotNull(spec.Profile);
    }

    [Fact]
    public void RepositoryCheckout_RecordsCloneMetadata()
    {
        var beforeClone = DateTimeOffset.UtcNow;
        var checkout = new RepositoryCheckout
        {
            RepoKey = "example-service",
            LocalPath = "/tmp/runs/run-1/repo",
            Branch = "main",
            CommitSha = "abc123def456",
            Transport = CloneTransport.HttpsPat,
            ClonedAt = DateTimeOffset.UtcNow,
        };

        Assert.Equal("main", checkout.Branch);
        Assert.NotNull(checkout.CommitSha);
        Assert.True(checkout.ClonedAt >= beforeClone);
    }

    [Fact]
    public void SourceControlRef_And_Status_CanBeConstructed()
    {
        var scRef = new SourceControlRef
        {
            Provider = "AzureDevOpsRepos",
            RepoKey = "example-service",
            Branch = "agent/123-fix",
            CommitSha = "def789",
        };

        var status = new SourceControlStatus
        {
            Exists = true,
            Branch = "agent/123-fix",
            PullRequestUrl = "https://dev.azure.com/org/project/_git/repo/pullrequest/5",
            PullRequestStatus = "active",
        };

        Assert.Equal("AzureDevOpsRepos", scRef.Provider);
        Assert.True(status.Exists);
        Assert.Equal("active", status.PullRequestStatus);
    }

    [Fact]
    public void EnvironmentSpec_CanBeConstructed()
    {
        var spec = new EnvironmentSpec
        {
            RunId = "run-1",
            Profile = "local-default",
            RootPath = "~/.agent-work-controller/runs/run-1",
            Metadata = new Dictionary<string, string> { ["os"] = "linux", ["arch"] = "x64" },
        };

        Assert.Equal("run-1", spec.RunId);
        Assert.Equal(2, spec.Metadata!.Count);
    }

    [Fact]
    public void EnvironmentHandle_CanBeConstructed()
    {
        var handle = new EnvironmentHandle
        {
            Id = "env-1",
            ProviderType = "LocalWorkspace",
            RootPath = "/tmp/runs/run-1",
            Status = "ready",
        };

        Assert.Equal("env-1", handle.Id);
        Assert.Equal("LocalWorkspace", handle.ProviderType);
    }

    [Fact]
    public void CommandSpec_And_CommandResult_CanBeConstructed()
    {
        var cmd = new CommandSpec
        {
            Command = "dotnet",
            Arguments = new List<string> { "build", "--configuration", "Release" },
            WorkingDirectory = "repo/",
            Timeout = TimeSpan.FromMinutes(5),
        };

        var result = new CommandResult
        {
            ExitCode = 0,
            StdOut = "Build succeeded.",
            Duration = TimeSpan.FromSeconds(12.5),
            TimedOut = false,
        };

        Assert.Equal("dotnet", cmd.Command);
        Assert.Equal(3, cmd.Arguments.Count);
        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public void AgentRunSpec_CanBeConstructed()
    {
        var spec = new AgentRunSpec
        {
            RunId = "run-1",
            WorkRef = new ExternalWorkRef { Source = "AzureDevOpsBoards", ExternalId = "12345" },
            RepoCheckout = new RepositoryCheckout
            {
                RepoKey = "example-service",
                LocalPath = "/tmp/runs/run-1/repo",
                Branch = "main",
            },
            EnvironmentHandle = new EnvironmentHandle
            {
                Id = "env-1",
                ProviderType = "LocalWorkspace",
                RootPath = "/tmp/runs/run-1",
                Status = "ready",
            },
            RuntimeProfile = "pi-materia-default",
            BranchNamingPrefix = "agent/12345",
            CallbackUrl = "http://localhost:5000/runtime-events",
        };

        Assert.Equal("run-1", spec.RunId);
        Assert.Equal("12345", spec.WorkRef.ExternalId);
        Assert.Equal("pi-materia-default", spec.RuntimeProfile);
        Assert.Equal("agent/12345", spec.BranchNamingPrefix);
    }

    [Fact]
    public void AgentRunHandle_DefaultsToQueued()
    {
        var handle = new AgentRunHandle { RunId = "run-1" };
        Assert.Equal(RunLifecycleState.Queued, handle.Status);
        Assert.Null(handle.StartedAt);
    }

    [Fact]
    public void AgentRuntimeStatus_CanBeConstructed()
    {
        var status = new AgentRuntimeStatus
        {
            Status = RunLifecycleState.AgentRunning,
            RuntimeRunId = "pi_456",
            StartedAt = DateTimeOffset.UtcNow,
            LastHeartbeatAt = DateTimeOffset.UtcNow,
            Error = null,
        };

        Assert.Equal(RunLifecycleState.AgentRunning, status.Status);
        Assert.Equal("pi_456", status.RuntimeRunId);
        Assert.Null(status.Error);
    }

    [Fact]
    public void RuntimeEvent_FullEnvelope()
    {
        var evt = new RuntimeEvent
        {
            EventId = "evt_001",
            RunId = "run_123",
            RuntimeRunId = "pi_456",
            Sequence = 12,
            OccurredAt = DateTimeOffset.UtcNow,
            EventType = RuntimeEventTypes.Status,
            Severity = EventSeverity.Info,
            Message = "Running unit tests",
            Payload = new Dictionary<string, object?>
            {
                ["phase"] = "validation",
                ["testCount"] = 42,
            },
        };

        Assert.Equal("evt_001", evt.EventId);
        Assert.Equal(RuntimeEventTypes.Status, evt.EventType);
        Assert.Equal(EventSeverity.Info, evt.Severity);
        Assert.Equal(12, evt.Sequence);
        Assert.NotNull(evt.Payload);
        Assert.Equal("validation", evt.Payload!["phase"]);
    }

    [Fact]
    public void RuntimeEvent_MinimalEnvelope()
    {
        var evt = new RuntimeEvent
        {
            EventId = "evt_002",
            RunId = "run_123",
            EventType = RuntimeEventTypes.Heartbeat,
        };

        Assert.Null(evt.RuntimeRunId);
        Assert.Null(evt.Sequence);
        Assert.Null(evt.Message);
        Assert.Null(evt.Payload);
        Assert.Equal(EventSeverity.Info, evt.Severity);
    }

    [Fact]
    public void LifecycleEvent_CanBeConstructed()
    {
        var lcEvent = new LifecycleEvent
        {
            Id = "lc_001",
            RunId = "run_123",
            EventId = "evt_001",
            EventType = RuntimeEventTypes.Completed,
            Severity = EventSeverity.Info,
            Message = "Run completed with PR opened",
            Payload = new Dictionary<string, object?>
            {
                ["outcome"] = "pull_request_opened",
                ["prUrl"] = "https://dev.azure.com/org/project/_git/repo/pullrequest/123",
                ["prNumber"] = 123,
                ["branchName"] = "agent/123-fix",
            },
        };

        Assert.Equal("lc_001", lcEvent.Id);
        Assert.Equal(RuntimeEventTypes.Completed, lcEvent.EventType);
        Assert.Equal("evt_001", lcEvent.EventId);
    }

    [Fact]
    public void AzureDevOpsStatesAndTags_TreatedAsDataNotEnums()
    {
        // Per architecture: states and tags are configuration-driven strings, not enums.
        // Verify that WorkCandidate accepts arbitrary status and tag strings.
        var candidate = new WorkCandidate
        {
            Id = "cand-1",
            ExternalId = "1",
            RepoKey = "repo",
            Title = "Test",
            Source = "fake",
            Status = "Committed", // Azure DevOps state — arbitrary string
            Tags = new List<string> // Azure DevOps tags — arbitrary strings
            {
                "agent-ready",
                "agent-blocked",
                "needs-human",
                "custom-tag-xyz",
            },
        };

        Assert.Equal("Committed", candidate.Status);
        Assert.Contains("custom-tag-xyz", candidate.Tags);
    }
}
