// ReSharper disable InconsistentNaming
namespace OpenFreqAudio.Models;

/// <summary>
/// Parameters for physics-based stepped-on transmission simulation.
/// Calculated from RF propagation data (signal strength, SNR, distance, frequency).
/// </summary>
public class SteppedOnParams
{
    /// <summary>
    /// Power difference in dBm between primary and secondary signals.
    /// Positive = primary stronger, Negative = secondary stronger.
    /// </summary>
    public float PowerDiff_dBm;
    
    /// <summary>
    /// FM capture ratio (0-1). How much the primary signal dominates.
    /// 1.0 = complete capture (clean), 0.5 = equal power (severe interference).
    /// </summary>
    public float CaptureRatio;
    
    /// <summary>
    /// Audible beat frequency in Hz from carrier offset.
    /// Caused by crystal instability (±2 ppm) and temperature drift.
    /// VHF (30-174 MHz): 150-250 Hz typical
    /// UHF (225-512 MHz): 200-350 Hz typical
    /// Real military audio: 187-217 Hz observed
    /// </summary>
    public float BeatFrequency_Hz;
    
    /// <summary>
    /// Frequency instability (warble width) in Hz.
    /// Poor SNR and long paths cause noisy frequency tracking.
    /// Creates slow wandering of the beat frequency.
    /// </summary>
    public float FreqInstability_Hz;
    
    /// <summary>
    /// Fast fading rate in Hz from AGC hunting and phase cancellation.
    /// Higher when signals are nearly equal power.
    /// Typical range: 15-50 Hz.
    /// </summary>
    public float FastFadingRate_Hz;
    
    /// <summary>
    /// Slow fading rate in Hz from multipath and atmospheric effects.
    /// Related to Doppler spread (velocity / wavelength).
    /// Typical range: 1-15 Hz.
    /// </summary>
    public float SlowFadingRate_Hz;
    
    /// <summary>
    /// Overall interference severity (0-1).
    /// Controls amplitude of all interference effects.
    /// 0 = clean capture, 1 = severe corruption.
    /// </summary>
    public float InterferenceLevel;
    
    /// <summary>
    /// Whether this is VHF (30-174 MHz) or UHF (225-512 MHz).
    /// VHF is more resilient to interference than UHF.
    /// </summary>
    public bool IsVHF;
    
    /// <summary>
    /// Average signal quality indicator (0-1).
    /// Based on combined SNR of both signals.
    /// </summary>
    public float SignalQuality;

    public override string ToString()
    {
        return $"PowerDiff_dBm = {PowerDiff_dBm} CaptureRatio = {CaptureRatio} BeatFrequency_Hz={BeatFrequency_Hz}";
    }
}