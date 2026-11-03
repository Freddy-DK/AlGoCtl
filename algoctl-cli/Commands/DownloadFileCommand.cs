using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;

namespace ALGoCtl;

/// <summary>
/// <c>downloadfile</c> — downloads a file over HTTP using parallel byte-range
/// streams, optionally extracting it as a zip archive.
/// </summary>
internal sealed class DownloadFileCommand
{
    public const string Name = "downloadfile";

    private const long MinRangeSize = 16L * 1024 * 1024;

    public const string Help = """
        algoctl downloadfile — download a file from Azure Front Door (or any HTTP
        server) fast, using parallel byte-range streams.
        Inspired by Stefan Maron's https://github.com/StefanMaron/MsDyn365Bc.On.Linux project.

        A HEAD request determines size, range support and cache state. A local
        cache hit (X-Cache: TCP_HIT) uses at most 2 streams; otherwise up to
        --maxthreads streams are used. Each stream retries and aborts a
        connection that stalls.

        Usage:
            algoctl downloadfile --url <url> --path <dest> [options]

        Options:
            --url <url>                       URL of the file to download. (required)
            --path <dest>                     Destination file, or the destination directory
                                              when --unzip is set. (required)
            --maxthreads <n>                  Parallel streams on a cache miss. Default: 16
            --stallseconds <n>                Abort a stream idle this long. Default: 60
            --maxattempts <n>                 Retries per chunk. Default: 4
            --unzip                           Extract the download into --path (a directory)
                                              instead of writing the file.
            --help, -h                        Show this help.
        """;

    public static int Run(string[] args)
    {
        // --help, -h — show command help and exit.
        if (ArgParser.HasHelpFlag(args.Skip(1)))
        {
            Console.WriteLine(Help);
            return 0;
        }

        var parameters = ArgParser.Parse(args.Skip(1));

        // --url (required)
        if (!parameters.TryGetValue("url", out var url) || string.IsNullOrWhiteSpace(url))
        {
            Console.Error.WriteLine("Error: --url is required.");
            Console.Error.WriteLine();
            Console.Error.WriteLine(Help);
            return 1;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            Console.Error.WriteLine($"Error: --url must be an absolute http(s) URL (got '{url}').");
            return 1;
        }

        // --path (required)
        if (!parameters.TryGetValue("path", out var path) || string.IsNullOrWhiteSpace(path))
        {
            Console.Error.WriteLine("Error: --path is required.");
            Console.Error.WriteLine();
            Console.Error.WriteLine(Help);
            return 1;
        }

        if (!TryParsePositiveInt(parameters, "maxthreads", 16, out var maxThreads)
            || !TryParsePositiveInt(parameters, "stallseconds", 60, out var stallSeconds)
            || !TryParsePositiveInt(parameters, "maxattempts", 4, out var maxAttempts))
            return 1;

        // --unzip (flag)
        var unzip = parameters.ContainsKey("unzip");

        return DownloadAsync(url, path, maxThreads, stallSeconds, maxAttempts, unzip).GetAwaiter().GetResult();
    }

    private static bool TryParsePositiveInt(Dictionary<string, string> parameters, string key, int defaultValue, out int value)
    {
        value = defaultValue;
        if (!parameters.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
            return true;
        if (int.TryParse(raw, out value) && value > 0)
            return true;
        Console.Error.WriteLine($"Error: --{key} must be a positive integer (got '{raw}').");
        return false;
    }

    private static async Task<int> DownloadAsync(string url, string path, int maxThreads, int stallSeconds, int maxAttempts, bool unzip)
    {
        // With --unzip, --path is the extract directory; the download lands in a temp file.
        var dlPath = unzip
            ? Path.Combine(Path.GetTempPath(), $"download-{Guid.NewGuid():N}.zip")
            : path;

        var dir = Path.GetDirectoryName(Path.GetFullPath(dlPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var handler = new SocketsHttpHandler { MaxConnectionsPerServer = Math.Max(2, maxThreads) };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        // Probe: size, range support, cache state.
        long? size = null;
        var acceptRanges = false;
        var xCache = "";
        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, url);
            using var resp = await client.SendAsync(head);
            resp.EnsureSuccessStatusCode();
            size = resp.Content.Headers.ContentLength;
            acceptRanges = resp.Headers.AcceptRanges.Contains("bytes");
            if (resp.Headers.TryGetValues("X-Cache", out var vals)) xCache = string.Join(",", vals);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"HEAD failed ({ex.Message}) — falling back to single stream");
        }

        // Only a local edge hit (TCP_HIT) saturates on one connection; remote/partial hits behave like a miss.
        var isHit = xCache.Contains("TCP_HIT", StringComparison.OrdinalIgnoreCase);
        var rangeable = acceptRanges && size is >= MinRangeSize;

        // Some proxies advertise Accept-Ranges but return 200 + full body; confirm with a 1-byte probe.
        if (rangeable)
        {
            try
            {
                using var pq = new HttpRequestMessage(HttpMethod.Get, url);
                pq.Headers.Range = new RangeHeaderValue(0, 0);
                using var pr = await client.SendAsync(pq, HttpCompletionOption.ResponseHeadersRead);
                if ((int)pr.StatusCode != 206)
                {
                    Console.WriteLine($"Server ignored Range (HTTP {(int)pr.StatusCode}) — using single stream");
                    rangeable = false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Range probe failed ({ex.Message}) — using single stream");
                rangeable = false;
            }
        }

        var streams = !rangeable ? 1
                    : isHit ? Math.Min(2, maxThreads)
                    : maxThreads;

        Console.WriteLine("Downloading file:");
        Console.WriteLine($"  URL:                     {url}");
        Console.WriteLine($"  Size:                    {(size.HasValue ? $"{size:N0} bytes" : "unknown")}");
        Console.WriteLine($"  X-Cache:                 {(xCache.Length > 0 ? xCache : "n/a")}");
        Console.WriteLine($"  Streams:                 {streams}");
        Console.WriteLine();

        // Plan the byte ranges (null start = whole file, no Range header).
        var ranges = new List<(int Index, long? Start, long? End)>();
        if (streams <= 1)
        {
            ranges.Add((0, null, null));
        }
        else
        {
            var chunk = (size!.Value + streams - 1) / streams;
            for (var i = 0; i < streams; i++)
            {
                var start = (long)i * chunk;
                if (start >= size.Value) break;
                var end = Math.Min(start + chunk - 1, size.Value - 1);
                ranges.Add((i, start, end));
            }
        }

        var sw = Stopwatch.StartNew();
        var tasks = ranges.Select(r => DownloadRangeAsync(client, url, dlPath, r.Index, r.Start, r.End, stallSeconds, maxAttempts)).ToArray();
        var failures = (await Task.WhenAll(tasks)).Where(e => e is not null).ToArray();
        if (failures.Length > 0)
        {
            foreach (var r in ranges) TryDelete($"{dlPath}.part{r.Index}");
            Console.Error.WriteLine($"Error: download failed: {string.Join("; ", failures)}");
            return 1;
        }

        // Stitch parts in order.
        await using (var outFs = File.Create(dlPath))
        {
            foreach (var r in ranges.OrderBy(r => r.Index))
            {
                var part = $"{dlPath}.part{r.Index}";
                await using (var inFs = File.OpenRead(part)) await inFs.CopyToAsync(outFs);
                File.Delete(part);
            }
        }

        var total = new FileInfo(dlPath).Length;
        if (size.HasValue && total != size.Value)
        {
            Console.Error.WriteLine($"Error: size mismatch: wrote {total}, expected {size}");
            if (unzip) TryDelete(dlPath);
            return 1;
        }

        sw.Stop();
        var mb = total / 1024.0 / 1024.0;
        var sec = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
        Console.WriteLine($"Downloaded {mb:N0} MB in {sec:N1}s (~{mb / sec:N0} MB/s)");

        if (unzip)
        {
            Console.WriteLine($"Extracting to {path}...");
            var ew = Stopwatch.StartNew();
            try { ExtractZip(dlPath, path); }
            finally { TryDelete(dlPath); }
            ew.Stop();
            Console.WriteLine($"Extracted in {ew.Elapsed.TotalSeconds:N1}s -> {path}");
        }
        else
        {
            Console.WriteLine($"Saved to {path}");
        }
        return 0;
    }

    /// <summary>Downloads one byte range to <c>{dlPath}.part{index}</c>.</summary>
    /// <returns><c>null</c> on success, or an error string after all attempts fail.</returns>
    private static async Task<string?> DownloadRangeAsync(HttpClient client, string url, string dlPath, int index, long? start, long? end, int stallSeconds, int maxAttempts)
    {
        var part = $"{dlPath}.part{index}";
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (start.HasValue) req.Headers.Range = new RangeHeaderValue(start, end);
                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                resp.EnsureSuccessStatusCode();

                await using (var body = await resp.Content.ReadAsStreamAsync())
                await using (var fs = File.Create(part))
                {
                    // Per-read stall timeout, like curl --speed-time.
                    var buf = new byte[1024 * 1024];
                    while (true)
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(stallSeconds));
                        int n;
                        try { n = await body.ReadAsync(buf, cts.Token); }
                        catch (OperationCanceledException) { throw new IOException("stalled (no data)"); }
                        if (n <= 0) break;
                        await fs.WriteAsync(buf.AsMemory(0, n));
                    }
                }

                var want = start.HasValue ? end!.Value - start.Value + 1 : new FileInfo(part).Length;
                var got = new FileInfo(part).Length;
                if (got != want) throw new IOException($"got {got} of {want} bytes");
                return null;
            }
            catch (Exception ex)
            {
                if (attempt == maxAttempts) return $"range {index}: {ex.Message}";
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2));
            }
        }
        return $"range {index}: exhausted retries";
    }

    private static void TryDelete(string p)
    {
        try { File.Delete(p); } catch { /* best effort */ }
    }

    /// <summary>
    /// Multi-threaded zip extraction. A <see cref="ZipArchive"/> isn't thread-safe,
    /// so each worker opens its own handle; entry order is stable across opens.
    /// </summary>
    private static void ExtractZip(string zipPath, string destDir)
    {
        var destFull = Path.GetFullPath(destDir);
        Directory.CreateDirectory(destFull);

        var files = new List<(int Index, string FullName, long Length)>();
        using (var za = ZipFile.OpenRead(zipPath))
        {
            for (var i = 0; i < za.Entries.Count; i++)
            {
                var e = za.Entries[i];
                if (string.IsNullOrEmpty(e.Name)) continue; // directory entry
                files.Add((i, e.FullName, e.Length));
            }
        }

        // Resolve target with a zip-slip guard.
        string SafeTarget(string fullName)
        {
            var target = Path.GetFullPath(Path.Combine(destFull, fullName));
            if (!target.StartsWith(destFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new IOException($"zip entry escapes destination: {fullName}");
            return target;
        }

        // Pre-create dirs up front: ExtractToFile won't, and doing it in the workers isn't race-safe.
        foreach (var d in files.Select(f => Path.GetDirectoryName(SafeTarget(f.FullName)))
                               .Where(d => !string.IsNullOrEmpty(d)).Distinct())
            Directory.CreateDirectory(d!);

        // Largest-first round-robin keeps per-worker byte counts balanced.
        files.Sort((a, b) => b.Length.CompareTo(a.Length));
        var workers = Math.Min(8, Environment.ProcessorCount);
        var chunks = Enumerable.Range(0, workers)
                               .Select(w => files.Where((_, idx) => idx % workers == w).ToList())
                               .ToList();

        Parallel.ForEach(chunks, new ParallelOptions { MaxDegreeOfParallelism = workers }, chunk =>
        {
            using var za = ZipFile.OpenRead(zipPath);
            foreach (var f in chunk)
                za.Entries[f.Index].ExtractToFile(SafeTarget(f.FullName), overwrite: true);
        });
    }
}
