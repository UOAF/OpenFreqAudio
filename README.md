# OpenFreqAudio

Physics-based radio communication simulation for flight simulators, primarily targeting Falcon BMS. Implements VHF/UHF radio propagation, audio processing, and real-world radio effects.

## Features

### RF Propagation Physics

Radio frequency propagation modeling based on ITU-R standards:

- **Knife-edge diffraction** (ITU-R P.526) for terrain obstruction
- **Two-ray ground reflection model** with curved-Earth geometry
  - Sea surface reflections with Debye-Kirchhoff roughness
- **Fresnel zone clearance** for path obstruction
- **Atmospheric refraction** using altitude-dependent refractivity (SAND2012-10690)
- **Doppler shift** from transmitter and receiver velocities

### Audio Processing

#### Automatic Gain Control (AGC)
- Logarithmic compression with tanh-based soft saturation
- Dynamic range compression

#### Squelch Control
- SNR-based squelch threshold with burst sounds
- Smooth gate transitions
- Configurable levels per frequency

#### Heterodyne Beat Frequencies
When multiple transmitters operate on the same frequency with slightly mistuned oscillators:
- **AM envelope detection** using I/Q signal processing
- **Beat frequency generation** from PPM offset differences
- **Ring modulation** of weaker signals
- **Amplitude modulation** between competing transmissions
- **AGC pumping** from low-frequency beats


### Background Noise

Frequency-appropriate ambient noise based on radio type:

#### Radio Type-Specific Noise
- **VHF AM**: Crackling static with atmospheric pops
  - Pink noise base with crackles (10-20 events/second)
  - Exponentially decaying envelopes (1-4ms)
- **UHF AM**: Lighter crackling, reduced atmospheric interference

#### Sender-Specific Ambient
- **Cockpit**: Engine rumble, avionics hum, airflow
- **Ground vehicle**: Engine and movement sounds
- **Stationary station**: Clean radio room ambiance


### Signal Quality

- **SNR calculation** from received power and thermal noise
- **Multipath fading**: fast flutter (dropout rate) and slow deep fades
- **Modulation-specific behavior**: AM graceful degradation, FM capture effect
- **Weather attenuation** (0.02 dB/km)

### Radio Bands

- **VHF (30-200 MHz)**: 3 kHz bandwidth, AM modulation
- **UHF (200-520 MHz)**: 3 kHz bandwidth, FM modulation
- Configurable bandwidth, diffraction, and modulation per band

### Audio Pipeline

- **8 kHz sample rate** (4 kHz Nyquist for VHF/UHF speech)
- **Ring buffer architecture** with jitter compensation
- **low end-to-end latency**
- **BASS audio library** for cross-platform support

## Architecture

### Projects

#### OpenFreqAudio
Core audio engine library providing:
- RF propagation simulation (`FastPathAudioSim`)
- Audio effects processing (`RadioEffect`)
- Real-time mixing and playback (`RadioPlayback`, `RadioMixer`)
- Background noise generation (`BackgroundNoiseGenerator`)
- DEM (Digital Elevation Model) terrain integration

#### BMSAudioSim
Demo application for testing and visualization:
- **Avalonia UI** cross-platform interface
- **2 transmitters + 1 receiver** simulation
- **BMS heightmap integration** for terrain-based propagation
- **3D position simulation** with velocity controls
- **Heterodyne testing**: adjustable PPM offset

Allows testing of Doppler effects, terrain fading, multipath interference, and distance-dependent propagation.

### Technology Stack

- **.NET 10.0**
- **BASS Audio Library** (ManagedBass, ManagedBass.Mix)
- **Avalonia 11.3**
- **Memory-mapped DEM files** for terrain access
- **Lock-free ring buffers** for real-time streaming

## Building

### Prerequisites
- .NET 10.0 SDK
- BASS audio library (native DLLs/SOs included in Assets/)

### Windows
```bash
dotnet build OpenFreqAudio.sln -c Release
```

### Linux
```bash
dotnet build OpenFreqAudio.sln -c Release
```

Native BASS libraries are automatically copied from `Assets/windows/` or `Assets/linux/` depending on the target platform.

## Running BMSAudioSim

```bash
# Windows
.\BMSAudioSim\bin\Release\net10.0\win-x64\BMSAudioSim.exe

# Linux
./BMSAudioSim/bin/Release/net10.0/linux-x64/BMSAudioSim
```

### Demo Features
- Load BMS theater heightmap (.DEM format)
- Position two transmitters and one receiver
- Adjust frequency, power, and PPM offsets
- Test heterodyne effects with same-frequency transmissions
- Visualize terrain profile along propagation path

## Physics References

- **ITU-R P.526**: Propagation by diffraction
- **SAND2012-10690**: Radar horizon and target visibility calculations (atmospheric refraction)
- **Debye-Kirchhoff roughness model**: Sea surface reflection attenuation
- **Two-ray propagation model**: Ground reflection interference
- **Fresnel zones**: Obstacle clearance and diffraction analysis

## License

Mozilla Public License Version 2.0 (MPLv2)