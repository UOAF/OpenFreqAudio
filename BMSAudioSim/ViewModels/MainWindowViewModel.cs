using System;
using System.Collections.Generic;
using System.Linq;
using OpenFreqAudio;

namespace BMSAudioSim.ViewModels;

using ReactiveUI;

public class MainWindowViewModel : ReactiveObject
{
    private int _tx1Altitude = 0;
    private int _tx2Altitude = 0;
    private int _rxAltitude = 0;

    private int _txWatts = 40;
    private int _rxDbm = -105;

    private int _frequencyKhz = 513750; // default UHF

    private bool _signal1Continuous = false;
    private bool _signal2Continuous = false;
    private int _squelchSliderValue = 10;
    private float _squelch = 1.0f;
    private bool _enable3dEffects = true;
    private float _ppm1 = 0;
    private float _ppm2 = 0;

    private AmbientNoiseType _ambientNoiseType = AmbientNoiseType.None;
    public IEnumerable<AmbientNoiseType> AmbientNoiseTypes { get; } =
        Enum.GetValues(typeof(AmbientNoiseType))
            .Cast<AmbientNoiseType>();

    public AmbientNoiseType AmbientNoiseType
    {
        get => _ambientNoiseType;
        set => this.RaiseAndSetIfChanged(ref _ambientNoiseType, value);
    }

    public float Squelch
    {
        get => _squelch;
        private set => this.RaiseAndSetIfChanged(ref _squelch, value);
    }

    public int SquelchSliderValue
    {
        get => _squelchSliderValue;
        set
        {
            this.RaiseAndSetIfChanged(ref _squelchSliderValue, value);
            Squelch = value / 10f;
        }
    }


    public int TX1Altitude
    {
        get => _tx1Altitude;
        set => this.RaiseAndSetIfChanged(ref _tx1Altitude, value);
    }

    public int TX2Altitude
    {
        get => _tx2Altitude;
        set => this.RaiseAndSetIfChanged(ref _tx2Altitude, value);
    }

    public int RXAltitude
    {
        get => _rxAltitude;
        set => this.RaiseAndSetIfChanged(ref _rxAltitude, value);
    }

    public int TxWatts
    {
        get => _txWatts;
        set => this.RaiseAndSetIfChanged(ref _txWatts, value);
    }
    
    public int RxDbm
    {
        get => _rxDbm;
        set => this.RaiseAndSetIfChanged(ref _rxDbm, value);
    }

    public int FrequencyKhz
    {
        get => _frequencyKhz;
        set => this.RaiseAndSetIfChanged(ref _frequencyKhz, value);
    }

    public bool Signal1Continuous
    {
        get => _signal1Continuous;
        set => this.RaiseAndSetIfChanged(ref _signal1Continuous, value);
    }
    
    public bool Signal2Continuous
    {
        get => _signal2Continuous;
        set => this.RaiseAndSetIfChanged(ref _signal2Continuous, value);
    }

    public bool Enable3dEffects
    {
        get => _enable3dEffects;
        set => this.RaiseAndSetIfChanged(ref _enable3dEffects, value);
    }

    public float Ppm1
    {
        get => _ppm1;
        set => this.RaiseAndSetIfChanged(ref _ppm1, value);
    }

    public float Ppm2
    {
        get => _ppm2;
        set => this.RaiseAndSetIfChanged(ref _ppm2, value);
    }

    public double FrequencyMhz => FrequencyKhz / 1000d;
}