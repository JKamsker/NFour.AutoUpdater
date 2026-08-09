using FourSaas.AutoUpdater.Core;
using FourSaas.AutoUpdater.Publishing;

namespace FourSaas.AutoUpdater.Cli;

public static class SliceLintCommand
{
    public const string CommandName = "slice";
    public const string LintSubcommandName = "lint";
    public const string AxisOption = "--axis";
    public const string WarningsAsErrorsOption = "--warnings-as-errors";

    private const int SuccessExitCode = 0;
    private const int ValidationExitCode = 1;
    private const char OptionPrefix = '-';
    private const char DirectorySeparator = '/';
    private const string OptionAssignmentSeparator = "=";
    private const string AllFilesSearchPattern = "*";

    public static ValueTask<int> RunAsync(string[] args)
    {
        if (!string.Equals(args.FirstOrDefault(), LintSubcommandName, StringComparison.Ordinal))
            throw new FormatException($"{CommandName} requires {LintSubcommandName}.");

        var options = ParseOptions(args.Skip(1).ToArray());
        if (!Directory.Exists(options.BuildRoot))
            throw new DirectoryNotFoundException($"Build root '{options.BuildRoot}' does not exist.");

        var paths = EnumerateRelativeFiles(options.BuildRoot);
        var diagnostics = BuildTreeLayout.Validate(paths, options.DeclaredAxes);
        foreach (var diagnostic in diagnostics)
            Console.Error.WriteLine($"{diagnostic.Code} {diagnostic.Severity}: {diagnostic.Message}");

        var warningCount = diagnostics.Count(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning);
        var hasFailure = diagnostics.Any(static diagnostic => diagnostic.IsError)
            || (options.WarningsAsErrors && warningCount > 0);
        if (!hasFailure)
            Console.WriteLine($"Build-tree layout valid: {paths.Length} files, {warningCount} warnings.");

        return ValueTask.FromResult(hasFailure ? ValidationExitCode : SuccessExitCode);
    }

    private static SliceLintOptions ParseOptions(string[] args)
    {
        var declaredAxes = new HashSet<string>(StringComparer.Ordinal);
        var positional = new List<string>();
        var warningsAsErrors = false;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (string.Equals(argument, AxisOption, StringComparison.Ordinal))
            {
                var axis = args.ElementAtOrDefault(++index)
                    ?? throw new FormatException($"{AxisOption} requires an axis name.");
                AddAxis(declaredAxes, axis);
                continue;
            }
            if (argument.StartsWith(AxisOption + OptionAssignmentSeparator, StringComparison.Ordinal))
            {
                AddAxis(declaredAxes, argument[(AxisOption.Length + OptionAssignmentSeparator.Length)..]);
                continue;
            }
            if (string.Equals(argument, WarningsAsErrorsOption, StringComparison.Ordinal))
            {
                warningsAsErrors = true;
                continue;
            }
            if (argument.StartsWith(OptionPrefix))
                throw new FormatException($"Unknown {CommandName} {LintSubcommandName} option '{argument}'.");
            positional.Add(argument);
        }

        if (positional.Count > 1)
            throw new FormatException($"{CommandName} {LintSubcommandName} accepts at most one build root.");

        var buildRoot = Path.GetFullPath(positional.FirstOrDefault() ?? Directory.GetCurrentDirectory());
        return new SliceLintOptions(buildRoot, declaredAxes.ToArray(), warningsAsErrors);
    }

    private static void AddAxis(ISet<string> declaredAxes, string axis)
    {
        if (string.IsNullOrWhiteSpace(axis)
            || axis.Contains(Path.DirectorySeparatorChar)
            || axis.Contains(Path.AltDirectorySeparatorChar))
            throw new FormatException($"{AxisOption} requires a non-empty directory name.");
        declaredAxes.Add(axis);
    }

    private static string[] EnumerateRelativeFiles(string buildRoot)
    {
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };
        return Directory.EnumerateFiles(buildRoot, AllFilesSearchPattern, enumeration)
            .Select(path => Path.GetRelativePath(buildRoot, path).Replace(Path.DirectorySeparatorChar, DirectorySeparator))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
    }

    private sealed record SliceLintOptions(
        string BuildRoot,
        IReadOnlyCollection<string> DeclaredAxes,
        bool WarningsAsErrors);
}
