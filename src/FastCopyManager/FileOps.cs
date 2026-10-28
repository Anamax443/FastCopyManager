using System.IO;
using Microsoft.VisualBasic.FileIO;

namespace FastCopyManager;

/// <summary>Vlastní kopírovací jádro: bufferované proudové kopírování, zápis do dočasného souboru a přejmenování až po dokončení.</summary>
public static class FileOps
{
    private const int BufferSize = 1024 * 1024;
    private const long Fat32Limit = 4L * 1024 * 1024 * 1024 - 1;

    public static bool Conflicts(IEnumerable<FileItem> items, string destDir) =>
        items.Any(i => i.IsDir ? Directory.Exists(Path.Combine(destDir, i.Name)) : File.Exists(Path.Combine(destDir, i.Name)));

    public static void Copy(IEnumerable<FileItem> items, string destDir, bool overwrite, Action<string> progress, CancellationToken ct)
    {
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(destDir, item.Name);
            if (item.IsDir) CopyDir(item.FullPath, target, overwrite, progress, ct);
            else CopyFile(item.FullPath, target, overwrite, progress, ct);
        }
    }

    private static void CopyDir(string src, string dst, bool overwrite, Action<string> progress, CancellationToken ct)
    {
        var srcFull = Path.GetFullPath(src).TrimEnd('\\') + "\\";
        if (Path.GetFullPath(dst).StartsWith(srcFull, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Nelze kopírovat složku sama do sebe.");

        Directory.CreateDirectory(dst);
        foreach (var f in Directory.EnumerateFiles(src))
            CopyFile(f, Path.Combine(dst, Path.GetFileName(f)), overwrite, progress, ct);
        foreach (var d in Directory.EnumerateDirectories(src))
            CopyDir(d, Path.Combine(dst, Path.GetFileName(d)), overwrite, progress, ct);
    }

    public static void CopyFile(string src, string dst, bool overwrite, Action<string> progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!overwrite && File.Exists(dst)) return;
        progress(src);

        var info = new FileInfo(src);
        if (info.Length > Fat32Limit && IsFat32(dst))
            throw new IOException($"Soubor {info.Name} je větší než 4 GB a cíl je FAT32.");

        var tmp = dst + ".fcm.tmp";
        try
        {
            using (var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan))
            using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.SequentialScan))
            {
                var buffer = new byte[BufferSize];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, read);
                }
            }
            File.SetLastWriteTime(tmp, info.LastWriteTime);
            File.Move(tmp, dst, overwrite);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* úklid dočasného souboru se nepovedl, nevadí */ }
            throw;
        }
    }

    private static bool IsFat32(string path)
    {
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
