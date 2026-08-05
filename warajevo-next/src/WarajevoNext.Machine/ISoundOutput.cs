// ============================================================================
// Copyright (c) 2026 Supratim Sanyal (port) of SANYALnet Labs.
// Licensed under GNU GPL v3-or-later.
// ============================================================================
namespace WarajevoNext.Machine;

/// <summary>
/// Cross-platform audio sink. The machine mixes beeper + AY samples and
/// queues them here. A NullSoundOutput exists so tests and headless CI never
/// touch audio hardware.
/// </summary>
public interface ISoundOutput : IDisposable
{
    int SampleRate { get; }
    void Queue(ReadOnlySpan<short> samples);
    void Start();
    void Stop();
}

public sealed class NullSoundOutput : ISoundOutput
{
    public int SampleRate { get; }
    public NullSoundOutput(int rate = 44100) { SampleRate = rate; }
    public void Queue(ReadOnlySpan<short> samples) { /* silence */ }
    public void Start() { }
    public void Stop() { }
    public void Dispose() { }
}
