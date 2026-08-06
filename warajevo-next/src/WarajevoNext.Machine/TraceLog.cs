// ============================================================================
// Copyright (c) 2026 Supratim Sanyal (port) of SANYALnet Labs.
// Licensed under GNU GPL v3-or-later.
// ----------------------------------------------------------------------------
// TraceLog - fixed-size 8 MB circular trace buffer, flushed to a file in
// the OS temp directory. Enabled by env WARAJEVO_NEXT_TRACE=1. Used for
// heavy instrumentation of the tape-loading path without dragging Console
// I/O onto the hot path.
//
// File path: <TempPath>/warajevo-next-trace.log
// When the buffer wraps, older content is overwritten; the flushed file
// always contains the LAST 8 MB of trace output. Auto-flushes every
// second and on process exit.
// ============================================================================
using System;
using System.IO;
using System.Text;
using System.Threading;

namespace WarajevoNext.Machine;

public static class TraceLog
{
    private const int CapBytes = 8 * 1024 * 1024;
    private static readonly byte[] _buf = new byte[CapBytes];
    private static long _totalWritten;
    private static readonly object _lock = new();
    public static readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "warajevo-next-trace.log");
    public static bool Enabled { get; private set; }
    private static Timer? _flushTimer;

    static TraceLog()
    {
        var env = Environment.GetEnvironmentVariable("WARAJEVO_NEXT_TRACE");
        Enabled = !string.IsNullOrEmpty(env) && env != "0" && !env.Equals("false", StringComparison.OrdinalIgnoreCase);
        if (Enabled)
        {
            // Wipe any prior trace file so the reader always sees THIS run.
            try { File.Delete(Path); } catch { }
            _flushTimer = new Timer(_ => Flush(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
        }
    }

    public static void Log(string s)
    {
        if (!Enabled) return;
        var bytes = Encoding.UTF8.GetBytes(s + "\n");
        lock (_lock)
        {
            for (int i = 0; i < bytes.Length; i++)
                _buf[(_totalWritten + i) % CapBytes] = bytes[i];
            _totalWritten += bytes.Length;
        }
    }

    public static void Flush()
    {
        if (!Enabled) return;
        lock (_lock)
        {
            try
            {
                using var fs = File.Create(Path);
                if (_totalWritten <= CapBytes)
                {
                    fs.Write(_buf, 0, (int)_totalWritten);
                }
                else
                {
                    int start = (int)(_totalWritten % CapBytes);
                    fs.Write(_buf, start, CapBytes - start);
                    fs.Write(_buf, 0, start);
                }
            }
            catch { /* best-effort; don't crash the emulator over a log write */ }
        }
    }
}
