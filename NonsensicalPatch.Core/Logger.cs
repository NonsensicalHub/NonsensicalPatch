namespace NonsensicalPatch.Core;

public class Logger
{
    public static Logger Instance = new Logger();

    public Action<string>? LogAction;

    public Logger()
    {
        Instance = this;
    }

    public void Log(string message)
    {
        LogAction?.Invoke(message);
    }
}
