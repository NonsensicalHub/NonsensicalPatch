namespace NonsensicalPatch.Core;

public class Logger
{
    public static Logger Instance = new Logger();

    public Action<string>? LogAction;

    private FileStream _stream;
    private StreamWriter _writer;

    public Logger()
    {
        Instance = this;
    }

    public Logger(string path)
    {
        Instance = this;
        _stream = new FileStream(path, FileMode.OpenOrCreate);
        _writer = new StreamWriter(_stream);
        LogAction += WriteLogFile;
    }

    ~Logger()
    {
        if (_stream != null)
        {
            _writer.Close();
            _stream.Flush();
            _stream.Close();
        }
    }

    public void Log(string message)
    {
        LogAction?.Invoke(message);
    }

    private void WriteLogFile(string message)
    {
        _writer.WriteLine(message);
        _writer.Flush();
    }
}