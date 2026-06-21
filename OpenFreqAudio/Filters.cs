namespace OpenFreqAudio;

/// <summary>
/// A first-order envelope follower with separate attack and decay time constants,
/// so it can track upward and downward at different rates.
/// AKA EnvelopeFollower in NWaves, but without a dumb private delay tap.
/// </summary>
public class AttackDecayFilter
{
    private float Attack;
    private float Decay;

    /// <summary>
    /// The delay tap - i.e. the current value of the filter
    /// </summary>
    public float D1;

    public AttackDecayFilter(float a, float d, float init)
    {
        Attack = a;
        Decay = d;
        D1 = init;
    }

    public float Apply(float x)
    {
        float alpha = x > D1 ? Attack : Decay;
        D1 = alpha * x + (1 - alpha) * D1;
        return D1;
    }

    /// <summary>
    /// Create an attack/decay filter from attack and decay time constants (seconds).
    /// </summary>
    /// <returns>The filter - not lifted into a closure so that you can query the previous value</returns>
    public static AttackDecayFilter MakeAttackDecayFilter(double attackTau, double decayTau, double sampleRate)
    {
        double attackApha = 1 - Math.Exp(-1 / (sampleRate * attackTau));
        double decayAlpha = 1 - Math.Exp(-1 / (sampleRate * decayTau));
        // Assume we're using this for an AGC or something similar where the initial gain should be 1.
        return new AttackDecayFilter((float)attackApha, (float)decayAlpha, 1.0f);
    }
}

/// <summary>
/// A first-order (single-pole, one-tap IIR) exponential smoother with a single time constant,
/// so it smooths symmetrically. See <see cref="AttackDecayFilter"/> for the asymmetric variant.
///
/// Feed it x*x and sqrt the output to track an RMS envelope, or feed it a control signal
/// (e.g. a gain target) to ramp it slowly.
/// </summary>
public class FirstOrderFilter
{
    private readonly float _alpha;

    /// <summary>
    /// The delay tap - i.e. the current value of the filter.
    /// </summary>
    public float D1;

    public FirstOrderFilter(float alpha, float init)
    {
        _alpha = alpha;
        D1 = init;
    }

    public float Apply(float x)
    {
        D1 = _alpha * x + (1 - _alpha) * D1;
        return D1;
    }

    /// <summary>
    /// Create a first-order filter from a time constant (seconds).
    /// </summary>
    public static FirstOrderFilter MakeFirstOrderFilter(double tau, double sampleRate, double init)
    {
        double alpha = 1 - Math.Exp(-1 / (sampleRate * tau));
        return new FirstOrderFilter((float)alpha, (float)init);
    }
}
