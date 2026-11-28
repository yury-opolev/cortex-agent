using System.Text.RegularExpressions;

namespace Cortex.Contained.Bridge.Tests.WebUi;

/// <summary>The web UI asks for confirmation in the page (Alpine store "confirm"), never with the browser's native dialogs.</summary>
public sealed partial class NativeDialogTests
{
    [Fact]
    public void WebUi_Scripts_UseNoNativeConfirmAlertOrPrompt()
    {
        var wwwroot = Path.Combine(RepoRoot(), "src", "Cortex.Contained.Bridge", "wwwroot");
        var offenders = Directory.EnumerateFiles(wwwroot, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".js", StringComparison.Ordinal) || f.EndsWith(".html", StringComparison.Ordinal))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "lib" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (File: Path.GetRelativePath(wwwroot, f), Line: i + 1, Text: line)))
            .Where(l => !l.Text.TrimStart().StartsWith("//", StringComparison.Ordinal) && !l.Text.TrimStart().StartsWith('*') && NativeDialog().IsMatch(l.Text))
            .Select(l => $"{l.File}:{l.Line}")
            .ToList();

        Assert.True(offenders.Count == 0, "Use Alpine.store(\"confirm\").ask(...) instead of a native dialog: " + string.Join(", ", offenders));
    }

    [GeneratedRegex(@"(?<![\w.$])(window\.)?(confirm|alert|prompt)\s*\(")]
    private static partial Regex NativeDialog();

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "cortex-contained.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("cortex-contained.sln not found above the test directory.");
    }
}
