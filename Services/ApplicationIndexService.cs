using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using SpotCont.Models;

namespace SpotCont.Services;

public sealed class ApplicationIndexService
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".lnk",
        ".appref-ms",
        ".url",
        ".exe"
    };

    private IndexedItem[] _snapshot;
    private int _started;

    public ApplicationIndexService()
    {
        _snapshot = CreateBuiltInItems().ToArray();
    }

    public event EventHandler? IndexUpdated;

    public IReadOnlyList<IndexedItem> GetSnapshot()
    {
        return Volatile.Read(ref _snapshot);
    }

    public void StartBackgroundIndexing()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        _ = Task.Run(BuildSnapshot);
    }

    private void BuildSnapshot()
    {
        var entries = new Dictionary<string, IndexedItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var builtInItem in CreateBuiltInItems())
        {
            entries[builtInItem.Id] = builtInItem;
        }

        AddShortcutEntries(entries, Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms));
        AddShortcutEntries(entries, Environment.GetFolderPath(Environment.SpecialFolder.Programs));
        AddShortcutEntries(entries, Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));
        AddShortcutEntries(entries, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        AddAppPathEntries(entries, Registry.CurrentUser);
        AddAppPathEntries(entries, Registry.LocalMachine);

        Volatile.Write(
            ref _snapshot,
            entries.Values
                .OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Target, StringComparer.OrdinalIgnoreCase)
                .ToArray());

        IndexUpdated?.Invoke(this, EventArgs.Empty);
    }

    private static IEnumerable<IndexedItem> CreateBuiltInItems()
    {
        return new[]
        {
            CreateBuiltInItem("settings", "Settings", "Windows settings", "ms-settings:", "settings preferences windows settings control"),
            CreateBuiltInItem("control-panel", "Control Panel", "Windows control panel", "control.exe", "control panel settings system"),
            CreateBuiltInItem("task-manager", "Task Manager", "Windows task manager", "taskmgr.exe", "task manager processes performance"),
            CreateBuiltInItem("services", "Services", "Windows services console", "services.msc", "services services.msc windows services"),
            CreateBuiltInItem("registry-editor", "Registry Editor", "Windows registry editor", "regedit.exe", "registry editor regedit registry"),
            CreateBuiltInItem("device-manager", "Device Manager", "Windows device manager", "devmgmt.msc", "device manager devices hardware devmgmt"),
            CreateBuiltInItem("disk-management", "Disk Management", "Windows disk management", "diskmgmt.msc", "disk management storage diskmgmt"),
            CreateBuiltInItem("computer-management", "Computer Management", "Windows computer management", "compmgmt.msc", "computer management compmgmt"),
            CreateBuiltInItem("cmd", "Command Prompt", "Windows command prompt", "cmd.exe", "command prompt cmd console terminal"),
            CreateBuiltInItem("powershell", "PowerShell", "Windows PowerShell", "powershell.exe", "powershell shell terminal"),
            CreateBuiltInItem("explorer", "File Explorer", "Windows file explorer", "explorer.exe", "file explorer explorer files folders"),
            CreateBuiltInItem("system-information", "System Information", "Windows system information", "msinfo32.exe", "system information msinfo32")
        };
    }

    private static IndexedItem CreateBuiltInItem(
        string id,
        string title,
        string subtitle,
        string target,
        string searchText)
    {
        return new IndexedItem(
            $"builtin::{id}",
            title,
            subtitle,
            target,
            SearchResultKind.Application,
            searchText,
            title,
            Bucket: SearchResultBucket.Executable,
            ResolvedTarget: target,
            SearchInternetText: title,
            CopyText: target);
    }

    private static void AddShortcutEntries(IDictionary<string, IndexedItem> entries, string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            return;
        }

        foreach (var filePath in EnumerateFilesSafe(rootPath))
        {
            var extension = Path.GetExtension(filePath);
            if (!SupportedExtensions.Contains(extension))
            {
                continue;
            }

            var title = Path.GetFileNameWithoutExtension(filePath);
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var resolvedTarget = ResolveTargetPath(filePath, extension);
            var bucket = extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                ? SearchResultBucket.Executable
                : SearchResultBucket.Shortcut;

            var entry = new IndexedItem(
                $"app::{filePath}",
                title,
                filePath,
                filePath,
                SearchResultKind.Application,
                BuildSearchText(title, filePath, resolvedTarget),
                title,
                Bucket: bucket,
                ResolvedTarget: resolvedTarget,
                ContainingDirectoryPath: Path.GetDirectoryName(filePath),
                SearchInternetText: title,
                CopyText: filePath);

            entries.TryAdd(entry.Id, entry);

            AddResolvedExecutableEntry(entries, title, resolvedTarget);
        }
    }

    private static void AddAppPathEntries(IDictionary<string, IndexedItem> entries, RegistryKey rootKey)
    {
        using var appPathsKey = rootKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths");
        if (appPathsKey is null)
        {
            return;
        }

        foreach (var subKeyName in appPathsKey.GetSubKeyNames())
        {
            using var subKey = appPathsKey.OpenSubKey(subKeyName);
            if (subKey is null)
            {
                continue;
            }

            var rawValue = subKey.GetValue(string.Empty) as string;
            var executablePath = NormalizeExecutablePath(rawValue);
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            {
                continue;
            }

            var title = Path.GetFileNameWithoutExtension(subKeyName);
            if (string.IsNullOrWhiteSpace(title))
            {
                title = Path.GetFileNameWithoutExtension(executablePath);
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var entry = new IndexedItem(
                $"apppath::{executablePath}",
                title,
                executablePath,
                executablePath,
                SearchResultKind.Application,
                $"{title} {executablePath} {subKeyName}",
                title,
                Bucket: SearchResultBucket.Executable,
                ResolvedTarget: executablePath,
                ContainingDirectoryPath: Path.GetDirectoryName(executablePath),
                SearchInternetText: title,
                CopyText: executablePath);

            entries.TryAdd(entry.Id, entry);
        }
    }

    private static void AddResolvedExecutableEntry(
        IDictionary<string, IndexedItem> entries,
        string title,
        string? resolvedTarget)
    {
        if (string.IsNullOrWhiteSpace(resolvedTarget) ||
            !Path.IsPathRooted(resolvedTarget) ||
            !File.Exists(resolvedTarget) ||
            !resolvedTarget.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var executableTitle = string.IsNullOrWhiteSpace(title)
            ? Path.GetFileNameWithoutExtension(resolvedTarget)
            : title;

        var executableEntry = new IndexedItem(
            $"approot::{resolvedTarget}",
            executableTitle,
            resolvedTarget,
            resolvedTarget,
            SearchResultKind.Application,
            BuildSearchText(executableTitle, resolvedTarget, resolvedTarget),
            executableTitle,
            LastModifiedUtc: File.GetLastWriteTimeUtc(resolvedTarget),
            Bucket: SearchResultBucket.Executable,
            ResolvedTarget: resolvedTarget,
            ContainingDirectoryPath: Path.GetDirectoryName(resolvedTarget),
            SearchInternetText: executableTitle,
            CopyText: resolvedTarget);

        entries.TryAdd(executableEntry.Id, executableEntry);
    }

    private static string BuildSearchText(string title, string sourcePath, string? resolvedTarget)
    {
        var resolvedName = string.IsNullOrWhiteSpace(resolvedTarget)
            ? string.Empty
            : Path.GetFileNameWithoutExtension(resolvedTarget);

        return $"{title} {sourcePath} {resolvedTarget} {resolvedName}".Trim();
    }

    private static string ResolveTargetPath(string sourcePath, string extension)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return string.Empty;
        }

        try
        {
            if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return sourcePath;
            }

            if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                return ResolveShellShortcut(sourcePath);
            }

            if (extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
            {
                return ResolveInternetShortcut(sourcePath);
            }
        }
        catch
        {
        }

        return sourcePath;
    }

    private static string ResolveShellShortcut(string shortcutPath)
    {
        object? shell = null;
        object? shortcut = null;

        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return shortcutPath;
            }

            shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return shortcutPath;
            }

            shortcut = shellType.InvokeMember(
                "CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod,
                null,
                shell,
                new object[] { shortcutPath });

            var targetPath = shortcut?.GetType().InvokeMember(
                "TargetPath",
                System.Reflection.BindingFlags.GetProperty,
                null,
                shortcut,
                null) as string;

            return string.IsNullOrWhiteSpace(targetPath)
                ? shortcutPath
                : Environment.ExpandEnvironmentVariables(targetPath);
        }
        catch
        {
            return shortcutPath;
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut))
            {
                Marshal.ReleaseComObject(shortcut);
            }

            if (shell is not null && Marshal.IsComObject(shell))
            {
                Marshal.ReleaseComObject(shell);
            }
        }
    }

    private static string ResolveInternetShortcut(string shortcutPath)
    {
        try
        {
            foreach (var line in File.ReadLines(shortcutPath))
            {
                if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                {
                    return line[4..].Trim();
                }
            }
        }
        catch
        {
        }

        return shortcutPath;
    }

    private static string NormalizeExecutablePath(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return string.Empty;
        }

        var expandedValue = Environment.ExpandEnvironmentVariables(rawValue).Trim();
        if (expandedValue.StartsWith('"'))
        {
            var closingQuoteIndex = expandedValue.IndexOf('"', 1);
            return closingQuoteIndex > 1
                ? expandedValue[1..closingQuoteIndex]
                : expandedValue.Trim('"');
        }

        const string executableSuffix = ".exe";
        var executableIndex = expandedValue.IndexOf(executableSuffix, StringComparison.OrdinalIgnoreCase);
        if (executableIndex >= 0)
        {
            return expandedValue[..(executableIndex + executableSuffix.Length)];
        }

        return expandedValue;
    }

    private static IEnumerable<string> EnumerateFilesSafe(string rootPath)
    {
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(rootPath);

        while (pendingDirectories.Count > 0)
        {
            var currentDirectory = pendingDirectories.Pop();
            IEnumerable<string> files = Array.Empty<string>();
            IEnumerable<string> directories = Array.Empty<string>();

            try
            {
                files = Directory.EnumerateFiles(currentDirectory);
                directories = Directory.EnumerateDirectories(currentDirectory);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            foreach (var directory in directories)
            {
                if (ShouldSkipDirectory(directory))
                {
                    continue;
                }

                pendingDirectories.Push(directory);
            }
        }
    }

    private static bool ShouldSkipDirectory(string directoryPath)
    {
        try
        {
            var attributes = File.GetAttributes(directoryPath);
            return attributes.HasFlag(FileAttributes.ReparsePoint) || attributes.HasFlag(FileAttributes.System);
        }
        catch
        {
            return true;
        }
    }
}
