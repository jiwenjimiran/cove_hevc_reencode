using System.Collections.Concurrent;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cove.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Cove.HevcReencode;

public sealed class HevcReencodeExtension : IExtension, IUIExtension, IStatefulExtension, IJobExtension, IApiExtension
{
    public const string ExtensionId = "cove.community.ai.hevc-reencode";
    private const string SettingsKey = "settings";
    private const string PendingReplacementsKey = "pending-replacements";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private static readonly HashSet<string> BrokenCudaDecodeCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "vc1", "wmv3", "wmv2", "wmv1", "msmpeg4v3", "msmpeg4v2", "msmpeg4v1"
    };

    private IExtensionStore? _store;
    private IServiceProvider? _services;
    private CancellationTokenSource? _finalizerCts;
    private Task? _finalizerTask;
    private readonly SemaphoreSlim _pendingQueueLock = new(1, 1);
    public string Id => ExtensionId;
    public string Name => "HEVC/AV1 Reencode";
    public string Version => "0.2.0";
    public string? Description => "GPU-accelerated HEVC and AV1 re-encoding for Cove videos.";
    public string? Author => "jiwenji";
    public string? Url => "https://github.com/jiwenjimiran/cove_hevc_reencode";
    public string? IconUrl => null;
    public IReadOnlyList<string> Categories => ["automation", "video", "tools"];
    public string? MinCoveVersion => "0.6.0";
    public IReadOnlyDictionary<string, string> Dependencies => new Dictionary<string, string>();

    public IReadOnlyList<ExtensionJobDefinition> Jobs { get; } =
    [
        new("reencode-all", "Re-encode all videos", "Re-encode every eligible Cove video to the configured output format.", false)
    ];

    public void ConfigureServices(IServiceCollection services, ExtensionContext context) { }
    public void SetStore(IExtensionStore store) => _store = store;
    public Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        _services = services;
        _finalizerCts = new CancellationTokenSource();
        _finalizerTask = Task.Run(() => RunPendingReplacementLoopAsync(_finalizerCts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task ShutdownAsync(CancellationToken ct = default)
    {
        if (_finalizerCts is null)
            return;

        await _finalizerCts.CancelAsync();
        if (_finalizerTask is not null)
        {
            try { await _finalizerTask.WaitAsync(TimeSpan.FromSeconds(5), ct); }
            catch { }
        }
        _finalizerCts.Dispose();
        _finalizerCts = null;
        _finalizerTask = null;
    }

    public UIManifest GetUIManifest()
    {
        var manifest = new UIManifest
        {
            SettingsPanels =
            [
                new UISettingsPanel(
                    Id: $"{ExtensionId}:installed",
                    Label: "HEVC/AV1 Reencode",
                    ExtensionId: ExtensionId,
                    ComponentName: "HevcReencodeSettingsPanel",
                    Order: 260,
                    TargetTab: "extensions")
            ],
            Actions =
            [
                CreateExtensionAction(
                    id: "hevc-reencode-video-toolbar",
                    label: "Re-encode video",
                    actionType: "toolbar",
                    entityTypes: ["video"],
                    icon: "video",
                    apiEndpoint: "/api/ext/hevc-reencode/queue",
                    order: 85),
                CreateExtensionAction(
                    id: "hevc-reencode-videos-bulk",
                    label: "Re-encode selected",
                    actionType: "bulk",
                    entityTypes: ["video", "videos"],
                    icon: "video",
                    apiEndpoint: "/api/ext/hevc-reencode/queue",
                    order: 85)
            ]
        };
        return manifest;
    }

    private static ExtensionAction CreateExtensionAction(
        string id,
        string label,
        string actionType,
        string[] entityTypes,
        string? icon,
        string? apiEndpoint,
        int order)
    {
        var ctor = typeof(ExtensionAction).GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .First();
        var args = ctor.GetParameters()
            .Select(p => p.Name switch
            {
                "Id" or "id" => id,
                "Label" or "label" => label,
                "ExtensionId" or "extensionId" => ExtensionId,
                "ActionType" or "actionType" => actionType,
                "EntityTypes" or "entityTypes" => entityTypes,
                "Icon" or "icon" => icon,
                "ApiEndpoint" or "apiEndpoint" => apiEndpoint,
                "HandlerName" or "handlerName" => null,
                "Order" or "order" => order,
                "Pages" or "pages" => null,
                "SuppressSuccessAlert" or "suppressSuccessAlert" => false,
                _ => p.HasDefaultValue ? p.DefaultValue : null
            })
            .ToArray();
        return (ExtensionAction)ctor.Invoke(args);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/ext/hevc-reencode/settings", async (HttpContext ctx) =>
        {
            var settings = await LoadSettingsAsync(ctx.RequestAborted);
            return Results.Json(settings, JsonOptions);
        });

        endpoints.MapPut("/api/ext/hevc-reencode/settings", async (HttpContext ctx) =>
        {
            var incoming = await JsonSerializer.DeserializeAsync<ReencodeSettings>(ctx.Request.Body, JsonOptions, ctx.RequestAborted);
            var settings = Normalize(incoming);
            await SaveSettingsAsync(settings, ctx.RequestAborted);
            return Results.Json(settings, JsonOptions);
        });

        endpoints.MapGet("/api/ext/hevc-reencode/encoder-health", async (HttpContext ctx) =>
        {
            var settings = await LoadSettingsAsync(ctx.RequestAborted);
            var health = await GetEncoderHealthAsync(settings, ctx.RequestAborted);
            return Results.Json(health, JsonOptions);
        });

        endpoints.MapGet("/api/ext/hevc-reencode/health", async (HttpContext ctx) =>
        {
            var settings = await LoadSettingsAsync(ctx.RequestAborted);
            var health = await GetEncoderHealthAsync(settings, ctx.RequestAborted);
            return Results.Json(health, JsonOptions);
        });

        endpoints.MapPost("/api/ext/hevc-reencode/queue", async (HttpContext ctx) =>
        {
            var payload = await JsonSerializer.DeserializeAsync<ActionPayload>(ctx.Request.Body, JsonOptions, ctx.RequestAborted)
                ?? new ActionPayload();
            var ids = payload.EntityIds.Count > 0 ? payload.EntityIds : payload.SelectedIds;
            ids = ids.Where(id => id > 0).Distinct().ToList();
            if (ids.Count == 0)
                return Results.BadRequest(new { message = "Select one or more videos before queueing reencode." });

            var jobId = EnqueueViaHostJobService(
                ctx.RequestServices,
                $"ext:{ExtensionId}:reencode",
                $"[Reencode] Re-encode {ids.Count} selected video{(ids.Count == 1 ? "" : "s")}",
                progress => RunSelectedVideosJobAsync(ids, progress, CancellationToken.None));

            return Results.Accepted(value: new
            {
                message = "Reencode queued.",
                jobId,
                description = $"Reencode queued for {ids.Count} selected video{(ids.Count == 1 ? "" : "s")}."
            });
        });
    }

    public async Task RunJobAsync(string jobId, IReadOnlyDictionary<string, string>? parameters, IJobProgress progress, CancellationToken ct)
    {
        var settings = await LoadSettingsAsync(ct);
        var ids = ParseIds(parameters);
        var targets = ResolveVideoTargets(ids, includeAll: jobId == "reencode-all");
        if (targets.Count == 0)
        {
            progress.Report(100, "No matching videos found.");
            return;
        }

        var outputLabel = OutputLabel(settings.OutputFormat);
        var health = await GetEncoderHealthAsync(settings, ct);
        if (!health.Ok)
        {
            progress.Report(100, $"{outputLabel} reencode failed before starting. {health.Error}");
            return;
        }

        var maxParallelism = await ResolveEncodingParallelismAsync(settings, health.SelectedEncoder, ct);
        progress.Report(0, $"Starting {outputLabel} reencode with {maxParallelism} encoding engine{(maxParallelism == 1 ? "" : "s")}.");

        var completed = 0;
        var succeeded = 0;
        var skipped = 0;
        var pending = 0;
        var failed = 0;
        var progressLock = new object();
        var activeProgress = new ConcurrentDictionary<int, double>();
        var errors = new ConcurrentBag<string>();
        var rescanPaths = new ConcurrentBag<string>();

        void Report(double percent, string message)
        {
            lock (progressLock)
                progress.Report(percent, message);
        }

        void ReportAggregate(string message)
        {
            var done = Volatile.Read(ref completed);
            var active = activeProgress.Values.Sum() / 100d;
            Report(Math.Clamp(((done + active) / targets.Count) * 100d, 0, 99), message);
        }

        await Parallel.ForEachAsync(
            targets.Select((target, index) => (target, index)),
            new ParallelOptions { MaxDegreeOfParallelism = maxParallelism, CancellationToken = ct },
            async (item, token) =>
            {
            var target = item.target;
            var index = item.index;
            var prefix = $"{index + 1}/{targets.Count}";
            var skipFamily = settings.SkipCodecs.FirstOrDefault(codec => CodecMatchesFamily(target.VideoCodec, codec));
            if (!settings.StripMetadata && !string.IsNullOrWhiteSpace(skipFamily))
            {
                Interlocked.Increment(ref skipped);
                Interlocked.Increment(ref completed);
                ReportAggregate($"{prefix} skipped {target.Basename}: codec {target.VideoCodec}");
                return;
            }

            if (!File.Exists(target.Path))
            {
                Interlocked.Increment(ref failed);
                Interlocked.Increment(ref completed);
                var message = $"missing file: {target.Path}";
                errors.Add($"{target.Basename}: {message}");
                ReportAggregate($"{prefix} {message}");
                return;
            }

            try
            {
                activeProgress[index] = 0;
                ReportAggregate($"{prefix} submitting {target.Basename}");
                var result = await EncodeOneAsync(target, settings, (pct, message) =>
                {
                    activeProgress[index] = pct;
                    ReportAggregate($"{prefix} {message}");
                }, token);

                if (result.Status == "skipped")
                    Interlocked.Increment(ref skipped);
                else if (result.Status == "pending")
                    Interlocked.Increment(ref pending);
                else if (result.Success)
                {
                    Interlocked.Increment(ref succeeded);
                    foreach (var path in result.RescanPaths)
                        rescanPaths.Add(path);
                }
                else
                {
                    Interlocked.Increment(ref failed);
                    errors.Add($"{target.Basename}: {result.Message}");
                }

                activeProgress.TryRemove(index, out _);
                Interlocked.Increment(ref completed);
                ReportAggregate($"{prefix} {result.Message}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                activeProgress.TryRemove(index, out _);
                Interlocked.Increment(ref failed);
                Interlocked.Increment(ref completed);
                var message = ex.Message;
                errors.Add($"{target.Basename}: {message}");
                ReportAggregate($"{prefix} failed {target.Basename}: {message}");
            }
            });

        var uniqueRescanPaths = rescanPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (uniqueRescanPaths.Count > 0)
        {
            var scanJobId = TryStartRescan(uniqueRescanPaths);
            if (!string.IsNullOrWhiteSpace(scanJobId))
                progress.Report(99, $"Queued Cove rescan for {uniqueRescanPaths.Count} path(s). Scan job: {scanJobId}");
        }

        var errorList = errors.ToList();
        var summary = $"{outputLabel} reencode complete. Success: {succeeded}, pending replacement: {pending}, skipped: {skipped}, failed: {failed}.";
        if (errorList.Count > 0)
            summary += " Errors: " + string.Join(" | ", errorList.Take(5)) + (errorList.Count > 5 ? $" | and {errorList.Count - 5} more" : "");
        progress.Report(100, summary);
    }

    private Task RunSelectedVideosJobAsync(IReadOnlyList<int> ids, object hostProgress, CancellationToken ct)
    {
        var parameters = new Dictionary<string, string> { ["ids"] = string.Join(",", ids) };
        return RunJobAsync("reencode", parameters, new ReflectedJobProgress(hostProgress), ct);
    }

    private static string EnqueueViaHostJobService(IServiceProvider services, string type, string description, Func<object, Task> work)
    {
        var jobServiceType = FindLoadedType("Cove.Core.Interfaces.IJobService")
            ?? throw new InvalidOperationException("Could not locate Cove job service contract.");
        var jobService = services.GetService(jobServiceType)
            ?? throw new InvalidOperationException("Could not resolve Cove job service.");
        var enqueue = jobServiceType.GetMethod("Enqueue")
            ?? throw new InvalidOperationException("Could not locate Cove job enqueue method.");
        var workParameter = enqueue.GetParameters()[2];
        var workDelegate = BuildHostJobDelegate(workParameter.ParameterType, work);
        return (string)(enqueue.Invoke(jobService, [type, description, workDelegate, false]) ?? "");
    }

    private static Delegate BuildHostJobDelegate(Type delegateType, Func<object, Task> work)
    {
        var invoke = delegateType.GetMethod("Invoke") ?? throw new InvalidOperationException("Invalid job delegate type.");
        var parameters = invoke.GetParameters()
            .Select(p => Expression.Parameter(p.ParameterType, p.Name ?? "arg"))
            .ToArray();
        var call = Expression.Call(
            typeof(HevcReencodeExtension).GetMethod(nameof(InvokeHostJobWork), BindingFlags.NonPublic | BindingFlags.Static)!,
            Expression.Constant(work),
            Expression.Convert(parameters[0], typeof(object)),
            parameters.Length > 1 ? parameters[1] : Expression.Constant(CancellationToken.None));
        return Expression.Lambda(delegateType, call, parameters).Compile();
    }

    private static Task InvokeHostJobWork(Func<object, Task> work, object progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return work(progress);
    }

    private async Task<EncodeResult> EncodeOneAsync(VideoTarget target, ReencodeSettings settings, Action<double, string> report, CancellationToken ct)
    {
        var tools = ResolveTools();
        if (tools.FfmpegPath is null || tools.FfprobePath is null)
            return new EncodeResult(false, "failed", "ffmpeg/ffprobe is not available. Start Cove once with network access or configure Cove.FfmpegPath.", []);

        var encoder = await SelectGpuEncoderAsync(tools.FfmpegPath, settings, ct);
        if (encoder is null)
            return new EncodeResult(false, "failed", $"No working GPU {OutputLabel(settings.OutputFormat)} encoder found. Checked {string.Join(", ", CandidateEncoders(settings))}; CPU fallback is disabled.", []);

        var originalSize = new FileInfo(target.Path).Length;
        if (originalSize <= 0)
            return new EncodeResult(false, "failed", $"{target.Basename}: file is empty", []);

        var info = await ProbeVideoAsync(tools.FfprobePath, target.Path, ct);
        var skipFamily = settings.SkipCodecs.FirstOrDefault(codec => CodecMatchesFamily(info.Codec, codec));
        if (!string.IsNullOrWhiteSpace(skipFamily))
        {
            if (!settings.StripMetadata)
            {
                UpdateVideoFileMetadata(target, target.Path, info);
                return new EncodeResult(true, "skipped", $"{target.Basename}: already {skipFamily.ToUpperInvariant()}, skipped.", []);
            }
        }

        var format = FormatForPath(target.Path);
        var outputExtension = Path.GetExtension(target.Path);
        var formatChanged = false;
        if (IsOutputIncompatibleFormat(format, settings.OutputFormat))
        {
            if (!settings.RemuxIncompatibleContainer)
                return new EncodeResult(true, "skipped", $"{target.Basename}: container {outputExtension} cannot hold {OutputLabel(settings.OutputFormat)} and remux is disabled.", []);

            format = "mp4";
            outputExtension = ".mp4";
            formatChanged = true;
        }

        var finalPath = BuildFinalOutputPath(target.Path, outputExtension, settings, formatChanged);
        if (!settings.DeleteAfterConvert && File.Exists(finalPath))
            return new EncodeResult(false, "failed", $"{target.Basename}: output path already exists: {finalPath}", []);
        var tempPath = CreateTempOutputPath(target.Path, outputExtension, settings.OutputFormat);

        var methods = BuildEncodeMethods(encoder, settings.OutputFormat, LooksTooLowBitrate(info.Width, info.Height, info.Bitrate), settings);
        var lastError = "";
        foreach (var decode in DecodeModesFor(encoder, info.Codec))
        {
            foreach (var method in methods)
            {
                ct.ThrowIfCancellationRequested();
                TryDelete(tempPath);
                var audioModes = formatChanged ? new[] { true } : new[] { false, true };
                foreach (var transcodeAudio in audioModes)
                {
                    var args = BuildFfmpegArgs(target.Path, tempPath, format, settings, method, decode, transcodeAudio);
                    var result = await RunFfmpegAsync(tools.FfmpegPath, args, info.Duration, pct =>
                    {
                        report(pct, $"{target.Basename} {pct:0.0}% ({method.Name})");
                    }, ct);

                    if (result.ExitCode != 0)
                    {
                        lastError = ShortError(result.Error);
                        TryDelete(tempPath);
                        if (!transcodeAudio && LooksLikeAudioCompatError(result.Error))
                            continue;
                        break;
                    }

                    var validation = await ValidateOutputAsync(tools.FfprobePath, tempPath, settings.OutputFormat, info.Duration, ct);
                    if (!validation.Valid)
                    {
                        lastError = validation.Error ?? "output validation failed";
                        TryDelete(tempPath);
                        break;
                    }

                    var newSize = new FileInfo(tempPath).Length;
                    var savings = originalSize > 0 ? ((originalSize - newSize) / (double)originalSize) * 100d : 0d;
                    if (savings < method.MinSavingsPct)
                    {
                        lastError = $"savings {savings:0.0}% below threshold {method.MinSavingsPct:0.0}%";
                        TryDelete(tempPath);
                        break;
                    }

                    try
                    {
                        FinalizeOutput(target, tempPath, finalPath, settings.DeleteAfterConvert);
                    }
                    catch (IOException ex) when (settings.DeleteAfterConvert && IsSharingOrLockViolation(ex))
                    {
                        var pendingItem = new PendingReplacement(
                            Id: Guid.NewGuid().ToString("N"),
                            VideoId: target.VideoId,
                            FileId: target.FileId,
                            OriginalPath: target.Path,
                            TempPath: tempPath,
                            FinalPath: finalPath,
                            CreatedAtUtc: DateTime.UtcNow,
                            RetryCount: 0,
                            LastError: ex.Message);
                        await EnqueuePendingReplacementAsync(pendingItem, ct);
                        return new EncodeResult(
                            true,
                            "pending",
                            $"{target.Basename}: encoded with {method.Name}, saved {savings:0.0}%. Original file is in use; replacement queued and will retry automatically.",
                            []);
                    }

                    if (settings.DeleteAfterConvert)
                    {
                        var finalInfo = await ProbeVideoAsync(tools.FfprobePath, finalPath, ct);
                        UpdateVideoFileMetadata(target, finalPath, finalInfo);
                    }

                    return new EncodeResult(
                        true,
                        "success",
                        $"{target.Basename}: encoded with {method.Name}, saved {savings:0.0}%.",
                        [finalPath]);
                }
            }
        }

        TryDelete(tempPath);
        return new EncodeResult(false, "failed", $"{target.Basename}: all GPU encode methods failed. {lastError}", []);
    }

    private static IReadOnlyList<bool> DecodeModesFor(string encoder, string codec)
    {
        if (!encoder.EndsWith("_nvenc", StringComparison.OrdinalIgnoreCase))
            return [false];
        return BrokenCudaDecodeCodecs.Contains(codec) ? [false] : [true, false];
    }

    private static IReadOnlyList<EncodeMethod> BuildEncodeMethods(string encoder, string outputFormat, bool lowBitrate, ReencodeSettings settings)
    {
        var cq = QualityValue(settings, lowBitrate, aggressive: false);
        var methods = new List<EncodeMethod>();
        if (outputFormat.Equals("hevc", StringComparison.OrdinalIgnoreCase))
        {
            AddMethod(methods, encoder, cq, settings.Preset, "main10", settings.MinSavingsPct);
            AddMethod(methods, encoder, cq, settings.Preset, "main", settings.MinSavingsPct);
        }
        else
        {
            AddMethod(methods, encoder, cq, settings.Preset, "av1", settings.MinSavingsPct);
        }

        if (settings.EnableRetries)
        {
            var aggressive = QualityValue(settings, lowBitrate: false, aggressive: true);
            var ceiling = QualityCeiling(settings);
            if (aggressive != cq)
                AddMethod(methods, encoder, aggressive, settings.Preset, outputFormat.Equals("hevc", StringComparison.OrdinalIgnoreCase) ? "main10" : "av1", 0);
            for (var retryCq = Math.Max(aggressive + 2, outputFormat.Equals("hevc", StringComparison.OrdinalIgnoreCase) ? 36 : aggressive + 2); retryCq <= ceiling; retryCq += 2)
            {
                AddMethod(methods, encoder, retryCq, settings.Preset, outputFormat.Equals("hevc", StringComparison.OrdinalIgnoreCase) ? "main10" : "av1", 0);
            }
        }

        return methods;
    }

    private static void AddMethod(List<EncodeMethod> methods, string encoder, int cq, string preset, string profile, double minSavings)
    {
        var args = new List<string>();
        if (encoder.Equals("hevc_nvenc", StringComparison.OrdinalIgnoreCase))
        {
            args.AddRange(["-c:v", encoder, "-rc", "constqp", "-qp", cq.ToString(CultureInfo.InvariantCulture), "-preset", preset,
                "-profile:v", profile, "-tier", "high", "-rc-lookahead", "32", "-spatial_aq", "1", "-aq-strength", "8", "-b:v", "0"]);
        }
        else if (encoder.Equals("av1_nvenc", StringComparison.OrdinalIgnoreCase))
        {
            args.AddRange(["-c:v", encoder, "-rc", "vbr", "-cq", cq.ToString(CultureInfo.InvariantCulture), "-preset", preset, "-b:v", "0"]);
        }
        else if (encoder.Equals("av1_amf", StringComparison.OrdinalIgnoreCase))
        {
            var amfQuality = Math.Clamp(cq, 0, 51).ToString(CultureInfo.InvariantCulture);
            args.AddRange(["-c:v", encoder, "-rc", "qvbr", "-qvbr_quality_level", amfQuality, "-quality", "high_quality"]);
        }
        else
        {
            args.AddRange(["-c:v", encoder, "-rc", "cqp", "-qp_i", cq.ToString(CultureInfo.InvariantCulture),
                "-qp_p", cq.ToString(CultureInfo.InvariantCulture), "-qp_b", cq.ToString(CultureInfo.InvariantCulture)]);
        }

        methods.Add(new EncodeMethod($"{encoder}_{profile}_cq{cq}", args, minSavings));
    }

    private static List<string> BuildFfmpegArgs(string inputPath, string outputPath, string format, ReencodeSettings settings, EncodeMethod method, bool cudaDecode, bool transcodeAudio)
    {
        var args = new List<string> { "-y" };
        if (cudaDecode)
            args.AddRange(["-hwaccel", "cuda", "-hwaccel_device", settings.GpuIndex.ToString(CultureInfo.InvariantCulture)]);

        args.AddRange(["-i", inputPath]);
        args.AddRange(method.Args);
        if (method.Args.Any(arg => arg.EndsWith("_nvenc", StringComparison.OrdinalIgnoreCase)))
            args.AddRange(["-gpu", settings.GpuIndex.ToString(CultureInfo.InvariantCulture)]);
        args.AddRange(transcodeAudio ? ["-c:a", "aac", "-b:a", "192k"] : ["-c:a", "copy"]);
        if (settings.StripMetadata)
            args.AddRange(["-map_metadata", "-1"]);
        args.AddRange(["-map", "0:V", "-map", "0:a?", "-f", format, "-progress", "pipe:1", "-nostats", outputPath]);
        return args;
    }

    private static string CreateTempOutputPath(string inputPath, string outputExtension, string outputFormat)
    {
        var dir = Path.GetDirectoryName(inputPath) ?? "";
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var token = Guid.NewGuid().ToString("N")[..12];
            var candidate = Path.Combine(dir, $".cove-{outputFormat}-{token}.tmp{outputExtension}");
            if (!File.Exists(candidate))
                return candidate;
        }

        throw new IOException($"Could not allocate a temporary output path in {dir}");
    }

    private static string BuildFinalOutputPath(string inputPath, string outputExtension, ReencodeSettings settings, bool formatChanged)
    {
        var dir = Path.GetDirectoryName(inputPath)!;
        var basename = Path.GetFileNameWithoutExtension(inputPath);
        if (!settings.DeleteAfterConvert)
        {
            var suffix = string.IsNullOrWhiteSpace(settings.OutputSuffix) ? $"-{settings.OutputFormat}" : settings.OutputSuffix.Trim();
            return Path.Combine(dir, basename + suffix + outputExtension);
        }

        return formatChanged ? Path.Combine(dir, basename + outputExtension) : inputPath;
    }

    private static void FinalizeOutput(VideoTarget target, string tempPath, string finalPath, bool replaceOriginal)
    {
        if (!replaceOriginal)
        {
            if (File.Exists(finalPath))
                throw new IOException($"Output path already exists: {finalPath}");
            File.Move(tempPath, finalPath);
            return;
        }

        if (!string.Equals(target.Path, finalPath, StringComparison.OrdinalIgnoreCase) && File.Exists(finalPath))
            throw new IOException($"Output path already exists: {finalPath}");

        var backup = target.Path + ".reencode-original";
        if (File.Exists(backup))
            File.Delete(backup);

        var originalMoved = false;
        try
        {
            File.Move(target.Path, backup);
            originalMoved = true;
            File.Move(tempPath, finalPath);
            File.Delete(backup);
        }
        catch
        {
            if (originalMoved && File.Exists(backup) && !File.Exists(target.Path))
            {
                try { File.Move(backup, target.Path); } catch { }
            }
            throw;
        }
    }

    private static bool IsSharingOrLockViolation(IOException ex)
    {
        var code = ex.HResult & 0xFFFF;
        return code is 32 or 33;
    }

    private async Task RunPendingReplacementLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ProcessFirstPendingReplacementAsync(ct);
                await timer.WaitForNextTickAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            }
        }
    }

    private async Task ProcessFirstPendingReplacementAsync(CancellationToken ct)
    {
        if (_store is null)
            return;

        await _pendingQueueLock.WaitAsync(ct);
        try
        {
            var pending = await LoadPendingReplacementsAsync(ct);
            if (pending.Count == 0)
                return;

            var item = pending[0];
            var remove = false;
            var update = false;

            try
            {
                if (!File.Exists(item.TempPath))
                {
                    remove = true;
                }
                else if (!File.Exists(item.OriginalPath))
                {
                    remove = true;
                }
                else
                {
                    FinalizeOutput(
                        new VideoTarget(item.VideoId, item.FileId, item.OriginalPath, Path.GetFileName(item.OriginalPath), "", 0, 0),
                        item.TempPath,
                        item.FinalPath,
                        replaceOriginal: true);

                    var tools = ResolveTools();
                    if (tools.FfprobePath is not null)
                    {
                        var info = await ProbeVideoAsync(tools.FfprobePath, item.FinalPath, ct);
                        UpdateVideoFileMetadata(
                            new VideoTarget(item.VideoId, item.FileId, item.OriginalPath, Path.GetFileName(item.OriginalPath), info.Codec, info.Bitrate, new FileInfo(item.FinalPath).Length),
                            item.FinalPath,
                            info);
                    }

                    TryStartRescan([item.FinalPath]);
                    remove = true;
                }
            }
            catch (IOException ex) when (IsSharingOrLockViolation(ex))
            {
                item = item with { RetryCount = item.RetryCount + 1, LastError = ex.Message, LastAttemptUtc = DateTime.UtcNow };
                update = true;
            }
            catch (Exception ex)
            {
                item = item with { RetryCount = item.RetryCount + 1, LastError = ex.Message, LastAttemptUtc = DateTime.UtcNow };
                remove = true;
            }

            if (remove)
                pending.RemoveAt(0);
            else if (update)
                pending[0] = item;

            await SavePendingReplacementsAsync(pending, ct);
        }
        finally
        {
            _pendingQueueLock.Release();
        }
    }

    private async Task EnqueuePendingReplacementAsync(PendingReplacement item, CancellationToken ct)
    {
        if (_store is null)
            throw new InvalidOperationException("Extension store is not initialized.");

        await _pendingQueueLock.WaitAsync(ct);
        try
        {
            var pending = await LoadPendingReplacementsAsync(ct);
            var existingIndex = pending.FindIndex(p =>
                p.FileId == item.FileId
                || p.OriginalPath.Equals(item.OriginalPath, StringComparison.OrdinalIgnoreCase)
                || p.TempPath.Equals(item.TempPath, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
                pending[existingIndex] = item;
            else
                pending.Add(item);
            await SavePendingReplacementsAsync(pending, ct);
        }
        finally
        {
            _pendingQueueLock.Release();
        }
    }

    private async Task<List<PendingReplacement>> LoadPendingReplacementsAsync(CancellationToken ct)
    {
        if (_store is null)
            return [];
        var json = await _store.GetAsync(PendingReplacementsKey, ct);
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<PendingReplacement>>(json, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private async Task SavePendingReplacementsAsync(List<PendingReplacement> pending, CancellationToken ct)
    {
        if (_store is null)
            return;
        if (pending.Count == 0)
            await _store.DeleteAsync(PendingReplacementsKey, ct);
        else
            await _store.SetAsync(PendingReplacementsKey, JsonSerializer.Serialize(pending, JsonOptions), ct);
    }

    private void UpdateVideoFileMetadata(VideoTarget target, string finalPath, VideoInfo info)
    {
        if (_services is null)
            return;

        try
        {
            using var scope = _services.CreateScope();
            var dbType = FindLoadedType("Cove.Data.CoveContext");
            if (dbType is null)
                return;
            var db = scope.ServiceProvider.GetService(dbType);
            var videoFiles = dbType.GetProperty("VideoFiles", BindingFlags.Public | BindingFlags.Instance)?.GetValue(db!) as IEnumerable;
            if (db is null || videoFiles is null)
                return;

            foreach (var file in videoFiles)
            {
                if (ReadIntProperty(file, "Id") != target.FileId)
                    continue;

                var stat = new FileInfo(finalPath);
                SetPropertyIfWritable(file, "Path", finalPath);
                SetPropertyIfWritable(file, "Basename", Path.GetFileName(finalPath));
                SetPropertyIfWritable(file, "Format", Path.GetExtension(finalPath).TrimStart('.').ToLowerInvariant());
                SetPropertyIfWritable(file, "Size", stat.Length);
                SetPropertyIfWritable(file, "ModTime", stat.LastWriteTimeUtc);
                SetPropertyIfWritable(file, "Width", info.Width);
                SetPropertyIfWritable(file, "Height", info.Height);
                SetPropertyIfWritable(file, "Duration", info.Duration);
                SetPropertyIfWritable(file, "VideoCodec", info.Codec);
                SetPropertyIfWritable(file, "AudioCodec", info.AudioCodec);
                SetPropertyIfWritable(file, "FrameRate", info.FrameRate);
                SetPropertyIfWritable(file, "BitRate", info.Bitrate);
                dbType.GetMethod("SaveChanges", Type.EmptyTypes)?.Invoke(db, []);
                TryRefreshVideoMetrics(dbType, db, target.VideoId);
                return;
            }
        }
        catch
        {
            // Rescan will still update size/modtime if direct metadata update is unavailable.
        }
    }

    private static void TryRefreshVideoMetrics(Type dbType, object db, int videoId)
    {
        try
        {
            var method = dbType.GetMethod("RefreshVideoMetrics", BindingFlags.Instance | BindingFlags.NonPublic);
            method?.Invoke(db, [new HashSet<int> { videoId }]);
            dbType.GetMethod("SaveChanges", Type.EmptyTypes)?.Invoke(db, []);
        }
        catch
        {
            // Aggregate metrics are refreshed by Cove's normal scan paths if reflection is unavailable.
        }
    }

    private string? TryStartRescan(IEnumerable<string> paths)
    {
        if (_services is null)
            return null;

        try
        {
            using var scope = _services.CreateScope();
            var scanServiceType = FindLoadedType("Cove.Core.Interfaces.IScanService");
            var optionsType = FindLoadedType("Cove.Core.Interfaces.ScanOperationOptions");
            if (scanServiceType is null || optionsType is null)
                return null;

            var scanService = scope.ServiceProvider.GetService(scanServiceType);
            if (scanService is null)
                return null;

            var options = Activator.CreateInstance(optionsType);
            optionsType.GetProperty("Paths")?.SetValue(options, paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
            optionsType.GetProperty("Rescan")?.SetValue(options, true);
            var startScan = scanServiceType.GetMethod("StartScan");
            return startScan?.Invoke(scanService, [options])?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private IReadOnlyList<VideoTarget> ResolveVideoTargets(IReadOnlySet<int> ids, bool includeAll)
    {
        var services = _services ?? throw new InvalidOperationException("Extension services are not initialized.");
        var dbType = FindLoadedType("Cove.Data.CoveContext");
        if (dbType is null)
            throw new InvalidOperationException("Could not locate Cove.Data.CoveContext.");

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetService(dbType);
        if (db is null)
            throw new InvalidOperationException("Could not resolve CoveContext from Cove services.");

        var videoFiles = dbType.GetProperty("VideoFiles", BindingFlags.Public | BindingFlags.Instance)?.GetValue(db) as IEnumerable;
        if (videoFiles is null)
            throw new InvalidOperationException("Could not read VideoFiles from CoveContext.");

        var results = new List<VideoTarget>();
        foreach (var file in videoFiles)
        {
            var videoId = ReadIntProperty(file, "VideoId");
            if (videoId is null || (!includeAll && !ids.Contains(videoId.Value)))
                continue;

            var path = ReadStringProperty(file, "Path");
            if (string.IsNullOrWhiteSpace(path))
                continue;

            results.Add(new VideoTarget(
                VideoId: videoId.Value,
                FileId: ReadIntProperty(file, "Id") ?? 0,
                Path: path,
                Basename: ReadStringProperty(file, "Basename") ?? Path.GetFileName(path),
                VideoCodec: ReadStringProperty(file, "VideoCodec") ?? "",
                BitRate: ReadLongProperty(file, "BitRate") ?? 0,
                Size: ReadLongProperty(file, "Size") ?? 0));
        }

        return results
            .GroupBy(v => v.VideoId)
            .Select(g => g.OrderByDescending(v => v.Size).First())
            .OrderBy(v => v.VideoId)
            .ToList();
    }

    private async Task<EncoderHealth> GetEncoderHealthAsync(ReencodeSettings settings, CancellationToken ct)
    {
        try
        {
            var tools = ResolveTools();
            if (tools.FfmpegPath is null || tools.FfprobePath is null)
                return new EncoderHealth(false, tools.FfmpegPath, tools.FfprobePath, [], null, "ffmpeg/ffprobe is not available.");

            var encoders = await ListFfmpegEncodersAsync(tools.FfmpegPath, ct);
            var candidates = CandidateEncoders(settings);
            var usable = new List<string>();
            var errors = new List<string>();
            foreach (var candidate in candidates)
            {
                if (!encoders.Contains(candidate))
                {
                    errors.Add($"{candidate} is not listed by ffmpeg");
                    continue;
                }

                if (await ProbeGpuEncoderAsync(tools.FfmpegPath, candidate, ct) is { } error)
                    errors.Add($"{candidate}: {error}");
                else
                    usable.Add(candidate);
            }

            var selected = usable.FirstOrDefault();
            return selected is null
                ? new EncoderHealth(false, tools.FfmpegPath, tools.FfprobePath, usable, null, $"No working GPU {OutputLabel(settings.OutputFormat)} encoder found. " + string.Join("; ", errors))
                : new EncoderHealth(true, tools.FfmpegPath, tools.FfprobePath, usable, selected, null);
        }
        catch (Exception ex)
        {
            return new EncoderHealth(false, null, null, [], null, ex.Message);
        }
    }

    private async Task<string?> SelectGpuEncoderAsync(string ffmpegPath, ReencodeSettings settings, CancellationToken ct)
    {
        var encoders = await ListFfmpegEncodersAsync(ffmpegPath, ct);
        foreach (var candidate in CandidateEncoders(settings))
        {
            if (!encoders.Contains(candidate))
                continue;
            if (await ProbeGpuEncoderAsync(ffmpegPath, candidate, ct) is null)
                return candidate;
        }
        return null;
    }

    private static async Task<int> ResolveEncodingParallelismAsync(ReencodeSettings settings, string? selectedEncoder, CancellationToken ct)
    {
        if (settings.MaxConcurrentEncodes > 0)
            return settings.MaxConcurrentEncodes;
        if (selectedEncoder is null || !selectedEncoder.EndsWith("_nvenc", StringComparison.OrdinalIgnoreCase))
            return 1;
        return Math.Max(1, await DetectNvencEngineCountAsync(settings.GpuIndex, ct));
    }

    private static async Task<int> DetectNvencEngineCountAsync(int gpuIndex, CancellationToken ct)
    {
        var nvidiaSmi = FindTool("nvidia-smi");
        if (nvidiaSmi is null)
            return 1;

        var result = await RunProcessCaptureAsync(
            nvidiaSmi,
            ["--query-gpu=name", "--format=csv,noheader"],
            TimeSpan.FromSeconds(10),
            ct);
        if (result.ExitCode != 0)
            return 1;

        var names = result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (gpuIndex < 0 || gpuIndex >= names.Length)
            return 1;

        return NvencEngineCountForGpu(names[gpuIndex]);
    }

    private static int NvencEngineCountForGpu(string name)
    {
        var normalized = name.ToUpperInvariant();
        if (normalized.Contains("RTX 5090", StringComparison.OrdinalIgnoreCase))
            return 3;
        if (normalized.Contains("RTX 4090", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("RTX 5080", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("RTX 5070 TI", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("RTX 4080", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("RTX 4070 TI", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("RTX 4070 SUPER", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("RTX 4060 TI", StringComparison.OrdinalIgnoreCase))
            return 2;
        return 1;
    }

    private ToolPaths ResolveTools()
    {
        string? ffmpeg = null;
        string? ffprobe = null;
        if (_services is not null)
        {
            var configType = FindLoadedType("Cove.Core.Interfaces.CoveConfiguration");
            var config = configType is null ? null : _services.GetService(configType);
            if (config is not null)
            {
                ffmpeg = ReadStringProperty(config, "FfmpegPath");
                ffprobe = ReadStringProperty(config, "FfprobePath");
            }
        }

        ffmpeg = ExistingFileOrNull(ffmpeg) ?? FindTool("ffmpeg");
        ffprobe = ExistingFileOrNull(ffprobe) ?? FindAdjacentProbe(ffmpeg) ?? FindTool("ffprobe");
        return new ToolPaths(ffmpeg, ffprobe);
    }

    private static async Task<HashSet<string>> ListFfmpegEncodersAsync(string ffmpegPath, CancellationToken ct)
    {
        var result = await RunProcessCaptureAsync(ffmpegPath, ["-hide_banner", "-encoders"], TimeSpan.FromSeconds(10), ct);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in result.Output.Split('\n'))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0].Length == 6 && parts[1] != "=")
                names.Add(parts[1]);
        }
        return names;
    }

    private static IReadOnlyList<string> CandidateEncoders(ReencodeSettings settings)
    {
        var preferred = settings.EncoderPreference.Trim().ToLowerInvariant();
        if (!preferred.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return [preferred];
        return settings.OutputFormat.Equals("av1", StringComparison.OrdinalIgnoreCase)
            ? ["av1_nvenc", "av1_amf"]
            : ["hevc_nvenc", "hevc_amf"];
    }

    private static async Task<string?> ProbeGpuEncoderAsync(string ffmpegPath, string encoder, CancellationToken ct)
    {
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=1",
            "-frames:v", "1", "-an", "-c:v", encoder
        };
        if (encoder.Equals("av1_nvenc", StringComparison.OrdinalIgnoreCase))
            args.AddRange(["-rc", "vbr", "-cq", "32", "-preset", "p4", "-b:v", "0", "-f", "null", "-"]);
        else if (encoder.EndsWith("_nvenc", StringComparison.OrdinalIgnoreCase))
            args.AddRange(["-rc", "constqp", "-qp", "32", "-preset", "p4", "-f", "null", "-"]);
        else if (encoder.Equals("av1_amf", StringComparison.OrdinalIgnoreCase))
            args.AddRange(["-rc", "qvbr", "-qvbr_quality_level", "32", "-quality", "high_quality", "-f", "null", "-"]);
        else
            args.AddRange(["-rc", "cqp", "-qp_i", "32", "-qp_p", "32", "-qp_b", "32", "-f", "null", "-"]);

        var result = await RunProcessCaptureAsync(ffmpegPath, args, TimeSpan.FromSeconds(20), ct);
        return result.ExitCode == 0 ? null : ShortError(result.Error);
    }

    private static async Task<VideoInfo> ProbeVideoAsync(string ffprobePath, string path, CancellationToken ct)
    {
        var result = await RunProcessCaptureAsync(ffprobePath,
            ["-v", "quiet", "-print_format", "json", "-show_format", "-show_streams", path],
            TimeSpan.FromSeconds(60), ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"ffprobe failed: {ShortError(result.Error)}");

        using var doc = JsonDocument.Parse(result.Output);
        JsonElement? video = null;
        JsonElement? audio = null;
        if (doc.RootElement.TryGetProperty("streams", out var streams))
        {
            foreach (var stream in streams.EnumerateArray())
            {
                if (stream.TryGetProperty("codec_type", out var type) && type.GetString() == "video")
                {
                    video ??= stream;
                }
                else if (stream.TryGetProperty("codec_type", out var audioType) && audioType.GetString() == "audio")
                {
                    audio ??= stream;
                }
            }
        }

        if (video is null)
            throw new InvalidOperationException("ffprobe found no video stream.");

        var streamElement = video.Value;
        var format = doc.RootElement.TryGetProperty("format", out var fmt) ? fmt : default;
        var bitrate = ReadLong(streamElement, "bit_rate") ?? ReadLong(format, "bit_rate") ?? 0;
        var duration = ReadDouble(streamElement, "duration") ?? ReadDouble(format, "duration") ?? 0;
        var frameRate = ParseFrameRate(ReadString(streamElement, "r_frame_rate"));
        return new VideoInfo(
            Codec: ReadString(streamElement, "codec_name")?.ToLowerInvariant() ?? "",
            AudioCodec: audio is { } audioElement ? ReadString(audioElement, "codec_name")?.ToLowerInvariant() ?? "" : "",
            Bitrate: bitrate,
            Width: (int)(ReadLong(streamElement, "width") ?? 0),
            Height: (int)(ReadLong(streamElement, "height") ?? 0),
            Duration: duration,
            FrameRate: frameRate);
    }

    private static async Task<ValidationResult> ValidateOutputAsync(string ffprobePath, string path, string outputFormat, double inputDuration, CancellationToken ct)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            return new ValidationResult(false, "output file is missing or empty");

        var info = await ProbeVideoAsync(ffprobePath, path, ct);
        if (!CodecMatchesFamily(info.Codec, outputFormat))
            return new ValidationResult(false, $"output codec is {info.Codec}, expected {OutputLabel(outputFormat)}");
        if (info.Width <= 0 || info.Height <= 0)
            return new ValidationResult(false, "output has invalid dimensions");
        if (inputDuration > 0 && info.Duration > 0 && info.Duration / inputDuration < 0.5)
            return new ValidationResult(false, "output duration is much shorter than input");
        return new ValidationResult(true, null);
    }

    private static async Task<ProcessResult> RunFfmpegAsync(string ffmpegPath, IReadOnlyList<string> args, double duration, Action<double> progress, CancellationToken ct)
    {
        using var process = CreateProcess(ffmpegPath, args);
        var stderr = new StringBuilder();
        process.Start();
        var errorTask = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(ct) is { } line)
                stderr.AppendLine(line);
        }, ct);

        var lastOutput = DateTime.UtcNow;
        while (!process.HasExited)
        {
            var readTask = process.StandardOutput.ReadLineAsync(ct).AsTask();
            var completed = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(120), ct));
            if (completed != readTask)
            {
                TryKill(process);
                return new ProcessResult(-1, "", "ffmpeg stalled with no progress output for 120 seconds");
            }

            var line = await readTask;
            if (line is null)
                break;
            lastOutput = DateTime.UtcNow;
            if (line.StartsWith("out_time_us=", StringComparison.OrdinalIgnoreCase)
                && long.TryParse(line.Split('=', 2)[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var us)
                && duration > 0)
            {
                progress(Math.Clamp(us / (duration * 1_000_000d) * 100d, 0, 100));
            }
            else if (line.Equals("progress=end", StringComparison.OrdinalIgnoreCase))
            {
                progress(100);
            }
        }

        await process.WaitForExitAsync(ct);
        try { await errorTask; } catch (OperationCanceledException) { }
        return new ProcessResult(process.ExitCode, "", stderr.ToString());
    }

    private static async Task<ProcessResult> RunProcessCaptureAsync(string fileName, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        using var process = CreateProcess(fileName, args);
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            return new ProcessResult(process.ExitCode, await outputTask, await errorTask);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            return new ProcessResult(-1, "", $"Timed out running {Path.GetFileName(fileName)}.");
        }
    }

    private static Process CreateProcess(string fileName, IReadOnlyList<string> args)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);
        return process;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch { }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }

    private static string? ExistingFileOrNull(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;

    private static string? FindAdjacentProbe(string? ffmpegPath)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath))
            return null;
        var dir = Path.GetDirectoryName(ffmpegPath);
        if (string.IsNullOrWhiteSpace(dir))
            return null;
        var probe = Path.Combine(dir, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
        return File.Exists(probe) ? probe : null;
    }

    private static string? FindTool(string name)
    {
        var exe = OperatingSystem.IsWindows() && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name + ".exe" : name;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;
            var candidate = Path.Combine(dir, exe);
            if (File.Exists(candidate))
                return candidate;
        }

        var managed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cove", "ffmpeg", exe);
        return File.Exists(managed) ? managed : null;
    }

    private static bool LooksLikeAudioCompatError(string stderr)
    {
        var lower = stderr.ToLowerInvariant();
        return lower.Contains("could not find tag for codec")
            || lower.Contains("not currently supported in container")
            || lower.Contains("unsupported codec")
            || lower.Contains("codec not currently supported")
            || lower.Contains("tag not found");
    }

    private static string ShortError(string error)
    {
        var lines = error.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
        var useful = lines.FirstOrDefault(line =>
            line.Contains("InitializeEncoder failed", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Error while opening encoder", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Cannot load", StringComparison.OrdinalIgnoreCase)
            || line.Contains("No capable devices", StringComparison.OrdinalIgnoreCase)
            || line.Contains("minimum supported", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(useful))
            return useful;

        var compact = string.Join(" ", lines);
        return compact.Length <= 500 ? compact : compact[^500..];
    }

    private static string FormatForPath(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" => "mp4",
        ".mkv" or ".webm" => "matroska",
        ".avi" => "avi",
        ".mov" => "mov",
        ".wmv" => "asf",
        ".flv" => "flv",
        ".ts" => "mpegts",
        ".mpg" or ".mpeg" => "mpeg",
        ".3gp" => "3gp",
        _ => "mp4"
    };

    private static bool IsOutputIncompatibleFormat(string format, string outputFormat) =>
        (outputFormat.Equals("hevc", StringComparison.OrdinalIgnoreCase) || outputFormat.Equals("av1", StringComparison.OrdinalIgnoreCase))
        && format is "asf" or "avi" or "flv" or "3gp" or "mpeg";

    private static string OutputLabel(string outputFormat) =>
        outputFormat.Equals("av1", StringComparison.OrdinalIgnoreCase) ? "AV1" : "HEVC";

    private static int QualityValue(ReencodeSettings settings, bool lowBitrate, bool aggressive) =>
        settings.OutputFormat.Equals("av1", StringComparison.OrdinalIgnoreCase)
            ? aggressive ? settings.Av1AggressiveCq : lowBitrate ? settings.Av1LowBitrateCq : settings.Av1Cq
            : aggressive ? settings.AggressiveCq : lowBitrate ? settings.CqLowBitrate : settings.Cq;

    private static int QualityCeiling(ReencodeSettings settings) =>
        settings.OutputFormat.Equals("av1", StringComparison.OrdinalIgnoreCase)
            ? settings.Av1UltraAggressiveCq
            : settings.UltraAggressiveCq;

    private static bool LooksTooLowBitrate(int width, int height, long bitrate)
    {
        var threshold = height switch
        {
            >= 4320 => 40_000_000,
            >= 2160 => 20_000_000,
            >= 1440 => 10_000_000,
            >= 1080 => 5_000_000,
            >= 720 => 2_500_000,
            >= 480 => 1_200_000,
            >= 360 => 800_000,
            >= 240 => 500_000,
            _ => 300_000
        };
        return bitrate > 0 && bitrate <= threshold;
    }

    private static bool CodecMatchesFamily(string codec, string family)
    {
        codec = NormalizeCodec(codec);
        family = NormalizeCodec(family);
        return family switch
        {
            "hevc" => codec is "hevc" or "h265" or "h.265",
            "av1" => codec is "av1",
            "vp9" => codec is "vp9",
            "vp8" => codec is "vp8",
            _ => codec.Equals(family, StringComparison.OrdinalIgnoreCase)
        };
    }

    private static long? ReadLong(JsonElement element, string property)
    {
        if (element.ValueKind == JsonValueKind.Undefined || !element.TryGetProperty(property, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            return number;
        return long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static double? ReadDouble(JsonElement element, string property)
    {
        if (element.ValueKind == JsonValueKind.Undefined || !element.TryGetProperty(property, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
            return number;
        return double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static string? ReadString(JsonElement element, string property)
    {
        if (element.ValueKind == JsonValueKind.Undefined || !element.TryGetProperty(property, out var value))
            return null;
        return value.ToString();
    }

    private static double ParseFrameRate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0;

        var parts = value.Split('/', 2);
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator)
            && denominator > 0)
            return numerator / denominator;

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    private static void SetPropertyIfWritable(object instance, string property, object? value)
    {
        var prop = instance.GetType().GetProperty(property);
        if (prop?.CanWrite == true)
            prop.SetValue(instance, value);
    }

    private async Task<ReencodeSettings> LoadSettingsAsync(CancellationToken ct)
    {
        if (_store is null)
            return Normalize(null);

        var json = await _store.GetAsync(SettingsKey, ct);
        if (string.IsNullOrWhiteSpace(json))
            return Normalize(null);

        try
        {
            return Normalize(JsonSerializer.Deserialize<ReencodeSettings>(json, JsonOptions));
        }
        catch
        {
            return Normalize(null);
        }
    }

    private async Task SaveSettingsAsync(ReencodeSettings settings, CancellationToken ct)
    {
        if (_store is null)
            throw new InvalidOperationException("Extension store is not initialized.");
        await _store.SetAsync(SettingsKey, JsonSerializer.Serialize(Normalize(settings), JsonOptions), ct);
    }

    private static ReencodeSettings Normalize(ReencodeSettings? value)
    {
        var settings = value ?? new ReencodeSettings();
        settings.OutputFormat = settings.OutputFormat?.Trim().ToLowerInvariant() is "av1" ? "av1" : "hevc";
        var allowedEncoders = settings.OutputFormat == "av1"
            ? new HashSet<string>(["auto", "av1_nvenc", "av1_amf"], StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(["auto", "hevc_nvenc", "hevc_amf"], StringComparer.OrdinalIgnoreCase);
        var encoderPreference = settings.EncoderPreference?.Trim() ?? "";
        settings.EncoderPreference = allowedEncoders.Contains(encoderPreference)
            ? encoderPreference.ToLowerInvariant()
            : "auto";
        settings.MaxConcurrentEncodes = Math.Max(-1, settings.MaxConcurrentEncodes);
        settings.Cq = Math.Clamp(settings.Cq, 0, 51);
        settings.CqLowBitrate = Math.Clamp(settings.CqLowBitrate, 0, 51);
        settings.Av1Cq = Math.Clamp(settings.Av1Cq, 0, 63);
        settings.Av1LowBitrateCq = Math.Clamp(settings.Av1LowBitrateCq, 0, 63);
        settings.Preset = IsPreset(settings.Preset) ? settings.Preset : "p7";
        settings.SkipCodecs = settings.SkipCodecs.Count == 0 ? ["hevc", "av1", "vp9"] : settings.SkipCodecs.Select(NormalizeCodec).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        settings.MinSavingsPct = Math.Clamp(settings.MinSavingsPct, 0, 100);
        settings.GpuIndex = Math.Max(0, settings.GpuIndex);
        settings.AggressiveCq = Math.Clamp(settings.AggressiveCq, 0, 51);
        settings.UltraAggressiveCq = Math.Clamp(settings.UltraAggressiveCq, 0, 51);
        settings.Av1AggressiveCq = Math.Clamp(settings.Av1AggressiveCq, 0, 63);
        settings.Av1UltraAggressiveCq = Math.Clamp(settings.Av1UltraAggressiveCq, 0, 63);
        settings.ReencodeFailedTag = string.IsNullOrWhiteSpace(settings.ReencodeFailedTag) ? "reencode_failed" : settings.ReencodeFailedTag.Trim();
        return settings;
    }

    private static IReadOnlySet<int> ParseIds(IReadOnlyDictionary<string, string>? parameters)
    {
        var raw = parameters is not null && parameters.TryGetValue("ids", out var ids) ? ids : "";
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0)
            .Where(id => id > 0)
            .ToHashSet();
    }

    private static double Percent(int completed, int total) => total <= 0 ? 100 : Math.Clamp((double)completed / total * 100d, 0, 100);
    private static bool IsPreset(string value) => value is "p1" or "p2" or "p3" or "p4" or "p5" or "p6" or "p7";
    private static string NormalizeCodec(string? codec) => (codec ?? "").Trim().ToLowerInvariant() switch
    {
        "h265" or "h.265" => "hevc",
        _ => (codec ?? "").Trim().ToLowerInvariant()
    };

    private static Type? FindLoadedType(string fullName) =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType(fullName, throwOnError: false, ignoreCase: false))
            .FirstOrDefault(t => t is not null);

    private static int? ReadIntProperty(object instance, string property) =>
        instance.GetType().GetProperty(property)?.GetValue(instance) switch
        {
            int i => i,
            _ => null
        };

    private static long? ReadLongProperty(object instance, string property) =>
        instance.GetType().GetProperty(property)?.GetValue(instance) switch
        {
            long l => l,
            int i => i,
            _ => null
        };

    private static string? ReadStringProperty(object instance, string property) =>
        instance.GetType().GetProperty(property)?.GetValue(instance)?.ToString();

    private static Dictionary<string, object?>? ReadObject(Dictionary<string, object?> source, string key)
    {
        if (!source.TryGetValue(key, out var value) || value is null)
            return null;
        if (value is Dictionary<string, object?> dict)
            return dict;
        if (value is JsonElement element && element.ValueKind == JsonValueKind.Object)
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(element.GetRawText(), JsonOptions);
        return null;
    }

    private static string? ReadString(Dictionary<string, object?> source, string key)
    {
        if (!source.TryGetValue(key, out var value) || value is null)
            return null;
        return value is JsonElement element ? element.ToString() : value.ToString();
    }

    private static double ReadDouble(Dictionary<string, object?> source, string key)
    {
        if (!source.TryGetValue(key, out var value) || value is null)
            return 0;
        if (value is JsonElement element && element.TryGetDouble(out var number))
            return number;
        return Convert.ToDouble(value, CultureInfo.InvariantCulture);
    }

    private static bool ReadBool(Dictionary<string, object?> source, string key)
    {
        if (!source.TryGetValue(key, out var value) || value is null)
            return false;
        if (value is bool b)
            return b;
        if (value is JsonElement element && (element.ValueKind == JsonValueKind.True || element.ValueKind == JsonValueKind.False))
            return element.GetBoolean();
        return bool.TryParse(value.ToString(), out var parsed) && parsed;
    }

    private sealed class ReflectedJobProgress(object hostProgress) : IJobProgress
    {
        private readonly MethodInfo? _report = hostProgress.GetType().GetMethod("Report", [typeof(double), typeof(string)]);

        public void Report(double percent, string? message = null)
            => _report?.Invoke(hostProgress, [percent, message]);
    }

    private sealed record VideoTarget(int VideoId, int FileId, string Path, string Basename, string VideoCodec, long BitRate, long Size);
    private sealed record EncodeResult(bool Success, string Status, string Message, IReadOnlyList<string> RescanPaths);
    private sealed record ToolPaths(string? FfmpegPath, string? FfprobePath);
    private sealed record ProcessResult(int ExitCode, string Output, string Error);
    private sealed record VideoInfo(string Codec, string AudioCodec, long Bitrate, int Width, int Height, double Duration, double FrameRate);
    private sealed record EncodeMethod(string Name, IReadOnlyList<string> Args, double MinSavingsPct);
    private sealed record ValidationResult(bool Valid, string? Error);
    private sealed record EncoderHealth(bool Ok, string? FfmpegPath, string? FfprobePath, IReadOnlyList<string> AvailableEncoders, string? SelectedEncoder, string? Error);
    private sealed record PendingReplacement(
        string Id,
        int VideoId,
        int FileId,
        string OriginalPath,
        string TempPath,
        string FinalPath,
        DateTime CreatedAtUtc,
        int RetryCount,
        string? LastError,
        DateTime? LastAttemptUtc = null);
}

public sealed class ActionPayload
{
    public List<int> EntityIds { get; set; } = [];
    public List<int> SelectedIds { get; set; } = [];
}

public sealed class ReencodeSettings
{
    public bool TagOnFailure { get; set; } = true;
    public string ReencodeFailedTag { get; set; } = "reencode_failed";
    public bool DeleteAfterConvert { get; set; } = true;
    public string OutputFormat { get; set; } = "hevc";
    public string EncoderPreference { get; set; } = "auto";
    public int MaxConcurrentEncodes { get; set; } = -1;
    public int Cq { get; set; } = 28;
    public int CqLowBitrate { get; set; } = 34;
    public int Av1Cq { get; set; } = 30;
    public int Av1LowBitrateCq { get; set; } = 36;
    public string Preset { get; set; } = "p7";
    public List<string> SkipCodecs { get; set; } = ["hevc", "av1", "vp9"];
    public bool SkipFailedTag { get; set; } = true;
    public bool RemuxIncompatibleContainer { get; set; } = true;
    public string OutputSuffix { get; set; } = "";
    public bool CopyMetadataOnSuffix { get; set; } = true;
    public int MinSavingsPct { get; set; } = 15;
    public int GpuIndex { get; set; } = 0;
    public bool EnableRetries { get; set; } = true;
    public int AggressiveCq { get; set; } = 34;
    public int UltraAggressiveCq { get; set; } = 40;
    public int Av1AggressiveCq { get; set; } = 38;
    public int Av1UltraAggressiveCq { get; set; } = 44;
    public bool StripMetadata { get; set; } = false;
    public bool EmbedStashMetadata { get; set; } = false;
}
