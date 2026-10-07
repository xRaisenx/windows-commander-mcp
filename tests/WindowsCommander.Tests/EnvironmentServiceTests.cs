using WindowsCommander.Windows.Services;

namespace WindowsCommander.Tests;

public class EnvironmentServiceTests
{
    [Fact]
    public void SecretNamedVariable_IsReportedPresentWithoutReturningSecret()
    {
        const string name = "WINDOWS_COMMANDER_TEST_API_KEY";
        var previous = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable(name, "super-secret-test-value", EnvironmentVariableTarget.Process);

        try
        {
            var service = new EnvironmentService();
            var value = service.GetEnvironmentVariable(name, "process");

            Assert.Equal("***REDACTED_PRESENT***", value);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous, EnvironmentVariableTarget.Process);
        }
    }

    [Fact]
    public void NonSecretNamedVariable_RemainsReadable()
    {
        const string name = "WINDOWS_COMMANDER_TEST_MODE";
        var previous = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable(name, "rescue", EnvironmentVariableTarget.Process);

        try
        {
            var service = new EnvironmentService();

            Assert.Equal("rescue", service.GetEnvironmentVariable(name, "process"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous, EnvironmentVariableTarget.Process);
        }
    }
}
