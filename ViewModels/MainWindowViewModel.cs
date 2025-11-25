namespace BMSAudioSim.ViewModels;

using ReactiveUI;

public class MainWindowViewModel : ReactiveObject
{
    private int _txAltitude = 0;
    private int _rxAltitude = 0;

    private int _txDbm = 40;
    private int _rxDbm = -105;

    private double _frequencyMhz = 85.0; // default UHF

    private bool _steppedEnabled = false;
    private int _steppedDiffDbm = 0;
    private bool _signal1Continuous = false;
    private bool _signal2Continuous = false;

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

    public int TxDbm
    {
        get => _txDbm;
        set => this.RaiseAndSetIfChanged(ref _txDbm, value);
    }

    public bool SteppedEnabled
    {
        get => _steppedEnabled;
        set => this.RaiseAndSetIfChanged(ref _steppedEnabled, value);
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
}