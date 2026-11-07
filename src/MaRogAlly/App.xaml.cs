using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MaRogAlly.Core;
using MaRogAlly.Models;
using MaRogAlly.Services;

namespace MaRogAlly;

public partial class App : Application
{
    private const string MutexName = "MaRogAlly.SingleInstance";
    private const string ShowEventName = "MaRogAlly.Show";

    /// <summary>Relancée depuis les réglages : comme après une mise à jour, elle attend que l'ancienne instance se ferme.</summary>
    public const string RestartArg = "--restarted";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        // « --render » : capture de vérification, à côté de l'application éventuellement ouverte et sans jamais la déranger.
        var rendering = e.Args.Contains("--render");

        // Une seule instance : relancer l'application ramène simplement la fenêtre existante.
        _mutex = new Mutex(true, MutexName, out var isFirst);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        // Relancée par une mise à jour : l'ancienne instance est en train de se fermer, on attend qu'elle libère la place.
        if (!isFirst && (e.Args.Contains(AppUpdater.UpdatedArg) || e.Args.Contains(RestartArg)))
        {
            try { isFirst = _mutex.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { isFirst = true; }
        }
        if (!isFirst && !rendering)
        {
            _showEvent.Set();
            Shutdown();
            return;
        }

        AppPaths.Ensure();
        Log.Info($"Démarrage {typeof(App).Assembly.GetName().Version} ({string.Join(' ', e.Args)})");
        DispatcherUnhandledException += OnUnhandled;

        // Pas d'infobulles Windows dans toute l'appli. La propriété ToolTip reste lue comme texte des libellés maison (barre latérale, fiche de jeu).
        ToolTipService.IsEnabledProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(false));

        var settings = AppSettings.Load();
        UI.AppTheme.Apply(settings.Theme);
        UI.AccentTheme.Apply(settings.AccentColor);
#if !DEBUG
        // Garde la clé « Run » à jour avec l'emplacement actuel de l'exécutable.
        AutoStart.Apply(settings.StartWithWindows);
#endif

        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        if (rendering)
        {
            // Ni focus ni barre des tâches : la fenêtre reste derrière toutes les autres.
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.ContentRendered += (_, _) => window.SendToBackNow();
            window.Show();
            window.SendToBackNow();
            return;
        }
        window.Show();

        var desktop = new Thread(() =>
        {
            while (_showEvent.WaitOne())
                Dispatcher.BeginInvoke(() =>
                {
                    window.WindowState = WindowState.Normal;
                    window.Activate();
                    window.Topmost = true;
                    window.Topmost = false;
                });
        }) { IsBackground = true, Name = "SingleInstanceListener" };
        desktop.Start();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Erreur non gérée", e.Exception);
        Log.Info(e.Exception.ToString());
        e.Handled = true;
    }

    /// <summary>Relance l'application avec les mêmes options, puis ferme celle-ci.</summary>
    public static void Restart()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        var start = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var arg in Environment.GetCommandLineArgs().Skip(1).Where(a => a is not (AppUpdater.UpdatedArg or RestartArg))) start.ArgumentList.Add(arg);
        start.ArgumentList.Add(RestartArg);
        System.Diagnostics.Process.Start(start);
        Current.Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Fermée pendant qu'un jeu est en pause : il ne doit pas rester figé.
        Services.GamePause.ResumeAll();
        _mutex?.Dispose();
        _showEvent?.Dispose();
        base.OnExit(e);
    }
}
