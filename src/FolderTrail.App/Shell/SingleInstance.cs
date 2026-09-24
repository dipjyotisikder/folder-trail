using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace FolderTrail.App.Shell;

/// <summary>
/// One FolderTrail per user session. A second launch asks the running instance (over a named pipe restricted to
/// the current user) to bring its window forward, then exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string ActivateMessage = "activate";

    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _server;

    private SingleInstance(Mutex mutex, bool isPrimary, string pipeName)
    {
        _mutex = mutex;
        IsPrimary = isPrimary;
        _pipeName = pipeName;
    }

    public bool IsPrimary { get; }

    public static SingleInstance Acquire(string scope = "FolderTrail")
    {
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Environment.UserDomainName}\\{Environment.UserName}")))[..16];
        var mutex = new Mutex(initiallyOwned: true, $@"Local\{scope}-{identity}", out var createdNew);
        return new SingleInstance(mutex, createdNew, $"{scope}-{identity}-activate");
    }

    /// <returns>True when the running instance received the request.</returns>
    public bool SignalPrimary(TimeSpan timeout)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect((int)timeout.TotalMilliseconds);
            using var writer = new StreamWriter(client, Encoding.UTF8);
            writer.WriteLine(ActivateMessage);
            writer.Flush();
            return true;
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void StartListening(Action onActivate)
    {
        if (!IsPrimary || _server is not null)
        {
            return;
        }

        var token = _stopping.Token;
        _server = Task.Run(
            async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await using var server = new NamedPipeServerStream(
                            _pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                        await server.WaitForConnectionAsync(token);
                        using var reader = new StreamReader(server, Encoding.UTF8);
                        var line = await reader.ReadLineAsync(token);
                        if (line == ActivateMessage)
                        {
                            onActivate();
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(250), CancellationToken.None);
                    }
                }
            },
            CancellationToken.None);
    }

    public void Dispose()
    {
        _stopping.Cancel();
        try
        {
            _server?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        if (IsPrimary)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
        }

        _mutex.Dispose();
        _stopping.Dispose();
    }
}
