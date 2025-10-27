namespace BMSAudioSim.ViewModels;
using ReactiveUI;

public class MainWindowViewModel : ReactiveObject
{
    private int _txAltitude = 0;
    private int _rxAltitude = 0;

    private int _txDbm = 40;
    private int _rxDbm = -105;

    private double _frequencyMhz = 339.75; // default UHF
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
}