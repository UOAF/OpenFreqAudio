namespace OpenFreqAudio;

/// <summary>
/// Preset configuration for different types of radio stations with realistic parameters
/// Platform altitude (MSL) is handled separately.
/// Band-specific characteristics (VHF/UHF) are handled via RadioType enum.
/// </summary>
public class RadioStationPreset
{
    public string? Name { get; set; }
    public string? Category { get; set; }
    public double AntennaElevation_m { get; set; }

    // VHF-specific parameters
    public double TxPower_VHF_W { get; set; }
    public double RxSensitivity_VHF_dBm { get; set; }

    // UHF-specific parameters
    public double TxPower_UHF_W { get; set; }
    public double RxSensitivity_UHF_dBm { get; set; }

    // Frequency stability (parts per million)
    public double MinPpm { get; set; }
    public double MaxPpm { get; set; }
    
    public double GetRandomPpm() => MinPpm + Random.Shared.NextDouble() * (MaxPpm - MinPpm);

    public string? Description { get; set; }
    
    public AmbientNoiseType AmbientNoiseType { get; set; }
    
    
    
    public RadioStationPreset()
    { }

    public RadioStationPreset(string name, string category, double antennaElevation,
        double txPowerVhf, double rxSensitivityVhf,
        double txPowerUhf, double rxSensitivityUhf,
        double minPpm, double maxPpm,
        AmbientNoiseType ambientNoiseType,
        string description)
    {
        Name = name;
        Category = category;
        AntennaElevation_m = antennaElevation;
        TxPower_VHF_W = txPowerVhf;
        RxSensitivity_VHF_dBm = rxSensitivityVhf;
        TxPower_UHF_W = txPowerUhf;
        RxSensitivity_UHF_dBm = rxSensitivityUhf;
        MinPpm = minPpm;
        MaxPpm = maxPpm;
        AmbientNoiseType = ambientNoiseType;
        Description = description;
    }
    
    public override bool Equals(object? obj)
    {
        return obj is RadioStationPreset other && Name == other.Name;
    }

    public override int GetHashCode() => Name?.GetHashCode() ?? 0;

    /// <summary>
    /// Get TX power for the specified radio type
    /// </summary>
    public double GetTxPower(BackgroundNoiseGenerator.RadioType radioType)
    {
        return radioType switch
        {
            BackgroundNoiseGenerator.RadioType.VHF_AM => TxPower_VHF_W,
            BackgroundNoiseGenerator.RadioType.UHF_AM => TxPower_UHF_W,
            BackgroundNoiseGenerator.RadioType.UHF_FM => TxPower_UHF_W * 1.5, // FM typically uses more power
            _ => TxPower_VHF_W
        };
    }

    /// <summary>
    /// Get RX sensitivity for the specified radio type
    /// </summary>
    public double GetRxSensitivity(BackgroundNoiseGenerator.RadioType radioType)
    {
        return radioType switch
        {
            BackgroundNoiseGenerator.RadioType.VHF_AM => RxSensitivity_VHF_dBm,
            BackgroundNoiseGenerator.RadioType.UHF_AM => RxSensitivity_UHF_dBm,
            BackgroundNoiseGenerator.RadioType.UHF_FM => RxSensitivity_UHF_dBm + 3.0, // FM has worse sensitivity (less negative)
            _ => RxSensitivity_VHF_dBm
        };
    }
    
    public static bool IsVHF(int frequencyKhz)
    {
        return frequencyKhz < 200000; // 200 MHz = 200000 kHz
    }
}

public static class RadioStationPresets
{
    // ================================================================
    // AIRBORNE PLATFORMS
    // ================================================================

    public static readonly RadioStationPreset AWACS = new(
        name: "AWACS",
        category: "Airborne",
        antennaElevation: 3.0,
        txPowerVhf: 50.0, // High power VHF for long-range C2
        rxSensitivityVhf: -110.0,
        txPowerUhf: 25.0, // Lower UHF power
        rxSensitivityUhf: -107.0,
        minPpm: 0.1, // Exceptional stability - military OCXO
        maxPpm: 0.5,
        ambientNoiseType: AmbientNoiseType.Stationary, // We dont want the engine whine there, stationary sounds better for that case
        description: "E-3 Sentry / E-2 Hawkeye - Airborne Early Warning & Control"
    );

    public static readonly RadioStationPreset FighterF16 = new(
        name: "F-16 Fighting Falcon",
        category: "Airborne",
        antennaElevation: 2.0,
        txPowerVhf: 10.0, // AN/ARC-210: 10W VHF
        rxSensitivityVhf: -110.0,
        txPowerUhf: 10.0, // AN/ARC-210: 10W UHF AM (20W FM handled in GetTxPower)
        rxSensitivityUhf: -107.0,
        minPpm: 0.1, // Modern OCXO in AN/ARC-210
        maxPpm: 0.5,
        ambientNoiseType: AmbientNoiseType.AirF16,
        description: "F-16C/D with AN/ARC-210 multiband radio"
    );

    public static readonly RadioStationPreset FighterF15 = new(
        name: "F-15 Eagle",
        category: "Airborne",
        antennaElevation: 2.5,
        txPowerVhf: 10.0, // AN/ARC-186: 10W VHF AM/FM, 30–174.975 MHz
        rxSensitivityVhf: -110.0,
        txPowerUhf: 10.0, // AN/ARC-164: 10W UHF AM, 225–399.975 MHz
        rxSensitivityUhf: -107.0,
        minPpm: 0.5, // Crystal oscillator in ARC-164, less stable than ARC-210 OCXO
        maxPpm: 2.0,
        ambientNoiseType: AmbientNoiseType.AirF15,
        description: "F-15C/D/E with AN/ARC-164 UHF and AN/ARC-186 VHF radios"
    );

    public static readonly RadioStationPreset FighterGeneric = new(
        name: "Fighter Aircraft",
        category: "Airborne",
        antennaElevation: 2.0,
        txPowerVhf: 10.0,
        rxSensitivityVhf: -108.0,
        txPowerUhf: 10.0,
        rxSensitivityUhf: -105.0,
        minPpm: 0.5,
        maxPpm: 2.0,
        ambientNoiseType: AmbientNoiseType.AirGeneric,
        description: "Generic tactical fighter with standard military radios"
    );

    public static readonly RadioStationPreset Tanker = new(
        name: "Tanker Aircraft",
        category: "Airborne",
        antennaElevation: 2.5,
        txPowerVhf: 10.0,
        rxSensitivityVhf: -110.0,
        txPowerUhf: 10.0,
        rxSensitivityUhf: -107.0,
        minPpm: 0.5, // Good quality TCXO
        maxPpm: 2.0,
        ambientNoiseType: AmbientNoiseType.AirGeneric,
        description: "KC-135/KC-10 aerial refueling aircraft"
    );

    public static readonly RadioStationPreset Transport = new(
        name: "Transport Aircraft",
        category: "Airborne",
        antennaElevation: 2.5,
        txPowerVhf: 10.0,
        rxSensitivityVhf: -107.0,
        txPowerUhf: 10.0,
        rxSensitivityUhf: -105.0,
        minPpm: 0.5, // Standard military TCXO
        maxPpm: 2.0,
        ambientNoiseType: AmbientNoiseType.AirGeneric,
        description: "C-130/C-17 military transport aircraft"
    );

    public static readonly RadioStationPreset Helicopter = new(
        name: "Helicopter",
        category: "Airborne",
        antennaElevation: 1.5,
        txPowerVhf: 10.0,
        rxSensitivityVhf: -107.0,
        txPowerUhf: 10.0,
        rxSensitivityUhf: -105.0,
        minPpm: 1.0, // More vibration, temperature variation
        maxPpm: 3.0,
        ambientNoiseType: AmbientNoiseType.AirGeneric,
        description: "AH-64/UH-60 attack and utility helicopters"
    );

    // ================================================================
    // GROUND MILITARY
    // ================================================================

    public static readonly RadioStationPreset GCI_LowTower = new(
        name: "GCI Station (Low Tower)",
        category: "Ground Military",
        antennaElevation: 50.0,
        txPowerVhf: 150.0, // High power VHF
        rxSensitivityVhf: -118.0, 
        txPowerUhf: 100.0, // Lower UHF power
        rxSensitivityUhf: -115.0,
        minPpm: 0.5, // Fixed installation, climate controlled
        maxPpm: 2.0,
        ambientNoiseType: AmbientNoiseType.Stationary,
        description: "Ground Control Intercept - 50m tower"
    );

    public static readonly RadioStationPreset GCI_HighTower = new(
        name: "GCI Station (High Tower)",
        category: "Ground Military",
        antennaElevation: 100.0,
        txPowerVhf: 150.0,
        rxSensitivityVhf: -118.0,
        txPowerUhf: 100.0,
        rxSensitivityUhf: -115.0,
        minPpm: 0.1, // Best ground infrastructure, OCXO
        maxPpm: 0.5,
        ambientNoiseType: AmbientNoiseType.Stationary,
        description: "Ground Control Intercept - 100m tower"
    );

    public static readonly RadioStationPreset FACC_Standard = new(
        name: "FACC",
        category: "Ground Military",
        antennaElevation: 10.0,
        txPowerVhf: 50.0, // Medium power VHF
        rxSensitivityVhf: -113.0,
        txPowerUhf: 30.0, // Medium power UHF
        rxSensitivityUhf: -110.0,
        minPpm: 1.0, // Field-deployable, less stable than fixed
        maxPpm: 4.0,
        ambientNoiseType: AmbientNoiseType.Stationary,
        description: "Forward Air Control Center - Standard mast"
    );

    public static readonly RadioStationPreset FACC_Extended = new(
        name: "FACC (Extended Mast)",
        category: "Ground Military",
        antennaElevation: 20.0,
        txPowerVhf: 50.0,
        rxSensitivityVhf: -113.0,
        txPowerUhf: 30.0,
        rxSensitivityUhf: -110.0,
        minPpm: 0.5, // Better equipment for extended ops
        maxPpm: 2.0,
        ambientNoiseType: AmbientNoiseType.Stationary,
        description: "Forward Air Control Center - Extended telescoping mast"
    );

    public static readonly RadioStationPreset GroundFAC = new(
        name: "Ground FAC",
        category: "Ground Military",
        antennaElevation: 2.0,
        txPowerVhf: 5.0, // Handheld low power
        rxSensitivityVhf: -113.0, // PRC-152
        txPowerUhf: 5.0,
        rxSensitivityUhf: -110.0,
        minPpm: 2.0, // Handheld, temperature extremes, battery variation
        maxPpm: 10.0,
        ambientNoiseType: AmbientNoiseType.Ground, // Make it a bit more messy
        description: "Forward Air Controller with handheld radio"
    );

    public static readonly RadioStationPreset JTAC = new(
        name: "JTAC",
        category: "Ground Military",
        antennaElevation: 2.0,
        txPowerVhf: 5.0,
        rxSensitivityVhf: -113.0,
        txPowerUhf: 5.0,
        rxSensitivityUhf: -110.0,
        minPpm: 2.0, // Field tactical radio, environmental stress
        maxPpm: 10.0,
        ambientNoiseType: AmbientNoiseType.Ground, // Make it a bit more messy
        description: "Joint Terminal Attack Controller with tactical radio"
    );

    public static readonly RadioStationPreset TacticalVehicle = new(
        name: "Tactical Vehicle",
        category: "Ground Military",
        antennaElevation: 3.5,
        txPowerVhf: 20.0, // Vehicle-mounted medium power
        rxSensitivityVhf: -113.0,
        txPowerUhf: 20.0,
        rxSensitivityUhf: -110.0,
        minPpm: 1.0, // Vehicle-mounted, some vibration
        maxPpm: 5.0,
        ambientNoiseType: AmbientNoiseType.Ground,
        description: "Military vehicle with mounted tactical radio"
    );

    // ================================================================
    // CIVILIAN
    // ================================================================

    public static readonly RadioStationPreset ATC_Small = new(
        name: "ATC Tower (Small)",
        category: "Civilian",
        antennaElevation: 30.0,
        txPowerVhf: 25.0, // Standard civilian VHF
        rxSensitivityVhf: -100.0,
        txPowerUhf: 25.0,
        rxSensitivityUhf: -97.0,
        minPpm: 2.0, // Budget equipment, standard TCXO
        maxPpm: 5.0,
        ambientNoiseType: AmbientNoiseType.Stationary,
        description: "Small airport control tower"
    );

    public static readonly RadioStationPreset ATC_Major = new(
        name: "ATC Tower (Major)",
        category: "Civilian",
        antennaElevation: 100.0,
        txPowerVhf: 25.0,
        rxSensitivityVhf: -100.0,
        txPowerUhf: 25.0,
        rxSensitivityUhf: -97.0,
        minPpm: 1.0, // Well-maintained, quality equipment
        maxPpm: 3.0,
        ambientNoiseType: AmbientNoiseType.Stationary,
        description: "Major international airport control tower"
    );

    public static readonly RadioStationPreset UNICOM = new(
        name: "UNICOM",
        category: "Civilian",
        antennaElevation: 15.0,
        txPowerVhf: 10.0, // Low power
        rxSensitivityVhf: -95.0, // Basic receiver
        txPowerUhf: 10.0,
        rxSensitivityUhf: -92.0,
        minPpm: 5.0, // Basic/older equipment, minimal maintenance
        maxPpm: 15.0,
        ambientNoiseType: AmbientNoiseType.Stationary,
        description: "Uncontrolled airport common traffic advisory frequency"
    );

    public static readonly RadioStationPreset FlightService = new(
        name: "Flight Service Station",
        category: "Civilian",
        antennaElevation: 40.0,
        txPowerVhf: 50.0,
        rxSensitivityVhf: -100.0,
        txPowerUhf: 50.0,
        rxSensitivityUhf: -97.0,
        minPpm: 1.0, // Professional service, maintained equipment
        maxPpm: 4.0,
        ambientNoiseType: AmbientNoiseType.Stationary,
        description: "FSS providing weather briefings and flight plan services"
    );

    // ================================================================
    // UTILITY METHODS
    // ================================================================

    /// <summary>
    /// Get all available presets
    /// </summary>
    public static IEnumerable<RadioStationPreset> GetAllPresets()
    {
        return new[]
        {
            // Airborne
            AWACS, FighterF16, FighterF15, FighterGeneric, Tanker, Transport, Helicopter,
            // Ground Military
            GCI_LowTower, GCI_HighTower,
            FACC_Standard, FACC_Extended,
            GroundFAC, JTAC, TacticalVehicle,
            // Civilian
            ATC_Small, ATC_Major, UNICOM, FlightService
        };
    }

    // For UI bindings
    public static readonly IEnumerable<RadioStationPreset> AllPresets =
    [
        // Airborne
        AWACS, FighterF16, FighterF15, FighterGeneric, Tanker, Transport, Helicopter,
        // Ground Military
        GCI_LowTower, GCI_HighTower,
        FACC_Standard, FACC_Extended,
        GroundFAC, JTAC, TacticalVehicle,
        // Civilian
        ATC_Small, ATC_Major, UNICOM, FlightService
    ];

    /// <summary>
    /// Get presets filtered by category
    /// </summary>
    public static IEnumerable<RadioStationPreset> GetPresetsByCategory(string category)
    {
        return GetAllPresets().Where(p => p.Category == category);
    }

    /// <summary>
    /// Get all unique categories
    /// </summary>
    public static IOrderedEnumerable<string?> GetCategories()
    {
        return GetAllPresets().Select(p => p.Category).Distinct().OrderBy(c => c);
    }

    /// <summary>
    /// Find a preset by name (case-insensitive)
    /// </summary>
    public static RadioStationPreset? FindByName(string name)
    {
        return GetAllPresets().FirstOrDefault(p =>
            p.Name != null && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }
    
    public static RadioStationPreset GetPresetByBmsAircraftNctr(string? aircraftNctr)
    {
        return aircraftNctr switch
        {
            "F16" => RadioStationPresets.FighterF16,
            "F15" => RadioStationPresets.FighterF15,
            _ => RadioStationPresets.FighterGeneric
        };
    }
}