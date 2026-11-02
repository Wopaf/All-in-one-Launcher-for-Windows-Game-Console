using System.IO;
using System.Text;

namespace MaRogAlly.Core;

/// <summary>Lecteur minimal du format texte KeyValues de Valve (.vdf / .acf).</summary>
public sealed class VdfNode
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, VdfNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? this[string key] => Values.TryGetValue(key, out var v) ? v : null;
    public VdfNode? Child(string key) => Children.TryGetValue(key, out var c) ? c : null;

    public static VdfNode? LoadFile(string path)
    {
        try { return File.Exists(path) ? Parse(File.ReadAllText(path)) : null; }
        catch (Exception ex) { Log.Error($"VDF illisible ({path})", ex); return null; }
    }

    public static VdfNode Parse(string text)
    {
        var tokens = Tokenize(text).GetEnumerator();
        var root = new VdfNode();
        ParseInto(root, tokens);
        return root;
    }

    private static void ParseInto(VdfNode node, IEnumerator<string?> tokens)
    {
        while (tokens.MoveNext())
        {
            var key = tokens.Current;
            if (key is null) return; // '}'
            if (!tokens.MoveNext()) return;
            var next = tokens.Current;
            if (next == "{")
            {
                var child = new VdfNode();
                ParseInto(child, tokens);
                node.Children[key] = child;
            }
            else if (next is not null)
            {
                node.Values[key] = next;
            }
        }
    }

    // Renvoie les chaînes, "{" pour une ouverture, null pour une fermeture.
    private static IEnumerable<string?> Tokenize(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }
            if (c == '{') { i++; yield return "{"; continue; }
            if (c == '}') { i++; yield return null; continue; }
            if (c == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        i++;
                        sb.Append(text[i] switch { 'n' => '\n', 't' => '\t', _ => text[i] });
                    }
                    else sb.Append(text[i]);
                    i++;
                }
                i++;
                yield return sb.ToString();
                continue;
            }
            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '{' && text[i] != '}') i++;
            yield return text[start..i];
        }
    }
}
