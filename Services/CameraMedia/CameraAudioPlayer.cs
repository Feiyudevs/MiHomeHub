// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using NAudio.Wave;

namespace MiHomeHub.Services.CameraMedia;

/// <summary>
/// 把解好的 PCM 直接推给 Windows 的音频输出（NAudio 的 WaveOut，随包引用，不装任何东西）。
/// 缓冲只留 2 秒并且满了就丢旧数据，声音永远贴着画面走，不会越播越迟。
/// </summary>
public sealed class CameraAudioPlayer : IDisposable
{
    private readonly BufferedWaveProvider _buffer;
    private readonly WaveOutEvent? _output;
    private bool _muted;

    public CameraAudioPlayer()
    {
        _buffer = new BufferedWaveProvider(new WaveFormat(CameraAudioSource.SampleRate, 16, 1))
        {
            BufferDuration = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = true,
        };
        try
        {
            _output = new WaveOutEvent { DesiredLatency = 180, NumberOfBuffers = 3 };
            _output.Init(_buffer);
            _output.Play();
        }
        catch
        {
            // 没有可用的输出设备时先不出声，画面照常
            _output = null;
        }
    }

    public bool Available => _output != null;

    public bool Muted
    {
        get => _muted;
        set
        {
            if (_output == null || _muted == value) return;
            _muted = value;
            try
            {
                if (_muted) _output.Pause();
                else _output.Play();
            }
            catch
            {
                // 设备中途被拔掉，忽略
            }
        }
    }

    public void Add(byte[] pcm)
    {
        if (_output == null || pcm.Length == 0) return;

        // 堆得太多说明这条链慢了半拍，先清空把延迟压回去
        if (_buffer.BufferedDuration > TimeSpan.FromSeconds(1)) _buffer.ClearBuffer();
        _buffer.AddSamples(pcm, 0, pcm.Length);
    }

    public void Dispose()
    {
        try { _output?.Stop(); } catch { }
        try { _output?.Dispose(); } catch { }
    }
}
