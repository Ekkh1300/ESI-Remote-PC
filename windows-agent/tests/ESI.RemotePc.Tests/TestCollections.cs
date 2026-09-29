using Xunit;

namespace Esi.Tests;

/// <summary>
/// Test collections.
/// <para>
/// Grouping exists to control parallelism, not for organisation. The integration tests boot
/// a real agent with real PDH counters and a real WMI client; running several at once
/// measures the machine's ability to host concurrent telemetry collectors rather than the
/// product's behaviour, and a suite whose results depend on how busy the CI box is is a
/// suite nobody trusts.
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AgentIntegrationCollection
{
    public const string Name = "agent-integration";
}
