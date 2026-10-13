using AppKit;
using Foundation;
using PenBridge.Mac.Services;

namespace PenBridge.Mac.Shell;

/// <summary>"Open logs": a Terminal window streaming this app's unified log (subsystem <see cref="OsLogLoggerProvider.Subsystem"/>).</summary>
/// <remarks>
/// Console.app has no URL scheme or launch argument that presets a search filter, so it would open unfiltered. Instead
/// this writes a small <c>.command</c> script to the per-user temporary directory and opens it; Terminal (the default
/// handler for <c>.command</c>) runs it. Opening a file needs no Apple Events (Automation) permission, unlike scripting
/// Terminal with <c>do script</c>. The script shows the last 30 minutes, then streams live.
/// </remarks>
internal static class LogViewer
{
    private const string FileName = "PenBridge Logs.command";

    private static readonly string Script =
        "#!/bin/zsh\n" +
        "# Written by PenBridge (\"Open logs\"). Safe to delete.\n" +
        "predicate='subsystem == \"" + OsLogLoggerProvider.Subsystem + "\"'\n" +
        "clear\n" +
        "echo 'PenBridge logs: last 30 minutes, then live (Ctrl-C to stop)'\n" +
        "/usr/bin/log show --last 30m --style compact --info --predicate \"$predicate\"\n" +
        "exec /usr/bin/log stream --style compact --level info --predicate \"$predicate\"\n";

    /// <summary>Opens the log window; false (with the reason) when the script couldn't be written or opened.</summary>
    public static bool Open(out string? error)
    {
        try
        {
            string path = Path.Combine(Path.GetTempPath(), FileName);
            string temp = path + "." + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".tmp";

            // Created 0700 (never briefly world-readable or non-executable), then moved over any old copy atomically.
            var options = new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            };
            using (var writer = new StreamWriter(temp, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false), options))
            {
                writer.Write(Script);
            }

            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.Move(temp, path, overwrite: true);
            if (!NSWorkspace.SharedWorkspace.OpenUrl(NSUrl.CreateFileUrl(path, isDir: false, relativeToUrl: null)))
            {
                error = "No app is set to open .command files.";
                return false;
            }

            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }
}
