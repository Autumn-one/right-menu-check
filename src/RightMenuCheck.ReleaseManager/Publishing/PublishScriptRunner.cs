using System.ComponentModel;
using System.Diagnostics;
using RightMenuCheck.Distribution;

namespace RightMenuCheck.ReleaseManager.Publishing;

public sealed record PublishScriptRequest(
    string RepositoryRoot,
    SemanticVersion Version,
    string ScriptPath,
    string ExpectedOutputDirectory);

public sealed record PublishScriptResult(
    int ExitCode,
    string OutputDirectory,
    bool VersionArgumentApplied,
    string StandardOutput,
    string StandardError);

public interface IPublishScriptRunner
{
    bool SupportsVersionArgument { get; }

    Task<PublishScriptResult> RunAsync(
        PublishScriptRequest request,
        CancellationToken cancellationToken);
}

public sealed class PowerShellPublishScriptRunner : IPublishScriptRunner
{
    public bool SupportsVersionArgument => true;

    public async Task<PublishScriptResult> RunAsync(
        PublishScriptRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var root = Path.GetFullPath(request.RepositoryRoot);
        var script = Path.GetFullPath(request.ScriptPath);
        var scriptsRoot = Path.GetFullPath(Path.Combine(root, "scripts")) + Path.DirectorySeparatorChar;
        if (!script.StartsWith(scriptsRoot, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(script))
        {
            throw new FileNotFoundException("发布脚本不存在或不在仓库 scripts 目录内。", script);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh.exe",
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(script);
        startInfo.ArgumentList.Add("-Version");
        startInfo.ArgumentList.Add(request.Version.ToString());

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("无法启动 PowerShell 发布脚本。");
            }
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException("无法启动 pwsh.exe。", exception);
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                    // The process exited between the state check and termination request.
                }
            }

            throw;
        }

        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        var outputDirectory = Path.GetFullPath(request.ExpectedOutputDirectory);
        if (process.ExitCode != 0)
        {
            throw new PublishScriptException(process.ExitCode, SanitizeDiagnostic(error));
        }

        if (!Directory.Exists(outputDirectory))
        {
            throw new DirectoryNotFoundException("发布脚本未生成固定发布目录。");
        }

        return new PublishScriptResult(
            process.ExitCode,
            outputDirectory,
            VersionArgumentApplied: true,
            SanitizeDiagnostic(output),
            SanitizeDiagnostic(error));
    }

    private static string SanitizeDiagnostic(string value)
    {
        const int maximumLength = 4000;
        var trimmed = value.Trim();
        return trimmed.Length <= maximumLength ? trimmed : trimmed[^maximumLength..];
    }
}

public sealed class PublishScriptException : Exception
{
    public PublishScriptException(int exitCode, string diagnostic)
        : base(string.IsNullOrWhiteSpace(diagnostic)
            ? $"发布脚本失败，退出码 {exitCode}。"
            : $"发布脚本失败，退出码 {exitCode}：{diagnostic}")
    {
        ExitCode = exitCode;
    }

    public int ExitCode { get; }
}
