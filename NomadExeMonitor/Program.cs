using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NomadExeMonitor;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    static Program()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public static async Task<int> Main(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine();
            Options.PrintHelp();
            return 64;
        }

        if (options.Help)
        {
            Options.PrintHelp();
            return 0;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            using var client = new NomadClient(options, JsonOptions);

            if (options.WatchSeconds > 0)
            {
                while (!cts.IsCancellationRequested)
                {
                    var snapshot = await MonitorService.BuildSnapshotAsync(client, options, cts.Token);

                    if (!Console.IsOutputRedirected && options.Format == OutputFormat.Table)
                        Console.Clear();

                    OutputWriter.Write(snapshot, options, JsonOptions);

                    await Task.Delay(TimeSpan.FromSeconds(options.WatchSeconds), cts.Token);
                }

                return 0;
            }

            var result = await MonitorService.BuildSnapshotAsync(client, options, cts.Token);
            OutputWriter.Write(result, options, JsonOptions);

            if (!options.NoFailExit && result.Rows.Any(r => r.Status == AppStatus.Failed))
                return 2;

            return 0;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return 130;
        }
        catch (NomadApiException ex)
        {
            Console.Error.WriteLine($"Nomad API error: {ex.Message}");
            return 3;
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"HTTP error: {ex.Message}");
            return 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fatal error: {ex.Message}");
            if (options.Verbose)
                Console.Error.WriteLine(ex);
            return 3;
        }
    }
}

internal static class MonitorService
{
    public static async Task<Snapshot> BuildSnapshotAsync(
        NomadClient client,
        Options options,
        CancellationToken cancellationToken)
    {
        var warnings = new ConcurrentQueue<string>();

        var allocations = await client.GetAllocationsAsync(options.Namespace, cancellationToken);
        var currentAllocations = SelectCurrentAllocations(allocations);

        if (!string.IsNullOrWhiteSpace(options.JobFilter))
        {
            currentAllocations = currentAllocations
                .Where(a => ContainsIgnoreCase(a.JobID, options.JobFilter!))
                .ToList();
        }

        if (!string.IsNullOrWhiteSpace(options.ServerFilter))
        {
            currentAllocations = currentAllocations
                .Where(a => ContainsIgnoreCase(a.NodeName ?? string.Empty, options.ServerFilter!))
                .ToList();
        }

        var jobKeys = currentAllocations
            .Select(a => new JobKey(a.Namespace ?? "default", a.JobID))
            .Distinct()
            .ToList();

        var versionsByJob = new ConcurrentDictionary<JobKey, JobVersionsResponse>();

        await AsyncUtil.ForEachBoundedAsync(
            jobKeys,
            options.Parallelism,
            async key =>
            {
                try
                {
                    var versions = await client.GetJobVersionsAsync(key.Namespace, key.JobID, cancellationToken);
                    versionsByJob[key] = versions;
                }
                catch (NomadApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    warnings.Enqueue($"Job versions not found: {key.Namespace}/{key.JobID}; allocation snapshot fallback will be used.");
                }
            },
            cancellationToken);

        // Normally the exact JobVersion is available from /versions. If it is not
        // (for example after deregistration/GC timing), fetch only those allocations
        // individually and use the Job snapshot embedded in the allocation.
        var fallbackJobByAllocation = new ConcurrentDictionary<string, JobDefinition>();
        var unresolved = currentAllocations
            .Where(a => !TryResolveJob(a, versionsByJob, out _))
            .ToList();

        await AsyncUtil.ForEachBoundedAsync(
            unresolved,
            options.Parallelism,
            async alloc =>
            {
                try
                {
                    var detail = await client.GetAllocationAsync(alloc.ID, cancellationToken);
                    if (detail.Job is not null)
                        fallbackJobByAllocation[alloc.ID] = detail.Job;
                }
                catch (NomadApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    warnings.Enqueue($"Allocation detail no longer exists: {ShortId(alloc.ID)} ({alloc.Name}).");
                }
            },
            cancellationToken);

        var rows = new List<AppRow>();

        foreach (var alloc in currentAllocations)
        {
            JobDefinition? job = null;
            if (!TryResolveJob(alloc, versionsByJob, out job))
                fallbackJobByAllocation.TryGetValue(alloc.ID, out job);

            if (job is null)
            {
                warnings.Enqueue($"Could not resolve job definition for allocation {ShortId(alloc.ID)} ({alloc.Namespace}/{alloc.JobID}, version {alloc.JobVersion}).");
                continue;
            }

            var group = job.TaskGroups?.FirstOrDefault(g =>
                string.Equals(g.Name, alloc.TaskGroup, StringComparison.Ordinal));

            if (group?.Tasks is null)
                continue;

            foreach (var task in group.Tasks)
            {
                if (!string.Equals(task.Driver, "raw_exec", StringComparison.OrdinalIgnoreCase))
                    continue;

                var command = TaskConfig.GetString(task.Config, "command");
                if (string.IsNullOrWhiteSpace(command))
                    continue;

                TaskState? taskState = null;
                alloc.TaskStates?.TryGetValue(task.Name, out taskState);

                var status = StatusClassifier.Classify(alloc, taskState);
                var lastEvent = taskState?.Events?
                    .OrderByDescending(e => e.Time)
                    .FirstOrDefault();

                var row = new AppRow
                {
                    Exe = TaskConfig.GetExecutableName(command),
                    Command = command,
                    Job = alloc.JobID,
                    Namespace = alloc.Namespace ?? "default",
                    Group = alloc.TaskGroup,
                    Task = task.Name,
                    Server = alloc.NodeName ?? "(unknown)",
                    Status = status,
                    Restarts = taskState?.Restarts ?? 0,
                    StartedAt = NormalizeDate(taskState?.StartedAt),
                    FinishedAt = NormalizeDate(taskState?.FinishedAt),
                    LastEvent = lastEvent?.Type ?? string.Empty,
                    LastEventMessage = EventText(lastEvent),
                    AllocationId = alloc.ID,
                    AllocationName = alloc.Name,
                    JobVersion = alloc.JobVersion,
                    DesiredStatus = alloc.DesiredStatus,
                    ClientStatus = alloc.ClientStatus
                };

                if (options.OnlyProblems && row.Status == AppStatus.Running)
                    continue;

                rows.Add(row);
            }
        }

        rows = rows
            .OrderBy(r => StatusClassifier.SortOrder(r.Status))
            .ThenBy(r => r.Exe, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Job, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Server, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new Snapshot
        {
            GeneratedAt = DateTimeOffset.Now,
            NomadAddress = client.Address,
            Namespace = options.Namespace,
            Rows = rows,
            Warnings = warnings.Distinct().OrderBy(x => x).ToList()
        };
    }

    private static bool TryResolveJob(
        Allocation alloc,
        ConcurrentDictionary<JobKey, JobVersionsResponse> versionsByJob,
        out JobDefinition? job)
    {
        var key = new JobKey(alloc.Namespace ?? "default", alloc.JobID);
        job = null;

        if (!versionsByJob.TryGetValue(key, out var response) || response.Versions is null)
            return false;

        job = response.Versions.FirstOrDefault(v => v.Version == alloc.JobVersion);
        return job is not null;
    }

    private static List<Allocation> SelectCurrentAllocations(List<Allocation> allocations)
    {
        // Allocation names are stable logical slots such as job.group[0].
        // For system/sysbatch jobs, however, the same logical slot exists once per
        // eligible node, so the node must also be part of the identity. For other job
        // types it must not be included, otherwise a reschedule to another node would
        // leave the old allocation visible as another current row.
        //
        // During rolling updates/reschedules several historical allocations can exist
        // for the same identity. Prefer the leaf of NextAllocation if that relationship
        // is available; otherwise select the newest ModifyIndex/ModifyTime.
        return allocations
            .GroupBy(a => new
            {
                Namespace = a.Namespace ?? "default",
                a.JobID,
                a.TaskGroup,
                Node = IsPerNodeJob(a.JobType) ? NodeIdentity(a) : string.Empty,
                Identity = string.IsNullOrWhiteSpace(a.Name) ? a.ID : a.Name
            })
            .Select(group =>
            {
                var ids = group.Select(a => a.ID).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var leaves = group
                    .Where(a => string.IsNullOrWhiteSpace(a.NextAllocation) || !ids.Contains(a.NextAllocation!))
                    .ToList();

                var candidates = leaves.Count > 0 ? leaves : group.ToList();
                return candidates
                    .OrderByDescending(a => a.ModifyIndex)
                    .ThenByDescending(a => a.ModifyTime)
                    .ThenByDescending(a => a.CreateIndex)
                    .First();
            })
            .ToList();
    }

    private static bool IsPerNodeJob(string? jobType) =>
        string.Equals(jobType, "system", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(jobType, "sysbatch", StringComparison.OrdinalIgnoreCase);

    private static string NodeIdentity(Allocation allocation)
    {
        if (!string.IsNullOrWhiteSpace(allocation.NodeID))
            return allocation.NodeID;

        if (!string.IsNullOrWhiteSpace(allocation.NodeName))
            return allocation.NodeName;

        // Do not collapse allocations when Nomad omitted both node fields.
        return allocation.ID;
    }

    private static DateTimeOffset? NormalizeDate(DateTimeOffset? value)
    {
        if (value is null || value.Value.Year <= 1)
            return null;
        return value;
    }

    private static string EventText(TaskEvent? e)
    {
        if (e is null)
            return string.Empty;

        var value = FirstNonEmpty(
            e.DisplayMessage,
            e.DriverError,
            e.SetupError,
            e.RestartReason,
            e.Message,
            e.KillReason);

        return SingleLine(value ?? string.Empty);
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string SingleLine(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static bool ContainsIgnoreCase(string source, string value) =>
        source.Contains(value, StringComparison.OrdinalIgnoreCase);

    private static string ShortId(string id) => id.Length <= 8 ? id : id[..8];
}

internal static class StatusClassifier
{
    public static AppStatus Classify(Allocation allocation, TaskState? task)
    {
        if (task?.Failed == true)
            return AppStatus.Failed;

        if (EqualsAny(allocation.ClientStatus, "failed", "lost"))
            return AppStatus.Failed;

        if (task is not null)
        {
            if (EqualsAny(task.State, "running"))
                return AppStatus.Running;

            if (EqualsAny(task.State, "pending"))
                return AppStatus.Pending;

            if (EqualsAny(task.State, "dead"))
            {
                if (EqualsAny(allocation.DesiredStatus, "stop") ||
                    EqualsAny(allocation.ClientStatus, "complete"))
                    return AppStatus.Stopped;

                return AppStatus.Failed;
            }
        }

        if (EqualsAny(allocation.DesiredStatus, "stop"))
            return AppStatus.Stopped;

        if (EqualsAny(allocation.ClientStatus, "pending"))
            return AppStatus.Pending;

        if (EqualsAny(allocation.ClientStatus, "unknown"))
            return AppStatus.Unknown;

        return AppStatus.Unknown;
    }

    public static int SortOrder(AppStatus status) => status switch
    {
        AppStatus.Failed => 0,
        AppStatus.Unknown => 1,
        AppStatus.Pending => 2,
        AppStatus.Stopped => 3,
        AppStatus.Running => 4,
        _ => 9
    };

    private static bool EqualsAny(string? value, params string[] expected) =>
        expected.Any(x => string.Equals(value, x, StringComparison.OrdinalIgnoreCase));
}

internal static class TaskConfig
{
    public static string? GetString(Dictionary<string, JsonElement>? config, string name)
    {
        if (config is null || !config.TryGetValue(name, out var element))
            return null;

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.ToString(),
            _ => element.ToString()
        };
    }

    public static string GetExecutableName(string command)
    {
        var value = command.Trim().Trim('"', '\'');
        value = value.Replace('\\', '/');
        var slash = value.LastIndexOf('/');
        return slash >= 0 ? value[(slash + 1)..] : value;
    }
}

internal static class OutputWriter
{
    public static void Write(Snapshot snapshot, Options options, JsonSerializerOptions jsonOptions)
    {
        switch (options.Format)
        {
            case OutputFormat.Json:
                Console.WriteLine(JsonSerializer.Serialize(snapshot.Rows, jsonOptions));
                break;
            case OutputFormat.Csv:
                WriteCsv(snapshot.Rows);
                break;
            default:
                WriteTable(snapshot, options);
                break;
        }

        if (options.Verbose && snapshot.Warnings.Count > 0)
        {
            foreach (var warning in snapshot.Warnings)
                Console.Error.WriteLine($"WARNING: {warning}");
        }
    }

    private static void WriteTable(Snapshot snapshot, Options options)
    {
        Console.WriteLine($"Nomad EXE Monitor  {snapshot.GeneratedAt:yyyy-MM-dd HH:mm:ss zzz}");
        Console.WriteLine($"Nomad: {snapshot.NomadAddress}   Namespace: {snapshot.Namespace}");
        Console.WriteLine();

        if (options.Wide)
        {
            Console.WriteLine(
                $"{Fit("STATUS", 9)} {Fit("EXE", 26)} {Fit("JOB", 22)} {Fit("SERVER", 20)} " +
                $"{Fit("NS", 12)} {Fit("TASK", 18)} {Fit("RESTART", 7)} {Fit("STARTED", 19)} {Fit("LAST EVENT", 18)} DETAIL");
            Console.WriteLine(new string('-', 165));

            foreach (var row in snapshot.Rows)
            {
                Console.WriteLine(
                    $"{Fit(row.Status.ToString(), 9)} {Fit(row.Exe, 26)} {Fit(row.Job, 22)} {Fit(row.Server, 20)} " +
                    $"{Fit(row.Namespace, 12)} {Fit(row.Task, 18)} {Fit(row.Restarts.ToString(CultureInfo.InvariantCulture), 7)} " +
                    $"{Fit(FormatTime(row.StartedAt), 19)} {Fit(row.LastEvent, 18)} {row.LastEventMessage}");
            }
        }
        else
        {
            Console.WriteLine(
                $"{Fit("STATUS", 9)} {Fit("EXE", 30)} {Fit("JOB", 26)} {Fit("SERVER", 24)} {Fit("RESTART", 7)} {Fit("STARTED", 19)}");
            Console.WriteLine(new string('-', 122));

            foreach (var row in snapshot.Rows)
            {
                Console.WriteLine(
                    $"{Fit(row.Status.ToString(), 9)} {Fit(row.Exe, 30)} {Fit(row.Job, 26)} {Fit(row.Server, 24)} " +
                    $"{Fit(row.Restarts.ToString(CultureInfo.InvariantCulture), 7)} {Fit(FormatTime(row.StartedAt), 19)}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(
            $"Total: {snapshot.Rows.Count}  " +
            $"Running: {snapshot.Rows.Count(r => r.Status == AppStatus.Running)}  " +
            $"Failed: {snapshot.Rows.Count(r => r.Status == AppStatus.Failed)}  " +
            $"Stopped: {snapshot.Rows.Count(r => r.Status == AppStatus.Stopped)}  " +
            $"Pending: {snapshot.Rows.Count(r => r.Status == AppStatus.Pending)}  " +
            $"Unknown: {snapshot.Rows.Count(r => r.Status == AppStatus.Unknown)}");
    }

    private static void WriteCsv(IEnumerable<AppRow> rows)
    {
        Console.WriteLine("status,exe,job,namespace,group,task,server,restarts,startedAt,finishedAt,lastEvent,lastEventMessage,allocationId,allocationName,jobVersion,desiredStatus,clientStatus,command");
        foreach (var r in rows)
        {
            Console.WriteLine(string.Join(',', new[]
            {
                Csv(r.Status.ToString()), Csv(r.Exe), Csv(r.Job), Csv(r.Namespace), Csv(r.Group), Csv(r.Task),
                Csv(r.Server), Csv(r.Restarts.ToString(CultureInfo.InvariantCulture)), Csv(r.StartedAt?.ToString("O") ?? ""),
                Csv(r.FinishedAt?.ToString("O") ?? ""), Csv(r.LastEvent), Csv(r.LastEventMessage), Csv(r.AllocationId),
                Csv(r.AllocationName), Csv(r.JobVersion.ToString(CultureInfo.InvariantCulture)), Csv(r.DesiredStatus),
                Csv(r.ClientStatus), Csv(r.Command)
            }));
        }
    }

    private static string Csv(string value)
    {
        var escaped = value.Replace("\"", "\"\"");
        return $"\"{escaped}\"";
    }

    private static string Fit(string value, int width)
    {
        value ??= string.Empty;
        if (value.Length == width)
            return value;
        if (value.Length < width)
            return value.PadRight(width);
        if (width <= 1)
            return value[..width];
        return value[..(width - 1)] + "…";
    }

    private static string FormatTime(DateTimeOffset? value) =>
        value?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "-";
}

internal sealed class NomadClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _jsonOptions;

    public string Address { get; }

    public NomadClient(Options options, JsonSerializerOptions jsonOptions)
    {
        _jsonOptions = jsonOptions;
        Address = options.Address.TrimEnd('/');

        var handler = BuildHandler(options);
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(Address + "/", UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds)
        };

        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("NomadExeMonitor/1.0");

        if (!string.IsNullOrWhiteSpace(options.Token))
            _http.DefaultRequestHeaders.Add("X-Nomad-Token", options.Token);
    }

    public Task<List<Allocation>> GetAllocationsAsync(string ns, CancellationToken ct) =>
        GetAsync<List<Allocation>>(
            "v1/allocations",
            new Dictionary<string, string?>
            {
                ["namespace"] = ns,
                ["task_states"] = "true",
                ["reverse"] = "true"
            },
            ct);

    public Task<JobVersionsResponse> GetJobVersionsAsync(string ns, string jobId, CancellationToken ct) =>
        GetAsync<JobVersionsResponse>(
            $"v1/job/{Uri.EscapeDataString(jobId)}/versions",
            new Dictionary<string, string?> { ["namespace"] = ns, ["diffs"] = "false" },
            ct);

    public Task<AllocationDetail> GetAllocationAsync(string allocationId, CancellationToken ct) =>
        GetAsync<AllocationDetail>(
            $"v1/allocation/{Uri.EscapeDataString(allocationId)}",
            null,
            ct);

    private async Task<T> GetAsync<T>(
        string path,
        Dictionary<string, string?>? query,
        CancellationToken ct)
    {
        var uri = BuildUri(path, query);
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            var snippet = body.Length > 500 ? body[..500] + "…" : body;
            throw new NomadApiException(response.StatusCode, $"GET {uri} -> {(int)response.StatusCode} {response.ReasonPhrase}. {snippet}");
        }

        try
        {
            return JsonSerializer.Deserialize<T>(body, _jsonOptions)
                ?? throw new NomadApiException(response.StatusCode, $"GET {uri} returned an empty JSON payload.");
        }
        catch (JsonException ex)
        {
            throw new NomadApiException(response.StatusCode, $"Invalid JSON from GET {uri}: {ex.Message}", ex);
        }
    }

    private static string BuildUri(string path, Dictionary<string, string?>? query)
    {
        if (query is null || query.Count == 0)
            return path;

        var items = query
            .Where(kv => kv.Value is not null)
            .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value!)}");
        return path + "?" + string.Join("&", items);
    }

    private static HttpClientHandler BuildHandler(Options options)
    {
        var handler = new HttpClientHandler();

        if (!string.IsNullOrWhiteSpace(options.ClientCert) || !string.IsNullOrWhiteSpace(options.ClientKey))
        {
            if (string.IsNullOrWhiteSpace(options.ClientCert) || string.IsNullOrWhiteSpace(options.ClientKey))
                throw new UsageException("Both NOMAD_CLIENT_CERT/--client-cert and NOMAD_CLIENT_KEY/--client-key are required together.");

            var cert = X509Certificate2.CreateFromPemFile(options.ClientCert, options.ClientKey);
            handler.ClientCertificates.Add(cert);
        }

        if (options.SkipTlsVerify)
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }
        else if (!string.IsNullOrWhiteSpace(options.CaCert))
        {
            var root = X509Certificate2.CreateFromPemFile(options.CaCert);
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            {
                if (certificate is null)
                    return false;

                using var chain = new X509Chain();
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(root);
                return chain.Build(certificate);
            };
        }

        return handler;
    }

    public void Dispose() => _http.Dispose();
}

internal static class AsyncUtil
{
    public static async Task ForEachBoundedAsync<T>(
        IEnumerable<T> source,
        int parallelism,
        Func<T, Task> action,
        CancellationToken cancellationToken)
    {
        using var semaphore = new SemaphoreSlim(parallelism);
        var tasks = source.Select(async item =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                await action(item);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks);
    }
}

internal sealed class Options
{
    public string Address { get; private set; } = Env("NOMAD_ADDR") ?? "http://127.0.0.1:4646";
    public string? Token { get; private set; } = Env("NOMAD_TOKEN");
    public string Namespace { get; private set; } = Env("NOMAD_NAMESPACE") ?? "*";
    public string? CaCert { get; private set; } = Env("NOMAD_CACERT");
    public string? ClientCert { get; private set; } = Env("NOMAD_CLIENT_CERT");
    public string? ClientKey { get; private set; } = Env("NOMAD_CLIENT_KEY");
    public bool SkipTlsVerify { get; private set; } = EnvBool("NOMAD_SKIP_VERIFY");
    public int TimeoutSeconds { get; private set; } = 15;
    public int Parallelism { get; private set; } = 8;
    public int WatchSeconds { get; private set; }
    public OutputFormat Format { get; private set; } = OutputFormat.Table;
    public bool Wide { get; private set; }
    public bool Verbose { get; private set; }
    public bool OnlyProblems { get; private set; }
    public bool NoFailExit { get; private set; }
    public bool Help { get; private set; }
    public string? JobFilter { get; private set; }
    public string? ServerFilter { get; private set; }

    public static Options Parse(string[] args)
    {
        var o = new Options();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string NextValue()
            {
                if (++i >= args.Length)
                    throw new UsageException($"Missing value after {arg}.");
                return args[i];
            }

            switch (arg)
            {
                case "-h":
                case "--help": o.Help = true; break;
                case "--addr": o.Address = NextValue(); break;
                case "--token": o.Token = NextValue(); break;
                case "--namespace": o.Namespace = NextValue(); break;
                case "--ca-cert": o.CaCert = NextValue(); break;
                case "--client-cert": o.ClientCert = NextValue(); break;
                case "--client-key": o.ClientKey = NextValue(); break;
                case "--skip-tls-verify": o.SkipTlsVerify = true; break;
                case "--timeout": o.TimeoutSeconds = ParsePositiveInt(NextValue(), arg); break;
                case "--parallel": o.Parallelism = ParsePositiveInt(NextValue(), arg); break;
                case "--watch": o.WatchSeconds = ParsePositiveInt(NextValue(), arg); break;
                case "--wide": o.Wide = true; break;
                case "--verbose": o.Verbose = true; break;
                case "--only-problems": o.OnlyProblems = true; break;
                case "--no-fail-exit": o.NoFailExit = true; break;
                case "--job": o.JobFilter = NextValue(); break;
                case "--server": o.ServerFilter = NextValue(); break;
                case "--format":
                    o.Format = NextValue().ToLowerInvariant() switch
                    {
                        "table" => OutputFormat.Table,
                        "json" => OutputFormat.Json,
                        "csv" => OutputFormat.Csv,
                        _ => throw new UsageException("--format must be table, json, or csv.")
                    };
                    break;
                default:
                    throw new UsageException($"Unknown option: {arg}");
            }
        }

        if (!Uri.TryCreate(o.Address, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new UsageException("Nomad address must be an absolute http:// or https:// URL.");

        return o;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
NomadExeMonitor - Nomad raw_exec task monitor for Windows executables

Usage:
  NomadExeMonitor [options]

Options:
  --addr URL              Nomad HTTP API address (default: NOMAD_ADDR or http://127.0.0.1:4646)
  --namespace NS          Namespace; '*' means all authorized namespaces (default: NOMAD_NAMESPACE or *)
  --token TOKEN           ACL token (prefer NOMAD_TOKEN instead of command line)
  --ca-cert FILE          PEM CA certificate (default: NOMAD_CACERT)
  --client-cert FILE      PEM client certificate (default: NOMAD_CLIENT_CERT)
  --client-key FILE       PEM client private key (default: NOMAD_CLIENT_KEY)
  --skip-tls-verify       Disable TLS certificate verification (or NOMAD_SKIP_VERIFY=true)
  --timeout SEC           HTTP timeout in seconds (default: 15)
  --parallel N            Maximum parallel API requests (default: 8)
  --format table|json|csv Output format (default: table)
  --wide                   Show task/event details in table output
  --watch SEC              Refresh continuously every N seconds
  --only-problems          Hide Running rows
  --job TEXT               Filter by Job ID substring
  --server TEXT            Filter by NodeName substring
  --verbose                Print warnings to stderr
  --no-fail-exit           Always return exit code 0 if API access succeeds
  -h, --help               Show this help

Exit codes:
  0  API access succeeded and no Failed rows (or --no-fail-exit)
  2  One or more Failed rows were found
  3  API/TLS/runtime error
  64 Invalid command-line usage
  130 Cancelled with Ctrl+C

Environment variables:
  NOMAD_ADDR, NOMAD_TOKEN, NOMAD_NAMESPACE, NOMAD_CACERT,
  NOMAD_CLIENT_CERT, NOMAD_CLIENT_KEY, NOMAD_SKIP_VERIFY
""");
    }

    private static int ParsePositiveInt(string value, string option)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) || result <= 0)
            throw new UsageException($"{option} must be a positive integer.");
        return result;
    }

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name);

    private static bool EnvBool(string name)
    {
        var value = Env(name);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class UsageException(string message) : Exception(message);

internal sealed class NomadApiException : Exception
{
    public HttpStatusCode StatusCode { get; }

    public NomadApiException(HttpStatusCode statusCode, string message) : base(message) => StatusCode = statusCode;
    public NomadApiException(HttpStatusCode statusCode, string message, Exception inner) : base(message, inner) => StatusCode = statusCode;
}

internal enum OutputFormat { Table, Json, Csv }
internal enum AppStatus { Running, Stopped, Failed, Pending, Unknown }
internal readonly record struct JobKey(string Namespace, string JobID);

internal sealed class Snapshot
{
    public DateTimeOffset GeneratedAt { get; set; }
    public string NomadAddress { get; set; } = "";
    public string Namespace { get; set; } = "";
    public List<AppRow> Rows { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

internal sealed class AppRow
{
    public string Exe { get; set; } = "";
    public string Command { get; set; } = "";
    public string Job { get; set; } = "";
    public string Namespace { get; set; } = "";
    public string Group { get; set; } = "";
    public string Task { get; set; } = "";
    public string Server { get; set; } = "";
    public AppStatus Status { get; set; }
    public int Restarts { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string LastEvent { get; set; } = "";
    public string LastEventMessage { get; set; } = "";
    public string AllocationId { get; set; } = "";
    public string AllocationName { get; set; } = "";
    public long JobVersion { get; set; }
    public string DesiredStatus { get; set; } = "";
    public string ClientStatus { get; set; } = "";
}

internal class Allocation
{
    public string ID { get; set; } = "";
    public string Name { get; set; } = "";
    public string JobID { get; set; } = "";
    public string JobType { get; set; } = "";
    public string? Namespace { get; set; }
    public string TaskGroup { get; set; } = "";
    public string? NodeID { get; set; }
    public string? NodeName { get; set; }
    public string DesiredStatus { get; set; } = "";
    public string ClientStatus { get; set; } = "";
    public long JobVersion { get; set; }
    public long CreateIndex { get; set; }
    public long ModifyIndex { get; set; }
    public long ModifyTime { get; set; }
    public string? PreviousAllocation { get; set; }
    public string? NextAllocation { get; set; }
    public Dictionary<string, TaskState>? TaskStates { get; set; }
}

internal sealed class AllocationDetail : Allocation
{
    public JobDefinition? Job { get; set; }
}

internal sealed class TaskState
{
    public string State { get; set; } = "";
    public bool Failed { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset? LastRestart { get; set; }
    public int Restarts { get; set; }
    public List<TaskEvent>? Events { get; set; }
}

internal sealed class TaskEvent
{
    public string Type { get; set; } = "";
    public long Time { get; set; }
    public string? DisplayMessage { get; set; }
    public string? DriverError { get; set; }
    public string? SetupError { get; set; }
    public string? RestartReason { get; set; }
    public string? Message { get; set; }
    public string? KillReason { get; set; }
    public int ExitCode { get; set; }
    public bool FailsTask { get; set; }
}

internal sealed class JobVersionsResponse
{
    public List<JobDefinition>? Versions { get; set; }
}

internal sealed class JobDefinition
{
    public string ID { get; set; } = "";
    public string? Namespace { get; set; }
    public string Type { get; set; } = "";
    public bool Stop { get; set; }
    public long Version { get; set; }
    public List<TaskGroup>? TaskGroups { get; set; }
}

internal sealed class TaskGroup
{
    public string Name { get; set; } = "";
    public List<NomadTask>? Tasks { get; set; }
}

internal sealed class NomadTask
{
    public string Name { get; set; } = "";
    public string Driver { get; set; } = "";
    public Dictionary<string, JsonElement>? Config { get; set; }
}
