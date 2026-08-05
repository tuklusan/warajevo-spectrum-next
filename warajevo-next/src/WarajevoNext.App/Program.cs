// ============================================================================
// Copyright (c) 2026 Supratim Sanyal (port) of SANYALnet Labs.
// Licensed under GNU GPL v3-or-later.
// ----------------------------------------------------------------------------
// App entry point. Supports two modes:
//   (default)      - launches the Avalonia GUI
//   --selftest N   - headless mode: boot the machine with the stub ROM and
//                    run N frames, print state, then exit 0. Used by CI.
// ============================================================================
using Avalonia;
using WarajevoNext.Machine;

namespace WarajevoNext.App;

public static class Program
{
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
