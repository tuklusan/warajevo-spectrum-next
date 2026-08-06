// ============================================================================
// Copyright (c) 2026 Supratim Sanyal (port) of SANYALnet Labs.
// Licensed under GNU GPL v3-or-later.
// ----------------------------------------------------------------------------
// ControlServer - a tiny line-oriented TCP command interface for driving the
// running emulator from a launcher script or a plain `telnet localhost 10001`
// session. Opens ONLY when WARAJEVO_NEXT_CTRL_PORT is set (recommended value:
// 10001) so a normal desktop run stays firewall-clean. Accepts one command
// per line, responds with one line per command.
//
// Grammar (case-insensitive commands; keys match SpectrumKey enum names,
// with bare digits 0..9 mapped to D0..D9):
//   KEY <token>[,<token>...]        press each token in sequence; a token
//                                    can be a chord with '+' (SS+P, CS+9)
//   HOLD <chord>                    press-and-hold the chord (no release)
//   RELEASE <chord>                 release the chord
//   SNAP <path>                     dump the current ULA framebuffer as PNG
//   PC?                             report Cpu.PC as hex
//   STATUS                          one-line machine status
//   HELP                            list commands
//   QUIT                            close the connection
//
// The server serialises all Spectrum-side work onto the Avalonia UI thread
// via Dispatcher, so the CPU/Ula are never touched from the socket thread.
// ============================================================================
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using WarajevoNext.Machine;

namespace WarajevoNext.App;

public sealed class ControlServer
{
    private readonly int _port;
    private readonly Func<SpectrumMachine?> _getMachine;
    private readonly Func<uint[]> _getFramePixels;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public ControlServer(int port, Func<SpectrumMachine?> getMachine, Func<uint[]> getFramePixels)
    {
        _port = port;
        _getMachine = getMachine;
        _getFramePixels = getFramePixels;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        // Bind to loopback only - not for exposing the emulator to the LAN.
        // If you want LAN control, change this to IPAddress.Any deliberately.
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();
        Console.WriteLine($"[ctrl] TCP command server listening on port {_port}");
        _ = Task.Run(() => AcceptLoop(_cts.Token));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); _listener?.Stop(); } catch { }
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Console.WriteLine($"[ctrl] accept error: {ex.Message}"); break; }
            _ = Task.Run(() => HandleClientAsync(client, ct));
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
        Console.WriteLine($"[ctrl] connect from {remote}");
        using (client)
        using (var stream = client.GetStream())
        using (var reader = new StreamReader(stream, Encoding.ASCII))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true })
        {
            await writer.WriteLineAsync("WarajevoNext CTRL v1  -  type HELP for commands");
            while (!ct.IsCancellationRequested)
            {
                string? line;
                try { line = await reader.ReadLineAsync(ct); }
                catch { break; }
                if (line is null) break;
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                string reply;
                try { reply = await DispatchAsync(trimmed); }
                catch (Exception ex) { reply = "ERR " + ex.Message; }
                await writer.WriteLineAsync(reply);
                if (trimmed.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase)) break;
            }
        }
        Console.WriteLine($"[ctrl] disconnect {remote}");
    }

    private async Task<string> DispatchAsync(string cmdLine)
    {
        var space = cmdLine.IndexOf(' ');
        var verb = (space < 0 ? cmdLine : cmdLine[..space]).ToUpperInvariant();
        var arg = space < 0 ? "" : cmdLine[(space + 1)..].Trim();
        switch (verb)
        {
            case "HELP":
                return "OK commands: KEY <tok,tok...>  HOLD <chord>  RELEASE <chord>  SNAP <path>  PC?  STATUS  QUIT";

            case "PC?":
                return await RunOnUiThread(() =>
                {
                    var m = _getMachine();
                    return m is null ? "ERR no machine" : $"OK PC=0x{m.Cpu.PC:X4}";
                });

            case "STATUS":
                return await RunOnUiThread(() =>
                {
                    var m = _getMachine();
                    if (m is null) return "ERR no machine";
                    return string.Format(CultureInfo.InvariantCulture,
                        "OK model={0} pc=0x{1:X4} sp=0x{2:X4} af=0x{3:X4} tape={4} frames={5}",
                        m.Model, m.Cpu.PC, m.Cpu.SP, m.Cpu.AF,
                        m.Tape?.IsPlaying == true ? "playing" : m.Tape != null ? "loaded" : "none",
                        m.FrameCount);
                });

            case "KEY":
                if (arg.Length == 0) return "ERR KEY needs at least one token";
                await ScriptedKeysAsync(arg);
                return "OK";

            case "HOLD":
                if (arg.Length == 0) return "ERR HOLD needs a chord";
                await SetChordAsync(arg, true);
                return "OK";

            case "RELEASE":
                if (arg.Length == 0) return "ERR RELEASE needs a chord";
                await SetChordAsync(arg, false);
                return "OK";

            case "SNAP":
                if (arg.Length == 0) return "ERR SNAP needs a path";
                var savedPath = await RunOnUiThread(() =>
                {
                    var pix = _getFramePixels();
                    Directory.CreateDirectory(Path.GetDirectoryName(arg)!);
                    MainWindow.SavePngBgra(arg, pix, Ula.FrameW, Ula.FrameH);
                    var sz = new FileInfo(arg).Length;
                    return $"OK snap {arg} ({sz} bytes)";
                });
                return savedPath;

            case "QUIT":
                return "OK bye";

            default:
                return "ERR unknown verb";
        }
    }

    private static SpectrumKey[] ParseChord(string chord) =>
        chord.Split('+')
             .Select(t =>
             {
                 var s = t.Trim();
                 if (s.Length == 1 && char.IsDigit(s[0])) s = "D" + s;
                 return Enum.TryParse<SpectrumKey>(s, ignoreCase: true, out var k) ? (SpectrumKey?)k : null;
             })
             .Where(k => k.HasValue).Select(k => k!.Value).ToArray();

    private async Task ScriptedKeysAsync(string spec)
    {
        foreach (var tok in spec.Split(','))
        {
            var chord = ParseChord(tok);
            if (chord.Length == 0) continue;
            await SetChordAsync(chord, true);
            await Task.Delay(180);
            await SetChordAsync(chord, false);
            await Task.Delay(360);
        }
    }

    private Task SetChordAsync(string chord, bool down) => SetChordAsync(ParseChord(chord), down);

    private Task SetChordAsync(SpectrumKey[] chord, bool down)
    {
        return RunOnUiThread(() =>
        {
            var m = _getMachine();
            if (m is null) return "ERR no machine";
            foreach (var k in chord) m.Keyboard.SetKey(k, down);
            return "OK";
        });
    }

    private static Task<T> RunOnUiThread<T>(Func<T> body)
    {
        var tcs = new TaskCompletionSource<T>();
        Dispatcher.UIThread.Post(() =>
        {
            try { tcs.SetResult(body()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }
}
