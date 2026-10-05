using System.Text;

namespace Campfire.Bench;

public sealed record ProcessOutput(byte[] StandardOutput, byte[] StandardError, int ExitCode)
{
    public string StandardOutputText => Encoding.UTF8.GetString(StandardOutput);
    public string StandardErrorText => Encoding.UTF8.GetString(StandardError);
}

public interface IRunningProcess : IAsyncDisposable
{
    string Output { get; }
}

public interface IProcessRunner
{
    Task<ProcessOutput> RunAsync(
        IReadOnlyList<string> arguments,
        byte[]? standardInput = null,
        CancellationToken cancellationToken = default);

    Task<IRunningProcess> StartAsync(
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default);

    Task RemoveContainerAsync(string name, CancellationToken cancellationToken = default);
}

public sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<ProcessOutput> RunAsync(
        IReadOnlyList<string> arguments,
        byte[]? standardInput = null,
        CancellationToken cancellationToken = default)
    {
        var output = await RunCapturingAsync(arguments, standardInput, cancellationToken);
        if (output.ExitCode != 0)
            throw new InvalidOperationException($"{arguments[0]} failed ({output.ExitCode}): {output.StandardErrorText}");
        return output;
    }

    public Task<IRunningProcess> StartAsync(
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default)
    {
        if (arguments.Count == 0)
            throw new ArgumentException("A process needs a program name.", nameof(arguments));

        cancellationToken.ThrowIfCancellationRequested();
        var process = new Process();
        process.StartInfo.FileName = arguments[0];
        foreach (var argument in arguments.Skip(1))
            process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.RedirectStandardInput = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;
        if (environment is not null)
        {
            foreach (var (name, value) in environment)
                process.StartInfo.Environment[name] = value;
        }

        if (!process.Start())
            throw new InvalidOperationException($"could not start {arguments[0]}");

        process.StandardInput.Close();
        return Task.FromResult<IRunningProcess>(new RunningProcess(process));
    }

    public async Task RemoveContainerAsync(string name, CancellationToken cancellationToken = default)
    {
        _ = await RunCapturingAsync(["docker", "rm", "-f", name], standardInput: null, cancellationToken);
    }

    private static async Task<ProcessOutput> RunCapturingAsync(
        IReadOnlyList<string> arguments,
        byte[]? standardInput,
        CancellationToken cancellationToken)
    {
        if (arguments.Count == 0)
            throw new ArgumentException("A process needs a program name.", nameof(arguments));

        using var process = new Process();
        process.StartInfo.FileName = arguments[0];
        foreach (var argument in arguments.Skip(1))
            process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.RedirectStandardInput = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;

        if (!process.Start())
            throw new InvalidOperationException($"could not start {arguments[0]}");

        if (standardInput is not null)
            await process.StandardInput.BaseStream.WriteAsync(standardInput, cancellationToken);
        process.StandardInput.Close();

        using var stdout = new MemoryStream();
        using var stderr = new MemoryStream();
        var outputCopy = process.StandardOutput.BaseStream.CopyToAsync(stdout, cancellationToken);
        var errorCopy = process.StandardError.BaseStream.CopyToAsync(stderr, cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        await Task.WhenAll(outputCopy, errorCopy);
        return new ProcessOutput(stdout.ToArray(), stderr.ToArray(), process.ExitCode);
    }

    private sealed class RunningProcess : IRunningProcess
    {
        private readonly Process process;
        private readonly StringBuilder output = new();
        private readonly Task stdout;
        private readonly Task stderr;
        private int disposed;

        public RunningProcess(Process process)
        {
            this.process = process;
            stdout = PumpAsync(process.StandardOutput);
            stderr = PumpAsync(process.StandardError);
        }

        public string Output
        {
            get
            {
                lock (output)
                    return output.ToString();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;

            // setsid --wait puts the server in its own session. Killing the tree
            // stops that waiter, `dotnet run`, and the Kestrel child together.
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
            }

            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                }
            }

            try
            {
                await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
            }

            process.Dispose();
        }

        private async Task PumpAsync(StreamReader reader)
        {
            var buffer = new char[4096];
            while (true)
            {
                int read;
                try
                {
                    read = await reader.ReadAsync(buffer);
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException)
                {
                    return;
                }

                if (read == 0)
                    return;

                lock (output)
                {
                    output.Append(buffer, 0, read);
                    const int maxChars = 256 * 1024;
                    if (output.Length > maxChars)
                        output.Remove(0, output.Length - maxChars);
                }
            }
        }
    }
}
