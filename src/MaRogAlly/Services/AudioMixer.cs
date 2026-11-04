using System.Diagnostics;
using System.IO;
using MaRogAlly.Core;
using NAudio.CoreAudioApi;

namespace MaRogAlly.Services;

/// <summary>Une application qui a ouvert le son : plusieurs flux d'un même processus sont réglés ensemble.</summary>
public sealed record AudioApp(uint ProcessId, string Name, string? ExePath, int Percent, bool Muted);

/// <summary>Volume général et mélangeur par application de la sortie audio par défaut. À appeler depuis le fil de l'interface.</summary>
public static class AudioMixer
{
    private static MMDevice Output()
    {
        using var devices = new MMDeviceEnumerator();
        return devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    /// <summary>Volume général, ou null sans sortie audio.</summary>
    public static (int Percent, bool Muted)? ReadMaster()
    {
        try
        {
            using var output = Output();
            var volume = output.AudioEndpointVolume;
            return ((int)Math.Round(volume.MasterVolumeLevelScalar * 100), volume.Mute);
        }
        catch (Exception ex)
        {
            Log.Error("Lecture du volume", ex);
            return null;
        }
    }

    public static void SetMaster(int percent) => Try(output => output.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(percent, 0, 100) / 100f);

    public static void SetMasterMuted(bool muted) => Try(output => output.AudioEndpointVolume.Mute = muted);

    /// <summary>Applications dont le son est actuellement réglable.</summary>
    public static List<AudioApp> ReadApps()
    {
        var apps = new List<AudioApp>();
        try
        {
            using var output = Output();
            foreach (var group in Sessions(output).GroupBy(s => s.GetProcessID))
            {
                var first = group.First();
                var (name, path) = Describe(first);
                apps.Add(new AudioApp(group.Key, name, path, (int)Math.Round(first.SimpleAudioVolume.Volume * 100), first.SimpleAudioVolume.Mute));
            }
        }
        catch (Exception ex)
        {
            Log.Error("Lecture du mélangeur", ex);
        }
        return apps.OrderBy(a => a.ProcessId == 0).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static void SetApp(uint processId, int percent) => Try(output =>
    {
        foreach (var session in Sessions(output).Where(s => s.GetProcessID == processId))
            session.SimpleAudioVolume.Volume = Math.Clamp(percent, 0, 100) / 100f;
    });

    public static void SetAppMuted(uint processId, bool muted) => Try(output =>
    {
        foreach (var session in Sessions(output).Where(s => s.GetProcessID == processId))
            session.SimpleAudioVolume.Mute = muted;
    });

    private static List<AudioSessionControl> Sessions(MMDevice output)
    {
        var manager = output.AudioSessionManager;
        manager.RefreshSessions();
        var sessions = manager.Sessions;
        var list = new List<AudioSessionControl>();
        for (var i = 0; i < sessions.Count; i++)
            if (sessions[i].State != NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateExpired)
                list.Add(sessions[i]);
        return list;
    }

    private static (string Name, string? Path) Describe(AudioSessionControl session)
    {
        if (session.IsSystemSoundsSession) return ("Sons de Windows", null);
        var name = string.IsNullOrWhiteSpace(session.DisplayName) ? $"Application {session.GetProcessID}" : session.DisplayName;
        try
        {
            using var process = Process.GetProcessById((int)session.GetProcessID);
            name = process.ProcessName;
            // Illisible pour une application lancée en administrateur : son nom de processus reste connu.
            var path = process.MainModule?.FileName;
            var description = path is null ? null : FileVersionInfo.GetVersionInfo(path).FileDescription;
            return (!string.IsNullOrWhiteSpace(description) ? description : name, path);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return (name, null);
        }
    }

    private static void Try(Action<MMDevice> change)
    {
        try
        {
            using var output = Output();
            change(output);
        }
        catch (Exception ex)
        {
            Log.Error("Réglage du son", ex);
        }
    }
}
