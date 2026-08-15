namespace NFour.AutoUpdater.Cli.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleCollection
{
    public const string Name = "CLI console";
}

[Collection(ConsoleCollection.Name)]
public sealed class SliceLintCommandTests : IDisposable
{
    private const string CompactGuidFormat = "N";
    private const string TemporaryRootPrefix = "4sup-layout-lint-";
    private const string PlaceholderFileContent = "fixture";
    private const string LanguageAxis = "lang";
    private const char PortablePathSeparator = '/';
    private const string CommonFile = "common/data/world.bin";
    private const string GermanFile = "axis/lang/de/data/strings.bin";
    private const string EnglishFile = "axis/lang/en/data/strings.bin";
    private const string UserInterfaceFile = "axis/ui/classic/data/strings.bin";
    private const string StrayFile = "stray.bin";
    private const string GermanOnlyFile = "axis/lang/de/data/credits.bin";
    private const string EnglishOnlyFile = "axis/lang/en/data/manual.bin";
    private const int SuccessExitCode = 0;
    private const int ValidationExitCode = 1;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        TemporaryRootPrefix + Guid.NewGuid().ToString(CompactGuidFormat));

    [Fact]
    public async Task CliDispatchAcceptsConformingBuildTree()
    {
        await CreateFileAsync(CommonFile);
        await CreateFileAsync(GermanFile);
        await CreateFileAsync(EnglishFile);

        var result = await RunCliAsync(
            SliceLintCommand.CommandName,
            SliceLintCommand.LintSubcommandName,
            _root,
            SliceLintCommand.AxisOption,
            LanguageAxis);

        Assert.Equal(SuccessExitCode, result.ExitCode);
        Assert.Contains("Build-tree layout valid", result.StandardOutput, StringComparison.Ordinal);
        Assert.Empty(result.StandardError);
    }

    [Fact]
    public async Task LintReportsClassificationDeclarationAndDisjointnessErrors()
    {
        await CreateFileAsync(StrayFile);
        await CreateFileAsync(GermanFile);
        await CreateFileAsync(UserInterfaceFile);

        var result = await RunCliAsync(
            SliceLintCommand.CommandName,
            SliceLintCommand.LintSubcommandName,
            _root,
            SliceLintCommand.AxisOption,
            LanguageAxis);

        Assert.Equal(ValidationExitCode, result.ExitCode);
        Assert.Contains("LAY001 Error", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("LAY002 Error", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("LAY003 Error", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WarningsCanFailAStrictBuild()
    {
        await CreateFileAsync(GermanOnlyFile);
        await CreateFileAsync(EnglishOnlyFile);

        var defaultResult = await RunCliAsync(
            SliceLintCommand.CommandName,
            SliceLintCommand.LintSubcommandName,
            _root,
            SliceLintCommand.AxisOption,
            LanguageAxis);
        var strictResult = await RunCliAsync(
            SliceLintCommand.CommandName,
            SliceLintCommand.LintSubcommandName,
            _root,
            SliceLintCommand.AxisOption,
            LanguageAxis,
            SliceLintCommand.WarningsAsErrorsOption);

        Assert.Equal(SuccessExitCode, defaultResult.ExitCode);
        Assert.Contains("LAY004 Warning", defaultResult.StandardError, StringComparison.Ordinal);
        Assert.Equal(ValidationExitCode, strictResult.ExitCode);
        Assert.Contains("LAY004 Warning", strictResult.StandardError, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private async Task CreateFileAsync(string relativePath)
    {
        var path = Path.Combine(_root, relativePath.Replace(PortablePathSeparator, Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, PlaceholderFileContent);
    }

    private static async Task<CommandResult> RunCliAsync(params string[] args)
    {
        var previousOutput = Console.Out;
        var previousError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var exitCode = await CliApplication.RunAsync(args);
            return new CommandResult(exitCode, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(previousOutput);
            Console.SetError(previousError);
        }
    }

    private sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);
}
