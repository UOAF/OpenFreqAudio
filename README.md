# BMS Audio Sim
Simulation of BMS new terrain effects on radio transmissions.

## Features

### Signal Propagation
* Adaptive sampling of terrain profiles between transmitter and receiver
* Knife-edge diffraction modeling for terrain obstruction
* Frequency-dependent propagation for VHF and UHF bands
* Two-ray sea reflection model
* Heuristic ocean segment detection

### Audio Chain
* Real-time DSP-based audio filtering (using BASS)
* Radio pre-filter for realism
* Frequency-based receiver sensitivity and noise floor

### Interference Simulations
* Modern digital radio interference
* Calculations based on respective signal strengths
* FM capture behaviour simulation

## Installation
* Unpack everything
* Run BMSAudio.exe
* Linux: Make sure to install libbass into your LD_LIB path

## Usage
* Open the BMS NT height map ``<BMS>\Data\TerrData\Korea\NewTerrain\HeightMaps``
* On the first run: wait a bit for the preview image to be generated
* Select 2 locations on the map
* Adjust the RX/TX AGL, positions, ...
* You can change the audio sample by modifying the ``countdown.wav`` - make sure it stays mono for more realism

## Error Reporting
Check ``BMSAudioSim_errors.log``

## Contact
UOAF / richionizor (Discord)
