// ============================================================================
// Copyright (c) 2026 Supratim Sanyal (port) of SANYALnet Labs.
// Licensed under GNU GPL v3-or-later.
// ----------------------------------------------------------------------------
// Main window: hosts the Spectrum screen, wires up the menus, runs the
// 50 Hz emulation loop via a DispatcherTimer, and pumps a WriteableBitmap
// with the ULA framebuffer every frame.
// ============================================================================
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using WarajevoNext.Machine;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace WarajevoNext.App;

public partial class MainWindow : Window
{
    private SpectrumMachine? _machine;
    private WriteableBitmap? _bmp;
    private readonly uint[] _framePix = new uint[Ula.FrameW * Ula.FrameH];
    private DispatcherTimer? _timer;
    private bool _paused;
    private SpectrumModel _model = SpectrumModel.FortyEight;
    private readonly Dictionary<Key, SpectrumKey> _keyMap = BuildKeyMap();

    public MainWindow()
    {
        InitializeComponent();
        _bmp = new WriteableBitmap(new PixelSize(Ula.FrameW, Ula.FrameH), new Vector(96, 96),
                                   PixelFormat.Bgra8888, AlphaFormat.Opaque);
        var screen = this.FindControl<Image>("ScreenImage")!;
        screen.Source = _bmp;
        Focusable = true;

        this.FindControl<MenuItem>("OpenSnapshotItem")!.Click += (_, _) => _ = OpenSnapshot();
        this.FindControl<MenuItem>("OpenTapeItem")!.Click     += (_, _) => _ = OpenTape();
        this.FindControl<MenuItem>("LoadRomItem")!.Click      += (_, _) => _ = LoadRom();
        this.FindControl<MenuItem>("ExitItem")!.Click         += (_, _) => Close();
        this.FindControl<MenuItem>("ResetItem")!.Click        += (_, _) => _machine?.Reset();
        this.FindControl<MenuItem>("PauseItem")!.Click        += (_, _) => _paused = !_paused;
        this.FindControl<MenuItem>("Sel48Item")!.Click        += (_, _) => SwitchModel(SpectrumModel.FortyEight);
        this.FindControl<MenuItem>("Sel128Item")!.Click       += (_, _) => SwitchModel(SpectrumModel.OneTwentyEight);
        this.FindControl<MenuItem>("AboutItem")!.Click        += (_, _) => ShowAbout();

        KeyDown += OnKeyDown;
        KeyUp   += OnKeyUp;

        TryAutoBoot();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        // Optional TCP control server. Only starts if WARAJEVO_NEXT_CTRL_PORT
        // is set (recommend 10001); see ControlServer.cs for the grammar.
        if (int.TryParse(Environment.GetEnvironmentVariable("WARAJEVO_NEXT_CTRL_PORT"), out var ctrlPort) && ctrlPort > 0)
        {
            try
            {
                _control = new ControlServer(ctrlPort, () => _machine, () => _framePix);
                _control.Start();
            }
            catch (Exception ex) { Console.WriteLine($"[ctrl] failed to start: {ex.Message}"); }
        }
    }

    private ControlServer? _control;

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void SetStatus(string s) => this.FindControl<TextBlock>("StatusBar")!.Text = s;

    private void TryAutoBoot()
    {
        // ROM lookup order:
        //   1. Program.RomPath      - --rom PATH on the command line
        //   2. $WARAJEVO_NEXT_ROMS  - directory containing 48.rom
        //   3. AppContext.BaseDirectory/roms
        //   4. ./roms
        //   5. ../../roms/spectrum-48k relative to BaseDirectory (repo layout,
        //      handy when running from bin/Release/net10.0 inside the checkout)
        byte[]? rom48 = null;
        if (!string.IsNullOrEmpty(Program.RomPath) && File.Exists(Program.RomPath))
            rom48 = File.ReadAllBytes(Program.RomPath);
        if (rom48 == null)
        {
            var envDir = Environment.GetEnvironmentVariable("WARAJEVO_NEXT_ROMS");
            var candidates = new[]
            {
                envDir,
                Path.Combine(AppContext.BaseDirectory, "roms"),
                "roms",
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "roms", "spectrum-48k"),
            }.Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).ToArray();
            foreach (var d in candidates)
            {
                var f = Path.Combine(d, "48.rom");
                if (File.Exists(f)) { rom48 = File.ReadAllBytes(f); break; }
            }
        }
        if (rom48 == null || rom48.Length < 0x4000)
        {
            SetStatus("No 48.rom found — using a blank stub ROM (CPU will run but not much). Use File > Load ROM.");
            rom48 = new byte[0x4000];
        }
        else SetStatus($"Booted with 48.rom ({rom48.Length} bytes).");
        _machine = new SpectrumMachine(SpectrumModel.FortyEight, rom48);
        _machine.FastLoadDiag = s => Console.WriteLine($"[fastload] {s}");
        // WARAJEVO_NEXT_FASTLOAD=0 / false / off disables the 0x0556 trap so
        // the tape loads through the ROM's real edge-decoded LD-BYTES routine
        // (with proper border stripes and audio pulses). Any other value or
        // the variable being unset leaves the trap on.
        var flEnv = Environment.GetEnvironmentVariable("WARAJEVO_NEXT_FASTLOAD");
        if (!string.IsNullOrEmpty(flEnv) &&
            (flEnv.Equals("0", StringComparison.Ordinal) ||
             flEnv.Equals("false", StringComparison.OrdinalIgnoreCase) ||
             flEnv.Equals("off", StringComparison.OrdinalIgnoreCase)))
        {
            _machine.FastLoad = false;
            Console.WriteLine("[boot] fast-load trap DISABLED (WARAJEVO_NEXT_FASTLOAD=" + flEnv + ")");
        }
        _machine.Reset();

        // If the user passed --tape PATH, auto-load and play it. Fast-load
        // trap at 0x0556 will consume the blocks as the ROM calls LD-BYTES.
        if (!string.IsNullOrEmpty(Program.TapePath) && File.Exists(Program.TapePath))
        {
            var data = File.ReadAllBytes(Program.TapePath);
            _machine.Tape = new TapeDevice();
            _machine.Tape.LoadTap(data);
            _machine.Tape.Play();
            SetStatus($"Auto-loaded tape: {Path.GetFileName(Program.TapePath)} ({_machine.Tape.Blocks} block(s)). Type J then Symbol-Shift+P Symbol-Shift+P Enter to LOAD.");
        }
    }

    private void SwitchModel(SpectrumModel m)
    {
        _model = m;
        SetStatus($"Model set to {m}. Use File > Load ROM if switching to 128K.");
    }

    private int _diagFrames;
    private static readonly string? SnapshotDir = Environment.GetEnvironmentVariable("WARAJEVO_NEXT_SNAP_DIR");
    private static readonly int SnapshotEvery = int.TryParse(Environment.GetEnvironmentVariable("WARAJEVO_NEXT_SNAP_EVERY"), out var _se) ? _se : 0;
    private static readonly int AutoLoadAtFrame = int.TryParse(Environment.GetEnvironmentVariable("WARAJEVO_NEXT_AUTOLOAD_FRAME"), out var _al) ? _al : 0;
    private static readonly string? StartKeysSpec = Environment.GetEnvironmentVariable("WARAJEVO_NEXT_STARTKEYS");
    private static readonly int StartKeysAtFrame = int.TryParse(Environment.GetEnvironmentVariable("WARAJEVO_NEXT_STARTKEYS_FRAME"), out var _sk) ? _sk : 0;
    private bool _autoLoadDone;
    private bool _startKeysDone;

    private void Tick()
    {
        if (_paused || _machine == null || _bmp == null) return;
        _machine.RunFrame();
        int borderWrites = _machine.Ula.BorderLogCount;
        _machine.Ula.RenderFrame(_framePix);
        if (_machine.Tape != null && _machine.Tape.IsPlaying && (_diagFrames % 100) == 0)
            Console.WriteLine($"[tape] f={_diagFrames} pc=0x{_machine.Cpu.PC:X4} state={_machine.Tape.StateName} pilot={_machine.Tape.PilotPulsesLeft} block={_machine.Tape.CurrentBlock} dataPtr={_machine.Tape.DataPtr}/{_machine.Tape.BlockLen} borderWrites={borderWrites} ldEdgeTrapFires={_machine.LdEdgeTrapFires}");
        using (var buf = _bmp.Lock())
        {
            unsafe
            {
                int rowBytes = buf.RowBytes;
                int srcRowBytes = Ula.FrameW * 4;
                byte* dstRow = (byte*)buf.Address;
                fixed (uint* srcBase = _framePix)
                {
                    byte* srcRow = (byte*)srcBase;
                    for (int y = 0; y < Ula.FrameH; y++)
                    {
                        System.Buffer.MemoryCopy(srcRow, dstRow, rowBytes, srcRowBytes);
                        srcRow += srcRowBytes;
                        dstRow += rowBytes;
                    }
                }
            }
        }
        this.FindControl<Image>("ScreenImage")!.InvalidateVisual();

        // --- Avalonia-internal periodic PNG snapshot ---------------------
        // Direct dump of the ULA framebuffer to a file; this bypasses all
        // desktop / Win32 capture flakiness and shows exactly what the
        // emulator is compositing.
        if (SnapshotDir != null && SnapshotEvery > 0 && (_diagFrames % SnapshotEvery) == 0)
        {
            try
            {
                Directory.CreateDirectory(SnapshotDir);
                var path = Path.Combine(SnapshotDir, $"screen_{_diagFrames:D5}.png");
                SavePngBgra(path, _framePix, Ula.FrameW, Ula.FrameH);
                Console.WriteLine($"[snap] f={_diagFrames} pc=0x{_machine.Cpu.PC:X4} -> {path}");
            }
            catch (Exception ex) { Console.WriteLine($"[snap] err: {ex.Message}"); }
        }

        // --- Auto-type LOAD "" after N frames, for hands-off tape test ---
        if (!_autoLoadDone && AutoLoadAtFrame > 0 && _diagFrames >= AutoLoadAtFrame && _machine.Tape != null)
        {
            _autoLoadDone = true;
            Console.WriteLine($"[auto] injecting LOAD \"\" at frame {_diagFrames} pc=0x{_machine.Cpu.PC:X4}");
            _ = InjectLoadSequenceAsync();
        }

        // --- Optional post-load "start the game" key sequence -------------
        // WARAJEVO_NEXT_STARTKEYS is a comma-separated list of Spectrum
        // key tokens; each token is one press. Chord several keys with '+',
        // eg "0,5" plays two presses ("0" then "5"), "SS+P" is one Sym-
        // Shift+P chord. Bare digits get their D-prefix (0..9 -> D0..D9);
        // everything else matches SpectrumKey enum names case-insensitively.
        if (!_startKeysDone && StartKeysAtFrame > 0 && _diagFrames >= StartKeysAtFrame
            && !string.IsNullOrWhiteSpace(StartKeysSpec) && _machine.Tape != null)
        {
            _startKeysDone = true;
            Console.WriteLine($"[auto] injecting STARTKEYS \"{StartKeysSpec}\" at frame {_diagFrames} pc=0x{_machine.Cpu.PC:X4}");
            _ = InjectStartKeysAsync(StartKeysSpec);
        }

        _diagFrames++;
    }

    private async Task InjectStartKeysAsync(string spec)
    {
        foreach (var token in spec.Split(','))
        {
            var chord = token.Split('+')
                             .Select(ParseSpectrumKey)
                             .Where(k => k.HasValue)
                             .Select(k => k!.Value)
                             .ToArray();
            if (chord.Length == 0)
            {
                Console.WriteLine($"[auto] STARTKEYS: skipping unknown token '{token}'");
                continue;
            }
            await HoldAsync(chord);
        }
        Console.WriteLine("[auto] STARTKEYS delivered");
    }

    private static SpectrumKey? ParseSpectrumKey(string raw)
    {
        var s = raw.Trim();
        if (s.Length == 0) return null;
        if (s.Length == 1 && char.IsDigit(s[0])) s = "D" + s; // 0 -> D0
        return Enum.TryParse<SpectrumKey>(s, ignoreCase: true, out var k) ? k : null;
    }

    private async Task InjectLoadSequenceAsync()
    {
        // Press J (LOAD keyword), Symbol-Shift+P, Symbol-Shift+P, Enter.
        // Each "press" holds the spectrum keys for a few frames so the
        // ROM's 50 Hz key scan definitely sees them.
        var kb = _machine!.Keyboard;
        await HoldAsync(new[] { SpectrumKey.J });
        await HoldAsync(new[] { SpectrumKey.SymShift, SpectrumKey.P });
        await HoldAsync(new[] { SpectrumKey.SymShift, SpectrumKey.P });
        await HoldAsync(new[] { SpectrumKey.Enter });
        Console.WriteLine("[auto] LOAD \"\" sequence delivered");
    }

    private async Task HoldAsync(SpectrumKey[] keys)
    {
        // 200 ms hold + 400 ms release. The ROM's own key-debounce
        // (KEY-INPUT at 0x02BF and REPEAT-KEY at 0x0310) needs to see a
        // clean released-then-pressed transition or it treats a follow-up
        // press of the same key as auto-repeat and drops it. 400 ms is
        // longer than the ROM's inter-key wait even at slow REPEAT-KEY.
        foreach (var k in keys) _machine!.Keyboard.SetKey(k, true);
        await Task.Delay(200);
        foreach (var k in keys) _machine!.Keyboard.SetKey(k, false);
        await Task.Delay(400);
    }

    // Public for use by the optional ControlServer's SNAP command.
    public static void SavePngBgra(string path, uint[] pixels, int w, int h)
    {
        // Minimal PNG writer: uncompressed deflate wrap of the raw filtered
        // scanlines. Keeps the app free of any external image dependency.
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        // IHDR
        WriteChunk(bw, "IHDR", BE(w).Concat(BE(h)).Concat(new byte[] { 8, 6, 0, 0, 0 }).ToArray());
        // IDAT: filter=0 then RGBA per row, deflate-compressed
        int stride = w * 4;
        var raw = new byte[(stride + 1) * h];
        int o = 0;
        for (int y = 0; y < h; y++)
        {
            raw[o++] = 0; // filter None
            int p = y * w;
            for (int x = 0; x < w; x++)
            {
                uint px = pixels[p + x]; // ARGB packed; store as R,G,B,A
                byte b = (byte)(px & 0xFF);
                byte g = (byte)((px >> 8) & 0xFF);
                byte r = (byte)((px >> 16) & 0xFF);
                byte a = (byte)((px >> 24) & 0xFF);
                raw[o++] = r; raw[o++] = g; raw[o++] = b; raw[o++] = a;
            }
        }
        using var ms = new MemoryStream();
        // Write zlib header manually then Deflate the raw data
        ms.WriteByte(0x78); ms.WriteByte(0x9C);
        using (var ds = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionLevel.Fastest, true))
            ds.Write(raw, 0, raw.Length);
        // Adler32 of raw
        uint adler = Adler32(raw);
        ms.WriteByte((byte)(adler >> 24)); ms.WriteByte((byte)(adler >> 16));
        ms.WriteByte((byte)(adler >> 8)); ms.WriteByte((byte)adler);
        WriteChunk(bw, "IDAT", ms.ToArray());
        WriteChunk(bw, "IEND", Array.Empty<byte>());
    }
    private static byte[] BE(int v) => new byte[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
    private static void WriteChunk(BinaryWriter bw, string tag, byte[] data)
    {
        bw.Write(BE(data.Length));
        var tagBytes = System.Text.Encoding.ASCII.GetBytes(tag);
        var buf = tagBytes.Concat(data).ToArray();
        bw.Write(buf);
        bw.Write(BE((int)Crc32(buf)));
    }
    private static uint Crc32(byte[] data)
    {
        uint c;
        uint[] table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        c = 0xFFFFFFFF;
        foreach (var b in data) c = table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }
    private static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (var byteVal in data) { a = (a + byteVal) % 65521; b = (b + a) % 65521; }
        return (b << 16) | a;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e) { if (_machine != null && _keyMap.TryGetValue(e.Key, out var k)) _machine.Keyboard.SetKey(k, true); }
    private void OnKeyUp  (object? sender, KeyEventArgs e) { if (_machine != null && _keyMap.TryGetValue(e.Key, out var k)) _machine.Keyboard.SetKey(k, false); }

    private static Dictionary<Key, SpectrumKey> BuildKeyMap() => new()
    {
        [Key.A]=SpectrumKey.A,[Key.B]=SpectrumKey.B,[Key.C]=SpectrumKey.C,[Key.D]=SpectrumKey.D,[Key.E]=SpectrumKey.E,
        [Key.F]=SpectrumKey.F,[Key.G]=SpectrumKey.G,[Key.H]=SpectrumKey.H,[Key.I]=SpectrumKey.I,[Key.J]=SpectrumKey.J,
        [Key.K]=SpectrumKey.K,[Key.L]=SpectrumKey.L,[Key.M]=SpectrumKey.M,[Key.N]=SpectrumKey.N,[Key.O]=SpectrumKey.O,
        [Key.P]=SpectrumKey.P,[Key.Q]=SpectrumKey.Q,[Key.R]=SpectrumKey.R,[Key.S]=SpectrumKey.S,[Key.T]=SpectrumKey.T,
        [Key.U]=SpectrumKey.U,[Key.V]=SpectrumKey.V,[Key.W]=SpectrumKey.W,[Key.X]=SpectrumKey.X,[Key.Y]=SpectrumKey.Y,
        [Key.Z]=SpectrumKey.Z,
        [Key.D0]=SpectrumKey.D0,[Key.D1]=SpectrumKey.D1,[Key.D2]=SpectrumKey.D2,[Key.D3]=SpectrumKey.D3,
        [Key.D4]=SpectrumKey.D4,[Key.D5]=SpectrumKey.D5,[Key.D6]=SpectrumKey.D6,[Key.D7]=SpectrumKey.D7,
        [Key.D8]=SpectrumKey.D8,[Key.D9]=SpectrumKey.D9,
        [Key.Enter]=SpectrumKey.Enter,[Key.Space]=SpectrumKey.Space,
        [Key.LeftShift]=SpectrumKey.CapsShift,[Key.RightShift]=SpectrumKey.CapsShift,
        [Key.LeftCtrl]=SpectrumKey.SymShift,[Key.RightCtrl]=SpectrumKey.SymShift,
    };

    private async Task OpenSnapshot()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open snapshot",
            FileTypeFilter = new[] { new FilePickerFileType("Snapshots") { Patterns = new[] { "*.sna", "*.z80" } } },
            AllowMultiple = false
        });
        if (files.Count == 0 || _machine == null) return;
        var f = files[0];
        var data = File.ReadAllBytes(f.Path.LocalPath);
        try
        {
            if (f.Name.EndsWith(".sna", StringComparison.OrdinalIgnoreCase)) SnapshotLoader.LoadSna(_machine, data);
            else SnapshotLoader.LoadZ80(_machine, data);
            SetStatus($"Loaded {f.Name} ({data.Length} bytes).");
        }
        catch (Exception ex) { SetStatus("Load failed: " + ex.Message); }
    }

    private async Task OpenTape()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open tape",
            FileTypeFilter = new[] { new FilePickerFileType("Tape") { Patterns = new[] { "*.tap" } } },
            AllowMultiple = false
        });
        if (files.Count == 0 || _machine == null) return;
        var data = File.ReadAllBytes(files[0].Path.LocalPath);
        _machine.Tape = new TapeDevice();
        _machine.Tape.LoadTap(data);
        _machine.Tape.Play();
        SetStatus($"Playing tape: {files[0].Name} ({_machine.Tape.Blocks} block(s)).");
    }

    private async Task LoadRom()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load ROM (16 KB)",
            FileTypeFilter = new[] { new FilePickerFileType("ROM") { Patterns = new[] { "*.rom", "*.bin" } } },
            AllowMultiple = false
        });
        if (files.Count == 0) return;
        var rom = File.ReadAllBytes(files[0].Path.LocalPath);
        if (rom.Length < 0x4000) { SetStatus("ROM too small."); return; }
        _machine = new SpectrumMachine(SpectrumModel.FortyEight, rom);
        _machine.Reset();
        SetStatus($"Loaded ROM {files[0].Name} and reset.");
    }

    private void ShowAbout()
    {
        var dlg = new Window { Title = "About Warajevo Next", Width = 460, Height = 260 };
        var text = new TextBlock
        {
            Margin = new Thickness(16),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Text = "Warajevo Next — ZX Spectrum 48K / 128K emulator.\n\n" +
                   "A modern .NET 10 + Avalonia port by Supratim Sanyal (SANYALnet Labs) of the\n" +
                   "original DOS-era Warajevo written by Zeljko Juric and Samir Ribic in Sarajevo.\n\n" +
                   "Licensed under the GNU General Public License v3-or-later (matching the\n" +
                   "original Warajevo GPL). No ROM images are bundled — supply your own via\n" +
                   "$WARAJEVO_NEXT_ROMS or File > Load ROM."
        };
        dlg.Content = text;
        dlg.ShowDialog(this);
    }
}
