namespace BMSAudioSim;

using System;
using System.Runtime.InteropServices;
using ManagedBass;

public static class RadioPlayback
{
    private static RadioEffect? _radioEffect;
    private static RadioPreFilter? _radioPreFilter;
    private static float[]? _dspScratch = new float[8192];
    private static DSPProcedure? _dspProc;
    private static int _stream;

    public static void Start(string filePath, AudioParams initialParams)
    {
        // Initialize BASS
        if (!Bass.Init())
            throw new Exception("Failed to initialize BASS.");

        // Create looping stream (Decode flag not needed unless you want to post-process offline)
        _stream = Bass.CreateStream(filePath, 0, 0, BassFlags.Loop | BassFlags.Float);
        if (_stream == 0)
            throw new Exception($"BASS error: {Bass.LastError}");
        
        // Create radio effect processor using stream format info
        var info = Bass.ChannelGetInfo(_stream);

        _radioPreFilter ??= new RadioPreFilter(info.Frequency);
        _radioEffect ??= new RadioEffect(info.Frequency, info.Channels, initialParams);

        // Define DSP callback
        _dspProc = (handle, channel, bufferPtr, length, user) =>
        {
            if (_dspScratch == null)
                throw new Exception("_dspScratch is null");
            
            int samples = length / sizeof(float);
            if (_dspScratch.Length < samples)
                _dspScratch = new float[samples];

            // Copy from unmanaged to managed
            Marshal.Copy(bufferPtr, _dspScratch, 0, samples);

            // Apply in-place processing
            _radioPreFilter.SetNoiseLevel(_radioEffect.Params.NoiseLevel);
            _radioPreFilter.Process(_dspScratch, 0, samples, 1);
            
            _radioEffect.Process(_dspScratch, 0, samples);

            // Copy back
            Marshal.Copy(_dspScratch, 0, bufferPtr, samples);
        };

        // Attach DSP and start playback
        Bass.ChannelSetDSP(_stream, _dspProc, IntPtr.Zero, 0);
        Bass.ChannelPlay(_stream);
    }

    public static void UpdateParams(AudioParams p)
    {
        if (_radioEffect != null) _radioEffect.Params = p;
    }

    public static void Stop()
    {
        Bass.ChannelStop(_stream);
        Bass.StreamFree(_stream);
        Bass.Free();
    }
}
