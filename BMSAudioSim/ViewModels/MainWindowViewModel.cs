namespace BMSAudioSim.ViewModels;

using ReactiveUI;

public class MainWindowViewModel : ReactiveObject
{
    private int _txAltitude = 0;
    private int _rxAltitude = 0;

    private int _txWatts = 40;
    private int _rxDbm = -105;

    private double _frequencyMhz = 513.75; // default UHF

    private int _steppedDiffDbm = 0;
    private bool _signal1Continuous = false;
    private bool _signal2Continuous = false;
    private int _squelchSliderValue = 10;
    private float _squelch = 1.0f;
    private bool _enable3dEffects = true;

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


    public int TXAltitude
    {
        get => _txAltitude;
        set => this.RaiseAndSetIfChanged(ref _txAltitude, value);
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
    
    public int SteppedDiffDbm
    {
        get => _steppedDiffDbm;
        set => this.RaiseAndSetIfChanged(ref _steppedDiffDbm, value);
    }

    public int RxDbm
    {
        get => _rxDbm;
        set => this.RaiseAndSetIfChanged(ref _rxDbm, value);
    }

    public double FrequencyMhz
    {
        get => _frequencyMhz;
        set => this.RaiseAndSetIfChanged(ref _frequencyMhz, value);
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
}