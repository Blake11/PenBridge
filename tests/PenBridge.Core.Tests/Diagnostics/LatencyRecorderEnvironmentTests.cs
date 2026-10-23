using PenBridge.Core.Diagnostics;

namespace PenBridge.Core.Tests.Diagnostics;

/// <summary>The environment switch (#279). One test: the variable is process-wide and tests run in parallel.</summary>
public class LatencyRecorderEnvironmentTests
{
    [Fact]
    public void Only_the_documented_spellings_turn_diagnostics_on()
    {
        string? original = Environment.GetEnvironmentVariable(LatencyRecorder.EnvironmentVariable);
        try
        {
            foreach (string on in new[] { "1", "true", "TRUE", "True" })
            {
                Environment.SetEnvironmentVariable(LatencyRecorder.EnvironmentVariable, on);
                Assert.True(LatencyRecorder.IsEnabledByEnvironment, on);
            }

            foreach (string? off in new[] { null, "", "0", "false", "FALSE", "tRUE", "yes", "on", "2", " 1" })
            {
                Environment.SetEnvironmentVariable(LatencyRecorder.EnvironmentVariable, off);
                Assert.False(LatencyRecorder.IsEnabledByEnvironment, off ?? "(unset)");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(LatencyRecorder.EnvironmentVariable, original);
        }
    }
}
