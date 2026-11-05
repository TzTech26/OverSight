using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Oversight.Services;
using Tmds.DBus.Protocol;

namespace Oversight.Linux.Platform;

/// <summary>Start when you log in, via the XDG autostart folder every desktop reads.</summary>
public static class Autostart
{
    // A profile copy (OVERSIGHT_PROFILE) gets its own entry so it never touches the real install's.
    private static string File =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "autostart", $"oversight{AppProfile.Suffix}.desktop");

    /// <summary>The AppImage when running from one, otherwise the binary itself.</summary>
    public static string Program => Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } a ? a : Environment.ProcessPath!;

    public static bool IsEnabled => System.IO.File.Exists(File);

    public static void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            if (System.IO.File.Exists(File)) System.IO.File.Delete(File);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        System.IO.File.WriteAllText(File, $"""
            [Desktop Entry]
            Type=Application
            Name=Oversight
            Comment=Screen zoom, crosshair overlay and auto clicker
            Exec="{Program}"
            Icon=oversight
            Terminal=false
            X-GNOME-Autostart-enabled=true

            """);
    }
}

/// <summary>
/// Puts Oversight in the app menu when it runs from an AppImage, which can't do that by itself. Rewritten on every
/// start so the entry follows the AppImage if it moves.
/// </summary>
public static class AppMenu
{
    private static string DataHome => Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } d ? d
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

    public static void Install(Func<Stream> icon)
    {
        if (Environment.GetEnvironmentVariable("APPIMAGE") is not { Length: > 0 } appImage) return;
        try
        {
            string iconPath = Path.Combine(DataHome, "icons", "hicolor", "256x256", "apps", "oversight.png");
            Directory.CreateDirectory(Path.GetDirectoryName(iconPath)!);
            using (var source = icon())
            using (var target = File.Create(iconPath))
                source.CopyTo(target);

            string entry = Path.Combine(DataHome, "applications", "oversight.desktop");
            Directory.CreateDirectory(Path.GetDirectoryName(entry)!);
            File.WriteAllText(entry, $"""
                [Desktop Entry]
                Type=Application
                Name=Oversight
                GenericName=Gaming overlay toolkit
                Comment=Screen zoom, crosshair overlay and auto clicker
                Exec="{appImage}"
                Icon=oversight
                Terminal=false
                Categories=Game;Utility;
                Keywords=zoom;magnifier;crosshair;auto clicker;
                StartupWMClass=oversight

                """);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Desktop notifications through org.freedesktop.Notifications (GNOME, Cinnamon, KDE, XFCE...).</summary>
public sealed class Notifier
{
    private Connection? _bus;
    private uint _last;

    public async Task ShowAsync(string title, string body)
    {
        try
        {
            if (_bus is null)
            {
                _bus = new Connection(Address.Session!);
                await _bus.ConnectAsync();
            }
            _last = await _bus.CallMethodAsync(Notify(_bus, _last, title, body), static (m, _) => m.GetBodyReader().ReadUInt32());
        }
        catch (Exception ex) when (ex is DBusException or ConnectException or IOException or SocketException or DisconnectedException or ArgumentNullException)
        {
            _bus = null; // no notification daemon; nothing else to do
        }
    }

    private static MessageBuffer Notify(Connection bus, uint replace, string title, string body)
    {
        using var w = bus.GetMessageWriter();
        w.WriteMethodCallHeader("org.freedesktop.Notifications", "/org/freedesktop/Notifications", "org.freedesktop.Notifications", "Notify", "susssasa{sv}i");
        w.WriteString("Oversight");
        w.WriteUInt32(replace);
        w.WriteString("oversight");
        w.WriteString(title);
        w.WriteString(body);
        w.WriteArray(Array.Empty<string>());
        var hints = w.WriteDictionaryStart();
        w.WriteDictionaryEnd(hints);
        w.WriteInt32(5000);
        return w.CreateMessage();
    }
}

/// <summary>
/// One copy at a time. The first copy listens on a socket in the user's runtime folder; a second copy connects, asks
/// the first to open Settings, and exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Socket? _listener;
    private readonly string _path;

    public bool IsFirst => _listener is not null;
    public event Action? ShowRequested;

    public SingleInstance(string name)
    {
        string dir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } r ? r : Path.GetTempPath();
        _path = Path.Combine(dir, $"{name}-{Environment.UserName}.sock");

        if (TrySignal()) return;
        try { File.Delete(_path); } catch (IOException) { }
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Bind(new UnixDomainSocketEndPoint(_path));
            socket.Listen(4);
        }
        catch (SocketException)
        {
            socket.Dispose();
            return;
        }
        _listener = socket;
        _ = AcceptAsync();
    }

    private bool TrySignal()
    {
        if (!File.Exists(_path)) return false;
        try
        {
            using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            s.Connect(new UnixDomainSocketEndPoint(_path));
            s.Send(Encoding.ASCII.GetBytes("show\n"));
            return true;
        }
        catch (SocketException) { return false; } // stale socket from a crashed copy
    }

    private async Task AcceptAsync()
    {
        while (_listener is not null)
        {
            try
            {
                using var client = await _listener.AcceptAsync();
                var buffer = new byte[16];
                int n = await client.ReceiveAsync(buffer);
                if (Encoding.ASCII.GetString(buffer, 0, n).StartsWith("show")) ShowRequested?.Invoke();
            }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { }
        }
    }

    public void Dispose()
    {
        _listener?.Dispose();
        if (_listener is not null) try { File.Delete(_path); } catch (IOException) { }
    }
}

public static class Shell
{
    /// <summary>Opens a URL, folder or file with the desktop's default app.</summary>
    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo("xdg-open", [target]) { UseShellExecute = false }); }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
