namespace Hotshot.Shell;

internal enum AppCommand
{
    None,
    Activate,
    Region,
    Monitor,
    AllMonitors,
    Record,
    RecordGif,
    Settings,
    History,
    Exit,
}

internal sealed record CommandLine(bool Background, AppCommand Command)
{
    public static CommandLine Parse(IEnumerable<string> args)
    {
        var background = false;
        var command = AppCommand.None;
        foreach (var raw in args)
        {
            var arg = raw.Trim().TrimStart('-', '/').ToLowerInvariant();
            switch (arg)
            {
                case "background": background = true; break;
                case "activate": command = AppCommand.Activate; break;
                case "region": command = AppCommand.Region; break;
                case "monitor": command = AppCommand.Monitor; break;
                case "all" or "screen" or "allmonitors": command = AppCommand.AllMonitors; break;
                case "record": command = AppCommand.Record; break;
                case "record-gif" or "gif": command = AppCommand.RecordGif; break;
                case "settings": command = AppCommand.Settings; break;
                case "history": command = AppCommand.History; break;
                case "exit" or "quit": command = AppCommand.Exit; break;
            }
        }

        return new CommandLine(background, command);
    }
}
