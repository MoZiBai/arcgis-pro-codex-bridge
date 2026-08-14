using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ArcGISCodexBridgeAddin;

internal static class ArcPyWorkerActions
{
    internal static async Task<object> RunAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var workerPath = BridgeSupport.String(payload, "worker_path")
            ?? throw new ArgumentException("worker_path is required");
        workerPath = ValidateWorkerPath(workerPath);
        if (!payload.TryGetProperty("request", out var request)
            || request.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("request must be an object");
        }

        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("ArcGIS Pro process path is unavailable");
        var binRoot = Path.GetDirectoryName(processPath)
            ?? throw new InvalidOperationException("ArcGIS Pro bin directory is unavailable");
        var pythonPath = Path.Combine(
            binRoot,
            "Python",
            "envs",
            "arcgispro-py3",
            "python.exe");
        if (!File.Exists(pythonPath))
        {
            throw new FileNotFoundException("ArcGIS Pro Python was not found", pythonPath);
        }

        var timeoutSeconds = Math.Clamp(
            BridgeSupport.Int(payload, "timeout_seconds", 1800),
            1,
            3600);
        var startInfo = new ProcessStartInfo
        {
            FileName = pythonPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(workerPath)!,
        };
        startInfo.ArgumentList.Add(workerPath);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("ArcGIS Pro could not start the ArcPy worker");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.StandardInput.WriteAsync(request.GetRawText());
        process.StandardInput.Close();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException(
                $"ArcPy worker exceeded the {timeoutSeconds}-second timeout");
        }
        catch
        {
            TryKill(process);
            throw;
        }

        var stdout = (await stdoutTask).Trim();
        var stderr = (await stderrTask).Trim();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"ArcPy worker failed: {Tail(stderr.Length > 0 ? stderr : stdout)}");
        }
        if (stdout.Length == 0)
        {
            throw new InvalidOperationException(
                $"ArcPy worker returned no JSON. stderr: {Tail(stderr)}");
        }

        var lastLine = stdout.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Last();
        using var document = JsonDocument.Parse(lastLine);
        return document.RootElement.Clone();
    }

    private static string ValidateWorkerPath(string value)
    {
        var workerPath = Path.GetFullPath(value);
        var allowedRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ArcGISCodexBridge",
            "runtime"));
        var relative = Path.GetRelativePath(allowedRoot, workerPath);
        if (Path.IsPathRooted(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException(
                $"worker_path must be under the installed runtime: {allowedRoot}");
        }
        if (!Path.GetFileName(workerPath).Equals("worker.py", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(workerPath))
        {
            throw new FileNotFoundException("Installed ArcPy worker was not found", workerPath);
        }
        return workerPath;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Preserve the original cancellation or execution error.
        }
    }

    private static string Tail(string value)
    {
        const int limit = 2000;
        return value.Length <= limit ? value : value[^limit..];
    }
}
