using System.Text.Json.Serialization;
using MaRogAlly.Core;
using MaRogAlly.Models;

namespace MaRogAlly.Services;

/// <summary>Jeu de la liste de souhaits : une fiche du magasin Steam, ou la page d'une autre boutique.</summary>
public sealed class WishItem
{
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public Platform Platform { get; set; }
    public DiscoverGame? Steam { get; set; }
    public string? StoreUrl { get; set; }
    public string? Price { get; set; }
    public bool IsDiscounted { get; set; }
    public string? CoverPath { get; set; }
    public string? HeroPath { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.Now;

    [JsonIgnore] public int? AppId => Steam?.AppId;
}

/// <summary>Liste de souhaits de l'application (locale, indépendante des comptes des boutiques), dans wishlist.json.</summary>
public sealed class WishlistService
{
    private static string FilePath => Path.Combine(AppPaths.Root, "wishlist.json");
    private readonly List<WishItem> _items = Json.Load<List<WishItem>>(FilePath) ?? [];

    /// <summary>Les plus récemment ajoutés d'abord.</summary>
    public IReadOnlyList<WishItem> Items => _items.OrderByDescending(i => i.AddedAt).ToList();

    public bool Contains(string key) => _items.Any(i => i.Key == key);

    /// <summary>Ajoute ou retire ; vrai si le jeu est maintenant dans la liste.</summary>
    public bool Toggle(WishItem item)
    {
        var added = _items.RemoveAll(i => i.Key == item.Key) == 0;
        if (added) _items.Add(item);
        Save();
        return added;
    }

    public void Save() => Json.Save(FilePath, _items);
}
