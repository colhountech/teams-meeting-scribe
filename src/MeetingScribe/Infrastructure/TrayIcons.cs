using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MeetingScribe.Pipeline;

namespace MeetingScribe.Infrastructure;

/// <summary>Generates the tray icons at runtime so the app ships without image assets.</summary>
[SupportedOSPlatform("windows")]
internal static partial class TrayIcons
{
    private static readonly Dictionary<AppState, Icon> Cache = [];

    public static Icon For(AppState state)
    {
        if (Cache.TryGetValue(state, out var cached)) return cached;

        var icon = Create(ColorFor(state), state == AppState.Recording);
        Cache[state] = icon;
        return icon;
    }

    private static Color ColorFor(AppState state) => state switch
    {
        AppState.Recording => Color.FromArgb(232, 62, 62),
        AppState.Transcribing => Color.FromArgb(66, 133, 244),
        AppState.Paused => Color.FromArgb(150, 150, 150),
        AppState.Error => Color.FromArgb(240, 173, 32),
        _ => Color.FromArgb(96, 186, 120),
    };

    private static Icon Create(Color color, bool filled)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            var bounds = new Rectangle(4, 4, 24, 24);
            using var brush = new SolidBrush(color);
            using var pen = new Pen(color, 4f);

            if (filled)
            {
                graphics.FillEllipse(brush, bounds);
            }
            else
            {
                graphics.DrawEllipse(pen, bounds);
                graphics.FillEllipse(brush, new Rectangle(13, 13, 6, 6));
            }
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr handle);

    public static void DisposeAll()
    {
        foreach (var icon in Cache.Values) icon.Dispose();
        Cache.Clear();
    }
}
