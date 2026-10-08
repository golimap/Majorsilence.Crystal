using System.Collections.Concurrent;
using System.Text.Json;
using Majorsilence.Crystal.Converter;
using Majorsilence.Crystal.Parser;

namespace Majorsilence.Crystal.Cli.Commands;

public static class ConvertCommand
{
    public static int Run(string[] args)
    {
        string? input = null, outDir = null, configPath = "migration.config.json";
        bool? recursive = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-o" when i + 1 < args.Length: outDir = args[++i]; break;
                case "-r": recursive = true; break;
                case "--config" when i + 1 < args.Length: configPath = args[++i]; break;
                default:
                    if (input is null && !args[i].StartsWith('-')) { input = args[i]; break; }
                    Console.Error.WriteLine($"error: unexpected argument '{args[i]}'");
                    return 2;
            }
        }

        MigrationConfig config = LoadConfig(configPath);
        input ??= config.InputDirectory;
        outDir ??= config.OutputDirectory;
        bool isRecursive = recursive ?? config.Recursive;
        if (input is null)
        {
            Console.Error.WriteLine("error: convert requires a .rpt file or directory, or InputDirectory in migration.config.json");
            return 2;
        }

        string root;
        List<string> files;
        if (File.Exists(input))
        {
            root = Path.GetDirectoryName(Path.GetFullPath(input)) ?? ".";
            files = [Path.GetFullPath(input)];
        }
        else if (Directory.Exists(input))
        {
            root = Path.GetFullPath(input);
            files = Directory.EnumerateFiles(root, "*.rpt", isRecursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }
        else
        {
            Console.Error.WriteLine($"error: '{input}' not found");
            return 2;
        }
        if (files.Count == 0)
        {
            Console.Error.WriteLine("error: no .rpt files found");
            return 2;
        }

        string logDirectory = config.LogDirectory ?? "logs";
        Directory.CreateDirectory(logDirectory);
        string logPath = Path.Combine(logDirectory, $"migration-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log");
        var log = new ConcurrentQueue<string>();
        log.Enqueue($"Started: {DateTimeOffset.Now:O}");
        log.Enqueue($"Input: {Path.GetFullPath(input)}");
        log.Enqueue($"Output: {(outDir is null ? "next to source" : Path.GetFullPath(outDir))}");

        int converted = 0, failed = 0, warned = 0;
        var consoleLock = new object();
        Parallel.ForEach(files, file =>
        {
            string rel = Path.GetRelativePath(root, file);
            string target = outDir is null ? Path.ChangeExtension(file, ".rdl") : Path.Combine(outDir, Path.ChangeExtension(rel, ".rdl"));
            var lines = new List<string>();
            bool ok = false, hasWarnings = false;
            try
            {
                var result = RptParser.Parse(file);
                if (!result.Success || result.Report is null)
                    foreach (var err in result.Errors) lines.Add($"  error: {err.Replace(file, rel)}");
                else
                {
                    foreach (var warn in result.Warnings) lines.Add($"  warn: {warn.Replace(file, rel)}");
                    hasWarnings = result.Warnings.Count > 0;
                    string stem = Path.GetFileNameWithoutExtension(target);
                    string? sqlQuery = ResolveSqlQuery(config.SqlQueries, rel, file);
                    string rdl = new RdlConverter
                    {
                        ConnectionStringOverride = config.DatabaseConnection,
                        UseWindowsAuthentication = config.WindowsAuthentication,
                        SqlQueryOverride = sqlQuery
                    }.Convert(result.Report, $"{stem}_");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllText(target, rdl);
                    WriteSubreportCompanions(result.Report, target, config.DatabaseConnection, config.WindowsAuthentication);
                    ok = true;
                }
            }
            catch (Exception ex) { lines.Add($"  error: {ex.GetType().Name}: {ex.Message.Replace(file, rel)}"); }

            lock (consoleLock)
            {
                if (ok) { converted++; if (hasWarnings) warned++; } else failed++;
                log.Enqueue($"{(ok ? (hasWarnings ? "WARN" : "OK") : "FAIL")}: {rel}");
                foreach (string line in lines) log.Enqueue(line);
                if (!ok || hasWarnings) { Console.WriteLine($"{(ok ? "warn" : "FAIL")}: {rel}"); lines.ForEach(Console.WriteLine); }
            }
        });

        string summary = $"converted {converted}/{files.Count} ({warned} with warnings, {failed} failed)";
        log.Enqueue(summary);
        log.Enqueue($"Finished: {DateTimeOffset.Now:O}");
        File.WriteAllLines(logPath, log);
        Console.WriteLine();
        Console.WriteLine(summary);
        Console.WriteLine($"log: {logPath}");
        return failed == 0 ? 0 : 1;
    }

    private static MigrationConfig LoadConfig(string configPath)
    {
        if (!File.Exists(configPath))
        {
            string bundledConfigPath = Path.Combine(AppContext.BaseDirectory, "migration.config.json");
            if (!File.Exists(bundledConfigPath)) return new MigrationConfig();
            configPath = bundledConfigPath;
        }
        try { return JsonSerializer.Deserialize<MigrationConfig>(File.ReadAllText(configPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new MigrationConfig(); }
        catch (JsonException ex) { throw new InvalidOperationException($"Invalid migration config '{configPath}': {ex.Message}", ex); }
    }

    private static string? ResolveSqlQuery(IReadOnlyDictionary<string, string>? queries, string relativePath, string filePath)
    {
        if (queries is null) return null;
        return queries.TryGetValue(relativePath, out string? query) ? query
            : queries.TryGetValue(Path.GetFileName(filePath), out query) ? query
            : null;
    }

    private static void WriteSubreportCompanions(Majorsilence.Crystal.Model.ReportDefinition report, string mainRdlPath,
        string? databaseConnection, bool windowsAuthentication)
    {
        string dir = Path.GetDirectoryName(mainRdlPath)!;
        string stem = Path.GetFileNameWithoutExtension(mainRdlPath);
        foreach (var sub in report.Sections.SelectMany(s => s.Objects).OfType<Majorsilence.Crystal.Model.Objects.SubreportObject>().Where(s => s.Report is not null))
        {
            string name = RdlConverter.SubreportRdlName($"{stem}_", sub.SubreportName);
            string path = Path.Combine(dir, name + ".rdl");
            File.WriteAllText(path, new RdlConverter
            {
                ConnectionStringOverride = databaseConnection,
                UseWindowsAuthentication = windowsAuthentication
            }.Convert(sub.Report!, $"{name}_"));
            WriteSubreportCompanions(sub.Report!, path, databaseConnection, windowsAuthentication);
        }
    }

    private sealed class MigrationConfig
    {
        public string? InputDirectory { get; init; }
        public string? OutputDirectory { get; init; }
        public string? LogDirectory { get; init; }
        public string? DatabaseConnection { get; init; }
        public bool WindowsAuthentication { get; init; }
        public Dictionary<string, string>? SqlQueries { get; init; }
        public bool Recursive { get; init; }
    }
}
