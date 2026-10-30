using RoboExp.Core.Install;
using RoboExp.Device;

namespace RoboExp.Cli;

internal static class DeviceCommands
{
    private const string Usage = """
        usage:
          roboexp device list --type <T> [--via hid|serial|bluetooth|wifi|drive]
          roboexp device download <file.bin> --type <T> [--via hid|serial|bluetooth|wifi|drive] [--target <id>]
                                  [--code <wifi pairing code>] [--timeout <seconds>]
        Without --via, waits for a HID device or controller drive (like the original "Download" menu).
        """;

    public static int Run(string[] args)
    {
        if (args.Length == 0) return Fail(Usage);
        try
        {
            var opts = Options.Parse(args[1..]);
            return args[0] switch
            {
                "list" => List(opts).GetAwaiter().GetResult(),
                "download" => Download(opts).GetAwaiter().GetResult(),
                _ => Fail($"unknown device command '{args[0]}'\n{Usage}"),
            };
        }
        catch (ArgumentException e)
        {
            return Fail(e.Message);
        }
    }

    private sealed record Options(RobotType Type, TransportKind? Via, string? Target, string? Code, TimeSpan? Timeout, string? File)
    {
        public static Options Parse(string[] args)
        {
            string? type = null, via = null, target = null, code = null, file = null;
            TimeSpan? timeout = null;
            for (var i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"missing value for {args[i]}");
                switch (args[i])
                {
                    case "--type": type = Next(); break;
                    case "--via": via = Next(); break;
                    case "--target": target = Next(); break;
                    case "--code": code = Next(); break;
                    case "--timeout": timeout = TimeSpan.FromSeconds(double.Parse(Next())); break;
                    default:
                        if (args[i].StartsWith("--", StringComparison.Ordinal) || file is not null)
                            throw new ArgumentException($"unexpected argument '{args[i]}'\n{Usage}");
                        file = args[i];
                        break;
                }
            }
            if (type is null) throw new ArgumentException($"--type is required\n{Usage}");
            return new Options(RobotType.Parse(type), via is null ? null : ParseVia(via), target, code, timeout, file);
        }

        private static TransportKind ParseVia(string v) => v.ToLowerInvariant() switch
        {
            "hid" => TransportKind.Hid,
            "serial" => TransportKind.Serial,
            "bluetooth" or "bt" => TransportKind.Bluetooth,
            "wifi" => TransportKind.Wifi,
            "drive" => TransportKind.RemovableDrive,
            _ => throw new ArgumentException($"unknown transport '{v}' (hid|serial|bluetooth|wifi|drive)"),
        };
    }

    private static IReadOnlyList<IDownloader> Downloaders(Options o)
    {
        var all = DownloadMethods.For(o.Type);
        foreach (var w in all.OfType<WifiDownloader>())
        {
            if (o.Code is not null) w.PairCode = o.Code;
            if (o.Timeout is { } t) w.PairTimeout = t;
        }
        foreach (var b in all.OfType<BluetoothDownloader>())
            if (o.Timeout is { } t) b.ConnectTimeout = t;
        if (o.Via is not { } via) return all;
        var chosen = all.Where(d => d.Kind == via).ToList();
        if (chosen.Count == 0)
            throw new ArgumentException($"{o.Type.Name} has no {via} download method (available: {string.Join(", ", all.Select(d => d.Kind))})");
        return chosen;
    }

    private static async Task<int> List(Options o)
    {
        var any = false;
        foreach (var d in Downloaders(o))
        {
            IReadOnlyList<DownloadTarget> targets;
            try { targets = await d.DiscoverAsync(o.Type, CancellationToken.None); }
            catch (Exception e)
            {
                Console.WriteLine($"{d.Kind}: discovery failed: {e.Message}");
                continue;
            }
            Console.WriteLine($"{d.Kind}: {(targets.Count == 0 ? "(none)" : "")}");
            foreach (var t in targets)
            {
                any = true;
                Console.WriteLine($"  {t.Id}\t{t.DisplayName}");
            }
        }
        return any ? 0 : 1;
    }

    private static async Task<int> Download(Options o)
    {
        if (o.File is null) return Fail($"missing <file.bin>\n{Usage}");
        if (!File.Exists(o.File)) return Fail($"file not found: {o.File}");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        if (o.Timeout is { } timeout && o.Via is null) cts.CancelAfter(timeout);

        // Without --via, poll HID + drive like DownloadX1RCU (Bluetooth/WiFi need an explicit choice).
        var downloaders = o.Via is null
            ? Downloaders(o).Where(d => d.Kind is TransportKind.Hid or TransportKind.RemovableDrive).ToList()
            : Downloaders(o);

        (IDownloader, DownloadTarget)? chosen = null;
        var waiting = false;
        try
        {
            while (chosen is null)
            {
                foreach (var d in downloaders)
                {
                    var targets = await d.DiscoverAsync(o.Type, cts.Token);
                    var match = o.Target is null ? null : targets.FirstOrDefault(t => t.Id == o.Target);
                    if (o.Target is not null && match is null && d.Kind is TransportKind.Wifi or TransportKind.Bluetooth)
                        match = new DownloadTarget(d.Kind, o.Target, o.Target); // explicit IP / port not in the list
                    if (match is null && o.Target is null)
                    {
                        if (d.Kind == TransportKind.Bluetooth && targets.Count > 1)
                            return Fail($"several serial ports, choose one with --target:\n  {string.Join("\n  ", targets.Select(t => t.Id))}");
                        match = targets.FirstOrDefault();
                    }
                    if (match is not null) { chosen = (d, match); break; }
                }
                if (chosen is not null) break;
                if (o.Via is TransportKind.Bluetooth or TransportKind.Wifi)
                    return Fail($"no {o.Via} target available");
                if (!waiting)
                {
                    Console.WriteLine($"Waiting for {o.Type.Name} ({string.Join(" or ", downloaders.Select(d => d.Kind))}): connect USB, power on, press the download button. Ctrl+C to cancel.");
                    waiting = true;
                }
                await Task.Delay(300, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled: no controller found");
            return 1;
        }

        var (downloader, target) = chosen.Value;
        Console.WriteLine($"Downloading {Path.GetFileName(o.File)} via {downloader.Kind} to {target.DisplayName}");
        var lastLine = "";
        var progress = new Progress<DownloadProgress>(p =>
        {
            var line = $"[{p.Percent,3}%] {p.Message}";
            if (line != lastLine) Console.WriteLine(line);
            lastLine = line;
        });
        var outcome = await downloader.DownloadAsync(o.File, o.Type, target, progress, cts.Token);
        await Task.Delay(50); // let queued progress callbacks flush
        Console.WriteLine($"{outcome.Status}: {outcome.Message}");
        return outcome.IsSuccess ? 0 : 1;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }
}
