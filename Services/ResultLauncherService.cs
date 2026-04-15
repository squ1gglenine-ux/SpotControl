using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using SpotCont.Models;

namespace SpotCont.Services;

public sealed class ResultLauncherService
{
    private readonly UsageHistoryService _usageHistoryService;

    public ResultLauncherService(UsageHistoryService usageHistoryService)
    {
        _usageHistoryService = usageHistoryService;
    }

    public async Task<bool> ExecuteAsync(
        SearchResultItem result,
        string? queryContext = null,
        CancellationToken cancellationToken = default)
    {
        var recordBeforeExecution =
            result.ActionKind == ResultActionKind.Shutdown ||
            result.ActionKind == ResultActionKind.Restart ||
            result.ActionKind == ResultActionKind.Lock;

        if (recordBeforeExecution)
        {
            await TryRecordLaunchAsync(result, queryContext, cancellationToken);
        }

        var success = result.ActionKind switch
        {
            ResultActionKind.OpenShellTarget => TryShellExecute(result.ActionValue),
            ResultActionKind.OpenUri => TryShellExecute(result.ActionValue),
            ResultActionKind.SearchWeb => TryShellExecute(BuildWebSearchUrl(result.ActionValue)),
            ResultActionKind.CopyText => TryCopyToClipboard(result.ActionValue),
            ResultActionKind.Shutdown => TryStartProcess("shutdown.exe", "/s /t 0"),
            ResultActionKind.Restart => TryStartProcess("shutdown.exe", "/r /t 0"),
            ResultActionKind.Lock => LockWorkStation(),
            _ => false
        };

        if (success && !recordBeforeExecution)
        {
            await TryRecordLaunchAsync(result, queryContext, cancellationToken);
        }

        return success;
    }

    public async Task<bool> ExecuteContextActionAsync(
        SearchResultItem result,
        SearchResultContextActionKind actionKind,
        string? queryContext = null,
        CancellationToken cancellationToken = default)
    {
        return actionKind switch
        {
            SearchResultContextActionKind.Open => await ExecuteAsync(result, queryContext, cancellationToken),
            SearchResultContextActionKind.RunAsAdministrator => await ExecuteAsAdministratorAsync(result, queryContext, cancellationToken),
            SearchResultContextActionKind.OpenContainingFolder => TryOpenContainingFolder(result),
            SearchResultContextActionKind.SearchOnInternet => TryShellExecute(BuildWebSearchUrl(
                string.IsNullOrWhiteSpace(result.SearchInternetText) ? result.Title : result.SearchInternetText)),
            SearchResultContextActionKind.Copy => TryCopyToClipboard(
                string.IsNullOrWhiteSpace(result.CopyText) ? result.ActionValue : result.CopyText),
            _ => false
        };
    }

    public static string BuildWebSearchUrl(string query)
    {
        return $"https://www.bing.com/search?q={Uri.EscapeDataString(query)}";
    }

    private static bool TryCopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryShellExecute(string target)
    {
        return TryShellExecute(target, null);
    }

    private static bool TryShellExecute(string target, string? verb)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true,
                Verb = verb ?? string.Empty
            });

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryStartProcess(string fileName, string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = true
            });

            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> ExecuteAsAdministratorAsync(
        SearchResultItem result,
        string? queryContext,
        CancellationToken cancellationToken)
    {
        var runAsTarget = GetRunAsTarget(result);
        if (string.IsNullOrWhiteSpace(runAsTarget))
        {
            return false;
        }

        var success = TryShellExecute(runAsTarget, "runas");
        if (success)
        {
            await TryRecordLaunchAsync(result, queryContext, cancellationToken);
        }

        return success;
    }

    private static bool TryOpenContainingFolder(SearchResultItem result)
    {
        if (result.Kind == SearchResultKind.Folder)
        {
            if (!string.IsNullOrWhiteSpace(result.ContainingDirectoryPath) &&
                Directory.Exists(result.ContainingDirectoryPath))
            {
                return TryShellExecute(result.ContainingDirectoryPath);
            }

            return !string.IsNullOrWhiteSpace(result.ActionValue) &&
                   Directory.Exists(result.ActionValue) &&
                   TryShellExecute(result.ActionValue);
        }

        var revealPath = !string.IsNullOrWhiteSpace(result.ActionValue) && Path.IsPathRooted(result.ActionValue)
            ? result.ActionValue
            : result.ResolvedTarget;

        if (!string.IsNullOrWhiteSpace(revealPath) &&
            (File.Exists(revealPath) || Directory.Exists(revealPath)))
        {
            return TryStartProcess("explorer.exe", $"/select,\"{revealPath}\"");
        }

        return !string.IsNullOrWhiteSpace(result.ContainingDirectoryPath) &&
               Directory.Exists(result.ContainingDirectoryPath) &&
               TryShellExecute(result.ContainingDirectoryPath);
    }

    private static string GetRunAsTarget(SearchResultItem result)
    {
        if (!string.IsNullOrWhiteSpace(result.ResolvedTarget) &&
            Path.IsPathRooted(result.ResolvedTarget) &&
            File.Exists(result.ResolvedTarget))
        {
            return result.ResolvedTarget;
        }

        if (!string.IsNullOrWhiteSpace(result.ActionValue) &&
            Path.IsPathRooted(result.ActionValue) &&
            File.Exists(result.ActionValue))
        {
            return result.ActionValue;
        }

        return string.Empty;
    }

    private async Task TryRecordLaunchAsync(
        SearchResultItem result,
        string? queryContext,
        CancellationToken cancellationToken)
    {
        try
        {
            await _usageHistoryService.RecordLaunchAsync(result, queryContext, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[SpotCont:ResultLauncher:RecordLaunch] {exception}");
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();
}
