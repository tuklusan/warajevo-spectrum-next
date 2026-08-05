// ============================================================================
// Copyright (c) 2026 Supratim Sanyal (port) of SANYALnet Labs.
// Licensed under GNU GPL v3-or-later.
// ----------------------------------------------------------------------------
// App entry point. Supports three modes:
//   (default)         - launches the Avalonia GUI
//   --selftest N      - headless: boot with a stub ROM, run N frames, exit 0
//   [--rom PATH]      - path to a 48.rom image; overrides $WARAJEVO_NEXT_ROMS
//                       and the ./roms lookup used by MainWindow.TryAutoBoot
//   [--tape PATH]     - .tap file loaded and Play()ed at boot, so the GUI
//                       shows a tape actually loading without menu clicks
// ============================================================================
using Avalonia;
using WarajevoNext.Machine;

namespace WarajevoNext.App;

public static class Program
{
    // Consumed by MainWindow at construction time.
    public static string? RomPath;
    public static string? TapePath;

    [STAThread]
    public static int Main(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--selftest")
            {
                int frames = (i + 1 < args.Length && int.TryParse(args[i + 1], out var n)) ? n : 100;
                return SelfTest(frames);
            }
            if (args[i] == "--rom"  && i + 1 < args.Length) { RomPath  = args[++i]; continue; }
            if (args[i] == "--tape" && i + 1 < args.Length) { TapePath = args[++i]; continue; }
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    private static int SelfTest(int frames)
    {
        var m = new SpectrumMachine(SpectrumModel.FortyEight, new byte[0x4000]);
        m.Reset();
        for (int i = 0; i < frames; i++) m.RunFrame();
        Console.WriteLine($"selftest ok: frames={m.FrameCount} tstates={m.Cpu.TStates} pc=0x{m.Cpu.PC:X4}");
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<AvaloniaApp>()
                  .UsePlatformDetect()
                  .LogToTrace();
}
