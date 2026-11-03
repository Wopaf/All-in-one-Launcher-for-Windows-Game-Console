using System.Speech.Recognition;
using MaRogAlly.Core;
using Microsoft.Win32;

namespace MaRogAlly.Services;

/// <summary>Problème à montrer tel quel à l'utilisateur (pas de micro, pas de reconnaissance installée).</summary>
public sealed class VoiceInputException(string message) : Exception(message);

/// <summary>
/// Dictée d'une phrase pour le clavier virtuel. La reconnaissance en ligne de Windows, bien meilleure sur les titres de jeux,
/// sert si elle est activée (Paramètres > Confidentialité > Voix) ; sinon, ou si elle échoue, celle installée sur la machine.
/// </summary>
public static class VoiceInput
{
    private const string Language = "fr-FR";
    private static readonly TimeSpan InitialSilence = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan EndSilence = TimeSpan.FromSeconds(1);

    /// <summary>Écoute jusqu'à la fin de la phrase. Renvoie le texte reconnu, vide si rien n'a été entendu.</summary>
    public static async Task<string> ListenAsync(CancellationToken cancel)
    {
        if (OnlineAllowed)
        {
            try
            {
                return Clean(await ListenOnlineAsync(cancel));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error("Dictée en ligne", ex);
            }
        }
        // La création du moteur local prend un instant : hors du fil de l'interface.
        return Clean(await Task.Run(() => ListenLocalAsync(cancel), cancel));
    }

    private static string Clean(string heard) => heard.Trim().TrimEnd('.');

    private static bool OnlineAllowed
        => Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy", "HasAccepted", 0) is 1;

    private static async Task<string> ListenOnlineAsync(CancellationToken cancel)
    {
        using var recognizer = new Windows.Media.SpeechRecognition.SpeechRecognizer(new Windows.Globalization.Language(Language));
        recognizer.Timeouts.InitialSilenceTimeout = InitialSilence;
        recognizer.Timeouts.EndSilenceTimeout = EndSilence;
        var compiled = await recognizer.CompileConstraintsAsync().AsTask(cancel);
        if (compiled.Status != Windows.Media.SpeechRecognition.SpeechRecognitionResultStatus.Success)
            throw new InvalidOperationException($"compilation : {compiled.Status}");
        var result = await recognizer.RecognizeAsync().AsTask(cancel);
        return result.Status switch
        {
            Windows.Media.SpeechRecognition.SpeechRecognitionResultStatus.Success => result.Text,
            Windows.Media.SpeechRecognition.SpeechRecognitionResultStatus.TimeoutExceeded => "",
            _ => throw new InvalidOperationException($"reconnaissance : {result.Status}"),
        };
    }

    private static async Task<string> ListenLocalAsync(CancellationToken cancel)
    {
        var installed = SpeechRecognitionEngine.InstalledRecognizers();
        var info = installed.FirstOrDefault(r => r.Culture.Name == Language) ?? installed.FirstOrDefault()
            ?? throw new VoiceInputException("Aucune reconnaissance vocale installée dans Windows");

        using var engine = new SpeechRecognitionEngine(info);
        engine.LoadGrammar(new DictationGrammar());
        try
        {
            engine.SetInputToDefaultAudioDevice();
        }
        catch (InvalidOperationException)
        {
            throw new VoiceInputException("Aucun micro disponible");
        }
        engine.InitialSilenceTimeout = InitialSilence;
        engine.EndSilenceTimeout = EndSilence;

        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.RecognizeCompleted += (_, e) =>
        {
            if (e.Cancelled) done.TrySetCanceled(cancel);
            else if (e.Error is { } error) done.TrySetException(error);
            else done.TrySetResult(e.Result?.Text ?? "");
        };
        engine.RecognizeAsync(RecognizeMode.Single);
        using (cancel.Register(engine.RecognizeAsyncCancel))
            return await done.Task;
    }
}
