using System;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using OpenFreqAudio;

namespace OpenFreqAudio.Tests
{
    /// <summary>
    /// Builds a synthetic DEM on disk from a 1-D elevation function (terrain varies
    /// along columns/x, constant along rows/y) and wires up a <see cref="FastPathAudioSim"/>
    /// over it. cellSize = 1 m and origin = (0,0), so world-x equals the DEM column and a
    /// horizontal path's along-track distance equals (sampleX - txX) in metres.
    ///
    /// DEMReader stores int16 feet and converts with *0.3048, so elevations round-trip to
    /// within ~0.15 m of the requested metres.
    /// </summary>
    internal sealed class TerrainHarness : IDisposable
    {
        private const double FeetPerMetre = 1.0 / 0.3048;

        public FastPathAudioSim Sim { get; }
        private readonly DEMReader _dem;
        private readonly string _path;

        public TerrainHarness(int width, int height, Func<int, double> elevationMetresByCol)
        {
            _path = Path.Combine(Path.GetTempPath(), $"ofa_dem_{Guid.NewGuid():N}.bin");

            var bytes = new byte[(long)width * height * 2];
            for (int row = 0; row < height; row++)
            {
                for (int col = 0; col < width; col++)
                {
                    short raw = (short)Math.Round(elevationMetresByCol(col) * FeetPerMetre);
                    long idx = ((long)row * width + col) * 2;
                    bytes[idx] = (byte)(raw & 0xFF);
                    bytes[idx + 1] = (byte)((raw >> 8) & 0xFF);
                }
            }
            File.WriteAllBytes(_path, bytes);

            _dem = new DEMReader(_path, width, height);
            Sim = new FastPathAudioSim(_dem, originX: 0.0, originY: 0.0, cellSizeMeters: 1.0,
                NullLogger<FastPathAudioSim>.Instance);
        }

        public void Dispose()
        {
            _dem.Dispose();
            try { File.Delete(_path); } catch { /* temp file best-effort */ }
        }
    }
}
