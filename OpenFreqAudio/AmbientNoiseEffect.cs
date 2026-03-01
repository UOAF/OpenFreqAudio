// ReSharper disable InconsistentNaming

using System;
using System.Collections.Generic;

namespace OpenFreqAudio;

/// <summary>The acoustic environment of the transmitting station.</summary>
public enum AmbientNoiseType
{
    /// <summary>No ambient layer — clean demodulated audio only.</summary>
    None,

    /// <summary>Airborne platform (fast jet / helicopter).</summary>
    Air,

    /// <summary>Ground vehicle (APC, HMMWV, tank).</summary>
    Ground,

    /// <summary>Stationary ground station or AWACS.</summary>
    Stationary,
}

/// <summary>
/// Ambient SFX applied in two passes around the RF fading stage.
/// PreFade — transmitter-side acoustics (cockpit noise, mask muffling) on clean PCM.
/// PostFade — receiver-side IF filtering after demodulation.
/// Not thread-safe; each RadioEffect owns its own instance and replaces it atomically.
/// </summary>
internal interface IAmbientNoiseEffect
{
    /// <summary>Transmitter-side acoustics. Called on clean PCM before RF fading.</summary>
    void ApplyPreFade(float[] buffer, int offset, int frames);

    /// <summary>Receiver-side filtering. Called after RF fading.</summary>
    void ApplyPostFade(float[] buffer, int offset, int frames);
}

// ---------------------------------------------------------------------------
//  Factory
// ---------------------------------------------------------------------------

internal static class AmbientNoiseEffectFactory
{
    public static IAmbientNoiseEffect Create(AmbientNoiseType type, int sampleRate, int channels)
        => type switch
        {
            AmbientNoiseType.Air        => new AirAmbientEffect(sampleRate, channels),
            AmbientNoiseType.Ground     => new GroundAmbientEffect(sampleRate, channels),
            AmbientNoiseType.Stationary => new StationaryAmbientEffect(sampleRate, channels),
            _                           => NullAmbientEffect.Instance,
        };
}

// ---------------------------------------------------------------------------
//  None — no-op
// ---------------------------------------------------------------------------

internal sealed class NullAmbientEffect : IAmbientNoiseEffect
{
    public static readonly NullAmbientEffect Instance = new();
    private NullAmbientEffect() { }

    public void ApplyPreFade(float[] buffer, int offset, int frames) { }
    public void ApplyPostFade(float[] buffer, int offset, int frames) { }
}

// ---------------------------------------------------------------------------
//  Air — fast jet / helicopter cockpit
// ---------------------------------------------------------------------------

/// <summary>
/// Signal chain:
///   clean PCM
///   → [PreFade]  airframe AM: two beating oscillators (55 Hz + 73 Hz)
///   → [PreFade]  inverter whine: 400 Hz + odd harmonics, FM wobble (0.8 Hz, ±3 Hz)
///   → [PreFade]  engine roar: noise → one-pole LPF 350 Hz (additive)
///   → [PreFade]  oxygen-mask two-pole LPF 900 Hz + nasal cavity blend
///   → RF fading  (handled by RadioEffect)
///   → [PostFade] 300–2700 Hz brick-wall biquad bandpass
/// </summary>
internal sealed class AirAmbientEffect : IAmbientNoiseEffect
{
    private readonly int _sampleRate;
    private readonly int _channels;

    // Biquad bandpass — post-fade receiver IF filter, per-channel state [x1, x2, y1, y2]
    private static readonly Dictionary<int, (float b0, float b1, float b2, float a1, float a2)> BiquadCache = new();
    private static readonly object BiquadCacheLock = new();
    private readonly float _b0, _b1, _b2, _a1, _a2;
    private readonly float[] _biquadState;

    // Oxygen-mask LPF — two cascaded one-pole stages at 900 Hz
    private const float MuffleCutoff = 900f;
    private readonly float _muffleA;
    private readonly float[] _muffleLP1;
    private readonly float[] _muffleLP2;

    // Inverter whine — 400 Hz near-square-wave: fundamental + 3rd + 5th harmonics.
    // FM wobble via 0.8 Hz LFO simulates power-supply frequency drift.
    private const float WhineFreq        = 400f;
    private const float WhineLevel       = 0.012f;
    private const float WhineH3Level     = 0.005f;  // 1200 Hz
    private const float WhineH5Level     = 0.002f;  // 2000 Hz
    private const float WhineWobbleRate  = 0.8f;    // Hz
    private const float WhineWobbleDepth = 3.0f;    // ±Hz drift
    private double _whinePhase;
    private double _whineWobblePhase;

    // Engine roar — LCG noise → one-pole LPF 350 Hz, additive acoustic bleed
    private const float RoarLevel = 0.020f;
    private readonly float _roarLpA;
    private float _roarLpState;
    private uint  _noiseState = 0x9E3779B9u;

    // Airframe AM — two independent oscillators beating for organic lope
    private const float RumbleFreq1  = 55f;
    private const float RumbleFreq2  = 73f;
    private const float RumbleDepth1 = 0.10f;
    private const float RumbleDepth2 = 0.10f;
    private double _rumblePhase1;
    private double _rumblePhase2;

    public AirAmbientEffect(int sampleRate, int channels)
    {
        _sampleRate = sampleRate;
        _channels   = channels;

        _biquadState = new float[channels * 4];
        lock (BiquadCacheLock)
        {
            if (!BiquadCache.TryGetValue(sampleRate, out var c))
            {
                c = CalculateBiquadCoefficients(sampleRate);
                BiquadCache[sampleRate] = c;
            }
            (_b0, _b1, _b2, _a1, _a2) = c;
        }

        _muffleA   = MathF.Exp(-2f * MathF.PI * MuffleCutoff / sampleRate);
        _muffleLP1 = new float[channels];
        _muffleLP2 = new float[channels];

        _roarLpA = MathF.Exp(-2f * MathF.PI * 350f / sampleRate);
    }

    public void ApplyPreFade(float[] buffer, int offset, int frames)
    {
        double wobbleInc  = 2.0 * Math.PI * WhineWobbleRate / _sampleRate;
        double rumbleInc1 = 2.0 * Math.PI * RumbleFreq1 / _sampleRate;
        double rumbleInc2 = 2.0 * Math.PI * RumbleFreq2 / _sampleRate;

        for (int frame = 0; frame < frames; frame++)
        {
            // Whine: wobble LFO shifts instantaneous phase increment for FM effect
            double wobble    = Math.Sin(_whineWobblePhase) * WhineWobbleDepth;
            double whineInc  = 2.0 * Math.PI * (WhineFreq + wobble) / _sampleRate;
            _whineWobblePhase += wobbleInc;
            if (_whineWobblePhase > Math.PI * 2) _whineWobblePhase -= Math.PI * 2;

            float whineSample = (float)Math.Sin(_whinePhase)      * WhineLevel
                              + (float)Math.Sin(_whinePhase * 3.0) * WhineH3Level
                              + (float)Math.Sin(_whinePhase * 5.0) * WhineH5Level;
            _whinePhase += whineInc;
            if (_whinePhase > Math.PI * 2) _whinePhase -= Math.PI * 2;

            // Airframe AM: two oscillators beating for organic lope
            float rumbleGain = 1f
                + (float)Math.Sin(_rumblePhase1) * RumbleDepth1
                + (float)Math.Sin(_rumblePhase2) * RumbleDepth2;
            _rumblePhase1 += rumbleInc1;
            _rumblePhase2 += rumbleInc2;
            if (_rumblePhase1 > Math.PI * 2) _rumblePhase1 -= Math.PI * 2;
            if (_rumblePhase2 > Math.PI * 2) _rumblePhase2 -= Math.PI * 2;

            // Engine roar: Knuth LCG → one-pole LPF, mono source shared across channels
            _noiseState  = _noiseState * 1664525u + 1013904223u;
            float roar   = _roarLpA * _roarLpState + (1f - _roarLpA) * ((int)_noiseState * (1f / 2147483648f));
            _roarLpState = roar;

            for (int c = 0; c < _channels; c++)
            {
                int idx = offset + frame * _channels + c;
                float x = buffer[idx];

                x *= rumbleGain;          // airframe vibration AM-modulates mic pickup
                x += whineSample;         // electrical wiring bleed
                x += roar * RoarLevel;    // structure-borne acoustic roar

                // Oxygen-mask two-pole LPF + nasal cavity blend
                float lp1 = _muffleA * _muffleLP1[c] + (1f - _muffleA) * x;
                _muffleLP1[c] = lp1;
                float lp2 = _muffleA * _muffleLP2[c] + (1f - _muffleA) * lp1;
                _muffleLP2[c] = lp2;

                buffer[idx] = lp2 * 0.75f + x * 0.55f * 0.25f;
            }
        }
    }

    public void ApplyPostFade(float[] buffer, int offset, int frames)
    {
        for (int frame = 0; frame < frames; frame++)
        {
            for (int c = 0; c < _channels; c++)
            {
                int idx       = offset + frame * _channels + c;
                int stateBase = c * 4;

                float xn1 = _biquadState[stateBase];
                float xn2 = _biquadState[stateBase + 1];
                float yn1 = _biquadState[stateBase + 2];
                float yn2 = _biquadState[stateBase + 3];

                float x = buffer[idx];
                float y = _b0 * x + _b1 * xn1 + _b2 * xn2 - _a1 * yn1 - _a2 * yn2;

                _biquadState[stateBase + 1] = xn1;
                _biquadState[stateBase]     = x;
                _biquadState[stateBase + 3] = yn1;
                _biquadState[stateBase + 2] = y;

                buffer[idx] = y;
            }
        }
    }

    /// <summary>2nd-order Butterworth bandpass: 300–2700 Hz.</summary>
    private static (float b0, float b1, float b2, float a1, float a2) CalculateBiquadCoefficients(int sampleRate)
    {
        const float LowFreq  = 300f;
        const float HighFreq = 2700f;
        float centerFreq = (LowFreq + HighFreq) / 2f;
        float bandwidth  = HighFreq - LowFreq;
        float w0     = 2f * MathF.PI * centerFreq / sampleRate;
        float Q      = centerFreq / bandwidth;
        float alpha  = MathF.Sin(w0) / (2f * Q);
        float cosw0  = MathF.Cos(w0);
        float b0 = alpha;  float b1 = 0f;  float b2 = -alpha;
        float a0 = 1f + alpha;  float a1 = -2f * cosw0;  float a2 = 1f - alpha;
        return (b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }
}

// ---------------------------------------------------------------------------
//  Ground — ground vehicle (APC / HMMWV / tank)
// ---------------------------------------------------------------------------

/// <summary>
/// Signal chain:
///   clean PCM
///   → [PreFade]  diesel AM: two oscillators (45 Hz firing + 67 Hz hull structural)
///   → [PreFade]  engine roar: noise → two-pole LPF 500 Hz (kept above 300 Hz PostFade cutoff)
///   → [PreFade]  drivetrain whine: bandpass noise 350–900 Hz (dominant in-band character)
///   → [PreFade]  chassis clatter: noise → LPF 450 Hz (track/suspension impacts)
///   → [PreFade]  crew compartment coloration: LPF 2000 Hz (sealed metal hull)
///   → RF fading  (handled by RadioEffect)
///   → [PostFade] 300–2700 Hz brick-wall biquad bandpass
/// </summary>
internal sealed class GroundAmbientEffect : IAmbientNoiseEffect
{
    private readonly int _sampleRate;
    private readonly int _channels;

    // Biquad bandpass — post-fade, same receiver spec as Air
    private static readonly Dictionary<int, (float b0, float b1, float b2, float a1, float a2)> BiquadCache = new();
    private static readonly object BiquadCacheLock = new();
    private readonly float _b0, _b1, _b2, _a1, _a2;
    private readonly float[] _biquadState;

    // Diesel AM — firing frequency + hull resonance beating for organic lope.
    // Higher depths than jet: no acoustic isolation between engine and crew.
    private const float ThrumFreq1  = 45f;
    private const float ThrumFreq2  = 67f;
    private const float ThrumDepth1 = 0.25f;
    private const float ThrumDepth2 = 0.13f;
    private double _thrumPhase1;
    private double _thrumPhase2;

    // Engine roar — noise → two-pole LPF 500 Hz. Two poles for heavier diesel tilt;
    // 500 Hz cutoff ensures energy survives the PostFade 300 Hz high-pass.
    private const float RoarLevel = 0.040f;
    private readonly float _roarLpA;
    private float _roarLpState1;
    private float _roarLpState2;
    private uint  _roarNoiseState = 0xDEADBEEFu;

    // Drivetrain whine — bandpass noise 350–900 Hz via LP subtraction.
    // Gearbox/differential grind; sits squarely inside the radio passband.
    private const float DrivetrainLevel = 0.018f;
    private readonly float _driveHpA;  // LP 350 Hz (subtract to remove low end)
    private readonly float _driveLpA;  // LP 900 Hz
    private float _driveHpState;
    private float _driveLpState;
    private uint  _driveNoiseState = 0xFEDCBA98u;

    // Chassis clatter — noise → LP 450 Hz. Track/suspension impact noise.
    private const float ClatterLevel = 0.014f;
    private readonly float _clatterLpA;
    private float _clatterLpState;
    private uint  _clatterNoiseState = 0xCAFEBABEu;

    // Crew compartment coloration — LP 2000 Hz, boxy sealed-hull character
    private const float CompartmentCutoff = 2000f;
    private readonly float _compartmentA;
    private readonly float[] _compartmentLP;

    public GroundAmbientEffect(int sampleRate, int channels)
    {
        _sampleRate = sampleRate;
        _channels   = channels;

        _biquadState = new float[channels * 4];
        lock (BiquadCacheLock)
        {
            if (!BiquadCache.TryGetValue(sampleRate, out var c))
            {
                c = CalculateBiquadCoefficients(sampleRate);
                BiquadCache[sampleRate] = c;
            }
            (_b0, _b1, _b2, _a1, _a2) = c;
        }

        _roarLpA    = MathF.Exp(-2f * MathF.PI * 500f / sampleRate);
        _driveHpA   = MathF.Exp(-2f * MathF.PI * 350f / sampleRate);
        _driveLpA   = MathF.Exp(-2f * MathF.PI * 900f / sampleRate);
        _clatterLpA = MathF.Exp(-2f * MathF.PI * 450f / sampleRate);

        _compartmentA  = MathF.Exp(-2f * MathF.PI * CompartmentCutoff / sampleRate);
        _compartmentLP = new float[channels];
    }

    public void ApplyPreFade(float[] buffer, int offset, int frames)
    {
        double thrumInc1 = 2.0 * Math.PI * ThrumFreq1 / _sampleRate;
        double thrumInc2 = 2.0 * Math.PI * ThrumFreq2 / _sampleRate;

        for (int frame = 0; frame < frames; frame++)
        {
            // Diesel AM: two oscillators beating
            float thrumGain = 1f
                + (float)Math.Sin(_thrumPhase1) * ThrumDepth1
                + (float)Math.Sin(_thrumPhase2) * ThrumDepth2;
            _thrumPhase1 += thrumInc1;
            _thrumPhase2 += thrumInc2;
            if (_thrumPhase1 > Math.PI * 2) _thrumPhase1 -= Math.PI * 2;
            if (_thrumPhase2 > Math.PI * 2) _thrumPhase2 -= Math.PI * 2;

            // Engine roar: noise → two-pole LPF 500 Hz
            _roarNoiseState = _roarNoiseState * 1664525u + 1013904223u;
            float rawRoar   = (int)_roarNoiseState * (1f / 2147483648f);
            float roarLp1   = _roarLpA * _roarLpState1 + (1f - _roarLpA) * rawRoar;
            float roarLp2   = _roarLpA * _roarLpState2 + (1f - _roarLpA) * roarLp1;
            _roarLpState1   = roarLp1;
            _roarLpState2   = roarLp2;

            // Drivetrain whine: bandpass via LP900 − LP350
            _driveNoiseState = _driveNoiseState * 1664525u + 1013904223u;
            float rawDrive   = (int)_driveNoiseState * (1f / 2147483648f);
            float driveLp    = _driveLpA * _driveLpState + (1f - _driveLpA) * rawDrive;
            float driveHp    = _driveHpA * _driveHpState + (1f - _driveHpA) * rawDrive;
            _driveLpState    = driveLp;
            _driveHpState    = driveHp;

            // Chassis clatter: noise → LP 450 Hz
            _clatterNoiseState = _clatterNoiseState * 1664525u + 1013904223u;
            float clatterLp    = _clatterLpA * _clatterLpState + (1f - _clatterLpA) * ((int)_clatterNoiseState * (1f / 2147483648f));
            _clatterLpState    = clatterLp;

            for (int c = 0; c < _channels; c++)
            {
                int idx = offset + frame * _channels + c;
                float x = buffer[idx];

                x *= thrumGain;                         // engine vibration AM-modulates mic pickup
                x += roarLp2   * RoarLevel;             // acoustic engine roar
                x += (driveLp - driveHp) * DrivetrainLevel; // drivetrain/gearbox grind
                x += clatterLp * ClatterLevel;          // track and chassis clatter

                float lp = _compartmentA * _compartmentLP[c] + (1f - _compartmentA) * x;
                _compartmentLP[c] = lp;

                buffer[idx] = lp;
            }
        }
    }

    public void ApplyPostFade(float[] buffer, int offset, int frames)
    {
        for (int frame = 0; frame < frames; frame++)
        {
            for (int c = 0; c < _channels; c++)
            {
                int idx       = offset + frame * _channels + c;
                int stateBase = c * 4;

                float xn1 = _biquadState[stateBase];
                float xn2 = _biquadState[stateBase + 1];
                float yn1 = _biquadState[stateBase + 2];
                float yn2 = _biquadState[stateBase + 3];

                float x = buffer[idx];
                float y = _b0 * x + _b1 * xn1 + _b2 * xn2 - _a1 * yn1 - _a2 * yn2;

                _biquadState[stateBase + 1] = xn1;
                _biquadState[stateBase]     = x;
                _biquadState[stateBase + 3] = yn1;
                _biquadState[stateBase + 2] = y;

                buffer[idx] = y;
            }
        }
    }

    /// <summary>2nd-order Butterworth bandpass: 300–2700 Hz.</summary>
    private static (float b0, float b1, float b2, float a1, float a2) CalculateBiquadCoefficients(int sampleRate)
    {
        const float LowFreq  = 300f;
        const float HighFreq = 2700f;
        float centerFreq = (LowFreq + HighFreq) / 2f;
        float bandwidth  = HighFreq - LowFreq;
        float w0    = 2f * MathF.PI * centerFreq / sampleRate;
        float Q     = centerFreq / bandwidth;
        float alpha = MathF.Sin(w0) / (2f * Q);
        float cosw0 = MathF.Cos(w0);
        float b0 = alpha;  float b1 = 0f;  float b2 = -alpha;
        float a0 = 1f + alpha;  float a1 = -2f * cosw0;  float a2 = 1f - alpha;
        return (b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }
}

// ---------------------------------------------------------------------------
//  Stationary — ground station / AWACS / GCI bunker
// ---------------------------------------------------------------------------

/// <summary>
/// Signal chain:
///   clean PCM
///   → [PreFade]  mains hum: 50 Hz + 100 Hz (2nd) + 150 Hz (3rd), electrical bleed
///   → [PreFade]  HVAC turbulence: noise → LPF 220 Hz, slow duct-pressure AM (0.25 Hz)
///   → [PreFade]  electronics hiss: noise → bandpass 300–1500 Hz (rack equipment + fans)
///   → [PreFade]  room coloration: LPF 3500 Hz (open desk mic in enclosed ops room)
///   → RF fading  (handled by RadioEffect)
///   → [PostFade] 300–2700 Hz brick-wall biquad bandpass
/// </summary>
internal sealed class StationaryAmbientEffect : IAmbientNoiseEffect
{
    private readonly int _sampleRate;
    private readonly int _channels;

    // Biquad bandpass — post-fade, shared receiver spec with AirAmbientEffect
    private static readonly Dictionary<int, (float b0, float b1, float b2, float a1, float a2)> BiquadCache = new();
    private static readonly object BiquadCacheLock = new();
    private readonly float _b0, _b1, _b2, _a1, _a2;
    private readonly float[] _biquadState;

    // Mains hum — 50 Hz fundamental + 2nd harmonic (rectifier ripple) + 3rd (odd distortion)
    private const float MainsFreq        = 50f;
    private const float MainsFundamental = 0.004f;
    private const float MainsH2Level     = 0.003f; // 100 Hz
    private const float MainsH3Level     = 0.002f; // 150 Hz
    private double _mainsPhase;

    // HVAC — broadband turbulence noise, LPF 220 Hz + 0.25 Hz AM for duct pressure swells
    private const float HvacLevel       = 0.010f;
    private const float HvacDuctAMRate  = 0.25f;
    private const float HvacDuctAMDepth = 0.30f;
    private readonly float _hvacLpA;
    private float _hvacLpState;
    private uint  _hvacNoiseState = 0x13579BDFu;
    private double _hvacAMPhase;

    // Electronics hiss — rack equipment + fans, bandpass 300–1500 Hz via LP subtraction
    private const float HissLevel = 0.003f;
    private readonly float _hissLpHighA; // LP 1500 Hz
    private readonly float _hissLpLowA;  // LP 300 Hz (subtract to strip low end)
    private float _hissLpHighState;
    private float _hissLpLowState;
    private uint  _hissNoiseState = 0x2468ACEFu;

    // Room coloration — LP 3500 Hz, mild HF absorption of a furnished ops room
    private const float RoomCutoff = 3500f;
    private readonly float _roomA;
    private readonly float[] _roomLP;

    public StationaryAmbientEffect(int sampleRate, int channels)
    {
        _sampleRate = sampleRate;
        _channels   = channels;

        _biquadState = new float[channels * 4];
        lock (BiquadCacheLock)
        {
            if (!BiquadCache.TryGetValue(sampleRate, out var c))
            {
                c = CalculateBiquadCoefficients(sampleRate);
                BiquadCache[sampleRate] = c;
            }
            (_b0, _b1, _b2, _a1, _a2) = c;
        }

        _hvacLpA     = MathF.Exp(-2f * MathF.PI * 220f  / sampleRate);
        _hissLpHighA = MathF.Exp(-2f * MathF.PI * 1500f / sampleRate);
        _hissLpLowA  = MathF.Exp(-2f * MathF.PI * 300f  / sampleRate);
        _roomA       = MathF.Exp(-2f * MathF.PI * RoomCutoff / sampleRate);
        _roomLP      = new float[channels];
    }

    public void ApplyPreFade(float[] buffer, int offset, int frames)
    {
        double mainsInc  = 2.0 * Math.PI * MainsFreq      / _sampleRate;
        double hvacAMInc = 2.0 * Math.PI * HvacDuctAMRate / _sampleRate;

        for (int frame = 0; frame < frames; frame++)
        {
            // Mains hum: harmonics share the same phase reference
            float mainsSample = (float)Math.Sin(_mainsPhase)       * MainsFundamental
                              + (float)Math.Sin(_mainsPhase * 2.0) * MainsH2Level
                              + (float)Math.Sin(_mainsPhase * 3.0) * MainsH3Level;
            _mainsPhase += mainsInc;
            if (_mainsPhase > Math.PI * 2) _mainsPhase -= Math.PI * 2;

            // HVAC: noise → LP 220 Hz, slow AM for duct pressure fluctuations
            _hvacNoiseState = _hvacNoiseState * 1664525u + 1013904223u;
            float hvacLp    = _hvacLpA * _hvacLpState + (1f - _hvacLpA) * ((int)_hvacNoiseState * (1f / 2147483648f));
            _hvacLpState    = hvacLp;
            float hvacAM    = 1f + (float)Math.Sin(_hvacAMPhase) * HvacDuctAMDepth;
            _hvacAMPhase   += hvacAMInc;
            if (_hvacAMPhase > Math.PI * 2) _hvacAMPhase -= Math.PI * 2;

            // Electronics hiss: bandpass via LP1500 − LP300
            _hissNoiseState  = _hissNoiseState * 1664525u + 1013904223u;
            float rawHiss    = (int)_hissNoiseState * (1f / 2147483648f);
            float hissHigh   = _hissLpHighA * _hissLpHighState + (1f - _hissLpHighA) * rawHiss;
            float hissLow    = _hissLpLowA  * _hissLpLowState  + (1f - _hissLpLowA)  * rawHiss;
            _hissLpHighState = hissHigh;
            _hissLpLowState  = hissLow;

            for (int c = 0; c < _channels; c++)
            {
                int idx = offset + frame * _channels + c;
                float x = buffer[idx];

                x += mainsSample;
                x += hvacLp * HvacLevel * hvacAM;
                x += (hissHigh - hissLow) * HissLevel;

                float lp = _roomA * _roomLP[c] + (1f - _roomA) * x;
                _roomLP[c] = lp;

                buffer[idx] = lp;
            }
        }
    }

    public void ApplyPostFade(float[] buffer, int offset, int frames)
    {
        for (int frame = 0; frame < frames; frame++)
        {
            for (int c = 0; c < _channels; c++)
            {
                int idx       = offset + frame * _channels + c;
                int stateBase = c * 4;

                float xn1 = _biquadState[stateBase];
                float xn2 = _biquadState[stateBase + 1];
                float yn1 = _biquadState[stateBase + 2];
                float yn2 = _biquadState[stateBase + 3];

                float x = buffer[idx];
                float y = _b0 * x + _b1 * xn1 + _b2 * xn2 - _a1 * yn1 - _a2 * yn2;

                _biquadState[stateBase + 1] = xn1;
                _biquadState[stateBase]     = x;
                _biquadState[stateBase + 3] = yn1;
                _biquadState[stateBase + 2] = y;

                buffer[idx] = y;
            }
        }
    }

    /// <summary>2nd-order Butterworth bandpass: 300–2700 Hz.</summary>
    private static (float b0, float b1, float b2, float a1, float a2) CalculateBiquadCoefficients(int sampleRate)
    {
        const float LowFreq  = 300f;
        const float HighFreq = 2700f;
        float centerFreq = (LowFreq + HighFreq) / 2f;
        float bandwidth  = HighFreq - LowFreq;
        float w0    = 2f * MathF.PI * centerFreq / sampleRate;
        float Q     = centerFreq / bandwidth;
        float alpha = MathF.Sin(w0) / (2f * Q);
        float cosw0 = MathF.Cos(w0);
        float b0 = alpha;  float b1 = 0f;  float b2 = -alpha;
        float a0 = 1f + alpha;  float a1 = -2f * cosw0;  float a2 = 1f - alpha;
        return (b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }
}