using System.Diagnostics;
using System.Runtime.InteropServices;
using MaRogAlly.Core;

namespace MaRogAlly.Services;

/// <summary>
/// Ouverture d'un dossier (ou d'un fichier mis en évidence) dans l'Explorateur de fichiers.
/// Passe par la fonction de Windows prévue pour cela (celle d'« Afficher dans le dossier ») : ouvrir le chemin du dossier
/// comme un document fait parfois arriver l'Explorateur sur son Accueil, le dossier voulu n'étant qu'un « retour » plus loin.
/// </summary>
public static class FolderOpener
{
    /// <summary>Ouvre ce dossier dans l'Explorateur.</summary>
    public static bool OpenFolder(string folder)
    {
        if (!Directory.Exists(folder)) return false;
        return Show(folder, select: null) || Fallback($"\"{folder}\"");
    }

    /// <summary>Ouvre le dossier de ce fichier dans l'Explorateur, le fichier sélectionné.</summary>
    public static bool ShowFile(string file)
    {
        if (!File.Exists(file)) return Path.GetDirectoryName(file) is { } dir && OpenFolder(dir);
        return Show(Path.GetDirectoryName(file)!, select: file) || Fallback($"/select,\"{file}\"");
    }

    private static bool Show(string folder, string? select)
    {
        IntPtr folderId = IntPtr.Zero, itemId = IntPtr.Zero;
        try
        {
            if (SHParseDisplayName(folder, IntPtr.Zero, out folderId, 0, out _) != 0 || folderId == IntPtr.Zero) return false;
            if (select is not null)
            {
                if (SHParseDisplayName(select, IntPtr.Zero, out itemId, 0, out _) != 0 || itemId == IntPtr.Zero) return false;
                return SHOpenFolderAndSelectItems(folderId, 1, [itemId], 0) == 0;
            }
            // Aucun élément à sélectionner : le dossier lui-même s'ouvre.
            return SHOpenFolderAndSelectItems(folderId, 0, null, 0) == 0;
        }
        catch (Exception ex)
        {
            Log.Error($"Ouverture du dossier {folder}", ex);
            return false;
        }
        finally
        {
            if (itemId != IntPtr.Zero) Marshal.FreeCoTaskMem(itemId);
            if (folderId != IntPtr.Zero) Marshal.FreeCoTaskMem(folderId);
        }
    }

    /// <summary>Secours : l'Explorateur lancé avec le chemin en argument.</summary>
    private static bool Fallback(string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Explorateur ({arguments})", ex);
            return false;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindingContext, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(IntPtr folder, uint count, [MarshalAs(UnmanagedType.LPArray)] IntPtr[]? items, uint flags);
}
