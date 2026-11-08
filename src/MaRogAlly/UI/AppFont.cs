using System.Windows;
using System.Windows.Media;

namespace MaRogAlly.UI;

/// <summary>
/// Police d'écriture de l'interface (Apparence). Les titres (« Display ») et le texte (« Body ») sont lus une fois,
/// à la construction des écrans : un changement s'applique au redémarrage de l'application.
/// </summary>
public static class AppFont
{
    public sealed record Font(string Id, string Name, string Display, string Body, string Detail);

    public const string Default = "segoe";

    // Polices fournies avec Windows : rien à installer.
    public static readonly IReadOnlyList<Font> Choices =
    [
        new("segoe", "Segoe UI", "Segoe UI Variable Display, Segoe UI", "Segoe UI Variable Text, Segoe UI", "Par défaut, la police de Windows 11"),
        new("bahnschrift", "Bahnschrift", "Bahnschrift SemiBold, Bahnschrift", "Bahnschrift", "Étroite et technique, style console"),
        new("trebuchet", "Trebuchet MS", "Trebuchet MS", "Trebuchet MS", "Ronde et dynamique"),
        new("corbel", "Corbel", "Corbel", "Corbel", "Douce et moderne"),
        new("candara", "Candara", "Candara", "Candara", "Légère et arrondie"),
    ];

    public static Font Find(string? id) => Choices.FirstOrDefault(f => f.Id == id) ?? Choices[0];

    /// <summary>À appeler au démarrage, avant la création des fenêtres.</summary>
    public static void Apply(string? id)
    {
        var f = Find(id);
        if (f.Id == Default) return;
        var display = new FontFamily(f.Display);
        var body = new FontFamily(f.Body);
        var res = Application.Current.Resources;
        res["Display"] = display;
        res["Body"] = body;
        // Les styles du thème cherchent d'abord dans leur propre dictionnaire.
        foreach (var dict in res.MergedDictionaries)
        {
            if (dict.Contains("Display")) dict["Display"] = display;
            if (dict.Contains("Body")) dict["Body"] = body;
        }
    }
}
