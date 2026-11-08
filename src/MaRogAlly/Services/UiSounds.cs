using System.Buffers.Binary;
using System.Text;
using System.Windows.Media;
using MaRogAlly.Core;
using MaRogAlly.Input;

namespace MaRogAlly.Services;

/// <summary>
/// Sons de l'interface, ceux du dossier « sounds » du projet (comme dans Ma TV) :
/// - switch : se déplacer d'un élément à l'autre, changer d'onglet, Retour ;
/// - keyboard : se déplacer entre les touches du clavier virtuel ;
/// - clic : valider ;
/// - open : au démarrage de l'application ;
/// - search : quand une recherche aboutit ;
/// - OpenVolet / CloseVolet : ouverture et fermeture du centre de contrôle ;
/// - ScreenCapture : capture d'écran ;
/// - MouseClic : clic de la souris à la manette.
///
/// Les fichiers sont en WAV 32 bits flottants : au premier lancement (et à chaque nouvelle version),
/// ils sont convertis en 16 bits, mis au même niveau de crête et gardés en cache, puis joués à un
/// volume réglable (<see cref="ShortVolume"/>, <see cref="LongVolume"/>).
/// </summary>
public static class UiSounds
{
    /// <summary>Direction maintenue : un son toutes les 45 ms au plus, pas une rafale.</summary>
    private static readonly TimeSpan MoveInterval = TimeSpan.FromMilliseconds(45);

    /// <summary>Niveau de crête visé après normalisation (≈ -0,5 dB).</summary>
    private const float Peak = 0.94f;

    /// <summary>Volume des sons courts (switch, keyboard, clic), de 0 à 1.</summary>
    private const double ShortVolume = 0.5;

    /// <summary>Volume des sons longs (open, search), de 0 à 1.</summary>
    private const double LongVolume = 0.7;

    /// <summary>Volume des sons du centre de contrôle : moitié moins fort que les autres sons longs, il s'ouvre souvent par-dessus un jeu.</summary>
    private const double PanelVolume = LongVolume / 2;

    /// <summary>Plusieurs lecteurs par son court : un déplacement rapide n'interrompt pas le précédent.</summary>
    private const int Voices = 3;

    private static readonly Dictionary<string, MediaPlayer[]> Players = [];
    private static readonly Dictionary<string, int> NextVoice = [];
    private static DateTime _lastMove;
    private static bool _openPending;

    /// <summary>Sons longs : un seul lecteur, joués moins fort que leur crête normalisée.</summary>
    private static readonly string[] Long = ["open", "search", "OpenVolet", "CloseVolet", "ScreenCapture"];

    public static bool Enabled { get; set; } = true;

    /// <summary>Prépare les sons en tâche de fond ; les lecteurs sont créés sur le fil de l'interface.</summary>
    public static async Task InitAsync()
    {
        var files = await Task.Run(PrepareFiles);
        foreach (var (name, path) in files)
        {
            var count = Long.Contains(name) ? 1 : Voices;
            Players[name] = Enumerable.Range(0, count).Select(_ =>
            {
                var player = new MediaPlayer { Volume = name is "OpenVolet" or "CloseVolet" ? PanelVolume : count == 1 ? LongVolume : ShortVolume };
                player.Open(new Uri(path));
                return player;
            }).ToArray();
            NextVoice[name] = 0;
        }
        if (_openPending)
        {
            _openPending = false;
            Play("open");
        }
    }

    /// <summary>Son associé à une action de la manette ou du clavier.</summary>
    public static void OnNav(NavAction action, bool onKeyboard)
    {
        switch (action)
        {
            case NavAction.Up or NavAction.Down or NavAction.Left or NavAction.Right:
                var now = DateTime.UtcNow;
                if (now - _lastMove < MoveInterval) return;
                _lastMove = now;
                Play(onKeyboard ? "keyboard" : "switch");
                break;
            case NavAction.Accept:
                Play("clic");
                break;
            case NavAction.Back or NavAction.PrevTab or NavAction.NextTab or NavAction.Home:
                Play("switch");
                break;
        }
    }

    /// <summary>Démarrage de l'application ; si les sons ne sont pas encore prêts, joué dès qu'ils le sont.</summary>
    public static void PlayOpen()
    {
        if (Players.ContainsKey("open")) Play("open");
        else _openPending = true;
    }

    /// <summary>Recherche aboutie.</summary>
    public static void PlaySearch() => Play("search");

    public static void PlayPanelOpen() => Play("OpenVolet");

    public static void PlayCapture() => Play("ScreenCapture");

    public static void PlayMouseClick() => Play("MouseClic");

    /// <summary>Fermeture du volet : B joue déjà le son de retour juste avant, il laisse la place à celui-ci.</summary>
    public static void PlayPanelClose()
    {
        if (Players.TryGetValue("switch", out var voices))
            foreach (var player in voices) player.Stop();
        Play("CloseVolet");
    }

    private static void Play(string name)
    {
        if (!Enabled || !Players.TryGetValue(name, out var voices)) return;
        var i = NextVoice[name];
        NextVoice[name] = (i + 1) % voices.Length;
        var player = voices[i];
        player.Stop();
        player.Position = TimeSpan.Zero;
        player.Play();
    }

    // ---------------- Conversion des fichiers ----------------

    private static List<(string Name, string Path)> PrepareFiles()
    {
        var result = new List<(string, string)>();
        var dir = Path.Combine(AppPaths.Root, "sounds");
        Directory.CreateDirectory(dir);
        // Nouvelle version de l'application (sons peut-être changés) : nouveaux fichiers en cache.
        var version = File.GetLastWriteTimeUtc(Environment.ProcessPath ?? "").Ticks;
        foreach (var old in Directory.EnumerateFiles(dir).Where(f => !f.EndsWith($"-{version}.wav")))
            try { File.Delete(old); } catch { /* fichier en cours de lecture */ }

        var assembly = typeof(UiSounds).Assembly;
        foreach (var name in new[] { "switch", "keyboard", "clic", "open", "search", "OpenVolet", "CloseVolet", "ScreenCapture", "MouseClic" })
        {
            try
            {
                var path = Path.Combine(dir, $"{name}-{version}.wav");
                if (!File.Exists(path))
                {
                    using var stream = assembly.GetManifestResourceStream($"Sounds.{name}.wav");
                    if (stream is null) continue;
                    using var memory = new MemoryStream();
                    stream.CopyTo(memory);
                    var (samples, rate, channels) = ToPcm16(memory.ToArray());
                    WriteWav(path, Normalized(samples), rate, channels);
                }
                result.Add((name, path));
            }
            catch (Exception ex)
            {
                Log.Error($"Son « {name} » illisible", ex);
            }
        }
        return result;
    }

    /// <summary>Lit un WAV (entier 16, 24 ou 32 bits, ou flottant 32 bits) et le convertit en entier 16 bits.</summary>
    private static (short[] Samples, int Rate, int Channels) ToPcm16(byte[] wav)
    {
        if (wav.Length < 12 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF" || Encoding.ASCII.GetString(wav, 8, 4) != "WAVE")
            throw new InvalidDataException("pas un WAV");
        int format = 1, channels = 2, rate = 48000, bits = 16, dataStart = -1, dataSize = 0, offset = 12;
        while (offset + 8 <= wav.Length)
        {
            var id = Encoding.ASCII.GetString(wav, offset, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(offset + 4));
            var body = offset + 8;
            if (id == "fmt ")
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(body));
                channels = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(body + 2));
                rate = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(body + 4));
                bits = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(body + 14));
                // WAVE_FORMAT_EXTENSIBLE : le vrai format est au début du sous-format
                if (format == 0xFFFE && size >= 26) format = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(body + 24));
            }
            else if (id == "data")
            {
                dataStart = body;
                dataSize = Math.Min(size, wav.Length - body);
            }
            offset = body + size + (size & 1);
        }
        if (dataStart < 0) throw new InvalidDataException("pas de données audio");

        var bytesPerSample = bits / 8;
        var samples = new short[dataSize / bytesPerSample];
        for (var i = 0; i < samples.Length; i++)
        {
            var at = dataStart + i * bytesPerSample;
            float value = (format, bits) switch
            {
                (3, 32) => BinaryPrimitives.ReadSingleLittleEndian(wav.AsSpan(at)),
                (_, 16) => BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(at)) / 32768f,
                (_, 24) => ((wav[at] | (wav[at + 1] << 8) | ((sbyte)wav[at + 2] << 16)) / 8388608f),
                (_, 32) => BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(at)) / 2147483648f,
                _ => 0f,
            };
            samples[i] = (short)MathF.Round(Math.Clamp(value, -1f, 1f) * 32767f);
        }
        return (samples, rate, channels);
    }

    /// <summary>Met le son à <see cref="Peak"/> en crête : tous les sons partent du même niveau.</summary>
    private static short[] Normalized(short[] samples)
    {
        var peak = Math.Max(1, samples.Length == 0 ? 1 : samples.Max(s => Math.Abs((int)s)));
        return samples.Select(s => (short)MathF.Round(s / (float)peak * Peak * 32767f)).ToArray();
    }

    private static void WriteWav(string path, short[] samples, int rate, int channels)
    {
        var data = samples.Length * 2;
        using var w = new BinaryWriter(File.Create(path + ".tmp"));
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + data); w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)channels);
        w.Write(rate); w.Write(rate * channels * 2); w.Write((short)(channels * 2)); w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data")); w.Write(data);
        foreach (var s in samples) w.Write(s);
        w.Close();
        File.Move(path + ".tmp", path, overwrite: true);
    }
}
