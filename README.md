# All-in-one Launcher for Windows Game Console

**Ma ROG Ally** : lanceur de jeux plein écran pour consoles portables sous Windows 11 (pensé pour l'Asus ROG Ally), entièrement pilotable à la manette. Il réunit les jeux de toutes les plateformes, remplace la barre des tâches de Windows par la sienne et gère le Wi-Fi et le Bluetooth sans passer par les paramètres.

## Installer

1. Télécharger `MaRogAlly.exe` depuis la dernière version, dans [Releases](https://github.com/Wopaf/All-in-one-Launcher-for-Windows-Game-Console/releases/latest). C'est un seul fichier, rien à installer.
2. Le lancer. Les nouvelles versions sont ensuite annoncées dans le menu principal et s'installent après validation.

L'exécutable n'est pas signé : Windows peut afficher un avertissement au premier lancement.

## Commandes

| Manette | Clavier | Action |
|---|---|---|
| Croix / stick gauche | Flèches | Naviguer ; gauche au bord d'une page entre dans la barre de gauche, droite en ressort |
| A | Entrée | Fiche du jeu / valider |
| B | Échap | Retour (retire les filtres sur une liste) |
| X | F | Filtres |
| Y | Espace | Trier |
| Menu (≡) | Échap, T | Menu : réglages, statistiques, plateformes, stockage, mises à jour, veille, arrêt |
| B (depuis une autre fenêtre) | | Réduire cette fenêtre, sauf un jeu de la bibliothèque ou une fenêtre en plein écran |
| LB / RB | Maj+Tab / Tab | Onglet précédent / suivant |
| LT / RT | Page préc. / suiv. | Bibliothèque : catégorie précédente / suivante |

## Données

`%LOCALAPPDATA%\MaRogAlly\` : `settings.json` (clés d'API, jeux ajoutés à la main dans `ManualGames`), `library.json`, `userdata.json` (jeux masqués, dernières parties), `wishlist.json` (liste de souhaits), caches `metadata\`, `images\`, `tags.json`, journal `log.txt`. Rien de tout cela n'est envoyé ailleurs.

## Sources

- Jeux installés : Steam, Epic, GOG, EA app, Ubisoft Connect, Battle.net, Xbox/Microsoft Store, Amazon Games, Riot, itch.io, Rockstar, Ankama, Minecraft Launcher, RSI Launcher, plus les jeux ajoutés à la main.
- Jeux possédés non installés : cache du lanceur Epic, base GOG Galaxy (et plateformes qui y sont connectées), Amazon, itch, API Steam (clé facultative).
- Fiches et catégories : magasin Steam (sans clé, en français), IGDB (clés Twitch), SteamGridDB (clé).

## Développement

- `.\publish.ps1` produit `publish\MaRogAlly.exe` (.NET 8, WPF).
- `.\release.ps1` publie une nouvelle version sur GitHub : augmenter d'abord `<Version>` dans `src\MaRogAlly\MaRogAlly.csproj`, puis commit et push.
