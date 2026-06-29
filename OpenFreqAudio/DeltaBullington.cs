namespace OpenFreqAudio;

/// <summary>
/// Delta-Bullington irregular-terrain diffraction loss
///
/// Taken from ITU-R P.526 and the US Army Corps of Engineers
/// Engineer Research and Development Center/Cold Regions Research and Engineering Laboratory
/// (ERDC/CRREL) TR-22-1,
/// "A Study on the Delta-Bullington Irregular Terrain RF Propagation Model" (Breton, 2022)
///
/// Instead of assuming a single "knife-edge" peak in terrain, which would underestimate
/// losses over rolling terrain or over-the-horizon comms, calculate an *equivalent*
/// knife-edge where the steepest TX ray meets the steepest RX ray,
/// then compare that to the "knife-edge" of the curvature of a smooth Earth.
/// The difference, or delta, gives us a decent approximation of overall loss.
/// </summary>
internal static class DeltaBullington
{
    private const double SpeedOfLight = 299792458.0; // m/s

    /// <summary>Total diffraction loss (dB, ≥ 0) for a sampled terrain profile.</summary>
    /// <param name="profile">(dist meters from TX, ground elevation m ASL), ascending.</param>
    /// <param name="htsM">TX antenna height, m ASL.</param>
    /// <param name="hrsM">RX antenna height, m ASL.</param>
    /// <param name="wavelength">wavelength, meters.</param>
    /// <param name="rEffM">effective Earth radius (based on diffraction), meters.</param>
    public static double Loss(
        List<(double dist, double elev)> profile, double htsM, double hrsM,
        double wavelength, double rEffM)
    {
        int n = profile.Count;
        // If there's no terrain in the way, assume no losses from terrain!
        if (n < 3) return 0.0;

        double dpKm = profile[n - 1].dist / 1000.0;
        if (dpKm <= 0.0) return 0.0;

        double aeKm = rEffM / 1000.0;
        double ce = 1.0 / aeKm; // Earth curvature

        // Lba: Bullington over the actual terrain profile,
        //      finding its "equivalent knife-edge" loss.
        double lba = Bullington(profile, flat: false, htsM, hrsM, wavelength, ce);

        // Equivalent smooth-earth surface heights under TX/RX (least-squares fit).
        SmoothSurface(profile, dpKm, out double hst, out double hsr);

        // Highest obstruction above the straight antenna line,
        // and the TX/RX horizon angles (ERDC §6.2).
        double hObs = double.NegativeInfinity;
        double aObt = double.NegativeInfinity, aObr = double.NegativeInfinity;
        foreach (var (dist, elev) in profile)
        {
            double di = dist / 1000.0;
            // Skip points coincident with an endpoint (incl. the TX/RX endpoints).
            // They would make the horizon angles infinite and aObt/(aObt+aObr) = ∞/∞ = NaN.
            if (di <= 0.0 || di >= dpKm) continue;
            // How far this terrain point pokes above the straight line between TX -> RX
            double hi = elev - (htsM * (dpKm - di) + hrsM * di) / dpKm;
            // hObs gives our highest obstruction
            if (hi > hObs) hObs = hi;
            // aObt gives our TX horizon angle - the steepest hi/di slope
            if (hi / di > aObt) aObt = hi / di;
            // aObr gives our RX horizon angle
            if (hi / (dpKm - di) > aObr) aObr = hi / (dpKm - di);
        }

        // ITU-R P.452-18 p. 51 - "diffraction-corrected smooth heights"
        // When there's an obstruction, the smooth-earth surface is pulled down.
        // i.e., a hill close to the transmitter makes the transmitter's effective height *higher*.
        // This isn't saying anything about RF physics; it's un-biasing the smooth earth model
        // because we expect nearby terrain to be handled by Lba, the pass that actually samples terrain.
        // We're just keeping nearby obstructions from being "double-counted" in the delta
        // between smooth-earth and terrain calcs.
        double hstd = 0.0;
        double hsrd = 0.0;
        if (hObs <= 0.0 || aObt + aObr <= 0.0)
        {
            hstd = hst;
            hsrd = hsr;
        }
        else
        {
            double hstp = hst - hObs * (aObt / (aObt + aObr));
            double hsrp = hsr - hObs * (aObr / (aObt + aObr));
            hstd = hstp > profile[0].elev ? profile[0].elev : hstp;
            hsrd = hsrp > profile[n - 1].elev ? profile[n - 1].elev : hsrp;
        }

        // antenna heights above the smooth earth (>= AGL >= 0)
        double htsEff = htsM - hstd;
        double hrsEff = hrsM - hsrd;

        // Lbs: Bullington over a zero-height (smooth) profile with the effective heights.
        // flat:true substitutes h = 0 per point, but still uses their distance from the TX.
        // This calculates the "knife-edge" of the earth curvature.
        double lbs = Bullington(profile, flat: true, htsEff, hrsEff, wavelength, ce);

        // Lsph: smooth/spherical-earth diffraction over the same distance.
        // Is usually greater than Lbs because moving around the sphere sheds energy continuously,
        // as opposed to a knife-edge approximation of the curvature.
        // (Knife-edge losses are always *minimum* since they only obstruct at one distance.)
        double lsph = SphericalEarthDiffraction(dpKm * 1000.0, htsEff, hrsEff, wavelength, rEffM);

        // The delta in delta-Bullington:
        // Because of the differences described above,
        // Lsph - Lbs is the systemic error of Bullington on a smooth earth,
        // so *add* that to our terrain loss estimate.
        // (Put another way: subtracting Lbs from Lsph removes the smooth-earth diffraction
        // in Lba to keep us from double-counting it.)
        return Math.Max(lba + Math.Max(lsph - lbs, 0.0), 0.0);
    }

    /// <summary>
    /// Bullington single-equivalent-knife-edge diffraction loss (dB). Reads distances
    /// (m, converted to km) and elevations from <paramref name="prof"/>; pass
    /// <paramref name="flat"/> = true to treat the terrain as a zero-height smooth profile..
    /// hts/hrs in m ASL, lambda in m, ce = 1/ae in 1/km.
    /// Endpoints (and any point coincident with one) fall out via the di guards.
    /// </summary>
    private static double Bullington(
        List<(double dist, double elev)> prof, bool flat,
        double hts, double hrs, double lambda, double ce)
    {
        double dp = prof[^1].dist / 1000.0;

        // Stm is the max slope from TX to any terrain point
        // (including the curvature of the earth through s below).
        double stm = double.NegativeInfinity;
        foreach (var (dist, elev) in prof)
        {
            double di = dist / 1000.0;
            if (di <= 0.0 || di >= dp) continue;
            double hi = flat ? 0.0 : elev;
            // 500*ce*d*(dp-d) is the smooth-earth bulge in meters.
            double s = (hi + 500.0 * ce * di * (dp - di) - hts) / di;
            if (s > stm) stm = s;
        }
        // Str is the slope of the line from TX -> RX
        double str = (hrs - hts) / dp;

        // LOS test: If no TX -> terrain is steeper than TX -> RX,
        // the path is line-of-sight.
        double luc;
        if (stm < str)
        {
            // Even if we are line-of-sight, terrain can poke into our Fresnel zone
            // and add diffraction losses. For each point,
            double vmax = double.NegativeInfinity;
            foreach (var (dist, elev) in prof)
            {
                double di = dist / 1000.0;
                if (di <= 0.0 || di >= dp) continue;
                double hi = flat ? 0.0 : elev;
                // Terrain height (with bulge) - chord height.
                // (How far does terrain stick into (+) or under (-) the ray?)
                double excess = hi + 500.0 * ce * di * (dp - di)
                                - (hts * (dp - di) + hrs * di) / dp;
                // Fresnel-Kirchoff diffraction parameter (P.526 equation 26).
                // The denominator normalizes the clearance by a Fresnel zone radius.
                // 0.002 takes us from km -> m (times 2 for 2d / lambda)
                double v = excess * Math.Sqrt(0.002 * dp / (lambda * di * (dp - di)));
                if (v > vmax) vmax = v;
            }
            luc = FastPathAudioSim.KnifeEdgeLoss_dB(vmax);
        }
        else
        {
            // Srm is the highest RX -> terrain line.
            // It meets Stm somewhere to form our knife-edge.
            double srm = double.NegativeInfinity;
            foreach (var (dist, elev) in prof)
            {
                double di = dist / 1000.0;
                if (di <= 0.0 || di >= dp) continue;
                double hi = flat ? 0.0 : elev;
                double s = (hi + 500.0 * ce * di * (dp - di) - hrs) / (dp - di);
                if (s > srm) srm = s;
            }
            // Distance where Stm and Srm intersect
            double db = (hrs - hts + srm * dp) / (stm + srm);
            // Avoid a divide by zero below, clamp to [epsilon, Dp - epsilon]
            db = Math.Clamp(db, 1e-9, dp - 1e-9);
            double excess = hts + stm * db - (hts * (dp - db) + hrs * db) / dp;
            double vb = excess * Math.Sqrt(0.002 * dp / (lambda * db * (dp - db)));
            luc = FastPathAudioSim.KnifeEdgeLoss_dB(vb);
        }

        // Empirical correction (ERDC §5.5).
        // Bullington underestimates losses because terrain isn't a single clean knife-edge.
        return luc + (1.0 - Math.Exp(-luc / 6.0)) * (10.0 + 0.02 * dp);
    }

    /// <summary>
    /// Least-squares smooth-earth surface heights (m ASL) under TX (hst) and RX (hsr).
    /// Distances read from <paramref name="prof"/> (m → km), heights m ASL (ERDC §6.2).
    /// Indexed (not foreach) because each term spans an adjacent pair of points.
    /// </summary>
    private static void SmoothSurface(
        List<(double dist, double elev)> prof, double dp, out double hst, out double hsr)
    {
        double v1 = 0.0, v2 = 0.0;
        for (int i = 1; i < prof.Count; i++)
        {
            double di = prof[i].dist / 1000.0, dim1 = prof[i - 1].dist / 1000.0;
            double hi = prof[i].elev, him1 = prof[i - 1].elev;
            double dd = di - dim1;
            v1 += dd * (hi + him1);
            v2 += dd * (hi * (2.0 * di + dim1) + him1 * (di + 2.0 * dim1));
        }
        hst = (2.0 * v1 * dp - v2) / (dp * dp);
        hsr = (v2 - v1 * dp) / (dp * dp);
    }

    /// <summary>
    /// Smooth/spherical-earth diffraction loss (dB, ≥ 0). ITU-R P.526-16 §3.2 (any
    /// distance, ≥ 10 MHz) calling the §3.1.1 first-term residue. All inputs in meters.
    /// </summary>
    private static double SphericalEarthDiffraction(
        double dM, double h1M, double h2M, double wavelength, double aeM)
    {
        // The residue formulae need positive antenna heights above the smooth earth.
        double h1 = Math.Max(h1M, 1.0);
        double h2 = Math.Max(h2M, 1.0);

        // Smooth earth horizon distance - at what distance
        // is LOS blocked by the curvature of the Earth?
        double dlos = Math.Sqrt(2.0 * aeM) * (Math.Sqrt(h1) + Math.Sqrt(h2));
        double fMHz = (SpeedOfLight / wavelength) / 1e6;

        if (dM >= dlos)
            return FirstTermResidue(dM / 1000.0, h1, h2, fMHz, aeM / 1000.0);

        // Within-horizon: interpolate between the smooth-earth diffraction loss (using a
        // notional effective radius aem) and zero, via the clearance ratio (§3.2 eqs 22-25).
        double c = (h1 - h2) / (h1 + h2);
        double m = dM * dM / (4.0 * aeM * (h1 + h2));
        double acosArg = Math.Clamp((3.0 * c / 2.0) * Math.Sqrt(3.0 * m / Math.Pow(m + 1.0, 3.0)), -1.0, 1.0);
        double b = 2.0 * Math.Sqrt((m + 1.0) / (3.0 * m)) *
                    Math.Cos(Math.PI / 3.0 + (1.0 / 3.0) * Math.Acos(acosArg));
        double d1 = (dM / 2.0) * (1.0 + b);
        if (d1 <= 0.0 || d1 >= dM) return 0.0;
        double d2 = dM - d1;

        double hClear = ((h1 - d1 * d1 / (2.0 * aeM)) * d2 + (h2 - d2 * d2 / (2.0 * aeM)) * d1) / dM;
        double hReq = 0.552 * Math.Sqrt(d1 * d2 * wavelength / dM);
        if (hClear > hReq) return 0.0;

        double aem = 0.5 * Math.Pow(dM / (Math.Sqrt(h1) + Math.Sqrt(h2)), 2.0);
        double ah = FirstTermResidue(dM / 1000.0, h1, h2, fMHz, aem / 1000.0);
        if (ah < 0.0) return 0.0;
        return (1.0 - hClear / hReq) * ah;
    }

    /// <summary>
    /// First-term residue of the spherical-earth diffraction series (ITU-R P.526-16
    /// §3.1.1, eqs 13-18), returned as loss (dB). Practical units: d/ae in km, h in m,
    /// f in MHz. beta = 1.
    /// </summary>
    private static double FirstTermResidue(double dKm, double h1, double h2, double fMHz, double aeKm)
    {
        const double beta = 1.0;
        double x = 2.188 * beta * Math.Cbrt(fMHz) * Math.Pow(aeKm, -2.0 / 3.0) * dKm;

        double fx = x >= 1.6
            ? 11.0 + 10.0 * Math.Log10(x) - 17.6 * x
            : -20.0 * Math.Log10(x) - 5.6488 * Math.Pow(x, 1.425);

        double field = fx + HeightGain(h1, fMHz, aeKm, beta) + HeightGain(h2, fMHz, aeKm, beta);
        return -field; // loss = -(20 log10 E/E0), generally positive
    }

    private static double HeightGain(double hM, double fMHz, double aeKm, double beta)
    {
        double y = 9.575e-3 * beta * Math.Pow(fMHz, 2.0 / 3.0) * Math.Pow(aeKm, -1.0 / 3.0) * hM;
        double bb = beta * y;
        // The 2+20log10(K) floor uses the surface-admittance factor K, negligible for
        // beta = 1, so it never binds and is omitted.
        return bb > 2.0
            ? 17.6 * Math.Sqrt(bb - 1.1) - 5.0 * Math.Log10(bb - 1.1) - 8.0
            : 20.0 * Math.Log10(bb + 0.1 * bb * bb * bb);
    }
}