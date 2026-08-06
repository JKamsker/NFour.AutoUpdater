using FourSaas.AutoUpdater.Core;
using FourSaas.AutoUpdater.Client;
using FourSaas.AutoUpdater.Repository;
using FourSaas.AutoUpdater.Publishing;
using FourSaas.AutoUpdater.Storage;
using FourSaas.AutoUpdater.Storage.Http;
using FourSaas.AutoUpdater.Storage.Local;
using FourSaas.AutoUpdater.Storage.S3;
using FourSaas.AutoUpdater.Storage.Ftp;
using FourSaas.AutoUpdater.Storage.Brokering;
using System.Net;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FourSaas.AutoUpdater.Cli;

public static class CliApplication
{
    public static async ValueTask<int> RunAsync(string[] args)
    {
        if (GetOptionValue(args, "--config") is { } configPath) Environment.SetEnvironmentVariable("FOURSUP_CONFIG_PATH", Path.GetFullPath(configPath));
        if (GetOptionValue(args, "--contentRoot") is { } contentRoot) Environment.SetEnvironmentVariable("FOURSUP_CONTENT_ROOT", Path.GetFullPath(contentRoot));
        if (args.Contains("--insecure-transport", StringComparer.Ordinal)) Environment.SetEnvironmentVariable("FOURSUP_INSECURE_TRANSPORT", "1");
        if (args.Length == 0 || args.Contains("--help", StringComparer.Ordinal) || args.Contains("-h", StringComparer.Ordinal)) { PrintHelp(); return 0; }
        if (args.Contains("--version", StringComparer.Ordinal)) { Console.WriteLine("4sup 1.0.0"); return 0; }
        try
        {
            return await Execute(args).ConfigureAwait(false);
        }
        catch (FormatException ex) { Console.Error.WriteLine($"2 Usage: {ex.Message}"); return 2; }
        catch (InvalidDataException ex) { Console.Error.WriteLine($"5 Integrity: {ex.Message}"); return 5; }
        catch (CryptographicException ex) { Console.Error.WriteLine($"5 Integrity: {ex.Message}"); return 5; }
        catch (ApplyPreconditionException ex) { Console.Error.WriteLine($"4 Precondition: {ex.Message}"); return 4; }
        catch (InstallConcurrencyException ex) { Console.Error.WriteLine($"6 Concurrency: {ex.Message}"); return 6; }
        catch (OperationCanceledException) { Console.Error.WriteLine("7 Cancelled"); return 7; }
        catch (UnauthorizedAccessException ex) { Console.Error.WriteLine($"4 Precondition: {ex.Message}"); return 4; }
        catch (JsonException ex) { Console.Error.WriteLine($"5 Integrity: {ex.Message}"); return 5; }
        catch (IndexOutOfRangeException ex) { Console.Error.WriteLine($"2 Usage: {ex.Message}"); return 2; }
        catch (IOException ex) { Console.Error.WriteLine($"3 Backend: {ex.Message}"); return 3; }
    }

    private static async ValueTask<int> Execute(string[] args)
    {
        switch (args[0])
        {
            case "parse-address":
                var address = RepositoryAddress.Parse(args.ElementAtOrDefault(1) ?? throw new FormatException("address is required.")); Console.WriteLine(JsonSerializer.Serialize(address)); return 0;
            case "select":
                var selectArgs = args.Skip(1).ToArray();
                if (selectArgs.Any(x => x.StartsWith("--", StringComparison.Ordinal) && !x.Equals("--select", StringComparison.Ordinal) && !x.StartsWith("--select=", StringComparison.Ordinal)))
                    throw new FormatException("select accepts only repeated --select axis=value options.");
                if (Positional(selectArgs).Any()) throw new FormatException("select accepts only repeated --select axis=value options.");
                var selection = SelectionParser.Parse(OptionValues(selectArgs, "--select")); Console.WriteLine(selection.ToCanonicalString()); return 0;
            case "verify-path":
                if (!VirtualPath.TryCreate(args.ElementAtOrDefault(1), out var path, out var error)) { Console.Error.WriteLine($"PKG012 Error: {error}"); return 1; } Console.WriteLine(path.Value); return 0;
            case "status":
                return await StatusAsync(args.Skip(1).ToArray()).ConfigureAwait(false);
            case "verify":
                return await VerifyAsync(args.Skip(1).ToArray()).ConfigureAwait(false);
            case "explain":
                return await ExplainAsync(args.Skip(1).ToArray()).ConfigureAwait(false);
            case "plan":
                return await InstallWorkflowAsync(args.Skip(1).ToArray(), apply: false, command: "plan").ConfigureAwait(false);
            case "install":
            case "update":
            case "switch":
            case "rollback":
                return await InstallWorkflowAsync(args.Skip(1).ToArray(), apply: true, command: args[0]).ConfigureAwait(false);
            case "pkg":
                return await PackageCommandAsync(args.Skip(1).ToArray()).ConfigureAwait(false);
            case "release":
                return await ReleaseCommandAsync(args.Skip(1).ToArray()).ConfigureAwait(false);
            case "channel":
                return await ChannelCommandAsync(args.Skip(1).ToArray()).ConfigureAwait(false);
            case "gc":
                return await GarbageCollectCommandAsync(args.Skip(1).ToArray()).ConfigureAwait(false);
            case "mirror":
                return await MirrorCommandAsync(args.Skip(1).ToArray()).ConfigureAwait(false);
            case "prune":
                return await GarbageCollectCommandAsync(args.Skip(1).ToArray()).ConfigureAwait(false);
            case "verify-repo":
                return await VerifyRepositoryCommandAsync(args.Skip(1).ToArray()).ConfigureAwait(false);
            case "config":
                return await ConfigurationCommandAsync(args.Skip(1).ToArray()).ConfigureAwait(false);
            case "daemon":
                return await DaemonCommandAsync(args.Skip(1).ToArray()).ConfigureAwait(false);
            case "help": PrintHelp(); return 0;
            default:
                Console.Error.WriteLine($"2 Usage: unknown command '{args[0]}'. Use --help."); return 2;
        }
    }

    private static void PrintHelp() => Console.WriteLine("4sup — content-addressed variant updater\n\nCommands: install, update, switch, rollback, plan, status, verify, explain, pkg, release, channel, gc, mirror, prune, verify-repo, config, daemon\n\nPackage helpers: pkg schema prints the slice authoring JSON Schema.\n\nTrust: first-party installs use the compiled root; self-hosted first install requires --trust-on-first-use with --trusted-key.\n\nDevelopment helpers: parse-address <address>, select --select=axis=value, verify-path <path>\n\nExit codes: 0 success, 1 validation, 2 usage, 3 backend, 4 precondition, 5 integrity, 6 concurrency, 7 cancelled.");

    private static async ValueTask<int> ConfigurationCommandAsync(string[] args)
    {
        var subcommand = args.FirstOrDefault() ?? "show";
        var loader = new ConfigurationLoader();
        if (subcommand == "show")
        {
            Console.WriteLine(JsonSerializer.Serialize(loader.Load(), new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
            return 0;
        }
        if (subcommand != "set") throw new FormatException("config requires show or set.");
        var key = args.ElementAtOrDefault(1) ?? throw new FormatException("config set requires <key> <value>.");
        var valueText = Positional(args.Skip(2)).FirstOrDefault() ?? throw new FormatException("config set requires <key> <value>.");
        JsonElement value;
        try { using var document = JsonDocument.Parse(valueText); value = document.RootElement.Clone(); }
        catch (JsonException) { value = JsonSerializer.SerializeToElement(valueText); }
        await loader.SaveAsync(key, value).ConfigureAwait(false);
        return 0;
    }

    private static async ValueTask<int> DaemonCommandAsync(string[] args)
    {
        if (!string.Equals(args.FirstOrDefault(), "run", StringComparison.Ordinal)) throw new FormatException("daemon requires run.");
        var positional = Positional(args.Skip(1)).ToArray();
        if (positional.Length is < 1 or > 2) throw new FormatException("daemon run requires <install-root> or <repository-address> <install-root>.");
        var intervalText = GetOptionValue(args, "--interval");
        var intervalSeconds = 900;
        if (intervalText is not null && (!int.TryParse(intervalText, NumberStyles.Integer, CultureInfo.InvariantCulture, out intervalSeconds) || intervalSeconds < 5 || intervalSeconds > 86400))
            throw new FormatException("--interval must be between 5 and 86400 seconds.");
        var once = args.Contains("--once", StringComparer.Ordinal);
        var command = positional.Length == 2 ? "install" : "update";
        var workflowArgs = positional.Length == 2 ? positional : [positional[0]];
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        while (true)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            try
            {
                var result = await InstallWorkflowAsync(workflowArgs, apply: true, command).ConfigureAwait(false);
                if (once) return result;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return 7; }
            catch (Exception ex) when (!once && ex is IOException or HttpRequestException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"daemon: transient update failure: {ex.Message}");
            }
            if (once) return 0;
            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), cancellation.Token).ConfigureAwait(false);
        }
    }

    private static async ValueTask<int> StatusAsync(string[] args)
    {
        var root = Positional(args).FirstOrDefault() ?? throw new FormatException("status requires an install root.");
        var document = await new InstallLedger(root).ReadAsync().ConfigureAwait(false);
        if (document is null)
        {
            Console.WriteLine(args.Contains("--json", StringComparer.Ordinal) ? "{\"installed\":false}" : "not installed");
            return 0;
        }
        if (args.Contains("--json", StringComparer.Ordinal))
        {
            Console.WriteLine(JsonSerializer.Serialize(new { installed = true, document.Lock.ProductId, document.Lock.ReleaseId, document.Lock.FileSetId, files = document.Files.Count }));
        }
        else Console.WriteLine($"{document.Lock.ProductId}@{document.Lock.ReleaseId} files={document.Files.Count} fileSet={document.Lock.FileSetId}");
        return 0;
    }

    private static async ValueTask<int> VerifyAsync(string[] args)
    {
        var root = Positional(args).FirstOrDefault() ?? throw new FormatException("verify requires an install root.");
        var ledger = new InstallLedger(root);
        LedgerDocument? document;
        try { document = await ledger.ReadAsync().ConfigureAwait(false); }
        catch (Exception) when (args.Contains("--rebuild-state", StringComparer.Ordinal)) { document = null; }
        if (args.Contains("--rebuild-state", StringComparer.Ordinal))
        {
            document = await RebuildStateAsync(root, document, args).ConfigureAwait(false);
            if (args.Contains("--repair", StringComparer.Ordinal)) return await InstallWorkflowAsync(args, apply: true, command: "update").ConfigureAwait(false);
        }
        if (args.Contains("--repair", StringComparer.Ordinal)) return await InstallWorkflowAsync(args, apply: true, command: "update").ConfigureAwait(false);
        if (document is null) throw new ApplyPreconditionException("No install ledger exists; use --rebuild-state with an explicit repository target.");
        var issues = new List<object>();
        if (args.Contains("--rehash-cas", StringComparer.Ordinal))
            await AddCasVerificationIssuesAsync(ResolveCacheRoot(), issues).ConfigureAwait(false);
        var unmanaged = document.Files.Values.Where(x => x.State == "adopted").Select(x => x.Path.Value).ToArray();
        foreach (var file in document.Files.Values.Where(x => x.State != "adopted" && x.Content is not null))
        {
            var full = Path.Combine(root, file.Path.Value.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full)) { issues.Add(new { path = file.Path.Value, issue = "missing" }); continue; }
            await using var stream = File.OpenRead(full);
            var actual = await ContentHash.ComputeAsync(stream, file.Content!.Value.Algorithm).ConfigureAwait(false);
            if (actual != file.Content.Value) issues.Add(new { path = file.Path.Value, issue = "digest-mismatch", expected = file.Content.Value.ToString(), actual = actual.ToString() });
        }
        if (args.Contains("--json", StringComparer.Ordinal)) Console.WriteLine(JsonSerializer.Serialize(new { valid = issues.Count == 0, issues, unmanaged }));
        else Console.WriteLine(issues.Count == 0 ? "valid" : $"invalid ({issues.Count} issue(s))");
        return issues.Count == 0 ? 0 : 5;
    }

    private static async ValueTask AddCasVerificationIssuesAsync(string cacheRoot, List<object> issues, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(cacheRoot))
        {
            issues.Add(new { path = cacheRoot, issue = "cas-missing" });
            return;
        }
        foreach (var path in Directory.EnumerateFiles(cacheRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Path.GetFileName(path).Contains(".tmp-", StringComparison.Ordinal)) continue;
            var relative = Path.GetRelativePath(cacheRoot, path).Replace(Path.DirectorySeparatorChar, '/');
            var parts = relative.Split('/');
            if (parts.Length != 4 || parts[1].Length != 2 || parts[2].Length != 2 || !ContentHash.TryParse(parts[0] + ":" + parts[3], out var expected))
            {
                issues.Add(new { path = relative, issue = "cas-layout" });
                continue;
            }
            var hex = parts[3].ToLowerInvariant();
            if (!string.Equals(parts[1], hex[..2], StringComparison.Ordinal) || !string.Equals(parts[2], hex[2..4], StringComparison.Ordinal))
            {
                issues.Add(new { path = relative, issue = "cas-shard" });
                continue;
            }
            await using var stream = File.OpenRead(path);
            var actual = await ContentHash.ComputeAsync(stream, expected.Algorithm, cancellationToken).ConfigureAwait(false);
            if (actual != expected) issues.Add(new { path = relative, issue = "cas-digest-mismatch", expected = expected.ToString(), actual = actual.ToString() });
        }
    }

    private static string ResolveCacheRoot()
    {
        var cacheRoot = Environment.GetEnvironmentVariable("FOURSUP_CAS_ROOT");
        if (!string.IsNullOrWhiteSpace(cacheRoot)) return Path.GetFullPath(cacheRoot);
        if (OperatingSystem.IsWindows())
        {
            var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(string.IsNullOrWhiteSpace(localData) ? Environment.CurrentDirectory : localData, "4Story", "4sup", "cas");
        }
        var cacheBase = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        if (string.IsNullOrWhiteSpace(cacheBase)) cacheBase = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        return Path.Combine(cacheBase, "4sup", "cas");
    }

    private static async ValueTask<LedgerDocument> RebuildStateAsync(string root, LedgerDocument? existing, string[] args)
    {
        var trusted = ParseTrustedKeysOrFirstParty(args);
        var repositoryTarget = GetOptionValue(args, "--repository");
        string addressText;
        VariantSelection selection;
        if (existing is not null)
        {
            var reference = existing.Lock.Channel is null ? "release:" + existing.Lock.ReleaseId : "channel:" + existing.Lock.Channel;
            addressText = repositoryTarget ?? new Uri(new Uri(existing.Lock.RepositoryUri, UriKind.Absolute), existing.Lock.ProductId + "@" + reference).ToString();
            selection = existing.Lock.Selection;
        }
        else
        {
            addressText = repositoryTarget ?? throw new FormatException("--rebuild-state requires --repository <repo/product@channel|release:id> when the ledger is missing.");
            selection = SelectionParser.Parse(OptionValues(args, "--select"));
        }
        var address = RepositoryAddress.Parse(ResolveAlias(addressText));
        await using var store = CreateReadStore(address);
        var (descriptor, layout) = await RepositoryFactory.LoadDescriptorAsync(store).ConfigureAwait(false);
        descriptor.ValidateAgainst(address.BaseUri);
        var reader = new RepositoryReader(store, descriptor);
        var verified = address.ReleaseRef.StartsWith("channel:", StringComparison.Ordinal)
            ? await reader.ReadChannelReleaseAsync(address.ProductId, address.ReleaseRef[8..], trusted).ConfigureAwait(false)
            : await reader.ReadReleaseAsync(address.ProductId, address.ReleaseRef[8..], trusted).ConfigureAwait(false);
        var resolution = new VariantResolver().Resolve(verified.Lock, selection);
        if (!resolution.IsValid) { PrintDiagnostics(resolution.Diagnostics); throw new ApplyPreconditionException("The requested release cannot be resolved for the installed selection."); }
        var composed = await new FileSetComposer().ComposeAsync(new StaticRepository(store, descriptor), resolution).ConfigureAwait(false);
        if (!composed.IsValid) { PrintDiagnostics(composed.Diagnostics); throw new ApplyPreconditionException("The requested release cannot be composed for the installed selection."); }
        var paths = composed.Files.Where(x => x.Value.Kind == FileEntryKind.File).Select(x => x.Key);
        var observed = await new LocalTreeScanner().ScanAsync(root, paths, paths, HashPolicy.Always).ConfigureAwait(false);
        var files = ImmutableDictionary.CreateBuilder<VirtualPath, InstalledFile>();
        foreach (var (path, wanted) in composed.Files.Where(x => x.Value.Kind == FileEntryKind.File))
        {
            if (!observed.Entries.TryGetValue(path, out var actual) || !actual.Exists || actual.Kind != ObservedKind.File || actual.Hash is null)
                throw new ApplyPreconditionException($"Cannot rebuild state because '{path}' is missing or is not a regular file.");
            if (wanted.Policy != FileInstallPolicy.Preserve && actual.Hash != wanted.Content)
                throw new CryptographicException($"Cannot rebuild state because '{path}' does not match the resolved release.");
            var adopted = wanted.Policy == FileInstallPolicy.Preserve && actual.Hash != wanted.Content;
            files[path] = new InstalledFile(path, adopted ? null : wanted.Content, wanted.Size, wanted.Owner, wanted.Policy, actual.Size, actual.MtimeUnixSeconds, adopted ? "adopted" : "managed", adopted ? actual.Hash : null);
        }
        var rebuiltLock = new InstallLock
        {
            RepositoryUri = address.BaseUri.ToString(), ProductId = verified.Lock.ProductId,
            Channel = address.ReleaseRef.StartsWith("channel:", StringComparison.Ordinal) ? address.ReleaseRef[8..] : null,
            ReleaseId = verified.Lock.ReleaseId, ReleaseSequence = verified.Lock.Sequence,
            ReleaseDigest = ContentHash.Compute(verified.EnvelopeBytes), Selection = selection, SelectionId = selection.SelectionId,
            FileSetId = composed.FileSetId, LastChannelSequence = verified.Pointer?.ChannelSequence ?? existing?.Lock.LastChannelSequence ?? 0,
            KeySequence = existing?.Lock.KeySequence ?? 0, KeyManifestDigest = existing?.Lock.KeyManifestDigest, RevocationSequence = existing?.Lock.RevocationSequence ?? 0, RevocationDigest = existing?.Lock.RevocationDigest,
            TrustedKeyIds = trusted.Keys.OrderBy(x => x, StringComparer.Ordinal).ToImmutableArray(),
            TrustedKeys = trusted.ToImmutableDictionary(x => x.Key, x => Base64Url.Encode(x.Value), StringComparer.Ordinal),
            AppliedAt = DateTimeOffset.UtcNow
        };
        await new InstallLedger(root).CommitAsync(rebuiltLock, files.ToImmutable()).ConfigureAwait(false);
        Console.WriteLine(JsonSerializer.Serialize(new { rebuilt = true, release = rebuiltLock.ReleaseId, fileSetId = rebuiltLock.FileSetId.ToString(), files = files.Count }));
        return new LedgerDocument(rebuiltLock, files.ToImmutable());
    }

    private static async ValueTask<int> ExplainAsync(string[] args)
    {
        var root = Positional(args).FirstOrDefault() ?? throw new FormatException("explain requires an install root.");
        var pathText = GetOptionValue(args, "--path") ?? throw new FormatException("explain requires --path <virtual-path>.");
        if (!VirtualPath.TryCreate(pathText, out var path, out var error)) throw new FormatException(error);
        var document = await new InstallLedger(root).ReadAsync().ConfigureAwait(false) ?? throw new ApplyPreconditionException("No install ledger exists.");
        if (!document.Files.TryGetValue(path, out var file))
        {
            Console.WriteLine(JsonSerializer.Serialize(new { path = path.Value, owner = (string?)null, state = "not-managed" }));
            return 0;
        }
        var full = Path.Combine(root, path.Value.Replace('/', Path.DirectorySeparatorChar));
        var actual = File.Exists(full) ? await HashFileAsync(full).ConfigureAwait(false) : (ContentHash?)null;
        Console.WriteLine(JsonSerializer.Serialize(new { path = path.Value, owner = file.Owner.Value, policy = file.Policy, state = file.State, expected = file.Content?.ToString(), actual = actual?.ToString(), matches = file.Content is null ? (bool?)null : actual == file.Content }));
        return 0;
    }

    private static async ValueTask<ContentHash?> HashFileAsync(string path)
    {
        await using var stream = File.OpenRead(path); return await ContentHash.ComputeAsync(stream).ConfigureAwait(false);
    }

    private static IEnumerable<string> Positional(IEnumerable<string> args)
    {
        var values = args.ToArray();
        for (var index = 0; index < values.Length; index++)
        {
            var value = values[index];
            if (value.StartsWith("--", StringComparison.Ordinal))
            {
                if (!value.Contains('=') && OptionTakesValue(value) && index + 1 < values.Length) index++;
                continue;
            }
            yield return value;
        }
    }

    private static IEnumerable<string> OptionValues(IEnumerable<string> args, string option)
    {
        var values = args.ToArray();
        for (var index = 0; index < values.Length; index++)
        {
            if (values[index].StartsWith(option + "=", StringComparison.Ordinal))
            {
                yield return values[index][(option.Length + 1)..];
            }
            else if (string.Equals(values[index], option, StringComparison.Ordinal) && index + 1 < values.Length)
            {
                yield return values[++index];
            }
        }
    }

    private static bool OptionTakesValue(string option) => option is "--select" or "--trusted-key" or "--sign" or "--path" or "--to" or "--repository" or "--release" or "--rules" or "--keep-releases" or "--min-age" or "--effect" or "--reason" or "--config" or "--contentRoot" or "--api" or "--token" or "--repository-id" or "--pinned-root-key" or "--minimum-client-version" or "--write-target" or "--interval";

    private static async ValueTask<int> InstallWorkflowAsync(string[] args, bool apply, string command)
    {
        var positional = Positional(args).ToArray();
        var installRoot = command == "install" ? positional.ElementAtOrDefault(1) : positional.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(installRoot)) throw new FormatException(command == "install" ? "install requires <repository-address> <install-root>." : $"{command} requires <install-root>.");
        var ledger = new InstallLedger(installRoot);
        var previous = await ledger.ReadAsync().ConfigureAwait(false);
        LedgerDocument? historicalRollback = null;
        string addressText;
        if (command == "install") addressText = ResolveAlias(positional[0]);
        else
        {
            if (previous is null) throw new ApplyPreconditionException($"No installed ledger exists; {command} requires an existing installation.");
            var target = command == "rollback" ? GetOptionValue(args, "--to") : null;
            if (command == "rollback" && string.IsNullOrWhiteSpace(target)) throw new FormatException("rollback requires --to <release-id>.");
            if (command == "rollback")
            {
                var history = await ledger.ReadHistoryAsync().ConfigureAwait(false);
                historicalRollback = history.LastOrDefault(x => string.Equals(x.Lock.ReleaseId, target, StringComparison.Ordinal));
                if (historicalRollback is null)
                    throw new ApplyPreconditionException($"Release '{target}' is not present in this install's local history; local rollback will not fetch an unrelated release.");
            }
            var reference = target is null ? (previous.Lock.Channel is null ? "release:" + previous.Lock.ReleaseId : previous.Lock.Channel) : "release:" + target;
            addressText = new Uri(new Uri(previous.Lock.RepositoryUri, UriKind.Absolute), previous.Lock.ProductId + "@" + reference).ToString();
        }
        var address = RepositoryAddress.Parse(ResolveAlias(addressText));
        if (previous is not null && command == "install" && !string.Equals(previous.Lock.RepositoryUri, address.BaseUri.ToString(), StringComparison.OrdinalIgnoreCase)
            && !args.Contains("--yes", StringComparer.Ordinal))
            throw new ApplyPreconditionException("The install root is already pinned to a different repository; pass --yes to explicitly rebind it.");
        var suppliedTrust = ParseTrustedKeys(args).ToDictionary(x => x.Key, x => x.Value.ToArray(), StringComparer.Ordinal);
        var compiledRoots = CompiledTrustRoots.FirstParty;
        var trusted = suppliedTrust.ToDictionary(x => x.Key, x => x.Value.ToArray(), StringComparer.Ordinal);
        if (previous is null)
        {
            if (suppliedTrust.TryGetValue(CompiledTrustRoots.FirstPartyKeyId, out var suppliedRoot))
            {
                var compiledRoot = compiledRoots[CompiledTrustRoots.FirstPartyKeyId];
                if (!CryptographicOperations.FixedTimeEquals(suppliedRoot, compiledRoot))
                    throw new CryptographicException($"The supplied first-party root '{CompiledTrustRoots.FirstPartyKeyId}' differs from the key compiled into this client.");
                if (suppliedTrust.Keys.Any(x => !string.Equals(x, CompiledTrustRoots.FirstPartyKeyId, StringComparison.Ordinal)))
                    throw new CryptographicException("A first-party bootstrap may contain only the compiled trust root.");
                trusted = new Dictionary<string, byte[]>(compiledRoots, StringComparer.Ordinal);
            }
            else if (suppliedTrust.Count == 0)
            {
                trusted = new Dictionary<string, byte[]>(compiledRoots, StringComparer.Ordinal);
            }
            else if (!args.Contains("--trust-on-first-use", StringComparer.Ordinal))
            {
                throw new ApplyPreconditionException("This repository is not signed by the compiled first-party root. First install requires explicit --trust-on-first-use consent.");
            }
            else
            {
                Console.Error.WriteLine("TOFU consent recorded for the supplied repository trust key(s); they will be pinned in the install ledger.");
            }
        }
        if (previous?.Lock.TrustedKeys.Count > 0)
        {
            var persisted = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var pair in previous.Lock.TrustedKeys)
            {
                var key = Base64Url.Decode(pair.Value);
                if (key.Length != 32) throw new CryptographicException($"Persisted trust key '{pair.Key}' is not a 32-byte Ed25519 public key.");
                persisted[pair.Key] = key;
                if (trusted.TryGetValue(pair.Key, out var supplied) && !CryptographicOperations.FixedTimeEquals(supplied, key))
                    throw new CryptographicException($"Trusted key '{pair.Key}' differs from the key pinned in the install ledger.");
            }
            if (trusted.Keys.Any(keyId => !persisted.ContainsKey(keyId)))
                throw new CryptographicException("The supplied trust set contains a key that is not pinned in the install ledger.");
            if (trusted.Count == 0) trusted = persisted;
            if (persisted.TryGetValue(CompiledTrustRoots.FirstPartyKeyId, out var persistedRoot))
            {
                if (!CryptographicOperations.FixedTimeEquals(persistedRoot, compiledRoots[CompiledTrustRoots.FirstPartyKeyId]))
                    throw new CryptographicException($"The persisted first-party root '{CompiledTrustRoots.FirstPartyKeyId}' differs from the key compiled into this client.");
                trusted[CompiledTrustRoots.FirstPartyKeyId] = compiledRoots[CompiledTrustRoots.FirstPartyKeyId].ToArray();
            }
        }
        if (previous is not null && previous.Lock.TrustedKeys.ContainsKey(CompiledTrustRoots.FirstPartyKeyId)
            && !trusted.ContainsKey(CompiledTrustRoots.FirstPartyKeyId))
            throw new CryptographicException($"The persisted first-party root '{CompiledTrustRoots.FirstPartyKeyId}' is missing from the current trust set.");
        if (trusted.Count == 0) throw new CryptographicException("No trusted Ed25519 public key was supplied; use --trusted-key=<keyId>:<base64url>.");
        var trustBeforeManifest = trusted.ToDictionary(x => x.Key, x => x.Value.ToArray(), StringComparer.Ordinal);
        var selectionValues = OptionValues(args, "--select").ToArray();
        var selection = historicalRollback is not null
            ? historicalRollback.Lock.Selection
            : selectionValues.Length == 0 && previous is not null ? previous.Lock.Selection : SelectionParser.Parse(selectionValues);
        await using var store = CreateReadStore(address);
        var (descriptor, layout) = await RepositoryFactory.LoadDescriptorAsync(store).ConfigureAwait(false);
        descriptor.ValidateAgainst(address.BaseUri);
        var reader = new RepositoryReader(store, descriptor);
        var keySequence = previous?.Lock.KeySequence ?? 0;
        ContentHash? currentKeysDigest = previous?.Lock.KeyManifestDigest;
        IReadOnlyDictionary<string, VerificationKey>? validityKeys = null;
        try
        {
            // A first-party install keeps the compiled root pinned for the entire
            // lifetime of the ledger. Self-hosted TOFU installs deliberately have
            // no implicit root pin and must opt into their own trust policy.
            var pinnedRoot = GetOptionValue(args, "--pinned-root-key")
                ?? (trusted.ContainsKey(CompiledTrustRoots.FirstPartyKeyId) ? CompiledTrustRoots.FirstPartyKeyId : null);
            var currentKeys = await reader.ReadKeyManifestAsync(trusted, keySequence, previous?.Lock.KeyManifestDigest, pinnedRoot, DateTimeOffset.UtcNow, cancellationToken: default).ConfigureAwait(false);
            if (previous is not null && !previous.Lock.TrustedKeys.ContainsKey(CompiledTrustRoots.FirstPartyKeyId)
                && currentKeys.Manifest.KeySequence > previous.Lock.KeySequence
                && !ConfirmTrustChange(args, currentKeys.Manifest.KeySequence))
                throw new ApplyPreconditionException("This self-hosted repository changed its trust manifest; explicit confirmation is required.");
            validityKeys = currentKeys.Manifest.Keys
                .Where(x => !currentKeys.Manifest.RevokedKeyIds.Contains(x.KeyId, StringComparer.Ordinal))
                .ToDictionary(x => x.KeyId, x => new VerificationKey(Base64Url.Decode(x.PublicKey), x.NotBefore, x.NotAfter), StringComparer.Ordinal);
            trusted = currentKeys.Manifest.Keys
                .Where(x => !currentKeys.Manifest.RevokedKeyIds.Contains(x.KeyId, StringComparer.Ordinal))
                .ToDictionary(x => x.KeyId, x => Base64Url.Decode(x.PublicKey), StringComparer.Ordinal);
            keySequence = currentKeys.Manifest.KeySequence;
            currentKeysDigest = ContentHash.Compute(currentKeys.EnvelopeBytes);
        }
        catch (FileNotFoundException) { }
        var verified = address.ReleaseRef.StartsWith("channel:", StringComparison.Ordinal)
            ? await reader.ReadChannelReleaseAsync(address.ProductId, address.ReleaseRef[8..], trusted).ConfigureAwait(false)
            : await reader.ReadReleaseAsync(address.ProductId, address.ReleaseRef[8..], trusted).ConfigureAwait(false);
        if (historicalRollback is not null)
        {
            if (ContentHash.Compute(verified.EnvelopeBytes) != historicalRollback.Lock.ReleaseDigest || verified.Lock.ProductId != historicalRollback.Lock.ProductId || verified.Lock.ReleaseId != historicalRollback.Lock.ReleaseId)
                throw new CryptographicException($"Repository release '{verified.Lock.ReleaseId}' does not match the exact release envelope recorded in local rollback history.");
        }
        var revocations = await reader.ReadRevocationsAsync(address.ProductId, trusted, previous?.Lock.RevocationSequence, previous?.Lock.RevocationDigest).ConfigureAwait(false);
        if (previous?.Lock.RevocationSequence > 0 && revocations is null)
            throw new ApplyPreconditionException("The repository revocation document is missing; refusing to forget previously accepted revocations.");
        if (previous is not null && revocations is not null && previous.Lock.KnownRevocations.Any(oldEntry => !revocations.Document.Entries.Contains(oldEntry)))
            throw new ApplyPreconditionException("The repository revocation document removed a previously accepted entry.");
        if (validityKeys is not null)
        {
            EnsureDocumentSigner(verified.Envelope, validityKeys, verified.Lock.CreatedAt);
            if (verified.Pointer is { } channelPointer && verified.PointerEnvelope is { } pointerEnvelope) EnsureDocumentSigner(pointerEnvelope, validityKeys, channelPointer.UpdatedAt);
            if (revocations is not null)
            {
                EnsureDocumentSigner(revocations.Envelope, validityKeys, revocations.Document.UpdatedAt);
            }
        }
        if (revocations is not null && (revocations.Document.UpdatedAt < DateTimeOffset.UtcNow.Subtract(TimeSpan.FromDays(7)) || revocations.Document.UpdatedAt > DateTimeOffset.UtcNow.Add(TimeSpan.FromDays(7))))
            throw new ApplyPreconditionException("The revocation document is outside the freshness bound; refusing to make an install decision without a fresh control document.");
        var revocation = revocations?.Document.Entries.LastOrDefault(x => string.Equals(x.ReleaseId, verified.Lock.ReleaseId, StringComparison.Ordinal) && string.Equals(x.Action, "yank", StringComparison.Ordinal));
        var installedRevocation = previous is null
            ? null
            : revocations?.Document.Entries.LastOrDefault(x => string.Equals(x.ReleaseId, previous.Lock.ReleaseId, StringComparison.Ordinal) && string.Equals(x.Action, "yank", StringComparison.Ordinal));
        if (apply && command != "rollback" && installedRevocation?.Effect == RevocationEffect.ForceMove && string.Equals(previous?.Lock.ReleaseId, verified.Lock.ReleaseId, StringComparison.Ordinal))
        {
            if (!ConfirmForceMove(args, previous, verified.Lock.ReleaseId))
                throw new ApplyPreconditionException($"Release '{verified.Lock.ReleaseId}' is force-move yanked; waiting for the channel head to advance to a replacement release.");
            throw new ApplyPreconditionException($"Release '{verified.Lock.ReleaseId}' is force-move yanked; the channel still points at the yanked release, so no safe replacement is available yet.");
        }
        if (apply && command != "rollback" && installedRevocation?.Effect == RevocationEffect.ForceMove && !string.Equals(previous?.Lock.ReleaseId, verified.Lock.ReleaseId, StringComparison.Ordinal))
        {
            if (!ConfirmForceMove(args, previous, verified.Lock.ReleaseId))
                throw new ApplyPreconditionException($"Installed release '{previous!.Lock.ReleaseId}' is force-move yanked; confirmation is required before moving to channel head '{verified.Lock.ReleaseId}'.");
            Console.Error.WriteLine($"warning: installed release '{previous!.Lock.ReleaseId}' is force-move yanked; moving to channel head '{verified.Lock.ReleaseId}'.");
        }
        if (apply && command != "rollback" && revocation is not null)
        {
            var isRepair = args.Contains("--repair", StringComparer.Ordinal);
            var blocked = revocation.Effect switch
            {
                // A normal install/update/switch is an install decision.  A plain
                // block-install does not brick an already-installed release during
                // an explicit repair; block-repair and force-move do.
                RevocationEffect.BlockInstall => !isRepair,
                RevocationEffect.BlockRepair => true,
                RevocationEffect.ForceMove => true,
                _ => true
            };
            if (blocked)
                throw new ApplyPreconditionException($"Release '{verified.Lock.ReleaseId}' is yanked ({revocation.Effect.ToString().ToLowerInvariant().Replace("blockinstall", "block-install", StringComparison.Ordinal).Replace("blockrepair", "block-repair", StringComparison.Ordinal).Replace("forcemove", "force-move", StringComparison.Ordinal)}): {revocation.Reason}");
        }
        if (verified.Pointer is { } pointer)
        {
            var previousSequence = previous?.Lock.LastChannelSequence ?? 0;
            var accepted = ControlDocumentPolicy.AcceptChannel(pointer, verified.Lock.ProductId, pointer.Channel, previousSequence, DateTimeOffset.UtcNow, TimeSpan.FromDays(7), previous?.Lock.ReleaseSequence, typeof(CliApplication).Assembly.GetName().Version ?? new Version(1, 0, 0));
            if (!accepted.Accepted) throw new ApplyPreconditionException(accepted.Error ?? "Channel pointer was rejected by freshness or replay policy.");
            if (accepted.IsRollback) Console.Error.WriteLine($"warning: channel {pointer.Channel} moved from release sequence {previous?.Lock.ReleaseSequence} to older sequence {pointer.ReleaseSequence} by signed rollback.");
        }
        var clientVersion = typeof(CliApplication).Assembly.GetName().Version ?? new Version(1, 0, 0);
        var minimumClientVersions = new List<Version>();
        foreach (var minimumText in new[] { descriptor.MinimumClientVersion, verified.Pointer?.MinimumClientVersion })
        {
            if (string.IsNullOrWhiteSpace(minimumText)) continue;
            if (!Version.TryParse(minimumText, out var minimum)) throw new ApplyPreconditionException($"Repository minimumClientVersion '{minimumText}' is invalid.");
            minimumClientVersions.Add(minimum);
        }
        var minimumClientVersion = minimumClientVersions.Count == 0 ? null : minimumClientVersions.Max();
        if (minimumClientVersion is not null && clientVersion < minimumClientVersion)
            throw new ApplyPreconditionException($"This updater ({clientVersion}) is older than the repository minimum client version ({minimumClientVersion}).");
        var resolution = new VariantResolver().Resolve(verified.Lock, selection);
        if (!resolution.IsValid)
        {
            PrintDiagnostics(resolution.Diagnostics);
            return 1;
        }
        var repository = new StaticRepository(store, descriptor);
        var composed = await new FileSetComposer().ComposeAsync(repository, resolution).ConfigureAwait(false);
        if (!composed.IsValid)
        {
            PrintDiagnostics(composed.Diagnostics);
            return 1;
        }
        if (historicalRollback is not null && composed.FileSetId != historicalRollback.Lock.FileSetId)
            throw new CryptographicException($"Repository release '{verified.Lock.ReleaseId}' does not reproduce the historical file set recorded for local rollback.");
        var current = previous?.Files;
        var observedPaths = (current?.Keys ?? Enumerable.Empty<VirtualPath>()).Concat(composed.Files.Keys);
        var observed = await new LocalTreeScanner().ScanAsync(installRoot, observedPaths, composed.Files.Keys, HashPolicy.Changed, current).ConfigureAwait(false);
        var plan = new InstallPlanner().Plan(composed, current, observed);
        if (!apply || args.Contains("--dry-run", StringComparer.Ordinal))
        {
            PrintPlan(plan, args.Contains("--json", StringComparer.Ordinal));
            return 0;
        }
        var installLock = new InstallLock
        {
            RepositoryUri = address.BaseUri.ToString(), ProductId = verified.Lock.ProductId, Channel = address.ReleaseRef.StartsWith("channel:", StringComparison.Ordinal) ? address.ReleaseRef[8..] : null,
            ReleaseId = verified.Lock.ReleaseId, ReleaseSequence = verified.Lock.Sequence, ReleaseDigest = ContentHash.Compute(verified.EnvelopeBytes), Selection = resolution.Selection, SelectionId = resolution.Selection.SelectionId,
            FileSetId = composed.FileSetId, LastChannelSequence = verified.Pointer?.ChannelSequence ?? previous?.Lock.LastChannelSequence ?? 0,
            KeySequence = keySequence, KeyManifestDigest = currentKeysDigest, RevocationSequence = revocations?.Document.RevocationSequence ?? previous?.Lock.RevocationSequence ?? 0, RevocationDigest = revocations is null ? previous?.Lock.RevocationDigest : ContentHash.Compute(revocations.EnvelopeBytes),
            KnownRevocations = revocations?.Document.Entries ?? previous?.Lock.KnownRevocations ?? [],
            TrustedKeyIds = trusted.Keys.OrderBy(x => x, StringComparer.Ordinal).ToImmutableArray(),
            TrustedKeys = trusted.ToImmutableDictionary(x => x.Key, x => Base64Url.Encode(x.Value), StringComparer.Ordinal),
            AppliedAt = DateTimeOffset.UtcNow
        };
        var cacheRoot = Environment.GetEnvironmentVariable("FOURSUP_CAS_ROOT");
        if (string.IsNullOrWhiteSpace(cacheRoot))
        {
            if (OperatingSystem.IsWindows())
            {
                var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                cacheRoot = Path.Combine(string.IsNullOrWhiteSpace(localData) ? Environment.CurrentDirectory : localData, "4Story", "4sup", "cas");
            }
            else
            {
                var cacheBase = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
                if (string.IsNullOrWhiteSpace(cacheBase)) cacheBase = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
                cacheRoot = Path.Combine(cacheBase, "4sup", "cas");
            }
        }
        var protectedCacheEntries = previous?.Files.Values.Where(x => x.Content is not null).Select(x => x.Content!.Value).ToHashSet() ?? new HashSet<ContentHash>();
        var mirrors = new[] { (IReadableObjectStore)store }
            .Concat(descriptor.BlobBaseUrls
            .Where(x => !string.Equals(x.ToString().TrimEnd('/'), address.BaseUri.ToString().TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            .Select(x => (IReadableObjectStore)new HttpObjectStore(x)))
            .ToArray();
        await new InstallApplier().ApplyAsync(installRoot, plan, composed, installLock, store, layout, ledger, mirrors: mirrors, cache: new LocalContentCache(cacheRoot), protectedCacheEntries: protectedCacheEntries, materializationProfile: args.Contains("--immutable-install", StringComparer.Ordinal) ? InstallMaterializationProfile.ImmutableInstall : InstallMaterializationProfile.CopyDefault, preconditions: new ApplyPreconditions { MinimumInstalledReleaseSequence = verified.Lock.MinimumInstalledRelease, CurrentInstalledReleaseSequence = previous?.Lock.ReleaseSequence, ClientVersion = clientVersion, MinimumClientVersion = minimumClientVersion }).ConfigureAwait(false);
        Console.WriteLine(args.Contains("--json", StringComparer.Ordinal) ? JsonSerializer.Serialize(new { applied = true, release = verified.Lock.ReleaseId, fileSetId = composed.FileSetId.ToString() }) : $"applied {verified.Lock.ReleaseId} ({composed.FileSetId})");
        return 0;
    }

    private static bool ConfirmForceMove(string[] args, LedgerDocument? previous, string targetRelease)
    {
        if (args.Contains("--yes", StringComparer.Ordinal)) return true;
        if (Console.IsInputRedirected) return false;
        Console.Error.Write($"Release '{previous?.Lock.ReleaseId ?? targetRelease}' is force-move yanked. Confirm moving to '{targetRelease}' [y/N]: ");
        var answer = Console.ReadLine()?.Trim();
        return string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) || string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ConfirmTrustChange(string[] args, long sequence)
    {
        if (args.Contains("--yes", StringComparer.Ordinal)) return true;
        if (Console.IsInputRedirected) return false;
        Console.Error.Write($"The repository trust manifest advanced to sequence {sequence}. Accept this self-hosted key change [y/N]: ");
        var answer = Console.ReadLine()?.Trim();
        return string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) || string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static async ValueTask<int> PackageCommandAsync(string[] args)
    {
        var subcommand = args.FirstOrDefault() ?? throw new FormatException("pkg requires slice, list, or show.");
        if (subcommand == "publish") return await PublishPackageCommandAsync(args.Skip(1).ToArray()).ConfigureAwait(false);
        if (subcommand == "schema") { Console.WriteLine(SliceRulesYaml.JsonSchema); return 0; }
        if (subcommand == "slice")
        {
            var rulesPath = GetOptionValue(args, "--rules") ?? throw new FormatException("pkg slice requires --rules <slice.yaml>.");
            var rules = SliceRulesYaml.Parse(await File.ReadAllTextAsync(rulesPath).ConfigureAwait(false));
            if (GetOptionValue(args, "--from") is { } sourceOverride) rules = rules with { Source = Path.GetFullPath(sourceOverride) };
            if (!long.TryParse(GetOptionValue(args, "--seq") ?? "0", NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence) || sequence < 0) throw new FormatException("--seq must be a non-negative integer.");
            var result = await new SliceEngine().SliceAsync(rules).ConfigureAwait(false);
            PrintDiagnostics(result.Diagnostics);
            if (!result.IsValid) return 1;
            Console.WriteLine(JsonSerializer.Serialize(new { sequence, source = rules.Source, packages = result.Packages.Select(x => new { id = x.Id.Value, files = x.Files.Length }) }));
            return 0;
        }
        var target = ParseTarget(args.ElementAtOrDefault(1) ?? throw new FormatException($"pkg {subcommand} requires a repository target."), "channel", "live");
        await using var store = CreateReadStore(target.Address);
        var (descriptor, layout) = await RepositoryFactory.LoadDescriptorAsync(store).ConfigureAwait(false);
        descriptor.ValidateAgainst(target.Address.BaseUri);
        if (!PackageId.TryCreate(target.PackageOrProduct, out var packageId)) throw new FormatException("Package id is invalid.");
        if (subcommand == "list")
        {
            var keys = new List<string>();
            foreach (var indexVersion in await ReadPackageIndexVersionsAsync(store, layout, packageId).ConfigureAwait(false))
                keys.Add(indexVersion.ManifestPath);
            Console.WriteLine(JsonSerializer.Serialize(new { package = packageId.Value, manifests = keys.OrderBy(x => x, StringComparer.Ordinal).ToArray() }));
            return 0;
        }
        if (subcommand != "show") throw new FormatException($"Unknown pkg command '{subcommand}'.");
        var version = target.ReleaseOrVersion ?? throw new FormatException("pkg show requires package@version.");
        var manifest = await ReadManifestByLabelAsync(store, layout, packageId, version).ConfigureAwait(false) ?? throw new FileNotFoundException($"Package '{packageId}@{version}' was not found.");
        var output = new { manifest.Id, manifest.Version, manifest.FileCount, manifest.InstallSize, manifest.DownloadSize, files = args.Contains("--files", StringComparer.Ordinal) ? await ReadManifestFilesAsync(store, descriptor, manifest).ConfigureAwait(false) : null };
        Console.WriteLine(JsonSerializer.Serialize(output));
        return 0;
    }

    private static async ValueTask<int> PublishPackageCommandAsync(string[] args)
    {
        var coordinate = Positional(args).FirstOrDefault() ?? throw new FormatException("pkg publish requires <package>@<version>.");
        var at = coordinate.LastIndexOf('@');
        if (at <= 0 || at == coordinate.Length - 1 || !PackageId.TryCreate(coordinate[..at], out var packageId)) throw new FormatException("Package coordinate must use package-id@version.");
        var versionLabel = coordinate[(at + 1)..];
        if (!Identifier.IsValid(versionLabel, "version", out var versionError)) throw new FormatException(versionError);
        var rulesPath = GetOptionValue(args, "--rules") ?? throw new FormatException("pkg publish requires --rules <slice.yaml>.");
        if (!long.TryParse(GetOptionValue(args, "--seq") ?? "0", NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence) || sequence < 0) throw new FormatException("--seq must be a non-negative integer.");
        var rules = SliceRulesYaml.Parse(await File.ReadAllTextAsync(rulesPath).ConfigureAwait(false));
        var sliced = await new SliceEngine().SliceAsync(rules).ConfigureAwait(false);
        PrintDiagnostics(sliced.Diagnostics);
        if (!sliced.IsValid) return 1;
        var package = sliced.Packages.FirstOrDefault(x => x.Id == packageId) ?? throw new FormatException($"Slice rules do not define package '{packageId}'.");
        var destinationText = GetOptionValue(args, "--to") ?? new ConfigurationLoader().Load().DefaultServer ?? throw new FormatException("pkg publish requires --to or a configured defaultServer.");
        var target = ParseRepositoryOnly(destinationText, write: true);
        if (target.Address.Backend is "http" or "https")
            return await PublishPackageViaApiAsync(args, target.Address, packageId, versionLabel, sequence, package).ConfigureAwait(false);
        await using var destination = CreateWriteStore(target.Address);
        var (descriptor, layout) = await RepositoryFactory.LoadDescriptorAsync(destination).ConfigureAwait(false);
        var published = await new PackageBuilder().BuildAsync(package, new PackageVersion(versionLabel, sequence), layout, destination, options: new PackageBuildOptions { HashCachePath = Path.GetFullPath(rulesPath) + ".hash-cache.json", RehashAll = args.Contains("--rehash-all", StringComparer.Ordinal), ReleaseSigningBuild = args.Contains("--release-signing", StringComparer.Ordinal), Validators = [new JsonContentValidator(), new PeHeaderValidator()] }).ConfigureAwait(false);
        await new StaticProjectionWriter(destination, layout).WriteRepositoryDescriptorAsync(descriptor).ConfigureAwait(false);
        await WritePackageIndexAsync(destination, layout, published.Manifest, published.Manifest.Id).ConfigureAwait(false);
        Console.WriteLine(JsonSerializer.Serialize(new { published = true, package = published.Manifest.Id.Value, version = published.Manifest.Version.Label, manifestDigest = ContentHash.Compute(published.ManifestBytes).ToString(), files = published.Manifest.FileCount }));
        return 0;
    }

    private static async ValueTask<int> PublishPackageViaApiAsync(string[] args, RepositoryAddress target, PackageId packageId, string versionLabel, long sequence, SlicedPackage package)
    {
        var apiText = GetOptionValue(args, "--api");
        var apiBase = apiText is null ? new Uri(target.BaseUri.GetLeftPart(UriPartial.Authority), UriKind.Absolute) : new Uri(ResolveAlias(apiText), UriKind.Absolute);
        var token = GetOptionValue(args, "--token") ?? Environment.GetEnvironmentVariable("FOURSUP_API_TOKEN");
        if (string.IsNullOrWhiteSpace(token)) throw new UnauthorizedAccessException("Brokered publishing requires --token or FOURSUP_API_TOKEN.");
        var repositoryId = GetOptionValue(args, "--repository-id") ?? Environment.GetEnvironmentVariable("FOURSUP_REPOSITORY_ID") ?? target.BaseUri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? throw new FormatException("Brokered publishing requires --repository-id.");
        var descriptorStore = new HttpObjectStore(target.BaseUri);
        await using (descriptorStore.ConfigureAwait(false))
        {
            var (descriptor, layout) = await RepositoryFactory.LoadDescriptorAsync(descriptorStore).ConfigureAwait(false);
            descriptor.ValidateAgainst(target.BaseUri);
            await using var api = new ManagementApiClient(apiBase, token);
            sequence = checked(await api.AllocateSequenceAsync(repositoryId, "package", packageId.Value).ConfigureAwait(false));
            var temporaryRoot = Path.Combine(Path.GetTempPath(), "4sup-publish-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
            try
            {
                await using var local = new LocalObjectStore(temporaryRoot);
                var built = await new PackageBuilder().BuildAsync(package, new PackageVersion(versionLabel, sequence), layout, local, options: new PackageBuildOptions { HashCachePath = Path.GetFullPath(GetOptionValue(args, "--rules")!) + ".hash-cache.json", RehashAll = args.Contains("--rehash-all", StringComparer.Ordinal), ReleaseSigningBuild = args.Contains("--release-signing", StringComparer.Ordinal), Validators = [new JsonContentValidator(), new PeHeaderValidator()] }).ConfigureAwait(false);
                var hashes = built.Blobs.Values.Concat(built.Manifest.FileTable.Shards.Select(x => x.Digest)).Distinct().OrderBy(x => x.ToString(), StringComparer.Ordinal).ToArray();
                var session = await api.OpenSessionAsync(repositoryId).ConfigureAwait(false);
                var present = await api.QueryBlobsAsync(repositoryId, hashes).ConfigureAwait(false);
                var lengths = new Dictionary<ContentHash, long>();
                foreach (var hash in hashes)
                {
                    var head = await local.HeadAsync(layout.Blob(hash)).ConfigureAwait(false) ?? throw new FileNotFoundException(hash.ToString());
                    lengths[hash] = head.Length;
                }
                foreach (var batch in hashes.Where(x => !present.Contains(x.ToString())).Chunk(100))
                {
                    var grants = await api.CreateGrantsAsync(repositoryId, session.SessionId.Value, batch.Select(x => (x, lengths[x]))).ConfigureAwait(false);
                    foreach (var grant in grants)
                    {
                        var source = await local.OpenAsync(layout.Blob(grant.ExpectedDigest)).ConfigureAwait(false) ?? throw new FileNotFoundException(grant.ExpectedDigest.ToString());
                        await using (source.ConfigureAwait(false)) await api.UploadAsync(grant, source.Content, grant.ExpectedLength).ConfigureAwait(false);
                    }
                }
                await api.SealAsync(repositoryId, session.SessionId.Value).ConfigureAwait(false);
                await api.RegisterPackageVersionAsync(repositoryId, packageId.Value, built.ManifestBytes).ConfigureAwait(false);
                await api.RegisterFileTableAsync(repositoryId, packageId.Value, versionLabel, built.Manifest.FileTable).ConfigureAwait(false);
                await api.PublishPackageVersionAsync(repositoryId, packageId.Value, versionLabel).ConfigureAwait(false);
                Console.WriteLine(JsonSerializer.Serialize(new { published = true, brokered = true, package = packageId.Value, version = versionLabel, manifestDigest = ContentHash.Compute(built.ManifestBytes).ToString(), files = built.Manifest.FileCount }));
                return 0;
            }
            finally
            {
                try { if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true); } catch (IOException) { }
            }
        }

    }

    private static async ValueTask<int> ReleaseCommandAsync(string[] args)
    {
        var subcommand = args.FirstOrDefault() ?? throw new FormatException("release requires check, explain, or matrix.");
        if (subcommand == "new") return await NewReleaseCommandAsync(args.Skip(1).ToArray()).ConfigureAwait(false);
        if (subcommand == "publish") return await PublishReleaseCommandAsync(args.Skip(1).ToArray()).ConfigureAwait(false);
        var target = ParseTarget(args.ElementAtOrDefault(1) ?? throw new FormatException($"release {subcommand} requires a repository target."), "release", null);
        var trusted = ParseTrustedKeysOrFirstParty(args);
        // `release check` is a read-only operation and must work against the
        // anonymous HTTP projection.  Publishing still obtains the write transport.
        IReadableObjectStore store = CreateReadStore(target.Address);
        await using (store.ConfigureAwait(false))
        {
        var (descriptor, layout) = await RepositoryFactory.LoadDescriptorAsync(store).ConfigureAwait(false);
        descriptor.ValidateAgainst(target.Address.BaseUri);
        var reader = new RepositoryReader(store, descriptor);
        if (subcommand == "check" && target.Address.ReleaseRef.StartsWith("release:", StringComparison.Ordinal))
        {
            var draftKey = layout.Release(target.Address.ProductId, target.Address.ReleaseRef[8..]);
            var draftResult = await store.OpenAsync(draftKey).ConfigureAwait(false);
            if (draftResult is not null)
            {
                await using (draftResult.ConfigureAwait(false))
                {
                    var draftBytes = await ReadAllAsync(draftResult.Content).ConfigureAwait(false);
                    try
                    {
                        var draft = RepositoryJson.Deserialize<ReleaseLock>(draftBytes, rejectUnknownFields: true);
                        if (draft.State == ReleaseState.Draft)
                        {
                            var draftRepository = new StaticRepository(store, descriptor);
                            var draftManifests = await LoadPinnedManifestsAsync(draftRepository, draft).ConfigureAwait(false);
                            var draftDiagnostics = await new PublishGate().CheckAsync(draft, draftManifests, new FileSetComposer(), draftRepository).ConfigureAwait(false);
                            var coverage = await store.OpenAsync(layout.Coverage(draft.ProductId, draft.ReleaseId)).ConfigureAwait(false);
                            if (coverage is null) draftDiagnostics = draftDiagnostics.Add(new Diagnostic("COV000", DiagnosticSeverity.Error, "coverage.json is missing."));
                            else
                            {
                                await using (coverage.ConfigureAwait(false))
                                {
                                    var coverageBytes = await ReadAllAsync(coverage.Content).ConfigureAwait(false);
                                    var document = RepositoryJson.Deserialize<CoverageDocument>(coverageBytes, rejectUnknownFields: true);
                                    if (document.Digest is not { } digest || digest != draft.CoverageDigest || ContentHash.Compute(CoverageGenerator.SerializeForDigest(document)) != draft.CoverageDigest)
                                        draftDiagnostics = draftDiagnostics.Add(new Diagnostic("COV001", DiagnosticSeverity.Error, "coverage.json does not match coverageDigest."));
                                }
                            }
                            if (args.Contains("--strict", StringComparer.Ordinal))
                                draftDiagnostics = draftDiagnostics.Select(x => x.Severity == DiagnosticSeverity.Warning ? x with { Severity = DiagnosticSeverity.Error } : x).ToImmutableArray();
                            PrintDiagnostics(draftDiagnostics);
                            if (args.Contains("--json", StringComparer.Ordinal)) Console.WriteLine(JsonSerializer.Serialize(new { valid = !draftDiagnostics.Any(x => x.IsError), diagnostics = draftDiagnostics }));
                            return draftDiagnostics.Any(x => x.IsError) ? 1 : 0;
                        }
                    }
                    catch (FormatException) { }
                }
            }
        }
        var verified = await reader.ReadReleaseAsync(target.Address.ProductId, target.ReleaseOrVersion!, trusted).ConfigureAwait(false);
        var repository = new StaticRepository(store, descriptor);
        var manifests = await LoadPinnedManifestsAsync(repository, verified.Lock).ConfigureAwait(false);
        if (subcommand == "check")
        {
            var diagnosticsBuilder = ImmutableArray.CreateBuilder<Diagnostic>();
            diagnosticsBuilder.AddRange(await new PublishGate().CheckAsync(verified.Lock, manifests, new FileSetComposer(), repository).ConfigureAwait(false));
            var against = GetOptionValue(args, "--against");
            var tolerance = ParseDriftTolerance(GetOptionValue(args, "--drift-tolerance"));
            if (against is not null)
            {
                var baseline = await reader.ReadReleaseAsync(target.Address.ProductId, against, trusted).ConfigureAwait(false);
                var currentCoverage = await new CoverageGenerator().GenerateAsync(verified.Lock, repository).ConfigureAwait(false);
                var baselineCoverage = await new CoverageGenerator().GenerateAsync(baseline.Lock, repository).ConfigureAwait(false);
                var baselinePoints = baselineCoverage.Document.Points.ToDictionary(x => x.Selection, StringComparer.Ordinal);
                foreach (var point in currentCoverage.Document.Points)
                {
                    if (!baselinePoints.TryGetValue(point.Selection, out var previousPoint))
                    {
                        diagnosticsBuilder.Add(new Diagnostic("COV001", DiagnosticSeverity.Error, $"Selection '{point.Selection}' is not present in baseline release '{against}'."));
                        continue;
                    }
                    var fileDrift = Math.Abs(point.FileCount - previousPoint.FileCount) / (double)Math.Max(1, previousPoint.FileCount);
                    var sizeDrift = Math.Abs(point.InstallSize - previousPoint.InstallSize) / (double)Math.Max(1L, previousPoint.InstallSize);
                    if (fileDrift > tolerance || sizeDrift > tolerance)
                        diagnosticsBuilder.Add(new Diagnostic("COV002", DiagnosticSeverity.Error, $"Selection '{point.Selection}' drifts beyond {tolerance:P0}: files {previousPoint.FileCount}->{point.FileCount}, install bytes {previousPoint.InstallSize}->{point.InstallSize}."));
                }
            }
            var diagnostics = diagnosticsBuilder.ToImmutable();
            if (args.Contains("--strict", StringComparer.Ordinal))
                diagnostics = diagnostics.Select(x => x.Severity == DiagnosticSeverity.Warning ? x with { Severity = DiagnosticSeverity.Error } : x).ToImmutableArray();
            PrintDiagnostics(diagnostics);
            if (args.Contains("--json", StringComparer.Ordinal)) Console.WriteLine(JsonSerializer.Serialize(new { valid = !diagnostics.Any(x => x.IsError), diagnostics }));
            return diagnostics.Any(x => x.IsError) ? 1 : 0;
        }
        if (subcommand == "matrix")
        {
            var points = SelectionEnumerator.Enumerate(verified.Lock.Axes, 4096).Select(x => x.ToCanonicalString()).ToArray();
            Console.WriteLine(JsonSerializer.Serialize(new { release = verified.Lock.ReleaseId, points }));
            return 0;
        }
        var selection = SelectionParser.Parse(OptionValues(args, "--select"));
        var resolution = new VariantResolver().Resolve(verified.Lock, selection);
        PrintDiagnostics(resolution.Diagnostics);
        if (!resolution.IsValid) return 1;
        if (subcommand != "explain") throw new FormatException($"Unknown release command '{subcommand}'.");
        var composed = await new FileSetComposer().ComposeAsync(repository, resolution).ConfigureAwait(false);
        PrintDiagnostics(composed.Diagnostics);
        Console.WriteLine(JsonSerializer.Serialize(new { release = verified.Lock.ReleaseId, selection = resolution.Selection.ToCanonicalString(), packages = resolution.Packages.Select(x => new { id = x.Pin.Id.Value, version = x.Pin.Version.Label, x.Layer, x.Discriminator }), fileSetId = composed.FileSetId.ToString() }));
        return composed.IsValid ? 0 : 1;
        }
    }

    private static double ParseDriftTolerance(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var normalized = text.Trim();
        var percent = normalized.EndsWith('%');
        if (percent) normalized = normalized[..^1];
        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0 || value > (percent ? 100 : 1))
            throw new FormatException("--drift-tolerance must be a percentage such as 2% or a fraction between 0 and 1.");
        return percent ? value / 100d : value;
    }

    private static async ValueTask<int> NewReleaseCommandAsync(string[] args)
    {
        var product = Positional(args).FirstOrDefault() ?? throw new FormatException("release new requires a product id.");
        var releaseId = GetOptionValue(args, "--as") ?? throw new FormatException("release new requires --as <release-id>.");
        var from = GetOptionValue(args, "--from") ?? throw new FormatException("release new requires --from <release-id>.");
        var repositoryText = GetOptionValue(args, "--repository") ?? new ConfigurationLoader().Load().DefaultLocalRepository ?? throw new FormatException("release new requires --repository or a configured defaultLocalRepository.");
        var target = ParseTarget(repositoryText.TrimEnd('/') + "/" + product + "@release:" + from, "release", null);
        var writeTarget = ParseTarget(repositoryText.TrimEnd('/') + "/" + product + "@release:" + from, "release", null, write: true);
        var trusted = ParseTrustedKeys(args);
        if (trusted.Count == 0) throw new CryptographicException("release new requires --trusted-key for the previous release.");
        if (target.Address.Backend is "http" or "https")
        {
            await using var remoteStore = CreateReadStore(target.Address);
            var (remoteDescriptor, remoteLayout) = await RepositoryFactory.LoadDescriptorAsync(remoteStore).ConfigureAwait(false);
            remoteDescriptor.ValidateAgainst(target.Address.BaseUri);
            var remoteReader = new RepositoryReader(remoteStore, remoteDescriptor);
            var previousRemote = await remoteReader.ReadReleaseAsync(product, from, trusted).ConfigureAwait(false);
            var remoteRepository = new StaticRepository(remoteStore, remoteDescriptor);
            var remoteManifests = new Dictionary<PackageId, PackageManifest>(await LoadPinnedManifestsAsync(remoteRepository, previousRemote.Lock).ConfigureAwait(false));
            foreach (var bump in OptionValues(args, "--bump"))
            {
                var separator = bump.LastIndexOf('@');
                if (separator <= 0 || separator == bump.Length - 1 || !PackageId.TryCreate(bump[..separator], out var packageId)) throw new FormatException("--bump must use package-id@version.");
                var manifest = await ReadManifestByLabelAsync(remoteStore, remoteLayout, packageId, bump[(separator + 1)..]).ConfigureAwait(false) ?? throw new FileNotFoundException($"Bumped manifest '{bump}' was not found.");
                remoteManifests[packageId] = manifest;
            }
            await using var api = CreateManagementApi(target.Address, args);
            var repositoryId = GetOptionValue(args, "--repository-id") ?? Environment.GetEnvironmentVariable("FOURSUP_REPOSITORY_ID") ?? throw new FormatException("Brokered release authoring requires --repository-id or FOURSUP_REPOSITORY_ID.");
            var allocatedSequence = await api.AllocateSequenceAsync(repositoryId, "release", product).ConfigureAwait(false);
            var builtRemote = await new ReleaseBuilder().BuildAsync(product, releaseId, allocatedSequence, previousRemote.Lock.Axes, previousRemote.Lock.Requirements, remoteManifests, remoteLayout, DateTimeOffset.UtcNow, remoteRepository, previousRemote.Lock.MinimumInstalledRelease).ConfigureAwait(false);
            var remoteDraftBytes = RepositoryJson.Serialize(builtRemote.Release);
            var draftId = await api.CreateReleaseDraftAsync(product, remoteDraftBytes).ConfigureAwait(false);
            await api.RegisterDraftCoverageAsync(product, draftId, CoverageGenerator.Serialize(builtRemote.Coverage)).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(new { draft = builtRemote.Release, draftId, coverageDigest = builtRemote.CoverageDigest.ToString(), brokered = true }));
            return 0;
        }
        if (!long.TryParse(GetOptionValue(args, "--seq"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence) || sequence < 1) throw new FormatException("release new requires a positive --seq.");
        await using var readStore = CreateReadStore(target.Address);
        var (descriptor, layout) = await RepositoryFactory.LoadDescriptorAsync(readStore).ConfigureAwait(false);
        descriptor.ValidateAgainst(target.Address.BaseUri);
        var reader = new RepositoryReader(readStore, descriptor);
        var previous = await reader.ReadReleaseAsync(product, from, trusted).ConfigureAwait(false);
        var repository = new StaticRepository(readStore, descriptor);
        var manifests = new Dictionary<PackageId, PackageManifest>(await LoadPinnedManifestsAsync(repository, previous.Lock).ConfigureAwait(false));
        foreach (var bump in OptionValues(args, "--bump"))
        {
            var separator = bump.LastIndexOf('@');
            if (separator <= 0 || separator == bump.Length - 1 || !PackageId.TryCreate(bump[..separator], out var packageId)) throw new FormatException("--bump must use package-id@version.");
            var manifest = await ReadManifestByLabelAsync(readStore, layout, packageId, bump[(separator + 1)..]).ConfigureAwait(false) ?? throw new FileNotFoundException($"Bumped manifest '{bump}' was not found.");
            manifests[packageId] = manifest;
        }
        var built = await new ReleaseBuilder().BuildAsync(product, releaseId, sequence, previous.Lock.Axes, previous.Lock.Requirements, manifests, layout, DateTimeOffset.UtcNow, repository, previous.Lock.MinimumInstalledRelease).ConfigureAwait(false);
        var draftBytes = RepositoryJson.Serialize(built.Release);
        await using var store = CreateWriteStore(writeTarget.Address);
        var (_, writeLayout) = await RepositoryFactory.LoadDescriptorAsync(store).ConfigureAwait(false);
        await using (var draft = new MemoryStream(draftBytes, writable: false))
        {
            if (store is not IConditionalWriteStore conditional || !await conditional.PutIfAbsentAsync(writeLayout.Release(product, releaseId), draft, draftBytes.LongLength).ConfigureAwait(false)) throw new InstallConcurrencyException($"Release draft '{releaseId}' already exists or the repository lacks conditional writes.");
        }
        await new ReleaseBuilder().WriteCoverageAsync(built.Release, built.Coverage, writeLayout, store).ConfigureAwait(false);
        Console.WriteLine(JsonSerializer.Serialize(new { draft = built.Release, coverageDigest = built.CoverageDigest.ToString() }, SignedDocument.JsonOptions));
        return 0;
    }

    private static async ValueTask<int> PublishReleaseCommandAsync(string[] args)
    {
        var value = Positional(args).FirstOrDefault() ?? throw new FormatException("release publish requires a repository target.");
        var target = ParseTarget(value, "release", null);
        var writeTarget = ParseTarget(value, "release", null, write: true);
        var signing = ParseSigningKey(args);
        if (target.Address.Backend is "http" or "https")
        {
            await using var remote = new HttpObjectStore(target.Address.BaseUri);
            var (remoteDescriptor, remoteLayout) = await RepositoryFactory.LoadDescriptorAsync(remote).ConfigureAwait(false);
            remoteDescriptor.ValidateAgainst(target.Address.BaseUri);
            var remoteReleaseId = target.Address.ReleaseRef.StartsWith("release:", StringComparison.Ordinal) ? target.Address.ReleaseRef[8..] : throw new FormatException("release publish requires @release:<id>.");
            var draftKey = remoteLayout.Release(target.Address.ProductId, remoteReleaseId);
            var draftResult = await remote.OpenAsync(draftKey).ConfigureAwait(false) ?? throw new FileNotFoundException(draftKey.Value);
            byte[] remoteDraftBytes;
            await using (draftResult.ConfigureAwait(false)) remoteDraftBytes = await ReadAllAsync(draftResult.Content).ConfigureAwait(false);
            var remoteDraft = RepositoryJson.Deserialize<ReleaseLock>(remoteDraftBytes, rejectUnknownFields: true);
            var remoteRepository = new StaticRepository(remote, remoteDescriptor);
            var pinnedManifests = await LoadPinnedManifestsAsync(remoteRepository, remoteDraft).ConfigureAwait(false);
            var reconstructedDraft = new ReleaseBuilder().Build(remoteDraft.ProductId, remoteDraft.ReleaseId, remoteDraft.Sequence, remoteDraft.Axes, remoteDraft.Requirements, pinnedManifests, remoteLayout, remoteDraft.CreatedAt, remoteDraft.MinimumInstalledRelease, remoteDraft.CoverageDigest);
            var reconstructedBytes = RepositoryJson.Serialize(reconstructedDraft);
            if (!reconstructedBytes.AsSpan().SequenceEqual(remoteDraftBytes))
                throw new CryptographicException("The remote release draft differs from the locally reconstructed package pins or receipts; refusing to sign server-provided bytes.");
            var diagnostics = await new PublishGate().CheckAsync(reconstructedDraft, pinnedManifests, new FileSetComposer(), remoteRepository).ConfigureAwait(false);
            if (diagnostics.Any(x => x.IsError)) throw new InvalidDataException("The reconstructed release draft failed the publish gate: " + string.Join("; ", diagnostics.Where(x => x.IsError).Select(x => x.Code + " " + x.Message)));
            var coverageResult = await remote.OpenAsync(remoteLayout.Coverage(remoteDraft.ProductId, remoteDraft.ReleaseId)).ConfigureAwait(false) ?? throw new FileNotFoundException("The remote release draft has no coverage.json.");
            await using (coverageResult.ConfigureAwait(false))
            {
                var coverageBytes = await ReadAllAsync(coverageResult.Content).ConfigureAwait(false);
                var coverage = RepositoryJson.Deserialize<CoverageDocument>(coverageBytes, rejectUnknownFields: true);
                if (coverage.Digest is not { } digest || digest != remoteDraft.CoverageDigest || ContentHash.Compute(CoverageGenerator.SerializeForDigest(coverage)) != remoteDraft.CoverageDigest)
                    throw new CryptographicException("The remote coverage.json does not match the draft coverageDigest.");
            }
            var remotePublished = new ReleaseBuilder().Publish(reconstructedDraft);
            var signedRemote = new ReleaseSigner().SignRelease(remotePublished, signing.KeyId, signing.PrivateKey);
            await using var apiRemote = CreateManagementApi(target.Address, args);
            await apiRemote.PlaceSignedReleaseAsync(remotePublished.ProductId, remotePublished.ReleaseId, signedRemote.EnvelopeBytes).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(new { published = true, brokered = true, release = remotePublished.ReleaseId, digest = signedRemote.Digest.ToString() }));
            return 0;
        }
        await using var readStore = CreateReadStore(target.Address);
        var (descriptor, layout) = await RepositoryFactory.LoadDescriptorAsync(readStore).ConfigureAwait(false);
        descriptor.ValidateAgainst(target.Address.BaseUri);
        var releaseId = target.Address.ReleaseRef.StartsWith("release:", StringComparison.Ordinal) ? target.Address.ReleaseRef[8..] : throw new FormatException("release publish requires @release:<id>.");
        var key = layout.Release(target.Address.ProductId, releaseId);
        var head = await readStore.HeadAsync(key).ConfigureAwait(false) ?? throw new FileNotFoundException(key.Value);
        var existing = await readStore.OpenAsync(key).ConfigureAwait(false) ?? throw new FileNotFoundException(key.Value);
        byte[] draftBytes;
        await using (existing.ConfigureAwait(false)) draftBytes = await ReadAllAsync(existing.Content).ConfigureAwait(false);
        var draft = RepositoryJson.Deserialize<ReleaseLock>(draftBytes, rejectUnknownFields: true);
        var published = new ReleaseBuilder().Publish(draft);
        var repository = new StaticRepository(readStore, descriptor);
        var manifests = await LoadPinnedManifestsAsync(repository, published).ConfigureAwait(false);
        var gateDiagnostics = await new PublishGate().CheckAsync(published, manifests, new FileSetComposer(), repository).ConfigureAwait(false);
        if (gateDiagnostics.Any(x => x.IsError)) throw new InvalidDataException("Release publish gate failed: " + string.Join("; ", gateDiagnostics.Where(x => x.IsError).Select(x => x.Code + " " + x.Message)));
        var coverageObject = await readStore.OpenAsync(layout.Coverage(published.ProductId, published.ReleaseId)).ConfigureAwait(false) ?? throw new InvalidDataException("Release publish requires coverage.json.");
        await using (coverageObject.ConfigureAwait(false))
        {
            var coverageBytes = await ReadAllAsync(coverageObject.Content).ConfigureAwait(false);
            var coverage = RepositoryJson.Deserialize<CoverageDocument>(coverageBytes, rejectUnknownFields: true);
            if (coverage.Digest is not { } digest || digest != published.CoverageDigest || ContentHash.Compute(CoverageGenerator.SerializeForDigest(coverage)) != published.CoverageDigest) throw new CryptographicException("coverage.json does not match coverageDigest.");
        }
        var signed = new ReleaseSigner().SignRelease(published, signing.KeyId, signing.PrivateKey);
        await using var store = CreateWriteStore(writeTarget.Address);
        var (_, writeLayout) = await RepositoryFactory.LoadDescriptorAsync(store).ConfigureAwait(false);
        var writeKey = writeLayout.Release(published.ProductId, releaseId);
        var writeHead = await store.HeadAsync(writeKey) ?? throw new FileNotFoundException(writeKey.Value);
        if (store is not IConditionalWriteStore conditional || writeHead.Validator is null) throw new IOException("release publish requires a conditional-write backend.");
        await using var body = new MemoryStream(signed.EnvelopeBytes, writable: false);
        if (!await conditional.CompareAndSwapAsync(writeKey, writeHead.Validator, body, signed.EnvelopeBytes.LongLength).ConfigureAwait(false)) throw new InstallConcurrencyException("release publish lost its conditional-write race.");
        var writableStore = store as IWritableObjectStore ?? throw new IOException("release publish requires a writable repository transport.");
        var projections = new StaticProjectionWriter(writableStore, writeLayout);
        await projections.WriteReleaseBundleAsync(published.ProductId, published.ReleaseId, signed.EnvelopeBytes, manifests.Values).ConfigureAwait(false);
        await projections.WriteRepositoryDescriptorAsync(descriptor).ConfigureAwait(false);
        await WriteReleaseIndexAsync(writableStore, writeLayout, published, signed.EnvelopeBytes).ConfigureAwait(false);
        Console.WriteLine(JsonSerializer.Serialize(new { published = true, release = published.ReleaseId, digest = signed.Digest.ToString() }));
        return 0;
    }

    private static async ValueTask<int> ChannelCommandAsync(string[] args)
    {
        var subcommand = args.FirstOrDefault() ?? throw new FormatException("channel requires show.");
        if (subcommand is "promote" or "rollback") return await WriteChannelCommandAsync(subcommand, args).ConfigureAwait(false);
        if (subcommand == "yank") return await WriteYankCommandAsync(args).ConfigureAwait(false);
        if (subcommand != "show") throw new FormatException("Unknown channel command.");
        var targetText = args.ElementAtOrDefault(1) ?? throw new FormatException("channel show requires <product> <channel> or <repo/product@channel>.");
        if (args.ElementAtOrDefault(2) is { } channel && !channel.StartsWith("--", StringComparison.Ordinal))
        {
            var repositoryBase = GetOptionValue(args, "--repository") ?? new ConfigurationLoader().Load().DefaultServer ?? throw new FormatException("Provide --repository or configure defaultServer.");
            targetText = repositoryBase.TrimEnd('/') + "/" + targetText + "@channel:" + channel;
        }
        var target = ParseTarget(targetText, "channel", "live");
        var trusted = ParseTrustedKeysOrFirstParty(args);
        await using var store = CreateReadStore(target.Address);
        var (descriptor, _) = await RepositoryFactory.LoadDescriptorAsync(store).ConfigureAwait(false);
        descriptor.ValidateAgainst(target.Address.BaseUri);
        var pointer = await new RepositoryReader(store, descriptor).ReadChannelAsync(target.Address.ProductId, target.ReleaseOrVersion!, trusted).ConfigureAwait(false);
        Console.WriteLine(JsonSerializer.Serialize(pointer.Pointer));
        return 0;
    }

    private static async ValueTask<int> WriteYankCommandAsync(string[] args)
    {
        if (args.Length < 2) throw new FormatException("channel yank requires <product>.");
        var product = args[1];
        if (!Identifier.IsValid(product, "productId", out var productError)) throw new FormatException(productError);
        var releaseId = GetOptionValue(args, "--release") ?? throw new FormatException("channel yank requires --release <release-id>.");
        if (!Identifier.IsValid(releaseId, "releaseId", out var releaseError)) throw new FormatException(releaseError);
        var effectText = GetOptionValue(args, "--effect") ?? throw new FormatException("channel yank requires --effect block-install|block-repair|force-move.");
        if (!Enum.TryParse<RevocationEffect>(effectText.Replace("-", string.Empty, StringComparison.Ordinal), ignoreCase: true, out var effect)) throw new FormatException("Unknown revocation effect.");
        var repositoryBase = GetOptionValue(args, "--repository") ?? new ConfigurationLoader().Load().DefaultServer ?? throw new FormatException("Provide --repository or configure defaultServer.");
        var target = ParseTarget(repositoryBase.TrimEnd('/') + "/" + product + "@channel:live", "channel", null);
        var writeTarget = ParseTarget(repositoryBase.TrimEnd('/') + "/" + product + "@channel:live", "channel", null, write: true);
        var trusted = ParseTrustedKeys(args);
        var signing = ParseSigningKey(args);
        if (trusted.Count == 0) throw new CryptographicException("No trusted public key was supplied.");
        await using var readStore = CreateReadStore(target.Address);
        var (descriptor, layout) = await RepositoryFactory.LoadDescriptorAsync(readStore).ConfigureAwait(false);
        descriptor.ValidateAgainst(target.Address.BaseUri);
        var reader = new RepositoryReader(readStore, descriptor);
        _ = await reader.ReadReleaseAsync(product, releaseId, trusted).ConfigureAwait(false);
        var current = await reader.ReadRevocationsAsync(product, trusted).ConfigureAwait(false);
        var reason = GetOptionValue(args, "--reason") ?? "release yanked by operator";
        var entry = new RevocationEntry { ReleaseId = releaseId, Action = "yank", Effect = effect, Reason = reason, At = DateTimeOffset.UtcNow };
        var document = new RevocationDocument { ProductId = product, RevocationSequence = (current?.Document.RevocationSequence ?? 0) + 1, UpdatedAt = DateTimeOffset.UtcNow, Entries = (current?.Document.Entries ?? []).Add(entry) };
        var signed = new ReleaseSigner().SignRevocations(document, signing.KeyId, signing.PrivateKey);
        if (target.Address.Backend is "http" or "https")
        {
            await using var api = CreateManagementApi(target.Address, args);
            var repositoryId = GetOptionValue(args, "--repository-id") ?? Environment.GetEnvironmentVariable("FOURSUP_REPOSITORY_ID") ?? target.Address.BaseUri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "default";
            var allocatedSequence = await api.AllocateSequenceAsync(repositoryId, "revocation", product).ConfigureAwait(false);
            var allocatedDocument = document with { RevocationSequence = allocatedSequence };
            var allocatedSigned = new ReleaseSigner().SignRevocations(allocatedDocument, signing.KeyId, signing.PrivateKey);
            await api.PlaceSignedRevocationAsync(product, releaseId, allocatedSigned.EnvelopeBytes).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(new { yanked = true, brokered = true, product, release = releaseId, effect, revocationSequence = allocatedDocument.RevocationSequence }));
            return 0;
        }
        await using var writeStore = CreateWriteStore(writeTarget.Address);
        var key = layout.Revocations(product);
        if (writeStore is not IConditionalWriteStore conditional) throw new IOException("channel yank requires a conditional-write backend.");
        await using var body = new MemoryStream(signed.EnvelopeBytes, writable: false);
        var accepted = current is null
            ? await conditional.PutIfAbsentAsync(key, body, signed.EnvelopeBytes.LongLength).ConfigureAwait(false)
            : await CompareAndSwapDocumentAsync(writeStore, conditional, key, current.EnvelopeBytes, signed.EnvelopeBytes).ConfigureAwait(false);
        if (!accepted) throw new InstallConcurrencyException("Channel yank lost its conditional-write race.");
        Console.WriteLine(JsonSerializer.Serialize(new { yanked = true, product, release = releaseId, effect, revocationSequence = document.RevocationSequence }));
        return 0;
    }

    private static async ValueTask<bool> CompareAndSwapDocumentAsync(IWritableObjectStore store, IConditionalWriteStore conditional, ObjectKey key, byte[] expectedBytes, byte[] replacementBytes)
    {
        var head = await store.HeadAsync(key).ConfigureAwait(false);
        if (head?.Validator is null) return false;
        var existing = await store.OpenAsync(key).ConfigureAwait(false);
        if (existing is null) return false;
        await using (existing.ConfigureAwait(false))
        {
            var actual = await ReadAllAsync(existing.Content).ConfigureAwait(false);
            if (!actual.AsSpan().SequenceEqual(expectedBytes)) return false;
        }
        await using var body = new MemoryStream(replacementBytes, writable: false);
        return await conditional.CompareAndSwapAsync(key, head.Validator, body, replacementBytes.LongLength).ConfigureAwait(false);
    }

    private static async ValueTask<int> WriteChannelCommandAsync(string subcommand, string[] args)
    {
        if (args.Length < 3) throw new FormatException($"channel {subcommand} requires <product> <channel>.");
        var product = args[1]; var channel = args[2];
        if (!Identifier.IsValid(product)) throw new FormatException("Product target is invalid.");
        if (!Identifier.IsValid(channel, "channel", out var identifierError)) throw new FormatException(identifierError ?? "Channel target is invalid.");
        var repositoryBase = GetOptionValue(args, "--repository") ?? new ConfigurationLoader().Load().DefaultServer ?? throw new FormatException("Provide --repository or configure defaultServer.");
        var target = ParseTarget(repositoryBase.TrimEnd('/') + "/" + product + "@" + channel, "channel", null);
        var writeTarget = ParseTarget(repositoryBase.TrimEnd('/') + "/" + product + "@" + channel, "channel", null, write: true);
        var trusted = ParseTrustedKeys(args);
        var signing = ParseSigningKey(args);
        if (trusted.Count == 0) throw new CryptographicException("No trusted public key was supplied.");
        await using var readStore = CreateReadStore(target.Address);
        var (descriptor, layout) = await RepositoryFactory.LoadDescriptorAsync(readStore).ConfigureAwait(false);
        descriptor.ValidateAgainst(target.Address.BaseUri);
        var reader = new RepositoryReader(readStore, descriptor);
        var current = await reader.ReadChannelAsync(product, channel, trusted).ConfigureAwait(false);
        var releaseId = GetOptionValue(args, "--release");
        if (string.IsNullOrWhiteSpace(releaseId) && subcommand == "rollback")
        {
            var candidates = new List<VerifiedRelease>();
            var indexed = await ReadReleaseIndexAsync(readStore, layout, product).ConfigureAwait(false);
            if (indexed.Count > 0)
            {
                foreach (var releaseObject in indexed)
                {
                    if (releaseObject.Sequence >= current.Pointer.ReleaseSequence) continue;
                    try
                    {
                        var candidate = await reader.ReadReleaseAsync(product, releaseObject.ReleaseId, trusted, releaseObject.LockDigest).ConfigureAwait(false);
                        if (candidate.Lock.Sequence < current.Pointer.ReleaseSequence) candidates.Add(candidate);
                    }
                    catch (Exception) { }
                }
            }
            else if (readStore is IListableObjectStore listable)
            {
                await foreach (var releaseObject in listable.ListAsync($"products/{product}/releases/", cancellationToken: default).ConfigureAwait(false))
                {
                    if (!releaseObject.Value.EndsWith("/release.lock.json", StringComparison.Ordinal)) continue;
                    var parts = releaseObject.Value.Split('/');
                    if (parts.Length < 4) continue;
                    try
                    {
                        var candidate = await reader.ReadReleaseAsync(product, parts[^2], trusted).ConfigureAwait(false);
                        if (candidate.Lock.Sequence < current.Pointer.ReleaseSequence) candidates.Add(candidate);
                    }
                    catch (Exception) { }
                }
            }
            releaseId = candidates.OrderByDescending(x => x.Lock.Sequence).ThenByDescending(x => x.Lock.ReleaseId, StringComparer.Ordinal).FirstOrDefault()?.Lock.ReleaseId;
        }
        if (string.IsNullOrWhiteSpace(releaseId)) throw new FormatException($"channel {subcommand} requires an older published release or --release <release-id>.");
        var release = await reader.ReadReleaseAsync(product, releaseId, trusted).ConfigureAwait(false);
        if (subcommand == "rollback" && release.Lock.Sequence >= current.Pointer.ReleaseSequence)
            throw new ApplyPreconditionException($"Rollback target '{release.Lock.ReleaseId}' (sequence {release.Lock.Sequence}) is not older than the channel head (sequence {current.Pointer.ReleaseSequence}).");
        var channelSequence = current.Pointer.ChannelSequence + 1;
        if (target.Address.Backend is "http" or "https")
        {
            await using var allocationApi = CreateManagementApi(target.Address, args);
            channelSequence = await allocationApi.AllocateSequenceAsync(GetOptionValue(args, "--repository-id") ?? Environment.GetEnvironmentVariable("FOURSUP_REPOSITORY_ID") ?? target.Address.BaseUri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "default", "channel", product + ":" + channel).ConfigureAwait(false);
        }
        var minimumClientVersion = GetOptionValue(args, "--minimum-client-version") ?? current.Pointer.MinimumClientVersion;
        if (minimumClientVersion is not null && !Version.TryParse(minimumClientVersion, out _)) throw new FormatException("--minimum-client-version must be a valid semantic version.");
        var pointer = new ChannelAuthoring().Promote(product, channel, channelSequence, current.Pointer.ChannelSequence, release.Lock.ReleaseId, release.Lock.Sequence, ContentHash.Compute(release.EnvelopeBytes), DateTimeOffset.UtcNow, minimumClientVersion) with { Reason = subcommand == "rollback" ? "rollback" : null };
        var signed = new ReleaseSigner().SignChannel(pointer, signing.KeyId, signing.PrivateKey);
        if (target.Address.Backend is "http" or "https")
        {
            await using var api = CreateManagementApi(target.Address, args);
            await api.PlaceSignedChannelAsync(product, channel, signed.EnvelopeBytes).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(pointer));
            return 0;
        }
        await using var writeStore = CreateWriteStore(writeTarget.Address);
        var key = layout.Channel(product, channel);
        var head = await writeStore.HeadAsync(key).ConfigureAwait(false);
        if (writeStore is not IConditionalWriteStore conditional)
        {
            if (!args.Contains("--force-unsafe-promote", StringComparer.Ordinal)) throw new IOException("Channel promotion requires a conditional-write backend; pass --force-unsafe-promote for a single-publisher FTP/B2 repository.");
            Console.Error.WriteLine("warning: promoting without compare-and-swap; this repository must have exactly one publisher.");
            await using var unsafeBody = new MemoryStream(signed.EnvelopeBytes, writable: false);
            await writeStore.PutAsync(key, unsafeBody, signed.EnvelopeBytes.LongLength).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(pointer));
            return 0;
        }
        await using var body = new MemoryStream(signed.EnvelopeBytes, writable: false);
        var accepted = head is null ? await conditional.PutIfAbsentAsync(key, body, signed.EnvelopeBytes.LongLength).ConfigureAwait(false) : head.Validator is not null && await conditional.CompareAndSwapAsync(key, head.Validator, body, signed.EnvelopeBytes.LongLength).ConfigureAwait(false);
        if (!accepted) throw new InstallConcurrencyException("Channel promotion lost its conditional-write race.");
        Console.WriteLine(JsonSerializer.Serialize(pointer));
        return 0;
    }

    private static async ValueTask<int> VerifyRepositoryCommandAsync(string[] args)
    {
        var target = ParseRepositoryOnly(args.FirstOrDefault() ?? throw new FormatException("verify-repo requires a repository target."));
        await using var store = CreateReadStore(target.Address);
        var (descriptor, layout) = await RepositoryFactory.LoadDescriptorAsync(store).ConfigureAwait(false);
        descriptor.ValidateAgainst(target.Address.BaseUri);
        var deep = args.Contains("--deep", StringComparer.Ordinal) || args.Contains("--rehash-cas", StringComparer.Ordinal);
        var trusted = ParseTrustedKeysOrFirstParty(args);
        var repository = new StaticRepository(store, descriptor);
        var checkedObjects = 0; var errors = new List<string>();
        var writeTargetText = GetOptionValue(args, "--write-target");
        if (writeTargetText is not null)
        {
            var writeTarget = ParseRepositoryOnly(writeTargetText, write: true);
            await using var writeStore = CreateWriteStore(writeTarget.Address);
            await CheckWriteReadPairingAsync(writeStore, store, errors).ConfigureAwait(false);
        }
        if (target.Address.Backend is "http" or "https")
            await CheckHttpStagingIsolationAsync(target.Address.BaseUri, errors).ConfigureAwait(false);
        if (store is not IListableObjectStore listable)
        {
            var knownKeys = new HashSet<ObjectKey> { new("repo.json"), layout.KeyManifest() };
            foreach (var product in descriptor.Products)
            {
                knownKeys.Add(layout.Channel(product, "live"));
                knownKeys.Add(layout.Channel(product, "ptr"));
                knownKeys.Add(layout.ReleaseIndex(product));
                knownKeys.Add(layout.Revocations(product));
                try
                {
                    foreach (var row in await ReadReleaseIndexAsync(store, layout, product).ConfigureAwait(false))
                    {
                        var lockKey = new ObjectKey(row.LockPath);
                        knownKeys.Add(lockKey);
                        knownKeys.Add(layout.ReleaseBundle(product, row.ReleaseId));
                        knownKeys.Add(layout.Coverage(product, row.ReleaseId));
                        var lockResult = await store.OpenAsync(lockKey).ConfigureAwait(false);
                        if (lockResult is null) continue;
                        await using (lockResult.ConfigureAwait(false))
                        {
                            var lockEnvelope = SignedDocument.DeserializeEnvelope(await ReadAllAsync(lockResult.Content).ConfigureAwait(false));
                            var release = SignedDocument.DeserializePayload<ReleaseLock>(Base64Url.Decode(lockEnvelope.Payload));
                            foreach (var pin in release.Packages)
                            {
                                knownKeys.Add(new ObjectKey(pin.ManifestPath));
                                knownKeys.Add(layout.PackageIndex(pin.Id));
                            }
                        }
                    }
                }
                catch (FileNotFoundException ex) { errors.Add($"missing-index:{product}:{ex.Message}"); }
            }
            foreach (var key in knownKeys)
            {
                var head = await store.HeadAsync(key).ConfigureAwait(false);
                if (head is null) { if (key.Value == "repo.json") errors.Add($"missing-head:{key.Value}"); continue; }
                checkedObjects++;
                if (head.ContentEncoding is not null) errors.Add($"content-encoding:{key.Value}");
                if (store.Capabilities.HasFlag(StorageCapabilities.Range) && !head.AcceptRanges) errors.Add($"range:{key.Value}");
                if (target.Address.Backend is "http" or "https" && string.IsNullOrWhiteSpace(head.CacheControl)) errors.Add($"cache-control:{key.Value}:missing");
                if (target.Address.Backend is "http" or "https" && head.CacheControl is not null)
                {
                    var expectedCache = ExpectedCacheControl(key);
                    if (!string.Equals(head.CacheControl, expectedCache, StringComparison.OrdinalIgnoreCase)) errors.Add($"cache-control:{key.Value}:{head.CacheControl}");
                }
                if (deep && (key.Value == layout.KeyManifest().Value || key.Value.Contains("/channels/", StringComparison.Ordinal) || key.Value.EndsWith("/revocations.json", StringComparison.Ordinal)))
                {
                    var read = await store.OpenAsync(key).ConfigureAwait(false);
                    if (read is null) { errors.Add($"missing:{key.Value}"); continue; }
                    await using (read.ConfigureAwait(false))
                    {
                        try
                        {
                            using var bytes = new MemoryStream(); await read.Content.CopyToAsync(bytes).ConfigureAwait(false);
                            var envelope = SignedDocument.DeserializeEnvelope(bytes.ToArray());
                            if (!SignedDocument.Verify(envelope, trusted, out _, out var error)) errors.Add($"signature:{key.Value}:{error}");
                        }
                        catch (Exception ex) when (ex is FormatException or InvalidDataException or CryptographicException) { errors.Add($"document:{key.Value}:{ex.Message}"); }
                    }
                }
            }
            if (deep)
                await VerifyRepositoryGraphAsync(store, descriptor, layout, repository, trusted, null, errors).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(new { valid = errors.Count == 0, checkedObjects, errors }));
            return errors.Count == 0 ? 0 : 5;
        }
        var listedKeys = new HashSet<ObjectKey>();
        await foreach (var key in listable.ListAsync(cancellationToken: default).ConfigureAwait(false))
        {
            listedKeys.Add(key);
            var head = await store.HeadAsync(key).ConfigureAwait(false);
            if (head is null) { errors.Add($"missing-head:{key.Value}"); continue; }
            if (head.ContentEncoding is not null) errors.Add($"content-encoding:{key.Value}");
            if (store.Capabilities.HasFlag(StorageCapabilities.Range) && !head.AcceptRanges) errors.Add($"range:{key.Value}");
            if ((target.Address.Backend is "http" or "https") && string.IsNullOrWhiteSpace(head.CacheControl)) errors.Add($"cache-control:{key.Value}:missing");
            else if ((target.Address.Backend is "http" or "https") && head.CacheControl is not null)
            {
                var expectedCache = ExpectedCacheControl(key);
                if (!string.Equals(head.CacheControl, expectedCache, StringComparison.OrdinalIgnoreCase)) errors.Add($"cache-control:{key.Value}:{head.CacheControl}");
            }
            if (key.Value.StartsWith("blobs/", StringComparison.Ordinal))
            {
                var parts = key.Value.Split('/');
                var name = parts.LastOrDefault();
                if (parts.Length != 5 || parts[1] != "sha256" || name is null || !string.Equals(name, name.ToLowerInvariant(), StringComparison.Ordinal) || parts[2] != name[..2] || parts[3] != name[2..4] || !ContentHash.TryParse("sha256:" + name, out var hash)) { errors.Add($"unparseable:{key.Value}"); continue; }
                var read = await store.OpenAsync(key).ConfigureAwait(false);
                if (read is null) { errors.Add($"missing:{key.Value}"); continue; }
                await using (read.ConfigureAwait(false)) if (await ContentHash.ComputeAsync(read.Content).ConfigureAwait(false) != hash) errors.Add($"digest:{key.Value}");
            }
            else if (deep && key.Value.StartsWith("packages/", StringComparison.Ordinal) && key.Value.EndsWith("/package.json", StringComparison.Ordinal))
            {
                try
                {
                    var read = await store.OpenAsync(key).ConfigureAwait(false) ?? throw new FileNotFoundException(key.Value);
                    await using (read.ConfigureAwait(false))
                    {
                        using var bytes = new MemoryStream(); await read.Content.CopyToAsync(bytes).ConfigureAwait(false);
                        var manifest = RepositoryJson.DeserializeManifest(bytes.ToArray());
                        await foreach (var _ in repository.ReadFileTableAsync(manifest).ConfigureAwait(false)) { }
                    }
                }
                catch (Exception ex) when (ex is FormatException or InvalidDataException or CryptographicException or FileNotFoundException) { errors.Add($"manifest:{key.Value}:{ex.Message}"); }
            }
            else if (deep && (key.Value == layout.KeyManifest().Value || (key.Value.StartsWith("products/", StringComparison.Ordinal) && (key.Value.EndsWith("/release.lock.json", StringComparison.Ordinal) || key.Value.Contains("/channels/", StringComparison.Ordinal) || key.Value.EndsWith("/revocations.json", StringComparison.Ordinal)))))
            {
                try
                {
                    var read = await store.OpenAsync(key).ConfigureAwait(false) ?? throw new FileNotFoundException(key.Value);
                    await using (read.ConfigureAwait(false))
                    {
                        using var bytes = new MemoryStream(); await read.Content.CopyToAsync(bytes).ConfigureAwait(false);
                        var envelope = SignedDocument.DeserializeEnvelope(bytes.ToArray());
                        if (!SignedDocument.Verify(envelope, trusted, out _, out var error)) errors.Add($"signature:{key.Value}:{error}");
                    }
                }
                catch (Exception ex) when (ex is FormatException or InvalidDataException or CryptographicException) { errors.Add($"document:{key.Value}:{ex.Message}"); }
            }
            checkedObjects++;
        }
        if (deep)
            await VerifyRepositoryGraphAsync(store, descriptor, layout, repository, trusted, listedKeys, errors).ConfigureAwait(false);
        Console.WriteLine(JsonSerializer.Serialize(new { valid = errors.Count == 0, checkedObjects, errors }));
        return errors.Count == 0 ? 0 : 5;
    }

    private static async ValueTask VerifyRepositoryGraphAsync(IReadableObjectStore store, RepositoryDescriptor descriptor, RepositoryLayout layout, StaticRepository repository, IReadOnlyDictionary<string, byte[]> trusted, IReadOnlySet<ObjectKey>? listedKeys, List<string> errors)
    {
        foreach (var packageId in (listedKeys is null ? Enumerable.Empty<ObjectKey>() : listedKeys).Select(x => x.Value.Split('/')).Where(x => x.Length >= 3 && x[0] == "packages" && x[^1] == "package.json").Select(x => x[1]).Distinct(StringComparer.Ordinal))
        {
            try
            {
                var id = new PackageId(packageId);
                var rows = await ReadPackageIndexVersionsAsync(store, layout, id).ConfigureAwait(false);
                foreach (var row in rows)
                {
                    if (!ContentHash.TryParse(row.ManifestDigest.ToString(), out var digest)) { errors.Add($"index-manifest-digest:{packageId}:{row.Version}"); continue; }
                    var manifest = await repository.GetManifestAsync(id, new PackageVersion(row.Version, row.Sequence), digest).ConfigureAwait(false);
                    if (manifest is null) errors.Add($"index-manifest-missing:{packageId}:{row.Version}");
                }
            }
            catch (Exception ex) when (ex is FormatException or InvalidDataException or FileNotFoundException or CryptographicException)
            { errors.Add($"package-index:{packageId}:{ex.Message}"); }
        }

        foreach (var product in descriptor.Products)
        {
            IReadOnlyList<ReleaseIndexRow> rows;
            try { rows = await ReadReleaseIndexAsync(store, layout, product).ConfigureAwait(false); }
            catch (Exception ex) when (ex is FormatException or InvalidDataException or FileNotFoundException or CryptographicException) { errors.Add($"release-index:{product}:{ex.Message}"); continue; }
            foreach (var row in rows)
            {
                try
                {
                    var verified = await new RepositoryReader(store, descriptor).ReadReleaseAsync(product, row.ReleaseId, trusted).ConfigureAwait(false);
                    if (ContentHash.Compute(verified.EnvelopeBytes) != row.LockDigest) errors.Add($"release-index-digest:{product}/{row.ReleaseId}");
                    var coverageKey = layout.Coverage(product, row.ReleaseId);
                    var coverage = await store.OpenAsync(coverageKey).ConfigureAwait(false);
                    if (coverage is null) errors.Add($"missing-coverage:{coverageKey}");
                    else
                    {
                        await using (coverage.ConfigureAwait(false))
                        {
                            var coverageBytes = await ReadAllAsync(coverage.Content).ConfigureAwait(false);
                            try
                            {
                                var coverageDocument = RepositoryJson.Deserialize<CoverageDocument>(coverageBytes, rejectUnknownFields: true);
                                var digest = ContentHash.Compute(CoverageGenerator.SerializeForDigest(coverageDocument));
                                if (coverageDocument.Digest is not { } storedDigest || storedDigest != digest || digest != row.CoverageDigest || digest != verified.Lock.CoverageDigest)
                                    errors.Add($"coverage-digest:{coverageKey}");
                            }
                            catch (Exception ex) when (ex is FormatException or InvalidDataException or JsonException)
                            { errors.Add($"coverage:{coverageKey}:{ex.Message}"); }
                        }
                    }
                    foreach (var pin in verified.Lock.Packages)
                    {
                        var manifest = await repository.GetManifestAsync(pin.Id, pin.Version, pin.ManifestDigest).ConfigureAwait(false);
                        if (manifest is null) { errors.Add($"missing-manifest:{pin.Id}@{pin.Version}"); continue; }
                        await foreach (var entry in repository.ReadFileTableAsync(manifest).ConfigureAwait(false))
                            if (listedKeys is not null && entry.Kind == FileEntryKind.File && !listedKeys.Contains(layout.Blob(entry.Content))) errors.Add($"missing-blob:{entry.Content}");
                    }
                }
                catch (Exception ex) when (ex is FormatException or InvalidDataException or FileNotFoundException or CryptographicException)
                { errors.Add($"release-graph:{product}/{row.ReleaseId}:{ex.Message}"); }
            }
        }

        foreach (var product in descriptor.Products)
            foreach (var channel in ChannelsForProduct(product, listedKeys))
            {
                try
                {
                    var pointer = await new RepositoryReader(store, descriptor).ReadChannelAsync(product, channel, trusted).ConfigureAwait(false);
                    var release = await new RepositoryReader(store, descriptor).ReadReleaseAsync(product, pointer.Pointer.ReleaseId, trusted, pointer.Pointer.ReleaseDigest).ConfigureAwait(false);
                    if (release.Lock.Sequence != pointer.Pointer.ReleaseSequence) errors.Add($"channel-release-sequence:{product}/{channel}");
                }
                catch (FileNotFoundException) { }
                catch (Exception ex) when (ex is FormatException or InvalidDataException or CryptographicException)
                { errors.Add($"channel:{product}/{channel}:{ex.Message}"); }
            }
    }

    private static IEnumerable<string> ChannelsForProduct(string product, IReadOnlySet<ObjectKey>? listedKeys)
    {
        if (listedKeys is null)
            return ["live", "ptr"];

        return listedKeys
            .Select(key => key.Value.Split('/'))
            .Where(parts => parts.Length == 4 && parts[0] == "products" && parts[1] == product && parts[2] == "channels" && parts[3].EndsWith(".json", StringComparison.Ordinal))
            .Select(parts => parts[3][..^5])
            .Where(channel => Identifier.IsValid(channel, 64))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(channel => channel, StringComparer.Ordinal)
            .ToArray();
    }

    private static async ValueTask CheckHttpStagingIsolationAsync(Uri repositoryBase, List<string> errors, CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.None });
        var probe = new Uri(repositoryBase, "_staging/4sup-verify-known");
        try
        {
            using var response = await client.GetAsync(probe, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is not (System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Forbidden))
                errors.Add($"staging-isolation:{(int)response.StatusCode}");
        }
        catch (HttpRequestException ex) { errors.Add($"staging-isolation:{ex.Message}"); }
    }

    private static string ExpectedCacheControl(ObjectKey key)
    {
        if (key.Value == "repo.json") return "max-age=300";
        if (key.Value.Contains("/channels/", StringComparison.Ordinal)) return "max-age=30, must-revalidate";
        if (key.Value == "keys.json"
            || key.Value.EndsWith("/revocations.json", StringComparison.Ordinal)
            || key.Value.EndsWith("/index.json", StringComparison.Ordinal)
            || key.Value.Contains("/index.", StringComparison.Ordinal)
            || key.Value.EndsWith("/product.json", StringComparison.Ordinal))
            return "max-age=30, must-revalidate";
        return "public, max-age=31536000, immutable";
    }

    private static async ValueTask CheckWriteReadPairingAsync(IWritableObjectStore writeStore, IReadableObjectStore readStore, List<string> errors, CancellationToken cancellationToken = default)
    {
        // _staging is intentionally hidden from the anonymous read endpoint.  A
        // probe written there can therefore only prove isolation, never pairing.
        // Use a disposable served-tree key for the positive write/read check and
        // validate _staging separately in CheckHttpStagingIsolationAsync.
        var key = new ObjectKey($"_verify-repo/4sup-{Guid.NewGuid():N}.bin");
        var payload = Encoding.UTF8.GetBytes("4sup verify-repo pairing probe\n" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            await using var body = new MemoryStream(payload, writable: false);
            await writeStore.PutAsync(key, body, payload.LongLength, cancellationToken).ConfigureAwait(false);
            var read = await readStore.OpenAsync(key, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (read is null) { errors.Add("write-read-pairing:probe-not-visible"); return; }
            await using (read.ConfigureAwait(false))
            {
                using var received = new MemoryStream();
                await read.Content.CopyToAsync(received, cancellationToken).ConfigureAwait(false);
                if (!received.ToArray().AsSpan().SequenceEqual(payload)) errors.Add("write-read-pairing:bytes-differ");
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidDataException)
        { errors.Add($"write-read-pairing:{ex.Message}"); }
        finally
        {
            try { await writeStore.DeleteAsync(key, CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
        }
    }

    private static async ValueTask<int> GarbageCollectCommandAsync(string[] args)
    {
        var target = ParseRepositoryOnly(args.FirstOrDefault() ?? throw new FormatException("gc requires a repository target."), write: true);
        var trusted = ParseTrustedKeys(args);
        if (trusted.Count == 0) throw new CryptographicException("No trusted key was supplied for GC live-set verification.");
        await using var store = CreateReadStore(target.Address);
        if (store is not IListableObjectStore listable || store is not IWritableObjectStore writable) throw new IOException("gc requires a listable writable backend.");
        var (descriptor, layout) = await RepositoryFactory.LoadDescriptorAsync(store).ConfigureAwait(false);
        descriptor.ValidateAgainst(target.Address.BaseUri);
        var releases = new List<ReleaseLock>();
        await foreach (var key in listable.ListAsync("products/", cancellationToken: default).ConfigureAwait(false))
        {
            if (!key.Value.EndsWith("/release.lock.json", StringComparison.Ordinal)) continue;
            var read = await store.OpenAsync(key).ConfigureAwait(false); if (read is null) continue;
            await using (read.ConfigureAwait(false))
            {
                var bytes = await ReadAllAsync(read.Content).ConfigureAwait(false);
                try
                {
                    var envelope = SignedDocument.DeserializeEnvelope(bytes);
                    if (!SignedDocument.Verify(envelope, trusted, out var payload, out _)) continue;
                    var release = SignedDocument.DeserializePayload<ReleaseLock>(payload);
                    if (release.State == ReleaseState.Published) releases.Add(release);
                }
                catch (Exception) { }
            }
        }
        if (int.TryParse(GetOptionValue(args, "--keep-releases"), out var keepReleases) && keepReleases >= 0)
            releases = releases.OrderByDescending(x => x.Sequence).ThenByDescending(x => x.ReleaseId, StringComparer.Ordinal).Take(keepReleases).ToList();
        var minimumAge = TimeSpan.FromHours(24);
        var minimumAgeText = GetOptionValue(args, "--min-age");
        if (minimumAgeText is not null && !TimeSpan.TryParse(minimumAgeText, CultureInfo.InvariantCulture, out minimumAge)) throw new FormatException("--min-age must be a TimeSpan such as 24:00:00.");
        var result = await new GarbageCollector().CollectLiveAsync(listable, layout, releases, new StaticRepository(store, descriptor), new GarbageCollectionOptions { DryRun = args.Contains("--dry-run", StringComparer.Ordinal), IncludeStaging = args.Contains("--include-staging", StringComparer.Ordinal), MinimumBlobAge = minimumAge }).ConfigureAwait(false);
        Console.WriteLine(JsonSerializer.Serialize(new { marked = result.Marked.Select(x => x.Value), quarantined = result.Quarantined.Select(x => x.Value), deleted = result.Deleted.Select(x => x.Value), diagnostics = result.Diagnostics }));
        return result.Diagnostics.Any(x => x.IsError) ? 3 : 0;
    }

    private static async ValueTask<int> MirrorCommandAsync(string[] args)
    {
        if (args.Length < 2) throw new FormatException("mirror requires <source> <destination>. ");
        var source = ParseRepositoryOnly(args[0]); var destination = ParseRepositoryOnly(args[1], write: true);
        var channel = GetOptionValue(args, "--channel");
        await using var sourceStore = CreateReadStore(source.Address);
        if (sourceStore is not IListableObjectStore sourceListable) throw new IOException("mirror source must be listable.");
        await using var destinationStore = CreateWriteStore(destination.Address);
        var result = await new MirrorService().MirrorAsync(sourceListable, destinationStore, args.Contains("--delete", StringComparer.Ordinal), channel).ConfigureAwait(false);
        PrintDiagnostics(result.Diagnostics);
        Console.WriteLine(JsonSerializer.Serialize(result));
        return result.Diagnostics.Any(x => x.IsError) ? 3 : 0;
    }

    private static async ValueTask<PackageManifest?> ReadManifestByLabelAsync(IReadableObjectStore store, RepositoryLayout layout, PackageId id, string label, CancellationToken cancellationToken = default)
    {
        var result = await store.OpenAsync(layout.Package(id, new PackageVersion(label, 0)), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result is null) return null;
        await using (result.ConfigureAwait(false))
        {
            var bytes = await ReadAllAsync(result.Content, cancellationToken).ConfigureAwait(false);
            var manifest = RepositoryJson.DeserializeManifest(bytes);
            if (manifest.Id != id || !string.Equals(manifest.Version.Label, label, StringComparison.Ordinal)) throw new InvalidDataException("Package manifest identity does not match its path.");
            return manifest;
        }
    }

    private static async ValueTask<IReadOnlyList<PackageIndexVersion>> ReadPackageIndexVersionsAsync(IReadableObjectStore store, RepositoryLayout layout, PackageId packageId, CancellationToken cancellationToken = default)
    {
        var versions = new List<PackageIndexVersion>();
        var foundIndex = false;
        for (var page = 0; page < 4096; page++)
        {
            ReadResult? result = null;
            foreach (var key in PackageIndexKeys(layout, packageId, page))
            {
                result = await store.OpenAsync(key, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (result is not null) break;
            }
            if (result is null) break;
            foundIndex = true;
            await using (result.ConfigureAwait(false))
            {
                var document = RepositoryJson.Deserialize<PackageIndexDocument>(await ReadAllAsync(result.Content, cancellationToken).ConfigureAwait(false), rejectUnknownFields: true);
                if (document.SchemaVersion != 1 || !string.Equals(document.PackageId, packageId.Value, StringComparison.Ordinal)) throw new InvalidDataException("Package index identity or schema is invalid.");
                versions.AddRange(document.Versions);
                if (document.Versions.Count == 0) break;
            }
        }
        if (!foundIndex) throw new FileNotFoundException($"Package index for '{packageId}' is missing.");
        return versions;
    }

    private static async ValueTask<IReadOnlyList<ReleaseIndexRow>> ReadReleaseIndexAsync(IReadableObjectStore store, RepositoryLayout layout, string productId, CancellationToken cancellationToken = default)
    {
        var releases = new List<ReleaseIndexRow>();
        var foundIndex = false;
        for (var page = 0; page < 4096; page++)
        {
            ReadResult? result = null;
            foreach (var key in ReleaseIndexKeys(layout, productId, page))
            {
                result = await store.OpenAsync(key, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (result is not null) break;
            }
            if (result is null) break;
            foundIndex = true;
            await using (result.ConfigureAwait(false))
            {
                var document = RepositoryJson.Deserialize<ReleaseIndexDocument>(await ReadAllAsync(result.Content, cancellationToken).ConfigureAwait(false), rejectUnknownFields: true);
                if (document.SchemaVersion != 1 || !string.Equals(document.ProductId, productId, StringComparison.Ordinal)) throw new InvalidDataException("Release index identity or schema is invalid.");
                releases.AddRange(document.Releases);
                if (document.Releases.Count == 0) break;
            }
        }
        if (!foundIndex) throw new FileNotFoundException($"Release index for '{productId}' is missing.");
        return releases;
    }

    private static IEnumerable<ObjectKey> PackageIndexKeys(RepositoryLayout layout, PackageId packageId, int page)
    {
        yield return layout.PackageIndex(packageId, page);
    }

    private static IEnumerable<ObjectKey> ReleaseIndexKeys(RepositoryLayout layout, string productId, int page)
    {
        yield return layout.ReleaseIndex(productId, page);
    }

    private static async ValueTask WritePackageIndexAsync(IWritableObjectStore store, RepositoryLayout layout, PackageManifest added, PackageId packageId, CancellationToken cancellationToken = default)
    {
        var manifests = new List<PackageManifest>();
        if (store is IListableObjectStore listable)
        {
            await foreach (var key in listable.ListAsync($"packages/{packageId.Value}/", cancellationToken).ConfigureAwait(false))
            {
                if (!key.Value.EndsWith("/package.json", StringComparison.Ordinal)) continue;
                var result = await store.OpenAsync(key, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (result is null) continue;
                await using (result.ConfigureAwait(false)) manifests.Add(RepositoryJson.DeserializeManifest(await ReadAllAsync(result.Content, cancellationToken).ConfigureAwait(false)));
            }
        }
        if (!manifests.Any(x => x.Id == added.Id && x.Version.Label == added.Version.Label)) manifests.Add(added);
        await new StaticProjectionWriter(store, layout).WritePackageIndexAsync(packageId, manifests, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask WriteReleaseIndexAsync(IWritableObjectStore store, RepositoryLayout layout, ReleaseLock added, byte[] addedEnvelope, CancellationToken cancellationToken = default)
    {
        var releases = new List<(ReleaseLock Lock, byte[] EnvelopeBytes)>();
        if (store is IListableObjectStore listable)
        {
            await foreach (var key in listable.ListAsync($"products/{added.ProductId}/releases/", cancellationToken).ConfigureAwait(false))
            {
                if (!key.Value.EndsWith("/release.lock.json", StringComparison.Ordinal)) continue;
                var result = await store.OpenAsync(key, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (result is null) continue;
                await using (result.ConfigureAwait(false))
                {
                    var bytes = await ReadAllAsync(result.Content, cancellationToken).ConfigureAwait(false);
                    var envelope = SignedDocument.DeserializeEnvelope(bytes);
                    if (!string.Equals(envelope.Type, "release-lock", StringComparison.Ordinal)) continue;
                    var release = SignedDocument.DeserializePayload<ReleaseLock>(Base64Url.Decode(envelope.Payload));
                    if (release.State == ReleaseState.Published && release.ReleaseId != added.ReleaseId)
                        releases.Add((release, bytes));
                }
            }
        }
        // Index rows are derived and do not authenticate installs. Keep the newly published
        // signed envelope even when no trusted-key material is available to the publisher.
        releases.Add((added, addedEnvelope));
        await new StaticProjectionWriter(store, layout).WriteReleaseIndexAsync(added.ProductId, releases, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<object[]> ReadManifestFilesAsync(IReadableObjectStore store, RepositoryDescriptor descriptor, PackageManifest manifest, CancellationToken cancellationToken = default)
    {
        var repository = new StaticRepository(store, descriptor); var rows = new List<object>();
        await foreach (var entry in repository.ReadFileTableAsync(manifest, cancellationToken).ConfigureAwait(false)) rows.Add(new { path = entry.Path.Value, hash = entry.Kind == FileEntryKind.File ? entry.Content.ToString() : null, entry.Size, entry.Policy, entry.Kind });
        return rows.ToArray();
    }

    private static async ValueTask<IReadOnlyDictionary<PackageId, PackageManifest>> LoadPinnedManifestsAsync(StaticRepository repository, ReleaseLock release, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<PackageId, PackageManifest>();
        foreach (var pin in release.Packages) result[pin.Id] = await repository.GetManifestAsync(pin.Id, new PackageVersion(pin.Version.Label, pin.Sequence), pin.ManifestDigest, cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException($"Manifest '{pin.Id}@{pin.Version}' is missing.");
        return result;
    }

    private static async ValueTask<byte[]> ReadAllAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream(); await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false); return buffer.ToArray();
    }

    private static IReadableObjectStore CreateReadStore(RepositoryAddress address)
    {
        if (address.Backend is "http" or "https") return new HttpObjectStore(address.BaseUri);
        if (address.Backend is "file") return new LocalObjectStore(address.BaseUri.LocalPath);
        if (address.Backend is "s3") return CreateS3Store(address);
        if (address.Backend is "ftp") throw new IOException("FTP is a write transport only; configure the paired HTTP read endpoint for client and repository reads.");
        if (address.Backend is "local")
        {
            var configured = Environment.GetEnvironmentVariable("FOURSUP_LOCAL_ROOT");
            if (string.IsNullOrWhiteSpace(configured) && address.BaseUri.LocalPath == Path.DirectorySeparatorChar.ToString()) throw new IOException("The local repository alias is not configured.");
            return new LocalObjectStore(configured ?? address.BaseUri.LocalPath);
        }
        throw new IOException($"Read backend '{address.Backend}' is not configured for this CLI invocation.");
    }

    private static ManagementApiClient CreateManagementApi(RepositoryAddress target, IEnumerable<string> args)
    {
        var apiText = GetOptionValue(args, "--api");
        var apiBase = apiText is null ? new Uri(target.BaseUri.GetLeftPart(UriPartial.Authority), UriKind.Absolute) : new Uri(ResolveAlias(apiText), UriKind.Absolute);
        var token = GetOptionValue(args, "--token") ?? Environment.GetEnvironmentVariable("FOURSUP_API_TOKEN");
        if (string.IsNullOrWhiteSpace(token)) throw new UnauthorizedAccessException("Brokered management operations require --token or FOURSUP_API_TOKEN.");
        return new ManagementApiClient(apiBase, token);
    }

    private static IWritableObjectStore CreateWriteStore(RepositoryAddress address)
    {
        if (address.Backend is "file") return new LocalObjectStore(address.BaseUri.LocalPath);
        if (address.Backend is "s3") return CreateS3Store(address);
        if (address.Backend is "ftp") return CreateFtpStore(address);
        if (address.Backend is "local")
        {
            var configured = Environment.GetEnvironmentVariable("FOURSUP_LOCAL_ROOT");
            if (string.IsNullOrWhiteSpace(configured) && address.BaseUri.LocalPath == Path.DirectorySeparatorChar.ToString()) throw new IOException("The local repository alias is not configured.");
            return new LocalObjectStore(configured ?? address.BaseUri.LocalPath);
        }
        throw new IOException($"Write backend '{address.Backend}' is not configured for this CLI invocation.");
    }

    private static S3ObjectStore CreateS3Store(RepositoryAddress address)
    {
        var endpoint = Environment.GetEnvironmentVariable("FOURSUP_S3_ENDPOINT");
        var accessKey = Environment.GetEnvironmentVariable("FOURSUP_S3_ACCESS_KEY");
        var secretKey = Environment.GetEnvironmentVariable("FOURSUP_S3_SECRET_KEY");
        var profileText = Environment.GetEnvironmentVariable("FOURSUP_S3_PROVIDER") ?? "aws";
        if (!Enum.TryParse<S3ProviderProfile>(profileText, ignoreCase: true, out var profile))
            throw new FormatException($"Unknown FOURSUP_S3_PROVIDER '{profileText}'. Use aws, minio, r2, b2, or generic.");
        var serviceUrl = string.IsNullOrWhiteSpace(endpoint) ? null : new Uri(endpoint);
        return profile is S3ProviderProfile.Aws or S3ProviderProfile.Minio or S3ProviderProfile.R2
            ? new S3ConditionalObjectStore(address.BaseUri.Host, address.BaseUri.AbsolutePath.Trim('/'), serviceUrl: serviceUrl, accessKey: accessKey, secretKey: secretKey, providerProfile: profile)
            : new S3ObjectStore(address.BaseUri.Host, address.BaseUri.AbsolutePath.Trim('/'), serviceUrl: serviceUrl, accessKey: accessKey, secretKey: secretKey, providerProfile: profile);
    }

    private static FtpObjectStore CreateFtpStore(RepositoryAddress address)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("FOURSUP_FTP_TLS"), "0", StringComparison.Ordinal) && !string.Equals(Environment.GetEnvironmentVariable("FOURSUP_INSECURE_TRANSPORT"), "1", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Plain FTP is disabled; pass --insecure-transport (or FOURSUP_INSECURE_TRANSPORT=1) to make the downgrade explicit.");
        var user = Environment.GetEnvironmentVariable("FOURSUP_FTP_USER") ?? address.BaseUri.UserInfo.Split(':').FirstOrDefault() ?? "anonymous";
        var password = Environment.GetEnvironmentVariable("FOURSUP_FTP_PASSWORD") ?? "anonymous@";
        return new FtpObjectStore(address.BaseUri, new NetworkCredential(user, password), enableSsl: !string.Equals(Environment.GetEnvironmentVariable("FOURSUP_FTP_TLS"), "0", StringComparison.Ordinal));
    }

    private sealed record PackageIndexDocument
    {
        public required int SchemaVersion { get; init; }
        public required string PackageId { get; init; }
        public required IReadOnlyList<PackageIndexVersion> Versions { get; init; }
    }

    private sealed record PackageIndexVersion
    {
        public required string Version { get; init; }
        public required long Sequence { get; init; }
        public required string ManifestPath { get; init; }
        public required ContentHash ManifestDigest { get; init; }
        public required int FileCount { get; init; }
        public required long InstallSize { get; init; }
        public required long DownloadSize { get; init; }
    }

    private sealed record ReleaseIndexDocument
    {
        public required int SchemaVersion { get; init; }
        public required string ProductId { get; init; }
        public required IReadOnlyList<ReleaseIndexRow> Releases { get; init; }
    }

    private sealed record ReleaseIndexRow
    {
        public required string ReleaseId { get; init; }
        public required long Sequence { get; init; }
        public required string LockPath { get; init; }
        public required ContentHash LockDigest { get; init; }
        public required ContentHash CoverageDigest { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
    }

    private sealed record CommandTarget(RepositoryAddress Address, string PackageOrProduct, string? ReleaseOrVersion);

    private static CommandTarget ParseTarget(string value, string coordinateKind, string? defaultCoordinate, bool write = false)
    {
        value = ResolveAlias(value, write);
        var at = value.LastIndexOf('@');
        var left = at > 0 ? value[..at] : value;
        var coordinate = at > 0 ? value[(at + 1)..] : defaultCoordinate;
        if (string.IsNullOrWhiteSpace(coordinate)) throw new FormatException("A target coordinate is required.");
        var raw = coordinate!;
        var normalized = coordinate.Contains(':', StringComparison.Ordinal) ? coordinate : coordinateKind + ":" + coordinate;
        var address = RepositoryAddress.Parse(left + "@" + normalized);
        return new CommandTarget(address, address.ProductId, raw.StartsWith(coordinateKind + ":", StringComparison.Ordinal) ? raw[(coordinateKind.Length + 1)..] : raw);
    }

    private static CommandTarget ParseRepositoryOnly(string value, bool write = false)
    {
        var configuration = new ConfigurationLoader().Load();
        var configuredAlias = configuration.Aliases.FirstOrDefault(x => string.Equals(x.Name, value, StringComparison.Ordinal));
        var configuredStorage = configuration.Storages.FirstOrDefault(x => string.Equals(x.Name, value, StringComparison.Ordinal));
        var resolved = (write ? configuredStorage?.WriteUrl : configuredStorage?.ReadUrl) ?? configuredAlias?.Address ?? value;
        if (Uri.TryCreate(resolved, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https" or "file" or "s3" or "ftp")
            return new CommandTarget(new RepositoryAddress(absolute.Scheme, new Uri(absolute.ToString().TrimEnd('/') + "/"), "repo", "channel:live"), "repo", null);
        if (Path.IsPathFullyQualified(resolved))
        {
            var path = Path.GetFullPath(resolved);
            var uriPath = (path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar).Replace(Path.DirectorySeparatorChar, '/');
            return new CommandTarget(new RepositoryAddress("file", new Uri("file://" + (uriPath.StartsWith('/') ? string.Empty : "/") + uriPath, UriKind.Absolute), "repo", "channel:live"), "repo", null);
        }
        throw new FormatException("Repository target must be an absolute URI/path or a configured alias.");
    }

    private static string ResolveAlias(string value, bool write = false)
    {
        var at = value.LastIndexOf('@');
        if (at <= 0) return value;
        var left = value[..at];
        var slash = left.IndexOf('/');
        if (slash <= 0) return value;
        var aliasName = left[..slash];
        var configuration = new ConfigurationLoader().Load();
        var alias = configuration.Aliases.FirstOrDefault(x => string.Equals(x.Name, aliasName, StringComparison.Ordinal));
        var storage = configuration.Storages.FirstOrDefault(x => string.Equals(x.Name, aliasName, StringComparison.Ordinal));
        var endpoint = (write ? storage?.WriteUrl : storage?.ReadUrl) ?? alias?.Address;
        return endpoint is null ? value : endpoint.TrimEnd('/') + "/" + left[(slash + 1)..] + value[at..];
    }

    private static IReadOnlyDictionary<string, byte[]> ParseTrustedKeys(IEnumerable<string> args)
    {
        var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var configured = Environment.GetEnvironmentVariable("FOURSUP_TRUSTED_KEYS");
        var values = OptionValues(args, "--trusted-key").Concat(string.IsNullOrWhiteSpace(configured)
            ? []
            : configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        foreach (var value in values)
        {
            var separator = value.IndexOf(':');
            if (separator <= 0 || separator == value.Length - 1) throw new FormatException("--trusted-key must use keyId:base64url.");
            var keyId = value[..separator];
            if (!Identifier.IsValid(keyId, "keyId", out var error)) throw new FormatException(error);
            keys[keyId] = Base64Url.Decode(value[(separator + 1)..]);
            if (keys[keyId].Length != 32) throw new FormatException($"Trusted key '{keyId}' is not a 32-byte Ed25519 public key.");
        }
        return keys;
    }

    private static IReadOnlyDictionary<string, byte[]> ParseTrustedKeysOrFirstParty(IEnumerable<string> args)
    {
        var supplied = ParseTrustedKeys(args);
        return supplied.Count == 0
            ? new Dictionary<string, byte[]>(CompiledTrustRoots.FirstParty, StringComparer.Ordinal)
            : supplied;
    }

    private static void EnsureDocumentSigner(SignedEnvelope envelope, IReadOnlyDictionary<string, VerificationKey> keys, DateTimeOffset documentTime)
    {
        var skewHours = double.TryParse(Environment.GetEnvironmentVariable("FOURSUP_CLOCK_SKEW_HOURS"), NumberStyles.Float, CultureInfo.InvariantCulture, out var configured) && configured >= 0 && configured <= 168 ? configured : 24;
        if (!SignedDocument.VerifyCryptographically(envelope, keys, out _, out _, out var error, signer => SignedDocument.IsKeyValidAt(keys, signer, documentTime, out _, TimeSpan.FromHours(skewHours)))) throw new CryptographicException(error);
    }

    private static (string KeyId, byte[] PrivateKey) ParseSigningKey(IEnumerable<string> args)
    {
        var value = GetOptionValue(args, "--sign") ?? throw new FormatException("A signing key id is required via --sign=<keyId>; the private key must come from FOURSUP_SIGNING_KEY_<keyId> or FOURSUP_SIGNING_KEYS.");
        var separator = value.IndexOf(':');
        if (separator > 0) throw new UnauthorizedAccessException("Private signing keys may not be supplied in process arguments; use FOURSUP_SIGNING_KEY_<keyId> or FOURSUP_SIGNING_KEYS.");
        var keyId = separator > 0 ? value[..separator] : value;
        if (!Identifier.IsValid(keyId, "keyId", out var error)) throw new FormatException(error);
        var encoded = separator > 0 ? value[(separator + 1)..] : Environment.GetEnvironmentVariable("FOURSUP_SIGNING_KEY_" + keyId);
        if (string.IsNullOrWhiteSpace(encoded))
        {
            var configured = Environment.GetEnvironmentVariable("FOURSUP_SIGNING_KEYS");
            encoded = configured?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => x.IndexOf(':') is var split && split > 0 && string.Equals(x[..split], keyId, StringComparison.Ordinal) ? x[(split + 1)..] : null)
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        }
        if (string.IsNullOrWhiteSpace(encoded)) throw new FormatException($"No private signing key is configured for '{keyId}'; set FOURSUP_SIGNING_KEY_{keyId} or FOURSUP_SIGNING_KEYS.");
        var privateKey = Base64Url.Decode(encoded);
        if (privateKey.Length != 32) throw new FormatException("Ed25519 private keys must be 32 bytes.");
        return (keyId, privateKey);
    }

    private static string? GetOptionValue(IEnumerable<string> args, string name)
    {
        var values = args.ToArray();
        for (var index = 0; index < values.Length; index++)
        {
            if (string.Equals(values[index], name, StringComparison.Ordinal) && index + 1 < values.Length) return values[index + 1];
            if (values[index].StartsWith(name + "=", StringComparison.Ordinal)) return values[index][(name.Length + 1)..];
        }
        return null;
    }

    private static void PrintDiagnostics(IEnumerable<Diagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics) Console.Error.WriteLine($"{diagnostic.Code} {diagnostic.Severity}: {diagnostic.Message}");
    }

    private static void PrintPlan(InstallPlan plan, bool json)
    {
        if (json) Console.WriteLine(JsonSerializer.Serialize(new { operations = plan.Operations.Length, blobs = plan.BlobsToFetch.Length, plan.BytesToDownload, plan.BytesToWrite, plan.NetInstallDelta, plan.PeakFreeSpaceRequiredByVolume }));
        else Console.WriteLine($"operations={plan.Operations.Length} blobs={plan.BlobsToFetch.Length} download={plan.BytesToDownload} write={plan.BytesToWrite} net={plan.NetInstallDelta}");
    }
}

public sealed record RepositoryAddress(string Backend, Uri BaseUri, string ProductId, string ReleaseRef)
{
    public static RepositoryAddress Parse(string value)
    {
        var at = value.LastIndexOf('@'); if (at <= 0 || at == value.Length - 1) throw new FormatException("address must end in @channel or @release:id.");
        var coordinate = value[(at + 1)..]; var release = coordinate.StartsWith("release:", StringComparison.Ordinal) || coordinate.StartsWith("channel:", StringComparison.Ordinal) ? coordinate : "channel:" + coordinate;
        var left = value[..at]; string backend; Uri baseUri; string product;
        if (Uri.TryCreate(left, UriKind.Absolute, out var absolute))
        {
            if (absolute.Scheme is not ("http" or "https" or "s3" or "ftp" or "file")) throw new FormatException($"Unsupported repository backend '{absolute.Scheme}'.");
            backend = absolute.Scheme; var path = absolute.AbsolutePath.Trim('/'); product = path.Split('/').LastOrDefault() ?? throw new FormatException("product id is missing."); baseUri = new UriBuilder(absolute) { Path = absolute.AbsolutePath[..(absolute.AbsolutePath.LastIndexOf(product, StringComparison.Ordinal))] }.Uri;
        }
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
