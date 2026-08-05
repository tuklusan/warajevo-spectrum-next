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
using System.Runtime.InteropServices;

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
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void SetStatus(string s) => this.FindControl<TextBlock>("StatusBar")!.Text = s;

    private void TryAutoBoot()
    {
        // Look for ROMs under $WARAJEVO_NEXT_ROMS or ./roms
        var envDir = Environment.GetEnvironmentVariable("WARAJEVO_NEXT_ROMS");
        var candidates = new[] { envDir, Path.Combine(AppContext.BaseDirectory, "roms"), "roms" }
                            .Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).ToArray();
        byte[]? rom48 = null;
        foreach (var d in candidates)
        {
            var f = Path.Combine(d, "48.rom");
            if (File.Exists(f)) { rom48 = File.ReadAllBytes(f); break; }
        }
        if (rom48 == null || rom48.Length < 0x4000)
        {
            SetStatus("No 48.rom found — using a blank stub ROM (CPU will run but not much). Use File > Load ROM.");
            rom48 = new byte[0x4000];
        }
        else SetStatus($"Booted with 48.rom ({rom48.Length} bytes).");
        _machine = new SpectrumMachine(SpectrumModel.FortyEight, rom48);
        _machine.Reset();
    }

    private void SwitchModel(SpectrumModel m)
    {
        _model = m;
        SetStatus($"Model set to {m}. Use File > Load ROM if switching to 128K.");
    }

    private void Tick()
    {
        if (_paused || _machine == null || _bmp == null) return;
        _machine.RunFrame();
        _machine.Ula.RenderFrame(_framePix);
        using (var buf = _bmp.Lock())
        {
            unsafe
            {
                fixed (uint* src = _framePix)
                    System.Buffer.MemoryCopy(src, (void*)buf.Address, _framePix.Length * 4L, _framePix.Length * 4L);
            }
        }
        this.FindControl<Image>("ScreenImage")!.InvalidateVisual();
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
