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

    public static readonly string[] ArchiveExtensions = { ".zip", ".rar", ".7z", ".tar" };

    private static SharpCompress.Readers.ReaderOptions ReaderOptionsFor(string? password) =>
        new() { Password = password };

    /// <summary>Je v archivu něco zašifrované (nebo se bez hesla nedá ani otevřít)?</summary>
    public static bool IsEncrypted(string archivePath)
    {
        if (IsTarGzName(archivePath)) return false;
        try
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath);
            return archive.Entries.Any(e => !e.IsDirectory && e.IsEncrypted);
        }
        catch
        {
            return true;   // zašifrované hlavičky: bez hesla nejde ani vypsat obsah
        }
    }

    public static bool IsArchiveName(string name) =>
        ArchiveExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase) || IsTarGzName(name);

    public static bool IsTarGzName(string name) =>
        name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase);

    /// <summary>Obsah TAR.GZ (SharpCompress ho neotevře): projde se proudově přes GZip a System.Formats.Tar.</summary>
    public static List<(string Key, bool IsDir, long Size, DateTime Mod)> ListTarGz(string archivePath)
    {
        var list = new List<(string Key, bool IsDir, long Size, DateTime Mod)>();
        using var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var tar = new System.Formats.Tar.TarReader(gz);
        while (tar.GetNextEntry(copyData: false) is { } entry)
        {
            var key = NormalizeKey(entry.Name);
            if (key.Length == 0) continue;
            if (entry.EntryType == System.Formats.Tar.TarEntryType.Directory)
                list.Add((key, true, 0, entry.ModificationTime.LocalDateTime));
            else if (entry.EntryType is System.Formats.Tar.TarEntryType.RegularFile or System.Formats.Tar.TarEntryType.V7RegularFile)
                list.Add((key, false, entry.Length, entry.ModificationTime.LocalDateTime));
        }
        return list;
    }

    private static void ExtractTarGz(string archivePath, IReadOnlyList<ExtractEntry> plan, string destDir, CopyOptions opt, CopyStats stats, CancellationToken ct)
    {
        var wanted = plan.GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.First().Rel);
        var destFull = Path.GetFullPath(destDir).TrimEnd((char)92) + (char)92;
        var jobs = new List<Job>();
        foreach (var (key, isDir, size, mod) in ListTarGz(archivePath))
        {
            if (isDir || !wanted.TryGetValue(key, out var rel)) continue;
            var dst = Path.GetFullPath(Path.Combine(destDir, rel));
            if (!dst.StartsWith(destFull, StringComparison.OrdinalIgnoreCase))
                throw new IOException(Loc.T("Nebezpečná cesta v archivu: {0}", key));
            jobs.Add(new Job(key, dst, size, mod));
        }

        CheckDestination(destDir, jobs, opt);
        stats.AddTotal(jobs.Sum(j => j.Size));
        var byKey = jobs.ToDictionary(j => j.Src);

        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            using var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var tar = new System.Formats.Tar.TarReader(gz);
            while (tar.GetNextEntry(copyData: false) is { } entry)
            {
                ct.ThrowIfCancellationRequested(); stats.Wait(ct);
                if (entry.DataStream is null || !byKey.TryGetValue(NormalizeKey(entry.Name), out var j)) continue;
                if (!opt.Overwrite && File.Exists(j.Dst)) continue;
                stats.Current = j.Src;
                Directory.CreateDirectory(Path.GetDirectoryName(j.Dst)!);
                var tmp = j.Dst + ".fcm.tmp";
                try
                {
                    using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.SequentialScan))
                    {
                        int n;
                        while ((n = entry.DataStream.Read(buffer, 0, ChunkSize)) > 0)
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

    /// <summary>Všechny soubory archivu jako plán rozbalení do kořene cíle.</summary>
    public static List<ExtractEntry> PlanAll(string archivePath, string? password = null)
    {
        try { return PlanAllCore(archivePath, password); }
        catch (Exception ex) when (WrongPassword(ex, password))
        {
            throw new IOException(Loc.T("Špatné heslo nebo poškozený archiv."), ex);
        }
    }

    private static List<ExtractEntry> PlanAllCore(string archivePath, string? password)
    {
        if (IsTarGzName(archivePath))
            return ListTarGz(archivePath).Where(r => !r.IsDir).Select(r => new ExtractEntry(r.Key, r.Key)).ToList();

        using var archive = ArchiveFactory.OpenArchive(archivePath, ReaderOptionsFor(password));
        return archive.Entries
            .Where(e => !e.IsDirectory)
            .Select(e => NormalizeKey(e.Key ?? ""))
            .Where(k => k.Length > 0)
            .Select(k => new ExtractEntry(k, k))
            .ToList();
    }

    /// <summary>Jedna položka k rozbalení: klíč v archivu a relativní cílová cesta.</summary>
    public sealed record ExtractEntry(string Key, string Rel);

    public static string NormalizeKey(string key) => key.Replace('\\', '/').Trim('/');

    public static bool ExtractConflicts(IEnumerable<ExtractEntry> plan, string destDir) =>
        plan.Any(p => File.Exists(Path.Combine(destDir, p.Rel)));

    /// <summary>Rozbalí vybrané položky archivu (zip, rar, 7z, tar) vlastním kopírovacím cyklem, přes dočasný soubor.</summary>
    public static void Extract(string archivePath, IReadOnlyList<ExtractEntry> plan, string destDir, CopyOptions opt, CopyStats stats, CancellationToken ct, string? password = null)
    {
        try { ExtractCore(archivePath, plan, destDir, opt, stats, ct, password); }
        catch (Exception ex) when (WrongPassword(ex, password))
        {
            throw new IOException(Loc.T("Špatné heslo nebo poškozený archiv."), ex);
        }
    }

    /// <summary>Chyba při otevírání s heslem (ne zrušení, ne chyba disku) = nejspíš špatné heslo.</summary>
    private static bool WrongPassword(Exception ex, string? password) =>
        !string.IsNullOrEmpty(password) && ex is not (OperationCanceledException or IOException or UnauthorizedAccessException);

    private static void ExtractCore(string archivePath, IReadOnlyList<ExtractEntry> plan, string destDir, CopyOptions opt, CopyStats stats, CancellationToken ct, string? password)
    {
        if (!Directory.Exists(destDir))
            throw new IOException(Loc.T("Cílová složka neexistuje: {0}", destDir));

        if (IsTarGzName(archivePath))
        {
            ExtractTarGz(archivePath, plan, destDir, opt, stats, ct);
            return;
        }

        // Zaheslované ZIPy (AES-256, ZipCrypto) čte SharpZipLib, SharpCompress si s AES ZIPy z jiných nástrojů neporadí.
        if (!string.IsNullOrEmpty(password) && string.Equals(Path.GetExtension(archivePath), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            ExtractZipWithPassword(archivePath, plan, destDir, opt, stats, ct, password);
            return;
        }

        using var archive = ArchiveFactory.OpenArchive(archivePath, ReaderOptionsFor(password));
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

    private static void ExtractZipWithPassword(string archivePath, IReadOnlyList<ExtractEntry> plan, string destDir, CopyOptions opt, CopyStats stats, CancellationToken ct, string password)
    {
        using var zip = new ICSharpCode.SharpZipLib.Zip.ZipFile(archivePath) { Password = password };
        var map = new Dictionary<string, ICSharpCode.SharpZipLib.Zip.ZipEntry>();
        foreach (ICSharpCode.SharpZipLib.Zip.ZipEntry e in zip)
            if (e.IsFile) map.TryAdd(NormalizeKey(e.Name), e);

        var destFull = Path.GetFullPath(destDir).TrimEnd((char)92) + (char)92;
        var jobs = new List<Job>();
        foreach (var p in plan)
        {
            if (!map.TryGetValue(p.Key, out var entry)) continue;
            var dst = Path.GetFullPath(Path.Combine(destDir, p.Rel));
            if (!dst.StartsWith(destFull, StringComparison.OrdinalIgnoreCase))
                throw new IOException(Loc.T("Nebezpečná cesta v archivu: {0}", p.Key));
            jobs.Add(new Job(p.Key, dst, entry.Size, entry.DateTime));
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
                    using (var input = zip.GetInputStream(map[j.Src]))
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
    public static void PackZip(IReadOnlyList<FileItem> items, string zipPath, CopyStats stats, CancellationToken ct, CompressionLevel level = CompressionLevel.Fastest)
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
                    var entry = zip.CreateEntry(f.Entry, level);
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

    // ---- balení: ZIP (volitelně s heslem AES-256), TAR, TAR.GZ ----

    public static void Pack(IReadOnlyList<FileItem> items, string path, PackFormat format, CompressionLevel level, string? password, CopyStats stats, CancellationToken ct)
    {
        switch (format)
        {
            case PackFormat.Zip when !string.IsNullOrEmpty(password):
                PackZipEncrypted(items, path, password, level, stats, ct);
                break;
            case PackFormat.Zip:
                PackZip(items, path, stats, ct, level);
                break;
            case PackFormat.SevenZip or PackFormat.Rar:
                PackExternal(items, path, format, level, password, stats, ct);
                break;
            default:
                PackTar(items, path, format == PackFormat.TarGz, level, stats, ct);
                break;
        }
    }

    private static List<(string Full, string Entry, long Size, DateTime Mod)> CollectFiles(IReadOnlyList<FileItem> items)
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
                files.Add((f.FullName, item.Name + "/" + Path.GetRelativePath(root.FullName, f.FullName).Replace(Path.DirectorySeparatorChar, '/'), f.Length, f.LastWriteTime));
        }
        return files;
    }

    private static void PackZipEncrypted(IReadOnlyList<FileItem> items, string zipPath, string password, CompressionLevel level, CopyStats stats, CancellationToken ct)
    {
        var files = CollectFiles(items);
        stats.AddTotal(files.Sum(f => f.Size));
        var zipFull = Path.GetFullPath(zipPath);
        var tmp = zipPath + ".fcm.tmp";
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, ChunkSize))
            using (var zs = new ICSharpCode.SharpZipLib.Zip.ZipOutputStream(fs))
            {
                zs.SetLevel(level switch
                {
                    CompressionLevel.NoCompression => 0,
                    CompressionLevel.Fastest => 1,
                    CompressionLevel.SmallestSize => 9,
                    _ => 6,
                });
                zs.Password = password;
                foreach (var f in files)
                {
                    ct.ThrowIfCancellationRequested(); stats.Wait(ct);
                    if (f.Full.Equals(zipFull, StringComparison.OrdinalIgnoreCase)) continue;
                    stats.Current = f.Full;
                    var entry = new ICSharpCode.SharpZipLib.Zip.ZipEntry(f.Entry)
                    {
                        DateTime = f.Mod.Year < 1980 ? new DateTime(1980, 1, 1) : f.Mod,
                        AESKeySize = 256,
                        IsUnicodeText = true,
                    };
                    zs.PutNextEntry(entry);
                    using (var input = new FileStream(f.Full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan))
                    {
                        int n;
                        while ((n = input.Read(buffer, 0, ChunkSize)) > 0)
                        {
                            ct.ThrowIfCancellationRequested(); stats.Wait(ct);
                            zs.Write(buffer, 0, n);
                            stats.AddDone(n);
                        }
                    }
                    zs.CloseEntry();
                }
                zs.Finish();
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

    /// <summary>
    /// 7z a RAR zapisuje externí nástroj (7-Zip, WinRAR): formát RAR smí vytvářet jen WinRAR.
    /// Postup z 7z se čte z jeho výpisu, zrušení nástroj ukončí a dočasný soubor smaže.
    /// </summary>
    private static void PackExternal(IReadOnlyList<FileItem> items, string path, PackFormat format, CompressionLevel level, string? password, CopyStats stats, CancellationToken ct)
    {
        var sevenZip = format == PackFormat.SevenZip;
        var tool = sevenZip ? ExternalTools.Find7z() : ExternalTools.FindRar();
        if (tool is null)
            throw new IOException(sevenZip
                ? Loc.T("Nenalezen 7-Zip (7z.exe). Nainstaluj 7-Zip nebo dej 7z.exe a 7z.dll do složky Tools vedle aplikace.")
                : Loc.T("RAR umí vytvářet jen WinRAR (Rar.exe, placený se zkušební dobou). Nainstaluj WinRAR nebo dej Rar.exe do složky Tools vedle aplikace."));

        var total = Math.Max(CollectFiles(items).Sum(f => f.Size), 1);
        stats.AddTotal(total);
        var tmp = path + ".fcm.tmp";
        TryDelete(tmp);   // nástroje by jinak přidávaly do existujícího archivu

        var psi = new System.Diagnostics.ProcessStartInfo(tool)
        {
            WorkingDirectory = Path.GetDirectoryName(items[0].FullPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (sevenZip)
        {
            var mx = level switch { CompressionLevel.NoCompression => 0, CompressionLevel.Fastest => 1, CompressionLevel.SmallestSize => 9, _ => 5 };
            foreach (var a in new[] { "a", "-t7z", "-y", "-bsp1", "-bb0", "-mx=" + mx }) psi.ArgumentList.Add(a);
            if (password is not null) { psi.ArgumentList.Add("-p" + password); psi.ArgumentList.Add("-mhe=on"); }
        }
        else
        {
            var m = level switch { CompressionLevel.NoCompression => 0, CompressionLevel.Fastest => 1, CompressionLevel.SmallestSize => 5, _ => 3 };
            foreach (var a in new[] { "a", "-r", "-y", "-idq", "-ma5", "-m" + m }) psi.ArgumentList.Add(a);
            if (password is not null) psi.ArgumentList.Add("-hp" + password);
        }
        psi.ArgumentList.Add(tmp);
        psi.ArgumentList.Add("--");
        foreach (var item in items) psi.ArgumentList.Add(item.Name);

        var output = new System.Text.StringBuilder();
        using var process = new System.Diagnostics.Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (output) output.AppendLine(e.Data);
            if (!sevenZip) return;
            var match = System.Text.RegularExpressions.Regex.Match(e.Data, @"(\d{1,3})%");
            if (!match.Success) return;
            var delta = total * Math.Min(100, int.Parse(match.Groups[1].Value)) / 100 - stats.Done;
            if (delta > 0) stats.AddDone(delta);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (output) output.AppendLine(e.Data);
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            using var registration = ct.Register(() =>
            {
                try { process.Kill(true); } catch { /* proces už skončil */ }
            });
            stats.Current = items[0].FullPath;
            process.WaitForExit();
            ct.ThrowIfCancellationRequested();
            if (process.ExitCode > 1)   // 0 = OK, 1 = varování (např. soubor používá jiný program)
            {
                string tail;
                lock (output) tail = string.Join(" ", output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(3));
                throw new IOException(Loc.T("Chyba nástroje {0}: {1}", Path.GetFileName(tool), tail));
            }
            var rest = total - stats.Done;
            if (rest > 0) stats.AddDone(rest);
            File.Move(tmp, path, true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    private static void PackTar(IReadOnlyList<FileItem> items, string path, bool gzip, CompressionLevel level, CopyStats stats, CancellationToken ct)
    {
        var files = CollectFiles(items);
        stats.AddTotal(files.Sum(f => f.Size));
        var full = Path.GetFullPath(path);
        var tmp = path + ".fcm.tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, ChunkSize))
            {
                Stream target = gzip ? new GZipStream(fs, level) : fs;
                using (target)
                using (var tar = new System.Formats.Tar.TarWriter(target, System.Formats.Tar.TarEntryFormat.Pax, leaveOpen: true))
                {
                    foreach (var f in files)
                    {
                        ct.ThrowIfCancellationRequested(); stats.Wait(ct);
                        if (f.Full.Equals(full, StringComparison.OrdinalIgnoreCase)) continue;
                        stats.Current = f.Full;
                        tar.WriteEntry(f.Full, f.Entry);
                        stats.AddDone(f.Size);
                    }
                }
            }
            File.Move(tmp, path, true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
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
