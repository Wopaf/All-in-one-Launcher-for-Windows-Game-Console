using System.Collections.Concurrent;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using MaRogAlly.Core;
using MaRogAlly.Models;

namespace MaRogAlly.Services;

/// <summary>Ce qui occupe un disque, du plus utile à regarder au moins modifiable.</summary>
public enum StorageCategory { Games, Apps, Personal, Downloads, Temp, RecycleBin, Other, System }

/// <summary>Grandes familles de fichiers, comptées hors jeux, applications et système.</summary>
public enum StorageFileType { Videos, Images, Music, Archives, Installers, Documents, Other }

public sealed record StorageDrive(string Root, string Label, long Total, long Free)
{
    public long Used => Total - Free;
    public string Letter => Root.TrimEnd('\\');
    public string Name => string.IsNullOrWhiteSpace(Label) ? Letter : $"{Letter}  {Label}";

    /// <summary>Disques internes et cartes SD prêts à être lus (les tailles sont celles de l'instant).</summary>
    public static List<StorageDrive> Read()
    {
        var drives = new List<StorageDrive>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType is DriveType.Fixed or DriveType.Removable && d.IsReady)
                    drives.Add(new StorageDrive(d.Name, d.VolumeLabel, d.TotalSize, d.AvailableFreeSpace));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return drives;
    }
}

/// <summary>Dossier mesuré ; les plus petits sont fondus dans la taille de leur parent.</summary>
public sealed class StorageFolder
{
    public string Name { get; set; } = "";
    public long Bytes { get; set; }
    public List<StorageFolder>? Children { get; set; }
}

public sealed record StorageFile(string Path, long Bytes, DateTime Modified, StorageCategory Category);

/// <summary>Résultat de l'analyse d'un disque.</summary>
public sealed class StorageScan
{
    public string Drive { get; set; } = "";
    public DateTime ScannedAt { get; set; }
    /// <summary>Espace occupé selon Windows, et ce que l'analyse a pu en lire : l'écart est du système illisible sans droits admin.</summary>
    public long Used { get; set; }
    public long Measured { get; set; }
    public long Files { get; set; }
    public Dictionary<StorageCategory, long> Categories { get; set; } = [];
    public Dictionary<StorageFileType, long> Types { get; set; } = [];
    public Dictionary<string, long> Games { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<StorageFile> LargestFiles { get; set; } = [];
    public StorageFolder Root { get; set; } = new();
}

/// <summary>
/// Dernières analyses de chaque disque, affichées tout de suite à l'ouverture de la page, et analyse en cours :
/// elle survit à la fermeture de la page, et s'arrête au lancement d'un jeu.
/// </summary>
public static class StorageStore
{
    private static CancellationTokenSource? _cts;

    public static StorageScanner? Running { get; private set; }
    /// <summary>Une analyse s'est arrêtée : son disque, et vrai si elle a abouti.</summary>
    public static event Action<string, bool>? ScanFinished;

    public static void CancelScan() => _cts?.Cancel();

    /// <summary>Lance l'analyse d'un disque, sauf si elle est déjà en cours ; une seule à la fois.</summary>
    public static async void Start(StorageDrive drive, IEnumerable<Game> games)
    {
        if (Running?.Root == drive.Root) return;
        CancelScan();
        var scanner = Running = new StorageScanner(drive, games);
        var cts = _cts = new CancellationTokenSource();
        var done = false;
        try
        {
            var started = DateTime.Now;
            var scan = await scanner.RunAsync(cts.Token);
            Log.Info($"Stockage : {scan.Drive} analysé en {(DateTime.Now - started).TotalSeconds:0} s ({scan.Files:N0} fichiers)");
            Set(scan);
            done = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("Analyse du stockage", ex); }
        finally
        {
            if (Running == scanner) Running = null;
            ScanFinished?.Invoke(drive.Root, done);
        }
    }

    private static readonly object WriteLock = new();
    private static string CachePath => Path.Combine(AppPaths.Root, "storage.json");
    private static Dictionary<string, StorageScan>? _scans;

    public static async Task LoadAsync()
    {
        if (_scans is not null) return;
        _scans = await Task.Run(() => Json.Load<Dictionary<string, StorageScan>>(CachePath)) ?? [];
        // Le fichier ne garde pas la règle de comparaison : la casse d'un chemin de jeu peut différer de celle du disque.
        foreach (var scan in _scans.Values) scan.Games = new Dictionary<string, long>(scan.Games, StringComparer.OrdinalIgnoreCase);
    }

    public static StorageScan? Get(string drive) => _scans?.GetValueOrDefault(drive);

    public static void Set(StorageScan scan)
    {
        (_scans ??= [])[scan.Drive] = scan;
        Save();
    }

    public static void Save()
    {
        if (_scans is not { } scans) return;
        var copy = new Dictionary<string, StorageScan>(scans);
        _ = Task.Run(() => { lock (WriteLock) Json.Save(CachePath, copy); });
    }
}

/// <summary>
/// Mesure un disque sans droits admin. Parcours en parallèle sur le haut de l'arborescence, sans allouer de texte par fichier :
/// seuls les dossiers assez gros sont gardés en mémoire. Les zones illisibles sont déduites de l'espace occupé annoncé par Windows.
/// </summary>
public sealed class StorageScanner(StorageDrive drive, IEnumerable<Game> games)
{
    /// <summary>En dessous, un dossier n'est plus détaillé : sa taille reste comptée dans son parent.</summary>
    private const long MinFolder = 16L << 20;
    private const int KeptFiles = 100;
    private const FileAttributes CloudOnly = FileAttributes.Offline | (FileAttributes)0x40000 | (FileAttributes)0x400000;

    private static readonly EnumerationOptions Options = new() { IgnoreInaccessible = true, AttributesToSkip = 0, ReturnSpecialDirectories = false };

    private readonly long[] _categories = new long[Enum.GetValues<StorageCategory>().Length];
    private readonly long[] _types = new long[Enum.GetValues<StorageFileType>().Length];
    private readonly ConcurrentDictionary<string, long> _gameSizes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _gameDirs = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<StorageFile> _largest = [];
    private readonly List<string> _cloudRoots = [];
    private Dictionary<string, StorageCategory> _rules = [];
    private CancellationToken _ct;
    private long _measured, _files, _largestMin = 100L << 20;

    public string Root => drive.Root;
    public long Measured => Interlocked.Read(ref _measured);
    /// <summary>Dossier en cours de lecture, pour l'afficher.</summary>
    public volatile string Current = "";

    /// <summary>Tailles par catégorie à l'instant, pendant l'analyse.</summary>
    public Dictionary<StorageCategory, long> LiveCategories()
        => Enum.GetValues<StorageCategory>().ToDictionary(c => c, c => Interlocked.Read(ref _categories[(int)c]));

    public static string Norm(string path)
    {
        try { path = Path.GetFullPath(path); } catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { }
        return path.Length > 3 ? path.TrimEnd('\\', '/') : path;
    }

    public Task<StorageScan> RunAsync(CancellationToken ct) => Task.Run(() =>
    {
        _ct = ct;
        BuildRules();
        StorageFolder root;
        try { root = Scan(drive.Root, drive.Root, StorageCategory.Other, 0); }
        catch (AggregateException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        return new StorageScan
        {
            Drive = drive.Root,
            ScannedAt = DateTime.Now,
            Used = drive.Used,
            Measured = Measured,
            Files = _files,
            Categories = LiveCategories(),
            Types = Enum.GetValues<StorageFileType>().ToDictionary(t => t, t => _types[(int)t]),
            Games = new Dictionary<string, long>(_gameSizes, StringComparer.OrdinalIgnoreCase),
            LargestFiles = [.. _largest.OrderByDescending(f => f.Bytes).Take(KeptFiles)],
            Root = root,
        };
    }, ct);

    private void BuildRules()
    {
        static string Known(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var local = Known(Environment.SpecialFolder.LocalApplicationData);
        var windows = Known(Environment.SpecialFolder.Windows);
        var rules = new Dictionary<string, StorageCategory>(StringComparer.OrdinalIgnoreCase);
        void Add(StorageCategory category, params string?[] paths)
        {
            foreach (var path in paths)
                if (!string.IsNullOrEmpty(path)) rules[Norm(path)] = category;
        }

        Add(StorageCategory.System, windows);
        Add(StorageCategory.Apps,
            Known(Environment.SpecialFolder.ProgramFiles), Known(Environment.SpecialFolder.ProgramFilesX86), Known(Environment.SpecialFolder.CommonApplicationData),
            Known(Environment.SpecialFolder.ApplicationData), local, Path.Combine(Known(Environment.SpecialFolder.UserProfile), "AppData"),
            Path.Combine(drive.Root, "Program Files"), Path.Combine(drive.Root, "Program Files (x86)"), Path.Combine(drive.Root, "WindowsApps"));
        Add(StorageCategory.Personal,
            Known(Environment.SpecialFolder.MyDocuments), Known(Environment.SpecialFolder.MyPictures), Known(Environment.SpecialFolder.MyVideos),
            Known(Environment.SpecialFolder.MyMusic), Known(Environment.SpecialFolder.DesktopDirectory));
        Add(StorageCategory.Downloads, DownloadsFolder);
        Add(StorageCategory.Temp, Path.GetTempPath(), Path.Combine(local, "Temp"), Path.Combine(windows, "Temp"), Path.Combine(local, "CrashDumps"),
            Path.Combine(local, "D3DSCache"), Path.Combine(local, "NVIDIA", "DXCache"), Path.Combine(local, "NVIDIA", "GLCache"), Path.Combine(local, "AMD", "DxCache"), Path.Combine(local, "AMD", "DxcCache"));
        Add(StorageCategory.RecycleBin, Path.Combine(drive.Root, "$Recycle.Bin"));

        // Les fichiers OneDrive sont des points d'analyse : c'est le seul cas où on les suit (ailleurs, ce sont des renvois à ne pas compter deux fois).
        foreach (var name in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            if (Environment.GetEnvironmentVariable(name) is not { Length: > 0 } cloud) continue;
            _cloudRoots.Add(Norm(cloud));
            Add(StorageCategory.Personal, cloud);
        }

        // En dernier : un jeu installé dans « Program Files » reste un jeu.
        foreach (var dir in games.Where(g => g.IsInstalled && g.InstallDir is not null).Select(g => Norm(g.InstallDir!)))
        {
            _gameDirs.Add(dir);
            rules[dir] = StorageCategory.Games;
        }
        _rules = rules;
    }

    private StorageFolder Scan(string path, string name, StorageCategory parent, int depth)
    {
        _ct.ThrowIfCancellationRequested();
        var category = _rules.TryGetValue(path, out var rule) ? rule
            : name.Equals("steamapps", StringComparison.OrdinalIgnoreCase) ? StorageCategory.Games : parent;
        Current = path;

        var dirs = new List<string>();
        long own = 0;
        try
        {
            using var reader = new Reader(path, this, category, dirs);
            while (reader.MoveNext()) { }
            own = reader.Bytes + reader.SystemBytes;
            Interlocked.Add(ref _measured, own);
            Interlocked.Add(ref _files, reader.Files);
            Interlocked.Add(ref _categories[(int)category], reader.Bytes);
            Interlocked.Add(ref _categories[(int)StorageCategory.System], reader.SystemBytes);
            if (reader.Types is { } types)
                for (var i = 0; i < types.Length; i++) Interlocked.Add(ref _types[i], types[i]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }

        var children = new StorageFolder[dirs.Count];
        void ScanChild(int i) => children[i] = Scan(Path.Join(path, dirs[i]), dirs[i], category, depth + 1);
        if (depth < 3 && dirs.Count > 1)
            Parallel.For(0, dirs.Count, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = _ct }, ScanChild);
        else
            for (var i = 0; i < dirs.Count; i++) ScanChild(i);

        var total = own + children.Sum(c => c.Bytes);
        if (_gameDirs.Contains(path)) _gameSizes[path] = total;
        var kept = children.Where(c => c.Bytes >= MinFolder).OrderByDescending(c => c.Bytes).ToList();
        return new StorageFolder { Name = name, Bytes = total, Children = kept.Count > 0 ? kept : null };
    }

    private void AddLargest(StorageFile file)
    {
        lock (_largest)
        {
            _largest.Add(file);
            if (_largest.Count < KeptFiles * 2) return;
            _largest.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
            _largest.RemoveRange(KeptFiles, _largest.Count - KeptFiles);
            Interlocked.Exchange(ref _largestMin, _largest[^1].Bytes);
        }
    }

    private bool IsCloud(string dir) => _cloudRoots.Exists(r => dir.StartsWith(r, StringComparison.OrdinalIgnoreCase));

    /// <summary>Compte les fichiers au fil de la lecture du dossier, sans jamais construire leur nom (sauf les plus gros).</summary>
    private sealed class Reader : FileSystemEnumerator<byte>
    {
        private readonly StorageScanner _scanner;
        private readonly StorageCategory _category;
        private readonly List<string> _dirs;
        private readonly bool _listed;
        public long Bytes, SystemBytes, Files;
        public readonly long[]? Types;

        public Reader(string path, StorageScanner scanner, StorageCategory category, List<string> dirs) : base(path, Options)
        {
            _scanner = scanner;
            _category = category;
            _dirs = dirs;
            _listed = category is not (StorageCategory.Games or StorageCategory.System or StorageCategory.RecycleBin);
            if (category is StorageCategory.Personal or StorageCategory.Downloads or StorageCategory.Other) Types = new long[Enum.GetValues<StorageFileType>().Length];
        }

        protected override bool ShouldIncludeEntry(ref FileSystemEntry entry)
        {
            var attributes = entry.Attributes;
            if (entry.IsDirectory)
            {
                if ((attributes & FileAttributes.ReparsePoint) == 0 || _scanner.IsCloud(entry.ToFullPath())) _dirs.Add(entry.FileName.ToString());
                return false;
            }
            // Fichier resté dans le cloud : il n'occupe rien sur le disque.
            if ((attributes & CloudOnly) != 0) return false;

            var length = entry.Length;
            Files++;
            if ((attributes & FileAttributes.System) != 0)
            {
                SystemBytes += length; // fichier d'échange, hibernation…
                return false;
            }
            Bytes += length;
            if (Types is not null) Types[(int)TypeOf(entry.FileName)] += length;
            if (_listed && length >= Interlocked.Read(ref _scanner._largestMin))
                _scanner.AddLargest(new StorageFile(Path.Join(entry.Directory, entry.FileName), length, entry.LastWriteTimeUtc.LocalDateTime, _category));
            return false;
        }

        protected override byte TransformEntry(ref FileSystemEntry entry) => 0;
    }

    private static StorageFileType TypeOf(ReadOnlySpan<char> name)
    {
        var extension = Path.GetExtension(name);
        Span<char> lower = stackalloc char[8];
        if (extension.Length is < 2 or > 8) return StorageFileType.Other;
        var written = extension.ToLowerInvariant(lower);
        return (ReadOnlySpan<char>)lower[..written] switch
        {
            ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".webm" or ".m4v" or ".ts" => StorageFileType.Videos,
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" or ".heic" or ".tif" or ".tiff" or ".raw" or ".psd" => StorageFileType.Images,
            ".mp3" or ".flac" or ".wav" or ".m4a" or ".ogg" or ".aac" or ".wma" => StorageFileType.Music,
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".iso" or ".img" or ".vhd" or ".vhdx" => StorageFileType.Archives,
            ".exe" or ".msi" or ".msix" or ".appx" or ".msixbundle" => StorageFileType.Installers,
            ".pdf" or ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".txt" or ".odt" or ".epub" => StorageFileType.Documents,
            _ => StorageFileType.Other,
        };
    }

    // ---------------- Windows ----------------

    public static string DownloadsFolder
    {
        get
        {
            var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
            return SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var path) == 0 && !string.IsNullOrEmpty(path)
                ? path
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }

    /// <summary>Taille actuelle de la corbeille d'un disque, tous éléments confondus.</summary>
    public static long RecycleBinSize(string root)
    {
        var info = new RecycleBinInfo { Size = Marshal.SizeOf<RecycleBinInfo>() };
        return SHQueryRecycleBinW(root, ref info) == 0 ? info.Bytes : 0;
    }

    /// <summary>Vide la corbeille d'un disque (définitif) ; la confirmation est à la charge de l'appelant.</summary>
    public static bool EmptyRecycleBin(string root) => SHEmptyRecycleBinW(IntPtr.Zero, root, 0x1 | 0x2 | 0x4) is 0 or 1;

    public static string Format(long bytes) => bytes switch
    {
        >= 1L << 40 => $"{bytes / (double)(1L << 40):0.0} To",
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} Go",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} Mo",
        _ => $"{bytes / 1024.0:0} Ko",
    };

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct RecycleBinInfo { public int Size; public long Bytes; public long Items; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, [MarshalAs(UnmanagedType.LPWStr)] out string path);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBinW(string root, ref RecycleBinInfo info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBinW(IntPtr owner, string root, uint flags);
}
