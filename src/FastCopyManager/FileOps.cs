using System.Buffers;
using System.IO;
using System.IO.Hashing;
using System.IO.Compression;
using System.Threading.Channels;
using Microsoft.VisualBasic.FileIO;
using SharpCompress.Archives;

namespace FastCopyManager;

public sealed record CopyOptions(bool Overwrite, bool Verify, bool Split = false);

/// <summary>Sdílený stav kopírování, čte ho UI přes časovač.</summary>
public sealed class CopyStats
{
    private long _total, _done;
    public long Total => Interlocked.Read(ref _total);
    public long Done => Interlocked.Read(ref _done);
    public volatile string Current = "";

    private readonly ManualResetEventSlim _gate = new(true);
    public bool Paused => !_gate.IsSet;
    public void TogglePause() { if (_gate.IsSet) _gate.Reset(); else _gate.Set(); }
    /// <summary>Blokuje, dokud je kopírování pozastavené (zrušení ho probudí).</summary>
    public void Wait(CancellationToken ct) => _gate.Wait(ct);
    public void AddTotal(long n) => Interlocked.Add(ref _total, n);
    public void AddDone(long n) => Interlocked.Add(ref _done, n);
}

/// <summary>
/// Vlastní rychlé kopírovací jádro:
/// velké soubory běží v rouře (jedno vlákno čte, druhé zapisuje, několik bufferů v předstihu),
/// malé soubory se kopírují paralelně, zápis jde do dočasného souboru a přejmenuje se až po dokončení,
/// volitelné ověření hashem (xxHash3) zdroje proti zapsanému cíli.
/// </summary>
public static class FileOps
{
    private const int ChunkSize = 4 * 1024 * 1024;     // buffer velkých souborů
    private const int PipelineDepth = 4;               // kolik bufferů je v předstihu
    private const long LargeFile = 8L * 1024 * 1024;   // od kdy se použije rouru
    internal static long Fat32Limit = 4L * 1024 * 1024 * 1024 - 1;   // v testech se zmenšuje

    private sealed record Job(string Src, string Dst, long Size, DateTime Modified, bool Split = false);

    internal static long PartSize = 2L * 1024 * 1024 * 1024;   // velikost části při dělení na FAT32

    public static bool Conflicts(IEnumerable<FileItem> items, string destDir) =>
        items.Any(i => i.IsDir ? Directory.Exists(Path.Combine(destDir, i.Name)) : File.Exists(Path.Combine(destDir, i.Name)));

    public static async Task CopyAsync(IEnumerable<FileItem> items, string destDir, CopyOptions opt, CopyStats stats, CancellationToken ct)
    {
        if (!Directory.Exists(destDir))
            throw new IOException(Loc.T("Cílová složka neexistuje: {0}", destDir));

        var jobs = new List<Job>();
        foreach (var item in items)
        {
            var target = Path.Combine(destDir, item.Name);
            if (item.IsDir) Plan(item.FullPath, target, opt, jobs);
            else PlanFile(new FileInfo(item.FullPath), target, opt, jobs);
        }

        CheckDestination(destDir, jobs, opt);
        stats.AddTotal(jobs.Sum(j => j.Size) * (opt.Verify ? 2 : 1));

        var small = jobs.Where(j => !j.Split && j.Size < LargeFile).ToList();
        var large = jobs.Where(j => j.Split || j.Size >= LargeFile).ToList();

        var dop = Math.Clamp(Environment.ProcessorCount, 2, 8);
        await Parallel.ForEachAsync(small, new ParallelOptions { MaxDegreeOfParallelism = dop, CancellationToken = ct },
            (j, token) => { CopySmall(j, opt, stats, token); return ValueTask.CompletedTask; });

        foreach (var j in large)
        {
            if (j.Split) CopySplit(j, opt, stats, ct);
            else await CopyPipelinedAsync(j, opt, stats, ct);
        }
    }

    /// <summary>Kontrola cíle před startem: existuje, jde do něj zapisovat, je v něm dost volného místa.</summary>
    private static void CheckDestination(string destDir, List<Job> jobs, CopyOptions opt)
    {
        if (!Directory.Exists(destDir))
            throw new IOException(Loc.T("Cílová složka neexistuje: {0}", destDir));

        var probe = Path.Combine(destDir, ".fcm_probe_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new IOException(Loc.T("Do cíle nelze zapisovat (jen pro čtení nebo chybí oprávnění): {0}", destDir));
        }

        var root = Path.GetPathRoot(Path.GetFullPath(destDir));
        if (root is null) return;
        // Přepisované soubory uvolní své místo, takže se odečtou.
        var needed = jobs.Sum(j => j.Size) - (opt.Overwrite ? jobs.Where(j => File.Exists(j.Dst)).Sum(j => new FileInfo(j.Dst).Length) : 0);
        var free = new DriveInfo(root).AvailableFreeSpace;
        if (needed > free)
            throw new IOException(Loc.T("V cíli není dost místa: potřeba {0} MB, volno {1} MB.", needed / 1048576, free / 1048576));
    }

    // ---- FAT32: dělení souborů nad 4 GB a spojování ----

    /// <summary>Je mezi položkami soubor nad 4 GB, zatímco cíl je FAT32?</summary>
    public static bool NeedsSplit(IEnumerable<FileItem> items, string destDir)
    {
        if (!IsFat32(destDir)) return false;
        foreach (var item in items)
        {
            if (!item.IsDir) { if (new FileInfo(item.FullPath).Length > Fat32Limit) return true; continue; }
            if (new DirectoryInfo(item.FullPath).EnumerateFiles("*", System.IO.SearchOption.AllDirectories).Any(f => f.Length > Fat32Limit))
                return true;
        }
        return false;
    }

    private static IEnumerable<string> PartFiles(string dst)
    {
        var dir = Path.GetDirectoryName(dst)!;
        var name = Path.GetFileName(dst);
        return Directory.EnumerateFiles(dir, name + ".???")
            .Where(f => Path.GetExtension(f).Length == 4 && Path.GetExtension(f)[1..].All(char.IsDigit));
    }

    private static void CopySplit(Job j, CopyOptions opt, CopyStats stats, CancellationToken ct)
    {
        stats.Current = j.Src;
        var finals = new List<string>();
        var tmps = new List<string>();
        var hasher = opt.Verify ? new XxHash3() : null;
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            if (opt.Overwrite)
                foreach (var old in PartFiles(j.Dst)) File.Delete(old);

            using (var input = new FileStream(j.Src, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan))
            {
                FileStream? output = null;
                long inPart = 0;
                try
                {
                    int n;
                    while ((n = input.Read(buffer, 0, ChunkSize)) > 0)
                    {
                        var off = 0;
                        while (off < n)
                        {
                            ct.ThrowIfCancellationRequested(); stats.Wait(ct);
                            if (output is null || inPart >= PartSize)
                            {
                                output?.Dispose();
                                var name = $"{j.Dst}.{finals.Count + 1:000}";
                                finals.Add(name);
                                tmps.Add(name + ".fcm.tmp");
                                output = new FileStream(tmps[^1], FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.SequentialScan);
                                inPart = 0;
                            }
                            var take = (int)Math.Min(n - off, PartSize - inPart);
                            output.Write(buffer, off, take);
                            hasher?.Append(buffer.AsSpan(off, take));
                            off += take;
                            inPart += take;
                            stats.AddDone(take);
                        }
                    }
                }
                finally
                {
                    output?.Dispose();
                }
            }

            if (hasher is not null)
            {
                var actual = new XxHash3();
                foreach (var t in tmps)
                {
                    using var check = new FileStream(t, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
                    int n;
                    while ((n = check.Read(buffer, 0, ChunkSize)) > 0)
                    {
                        ct.ThrowIfCancellationRequested(); stats.Wait(ct);
                        actual.Append(buffer.AsSpan(0, n));
                        stats.AddDone(n);
                    }
                }
                if (!hasher.GetCurrentHash().AsSpan().SequenceEqual(actual.GetCurrentHash()))
                    throw new IOException(Loc.T("Ověření selhalo (hash se neshoduje): {0}", j.Src));
            }

            for (var i = 0; i < tmps.Count; i++)
                File.Move(tmps[i], finals[i], opt.Overwrite);
        }
        catch
        {
            foreach (var t in tmps) TryDelete(t);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Rozpozná část rozděleného souboru (název.001, .002 ...) a vrátí cestu bez číslovky.</summary>
    public static string? SplitBaseName(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Length == 4 && ext[1..].All(char.IsDigit) ? path[..^4] : null;
    }

    /// <summary>Spojí části název.001, název.002 ... do jednoho souboru.</summary>
    public static void Join(string anyPart, string destFile, CopyStats stats, CancellationToken ct)
    {
        var baseName = SplitBaseName(anyPart) ?? throw new IOException(Loc.T("Vyber soubor s příponou .001, .002 ..."));
        var parts = new List<string>();
        for (var i = 1; File.Exists($"{baseName}.{i:000}"); i++) parts.Add($"{baseName}.{i:000}");
        if (parts.Count == 0) throw new IOException(Loc.T("Nenalezena první část (.001)."));

        var total = parts.Sum(p => new FileInfo(p).Length);
        if (total > Fat32Limit && IsFat32(destFile))
            throw new IOException(Loc.T("Spojený soubor by měl přes 4 GB a cíl je FAT32."));
        var root = Path.GetPathRoot(Path.GetFullPath(destFile));
        if (root is not null && new DriveInfo(root).AvailableFreeSpace < total)
            throw new IOException(Loc.T("V cíli není dost místa: potřeba {0} MB.", total / 1048576));

        stats.AddTotal(total);
        var tmp = destFile + ".fcm.tmp";
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.SequentialScan))
                foreach (var part in parts)
                {
                    stats.Current = part;
                    using var input = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
                    int n;
                    while ((n = input.Read(buffer, 0, ChunkSize)) > 0)
                    {
                        ct.ThrowIfCancellationRequested(); stats.Wait(ct);
                        output.Write(buffer, 0, n);
                        stats.AddDone(n);
                    }
                }
            File.Move(tmp, destFile, true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // ---- archivy ----

    /// <summary>Jedna položka k rozbalení: klíč v archivu a relativní cílová cesta.</summary>
    public sealed record ExtractEntry(string Key, string Rel);

    public static string NormalizeKey(string key) => key.Replace('\\', '/').Trim('/');

    public static bool ExtractConflicts(IEnumerable<ExtractEntry> plan, string destDir) =>
        plan.Any(p => File.Exists(Path.Combine(destDir, p.Rel)));

    /// <summary>Rozbalí vybrané položky archivu (zip, rar, 7z, tar) vlastním kopírovacím cyklem, přes dočasný soubor.</summary>
    public static void Extract(string archivePath, IReadOnlyList<ExtractEntry> plan, string destDir, CopyOptions opt, CopyStats stats, CancellationToken ct)
    {
        if (!Directory.Exists(destDir))
            throw new IOException(Loc.T("Cílová složka neexistuje: {0}", destDir));

        using var archive = ArchiveFactory.OpenArchive(archivePath);
        var map = new Dictionary<string, IArchiveEntry>();
        foreach (var e in archive.Entries.Where(e => !e.IsDirectory))
            map.TryAdd(NormalizeKey(e.Key ?? ""), e);

        var destFull = Path.GetFullPath(destDir).TrimEnd('\\') + "\\";
        var jobs = new List<Job>();
        foreach (var p in plan)
        {
            if (!map.TryGetValue(p.Key, out var entry)) continue;
            var dst = Path.GetFullPath(Path.Combine(destDir, p.Rel));
            if (!dst.StartsWith(destFull, StringComparison.OrdinalIgnoreCase))
                throw new IOException(Loc.T("Nebezpečná cesta v archivu: {0}", p.Key));
            jobs.Add(new Job(p.Key, dst, entry.Size, entry.LastModifiedTime ?? DateTime.Now));
        }

        CheckDestination(destDir, jobs, opt);
        stats.AddTotal(jobs.Sum(j => j.Size));

        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            foreach (var j in jobs)
            {
                ct.ThrowIfCancellationRequested(); stats.Wait(ct);
                if (!opt.Overwrite && File.Exists(j.Dst)) continue;
                stats.Current = j.Src;
                Directory.CreateDirectory(Path.GetDirectoryName(j.Dst)!);
                var tmp = j.Dst + ".fcm.tmp";
                try
                {
                    using (var input = map[j.Src].OpenEntryStream())
                    using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.SequentialScan))
                    {
                        int n;
                        while ((n = input.Read(buffer, 0, ChunkSize)) > 0)
                        {
                            ct.ThrowIfCancellationRequested(); stats.Wait(ct);
                            output.Write(buffer, 0, n);
                            stats.AddDone(n);
                        }
                    }
                    if (j.Modified.Year >= 1980) File.SetLastWriteTime(tmp, j.Modified);
                    File.Move(tmp, j.Dst, opt.Overwrite);
                }
                catch
                {
                    TryDelete(tmp);
                    throw;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Zabalí vybrané soubory a složky do ZIPu (zápis do dočasného souboru, přejmenování až po dokončení).</summary>
    public static void PackZip(IReadOnlyList<FileItem> items, string zipPath, CopyStats stats, CancellationToken ct)
    {
        var files = new List<(string Full, string Entry, long Size, DateTime Mod)>();
        foreach (var item in items)
        {
            if (!item.IsDir)
            {
                var fi = new FileInfo(item.FullPath);
                files.Add((fi.FullName, fi.Name, fi.Length, fi.LastWriteTime));
                continue;
            }
            var root = new DirectoryInfo(item.FullPath);
            foreach (var f in root.EnumerateFiles("*", System.IO.SearchOption.AllDirectories))
                files.Add((f.FullName, item.Name + "/" + Path.GetRelativePath(root.FullName, f.FullName).Replace('\\', '/'), f.Length, f.LastWriteTime));
        }

        stats.AddTotal(files.Sum(f => f.Size));
        var zipFull = Path.GetFullPath(zipPath);
        var tmp = zipPath + ".fcm.tmp";
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, ChunkSize))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (var f in files)
                {
                    ct.ThrowIfCancellationRequested(); stats.Wait(ct);
                    if (f.Full.Equals(zipFull, StringComparison.OrdinalIgnoreCase)) continue;
                    stats.Current = f.Full;
                    var entry = zip.CreateEntry(f.Entry, CompressionLevel.Fastest);
                    entry.LastWriteTime = f.Mod.Year < 1980 ? new DateTime(1980, 1, 1) : f.Mod;
                    using var es = entry.Open();
                    using var input = new FileStream(f.Full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan);
                    int n;
                    while ((n = input.Read(buffer, 0, ChunkSize)) > 0)
                    {
                        ct.ThrowIfCancellationRequested(); stats.Wait(ct);
                        es.Write(buffer, 0, n);
                        stats.AddDone(n);
                    }
                }
            }
            File.Move(tmp, zipPath, true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void Plan(string src, string dst, CopyOptions opt, List<Job> jobs)
    {
        var srcFull = Path.GetFullPath(src).TrimEnd('\\') + "\\";
        if (Path.GetFullPath(dst).StartsWith(srcFull, StringComparison.OrdinalIgnoreCase))
            throw new IOException(Loc.T("Nelze kopírovat složku sama do sebe."));

        Directory.CreateDirectory(dst);
        var dir = new DirectoryInfo(src);
        foreach (var f in dir.EnumerateFiles())
            PlanFile(f, Path.Combine(dst, f.Name), opt, jobs);
        foreach (var d in dir.EnumerateDirectories())
            Plan(d.FullName, Path.Combine(dst, d.Name), opt, jobs);
    }

    private static void PlanFile(FileInfo f, string dst, CopyOptions opt, List<Job> jobs)
    {
        var split = f.Length > Fat32Limit && IsFat32(dst);
        if (split && !opt.Split)
            throw new IOException(Loc.T("Soubor {0} je větší než 4 GB a cíl je FAT32.", f.Name));
        if (!opt.Overwrite && File.Exists(split ? dst + ".001" : dst)) return;
        jobs.Add(new Job(f.FullName, dst, f.Length, f.LastWriteTime, split));
    }

    /// <summary>Malý soubor: jeden průchod čtení → zápis, bez režie roury.</summary>
    private static void CopySmall(Job j, CopyOptions opt, CopyStats stats, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); stats.Wait(ct);
        stats.Current = j.Src;
        var tmp = j.Dst + ".fcm.tmp";
        var hasher = opt.Verify ? new XxHash3() : null;
        var size = (int)Math.Clamp(j.Size, 4096, ChunkSize);
        var buffer = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            using (var input = new FileStream(j.Src, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan))
            using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.SequentialScan))
            {
                int n;
                while ((n = input.Read(buffer, 0, size)) > 0)
                {
                    ct.ThrowIfCancellationRequested(); stats.Wait(ct);
                    hasher?.Append(buffer.AsSpan(0, n));
                    output.Write(buffer, 0, n);
                    stats.AddDone(n);
                }
            }
            Finish(j, tmp, hasher, opt, stats, ct);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Velký soubor: čtecí úloha plní buffery do fronty, zápis je odebírá, takže disk čte a zapisuje zároveň.</summary>
    private static async Task CopyPipelinedAsync(Job j, CopyOptions opt, CopyStats stats, CancellationToken ct)
    {
        stats.Current = j.Src;
        var tmp = j.Dst + ".fcm.tmp";
        var hasher = opt.Verify ? new XxHash3() : null;
        var channel = Channel.CreateBounded<(byte[] Buf, int Len)>(
            new BoundedChannelOptions(PipelineDepth) { SingleReader = true, SingleWriter = true });
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var reader = Task.Run(async () =>
        {
            try
            {
                using var input = new FileStream(j.Src, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
                while (true)
                {
                    var buf = ArrayPool<byte>.Shared.Rent(ChunkSize);
                    var n = input.Read(buf, 0, ChunkSize);
                    if (n <= 0) { ArrayPool<byte>.Shared.Return(buf); break; }
                    hasher?.Append(buf.AsSpan(0, n));
                    await channel.Writer.WriteAsync((buf, n), linked.Token);
                }
                channel.Writer.Complete();
            }
            catch (Exception ex)
            {
                channel.Writer.Complete(ex);
            }
        }, linked.Token);

        try
        {
            using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.SequentialScan))
            {
                await foreach (var (buf, len) in channel.Reader.ReadAllAsync(linked.Token))
                {
                    stats.Wait(linked.Token);
                    output.Write(buf, 0, len);
                    stats.AddDone(len);
                    ArrayPool<byte>.Shared.Return(buf);
                }
            }
            await reader;
            Finish(j, tmp, hasher, opt, stats, ct);
        }
        catch
        {
            linked.Cancel();
            TryDelete(tmp);
            throw;
        }
    }

    private static void Finish(Job j, string tmp, XxHash3? sourceHash, CopyOptions opt, CopyStats stats, CancellationToken ct)
    {
        if (opt.Verify && sourceHash is not null)
        {
            var expected = sourceHash.GetCurrentHash();
            var actual = HashFile(tmp, stats, ct);
            if (!expected.AsSpan().SequenceEqual(actual))
                throw new IOException(Loc.T("Ověření selhalo (hash se neshoduje): {0}", j.Src));
        }
        File.SetLastWriteTime(tmp, j.Modified);
        File.Move(tmp, j.Dst, opt.Overwrite);
    }

    private static byte[] HashFile(string path, CopyStats stats, CancellationToken ct)
    {
        var hasher = new XxHash3();
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
            int n;
            while ((n = input.Read(buffer, 0, ChunkSize)) > 0)
            {
                ct.ThrowIfCancellationRequested(); stats.Wait(ct);
                hasher.Append(buffer.AsSpan(0, n));
                stats.AddDone(n);
            }
            return hasher.GetCurrentHash();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* úklid dočasného souboru se nepovedl, nevadí */ }
    }

    internal static bool? ForceFat32 = null;   // jen pro testy

    private static bool IsFat32(string path)
    {
        if (ForceFat32 is { } forced) return forced;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return root is not null && new DriveInfo(root).DriveFormat.Equals("FAT32", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Koš existuje jen na pevných discích, na výměnných (flashka) se maže natrvalo.</summary>
    public static bool HasRecycleBin(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return root is not null && new DriveInfo(root).DriveType == DriveType.Fixed;
        }
        catch { return false; }
    }

    public static void Delete(IEnumerable<FileItem> items, bool recycle)
    {
        var mode = recycle ? RecycleOption.SendToRecycleBin : RecycleOption.DeletePermanently;
        foreach (var i in items)
        {
            if (i.IsDir) FileSystem.DeleteDirectory(i.FullPath, UIOption.OnlyErrorDialogs, mode);
            else FileSystem.DeleteFile(i.FullPath, UIOption.OnlyErrorDialogs, mode);
        }
    }
}
