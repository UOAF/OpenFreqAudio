using System;
using System.Collections.Generic;

namespace BMSAudioSim.Models;

public class AudioMixerParams
{
    public AudioParams Primary;        // Strongest signal
    public AudioParams Secondary;      // Weaker signal (if any)
    public float CaptureRatio;         // 0-1, how much primary dominates
    public float HeterodyneFreq;       // Beat frequency (Hz)
    public float InterferenceLevel;    // 0-1, amount of interference
}
