using System.Text;

namespace Campfire.Bench;

public sealed record ProcessOutput(byte[] StandardOutput, byte[] StandardError, int ExitCode)
{
    public string StandardOutputText => Encoding.UTF8.GetString(StandardOutput);
    public string StandardErrorText => Encoding.UTF8.GetString(StandardError);
}

public interface IProcessRunner
{
    Task<ProcessOutput> RunAsync(
        IReadOnlyList<string> arguments,
        byte[]? standardInput = null,
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
}
