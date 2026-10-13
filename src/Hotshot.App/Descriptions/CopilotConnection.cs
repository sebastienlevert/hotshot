using GitHub.Copilot;

namespace Hotshot.Descriptions;

internal static class CopilotConnection
{
    public static CopilotClient Create(string directory, string runtime)
    {
        Directory.CreateDirectory(directory);
        return new CopilotClient(new CopilotClientOptions
        {
            Mode = CopilotClientMode.Empty,
            Connection = RuntimeConnection.ForStdio(runtime),
            WorkingDirectory = directory,
            BaseDirectory = Path.Combine(directory, "copilot"),
            UseLoggedInUser = true,
            LogLevel = CopilotLogLevel.None,
        });
    }
}
