using FourSaas.AutoUpdater.Core;

namespace FourSaas.AutoUpdater.Cli;

public static class CliApplication
{
    public static ValueTask<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help", StringComparer.Ordinal) || args.Contains("-h", StringComparer.Ordinal)) { PrintHelp(); return ValueTask.FromResult(0); }
        if (args.Contains("--version", StringComparer.Ordinal)) { Console.WriteLine("4sup 1.0.0"); return ValueTask.FromResult(0); }
        try
        {
            return ValueTask.FromResult(Execute(args));
        }
        catch (FormatException ex) { Console.Error.WriteLine($"2 Usage: {ex.Message}"); return ValueTask.FromResult(2); }
        catch (InvalidDataException ex) { Console.Error.WriteLine($"5 Integrity: {ex.Message}"); return ValueTask.FromResult(5); }
        catch (IOException ex) { Console.Error.WriteLine($"3 Backend: {ex.Message}"); return ValueTask.FromResult(3); }
    }

    private static int Execute(string[] args)
    {
        switch (args[0])
        {
            case "parse-address":
                var address = RepositoryAddress.Parse(args.ElementAtOrDefault(1) ?? throw new FormatException("address is required.")); Console.WriteLine(JsonSerializer.Serialize(address)); return 0;
            case "select":
                var selection = SelectionParser.Parse(args.Skip(1).Where(x => x.StartsWith("--select=", StringComparison.Ordinal)).Select(x => x[9..])); Console.WriteLine(selection.ToCanonicalString()); return 0;
            case "verify-path":
                if (!VirtualPath.TryCreate(args.ElementAtOrDefault(1), out var path, out var error)) { Console.Error.WriteLine($"PKG012 Error: {error}"); return 1; } Console.WriteLine(path.Value); return 0;
            case "help": PrintHelp(); return 0;
            default:
                Console.Error.WriteLine($"2 Usage: unknown command '{args[0]}'. Use --help."); return 2;
        }
    }

    private static void PrintHelp() => Console.WriteLine("4sup — content-addressed variant updater\n\nCommands: install, update, switch, rollback, plan, status, verify, explain, pkg, release, channel, gc, mirror, prune, verify-repo\n\nDevelopment helpers: parse-address <address>, select --select=axis=value, verify-path <path>\n\nExit codes: 0 success, 1 validation, 2 usage, 3 backend, 4 precondition, 5 integrity, 6 concurrency, 7 cancelled.");
}

public sealed record RepositoryAddress(string Backend, Uri BaseUri, string ProductId, string ReleaseRef)
{
    public static RepositoryAddress Parse(string value)
    {
        var at = value.LastIndexOf('@'); if (at <= 0 || at == value.Length - 1) throw new FormatException("address must end in @channel or @release:id.");
        var coordinate = value[(at + 1)..]; var release = coordinate.StartsWith("release:", StringComparison.Ordinal) ? coordinate : "channel:" + coordinate;
        var left = value[..at]; string backend; Uri baseUri; string product;
        if (Uri.TryCreate(left, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https" or "s3" or "ftp" or "file") { backend = absolute.Scheme; var path = absolute.AbsolutePath.Trim('/'); product = path.Split('/').LastOrDefault() ?? throw new FormatException("product id is missing."); baseUri = new UriBuilder(absolute) { Path = absolute.AbsolutePath[..(absolute.AbsolutePath.LastIndexOf(product, StringComparison.Ordinal))] }.Uri; }
        else
        {
            var normalized = left.Replace('\\', '/');
            var slash = normalized.LastIndexOf('/');
            product = slash > 0 ? normalized[(slash + 1)..] : normalized;
            if (normalized.Length >= 3 && char.IsLetter(normalized[0]) && normalized[1] == ':' && normalized[2] == '/')
            {
                backend = "file";
                var directory = normalized[..slash].TrimEnd('/');
                baseUri = new Uri("file:///" + directory, UriKind.Absolute);
            }
            else if (normalized.StartsWith("/", StringComparison.Ordinal))
            {
                backend = "file";
                baseUri = new Uri("file://" + normalized[..slash].TrimEnd('/'), UriKind.Absolute);
            }
            else
            {
                backend = slash > 0 ? normalized[..slash] : "local";
                baseUri = new Uri("file:///");
            }
        }
        if (!Identifier.IsValid(product)) throw new FormatException("product id is invalid."); return new RepositoryAddress(backend, baseUri, product, release);
    }
}

public static class SelectionParser
{
    public static VariantSelection Parse(IEnumerable<string> values)
    {
        var builder = ImmutableSortedDictionary.CreateBuilder<string, ImmutableSortedSet<string>>(StringComparer.Ordinal);
        foreach (var item in values)
        {
            var split = item.IndexOf('='); if (split <= 0 || split == item.Length - 1) throw new FormatException("--select must use axis=value.");
            var axis = item[..split]; var value = item[(split + 1)..]; if (!Identifier.IsValid(axis, "axis", out var error) || !Identifier.IsValid(value, "value", out error)) throw new FormatException(error);
            builder[axis] = builder.TryGetValue(axis, out var existing) ? existing.Add(value) : ImmutableSortedSet.Create(StringComparer.Ordinal, value);
        }
        return new VariantSelection { Axes = builder.ToImmutable() };
    }
}
