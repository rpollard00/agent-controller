namespace AgentController.Api.Tests;

/// <summary>
/// Groups <c>WebApplicationFactory&lt;Program&gt;</c>-based API integration tests
/// so xUnit runs them sequentially rather than in parallel.
///
/// These tests drive the full host and configure it through process-global
/// environment variables (<c>persistence__*</c>, <c>agentController__*</c>) that
/// <c>WebApplicationFactory</c> reads during host construction. Running two such
/// classes concurrently would race on those shared environment variables (e.g.
/// one class overwriting another's SQLite connection string mid-start). The
/// collection has no shared fixture — it only enforces sequential execution.
/// </summary>
[CollectionDefinition("ApiWebFactory")]
public sealed class ApiWebFactoryGroup;
