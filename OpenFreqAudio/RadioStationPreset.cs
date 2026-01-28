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

    public string? Description { get; set; }
    
    
    
    public RadioStationPreset()
    { }

    public RadioStationPreset(string name, string category, double antennaElevation,
        double txPowerVhf, double rxSensitivityVhf,
        double txPowerUhf, double rxSensitivityUhf,
        string description)
    {
        Name = name;
        Category = category;
        AntennaElevation_m = antennaElevation;
        TxPower_VHF_W = txPowerVhf;
        RxSensitivity_VHF_dBm = rxSensitivityVhf;
        TxPower_UHF_W = txPowerUhf;
        RxSensitivity_UHF_dBm = rxSensitivityUhf;
        Description = description;
    }
    
    public override bool Equals(object? obj)
    {
        return obj is RadioStationPreset other && Name == other.Name;
    }

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
        return frequencyKhz < 200000;
  // 200 MHz = 200000 kHz
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
        description: "E-3 Sentry / E-2 Hawkeye - Airborne Early Warning & Control"
    );

    public static readonly RadioStationPreset Fighter = new(
        name: "Fighter Aircraft",
        category: "Airborne",
        antennaElevation: 2.0,
        txPowerVhf: 10.0, // AN/ARC-210: 10W VHF
        rxSensitivityVhf: -110.0,
        txPowerUhf: 10.0, // AN/ARC-210: 10W UHF AM (20W FM handled in GetTxPower)
        rxSensitivityUhf: -107.0,
        description: "F-16/F-15/F/A-18 with AN/ARC-210/220 radios"
    );

    public static readonly RadioStationPreset Tanker = new(
        name: "Tanker Aircraft",
        category: "Airborne",
        antennaElevation: 2.5,
        txPowerVhf: 10.0,
        rxSensitivityVhf: -110.0,
        txPowerUhf: 10.0,
        rxSensitivityUhf: -107.0,
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
            AWACS, Fighter, Tanker, Transport, Helicopter,
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
        AWACS, Fighter, Tanker, Transport, Helicopter,
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
    public static IEnumerable<string> GetCategories()
    {
        return GetAllPresets().Select(p => p.Category).Distinct().OrderBy(c => c);
    }

    /// <summary>
    /// Find a preset by name (case-insensitive)
    /// </summary>
    public static RadioStationPreset? FindByName(string name)
    {
        return GetAllPresets().FirstOrDefault(p =>
            p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }
}