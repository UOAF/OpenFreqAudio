using System.Diagnostics;
using System.IO.MemoryMappedFiles;

namespace OpenFreqAudio.TerrainSampling;

/// <summary>
/// Memory-maps the BMS terrain heightmap ("digital elevation map - DEM") file.
/// We expect this to be very large (32768^2 Int16),
/// so let the OS page it in and out for us on demand
/// instead of keeping the entire thing resident.
/// </summary>
public class DEMReader : IDisposable
{
    private readonly MemoryMappedFile mmf;
    private readonly MemoryMappedViewAccessor accessor;
    public readonly int Width;
    public readonly int Height;
    public DEMReader(string path, int width, int height)
    {
        this.Width = width;
        this.Height = height;

        mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        if (accessor.SafeMemoryMappedViewHandle.ByteLength < (ulong)width * (ulong)height * 2)
        {
            throw new ArgumentOutOfRangeException($"{path} does not have enough bytes to be {width}x{height}");
        }
    }

    public Int16 Sample(int x, int y)
    {
        return Sample((long)y * Width + x);
    }

    /// <summary>
    /// Sample by row-major index.
    /// </summary>
    /// <remarks>Helpful for traversing a row without multiplying y * width each time</remarks>
    public Int16 Sample(long offset)
    {
        return accessor.ReadInt16(offset * 2);
    }

    /// <summary>
    /// Acquires a raw pointer to the int16 sample data for fast bulk reads (when building
    /// the pyramid). Must be paired with <see cref="ReleaseRawPointer"/>.
    /// Later individual lookups should use <see cref="Sample(long)"/> (bounds-checked) instead.
    /// </summary>
    internal unsafe Int16* AcquireRawPointer()
    {
        byte* p = null;
        accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        return (Int16*)(p + accessor.PointerOffset);
    }

    internal void ReleaseRawPointer()
    {
        accessor.SafeMemoryMappedViewHandle.ReleasePointer();
    }

    public void Dispose()
    {
        accessor.Dispose();
        mmf.Dispose();
    }
}

/// <summary>
/// A level of the pyramid, where each is half the resolution of the previous
/// </summary>
class Level
{
    public int width;
    public int height;
    public Int16[] samples;

    public Level(int w, int h)
    {
        width = w;
        height = h;
        samples = new short[width * height];
    }
}

/// <summary>
/// A max-value quadtree pyramid.
/// 
/// At the bottom is the actual memory-mapped heightmap file from BMS.
/// Each layer above is a lower-res copy of the previous layer.
/// Eventually we reach the top, where we have a single pixel
/// representing the max height of the whole terrain.
/// 
/// This lets us do adaptive sampling! (<see cref="SampleProfile"/>)
/// When both antennas (and the Fresnel zone between them) are far above the terrain,
/// we sample sparsely. But when any of those drops below the max height,
/// we descend a level, 2x larger, so each pixel is split into 4 subpixels.
/// We break our path into the segments that cross those subpixels, and we sample again.
/// The result is a terrain profile that's only detailed where we need it.
/// 
/// A naive implementation would do this all the way down, from the 1 pixel of the base heightmap,
/// growing by a factor of two each time. But the bottom layers start to get real big!
/// Assuming a 32768^2 base heightmap (2 GB), half that size would still be 512 MB,
/// and half that 128 MB. Instead, skip some levels until the first above the base heightmap
/// is 32x smaller. Now it's only 2 MB, and when we subdivide it,
/// we still only have to walk at most 32 pixels of the base heightmap.
/// </summary>
/// <seealso cref="https://en.wikipedia.org/wiki/Quadtree"/>
public class HeightPyramid : IDisposable
{
    private readonly DEMReader map;
    private readonly List<Level> levels;

    public HeightPyramid(string path, int width, int height)
    {
        map = new DEMReader(path, width, height);
        levels = new List<Level>();

        // Make the first pyramid level 32x smaller.
        // (For the 2 GB BMS heightmaps, this gives us a 2MB map, with each pixel mapping ~1km)
        if (width < 32 || height < 32)
        {
            throw new InvalidDataException($"{path} is an absurdly small height map ({width}x{height})");
        }
        // We want to round the dimensions up - for a 63-pixel map,
        // we want two subsamples [0, 32) and [32, 63).
        var l1w = (width + 31) / 32;
        var l1h = (height + 31) / 32;
        Level l1 = new Level(l1w, l1h);
        // This is the expensive level — it touches every base heightmap sample.
        // Read through a raw pointer acquired once (no per-sample bounds check from ReadInt16()),
        // and parallelize over output rows.
        // Each (bx,by) cell writes a distinct samples[] index, so no locking is needed.
        // Individual lookups elsewhere still go through DEMReader.Sample (ReadInt16).
        // (The coarser levels below are tiny and stay serial.)
        unsafe
        {
            IntPtr basePtr = (IntPtr)map.AcquireRawPointer();
            try
            {
                Parallel.For(0, l1h, by =>
                {
                    Int16* data = (Int16*)basePtr;
                    int blockY = by * 32;
                    int yEnd = Math.Min(blockY + 32, height);
                    int rowBase = by * l1w;
                    for (int bx = 0; bx < l1w; bx++)
                    {
                        int blockX = bx * 32;
                        int xEnd = Math.Min(blockX + 32, width);
                        // Max of the 32x32 heightmap square with corner (blockX, blockY).
                        Int16 acc = Int16.MinValue;
                        for (int y = blockY; y < yEnd; ++y)
                        {
                            long yOff = (long)y * width;
                            for (int x = blockX; x < xEnd; ++x)
                            {
                                acc = Math.Max(acc, data[yOff + x]);
                            }
                        }
                        l1.samples[rowBase + bx] = acc;
                    }
                });
            }
            finally
            {
                map.ReleaseRawPointer();
            }
        }
        levels.Add(l1);

        // Then for each subsequent level, go down 2x until we have a single pixel
        // that's "the max height of the entire map"
        var prev = levels.Last();
        while (prev.width > 1 || prev.height > 1)
        {
            var pw = prev.width;
            var ph = prev.height;
            var ps = prev.samples;
            // ceil() dimensions again, see above.
            var nw = (pw + 1) / 2;
            var nh = (ph + 1) / 2;

            Level next = new Level(nw, nh);
            int idx = 0;
            for (int blockY = 0; blockY < ph; blockY += 2)
            {
                for (int blockX = 0; blockX < pw; blockX += 2)
                {
                    Int16 acc = Int16.MinValue;
                    for (int y = blockY; y < blockY + 2 && y < ph; ++y)
                    {
                        int yOff = y * pw;
                        for (int x = blockX; x < blockX + 2 && x < pw; ++x)
                        {
                            acc = Math.Max(acc, ps[yOff + x]);
                        }
                    }
                    next.samples[idx++] = acc;
                }
            }
            levels.Add(next);
            prev = next;
        }
    }

    public const double FeetToMeters = 0.3048;

    public int Width => map.Width;
    public int Height => map.Height;

    /// <summary>Clamped native-resolution sample, raw int16 feet.</summary>
    public short SampleNativeFeet(int x, int y)
    {
        x = Math.Clamp(x, 0, map.Width - 1);
        y = Math.Clamp(y, 0, map.Height - 1);
        return map.Sample(x, y);
    }

    /// <summary>
    /// Traverse our max-pyramid. Returns a terrain profile sampled along the path from
    /// (ax, ay) to (bx, by).
    /// Samples are dense (i.e. native heightmap resolution) where terrain nears the first Fresnel zone,
    /// and sparse where it clears.
    ///
    /// The remaining args are used to calculate the Fresnel zone.
    /// Horizontal coordinates (ax,ay) and (bx,by) are native DEM pixels.
    /// The other parameters are used to calculate the Fresnel zone - the *altM give altitude in meters,
    /// wavelength is that of the frequency in meters,
    /// rEffM is the effective radius of the earth in meters
    /// (this changes based on diffraction coefficients, which changes based on altitude!),
    /// and cellSizeM is the width of each heightmap pixel, in meters.
    /// txAltM/rxAltM/wavelength/rEffM/cellSizeM are meters.
    /// </summary>
    /// <returns>
    /// Distances are in horizontal meters from TX, and elevation is meters ASL.
    /// (Curvature is not included; the diffraction model handles this.)
    /// If allClear is set, assume terrain is outside the Fresnel zone.
    /// </returns>
    public (List<(double dist, double elev)> profile, bool allClear) SampleProfile(
        double ax, double ay, double bx, double by,
        double txAltM, double rxAltM, double wavelength, double rEffM, double cellSizeM)
    {
        // An arbitrary small epsilon used to skip line subsegments that just kiss a subpixel.
        const double EPS = 1e-9;
        double dx = bx - ax;
        double dy = by - ay;
        double D = Math.Sqrt(dx * dx + dy * dy) * cellSizeM; // horizontal path length, meters

        var profile = new List<(double dist, double elev)>();

        // Get the elevation at the given location in meters.
        // Deliberately use nearest-neighbor sampling.
        // Bilinear/trilinear/sinc/etc. would bias towards _lower_ heights
        // by averaging out peaks, when peak height is exactly what we want!
        double GroundAt(double px, double py) =>
            SampleNativeFeet((int)Math.Round(px), (int)Math.Round(py)) * FeetToMeters;

        if (D < 1.0)
        {
            profile.Add((0.0, GroundAt(ax, ay)));
            return (profile, true);
        }

        double bulge = D * D / (2.0 * rEffM); // coefficient for below
        // Along our path t = [0, 1],the line-of-sight height from Earth curvature
        // (from 0ft MSL, not AGL)
        double LosHeight(double t) => txAltM + (rxAltM - txAltM) * t - bulge * t * (1.0 - t);
        // Get the lowest point along the LosHeight parabola:
        double tStar = 0.5 - rEffM * (rxAltM - txAltM) / (D * D);
        double LosMin(double t0, double t1) => LosHeight(Math.Clamp(tStar, t0, t1));
        // The fattest first Fresnel zone radius in the [t0, t1] segment of the LosHeight arc.
        double F1Max(double t0, double t1)
        {
            double tc = Math.Clamp(0.5, t0, t1);
            return Math.Sqrt(wavelength * D * tc * (1.0 - tc));
        }

        bool hadNativeDescent = false;

        // Amanatides–Woo DDA over native pixels for the [t0, t1] segment: one real
        // ground sample per native cell the ray crosses.
        void NativeWalk(double t0, double t1)
        {
            hadNativeDescent = true;
            double px0 = ax + t0 * dx, py0 = ay + t0 * dy;
            int ix = (int)Math.Floor(px0), iy = (int)Math.Floor(py0);
            int stepX = dx > 0 ? 1 : (dx < 0 ? -1 : 0);
            int stepY = dy > 0 ? 1 : (dy < 0 ? -1 : 0);
            double tMaxX = dx != 0 ? t0 + ((stepX > 0 ? ix + 1 : ix) - px0) / dx : double.PositiveInfinity;
            double tMaxY = dy != 0 ? t0 + ((stepY > 0 ? iy + 1 : iy) - py0) / dy : double.PositiveInfinity;
            double tDeltaX = dx != 0 ? Math.Abs(1.0 / dx) : double.PositiveInfinity;
            double tDeltaY = dy != 0 ? Math.Abs(1.0 / dy) : double.PositiveInfinity;
            double t = t0;
            while (t < t1)
            {
                profile.Add((t * D, SampleNativeFeet(ix, iy) * FeetToMeters));
                if (tMaxX < tMaxY) { t = tMaxX; ix += stepX; tMaxX += tDeltaX; }
                else { t = tMaxY; iy += stepY; tMaxY += tDeltaY; }
            }
        }

        // Check the pixel at (cx, cy) at the given level,
        // checking against the max Fresnel zone radius in the arc t = [t0, t1]
        // Recurses if the first Fresnel zone dips below this pixel's height.
        void Visit(int level, int cx, int cy, double t0, double t1)
        {
            Level lvl = levels[level];
            // Get the max altitude of the pixel at the given level.
            double termMax = lvl.samples[cy * lvl.width + cx] * FeetToMeters;
            // Is that terrain below the first Fresnel zone?
            if (LosMin(t0, t1) - F1Max(t0, t1) > termMax)
            {
                // Clear: terrain provably below the first Fresnel zone here.
                // Sample at the midpoint.
                double tmc = 0.5 * (t0 + t1);
                profile.Add((tmc * D, GroundAt(ax + tmc * dx, ay + tmc * dy)));
                return;
            }
            // If not, and we made it all the way down to the heightmap, sample that.
            if (level == 0)
            {
                NativeWalk(t0, t1);
                return;
            }

            // Otherwise, each of our pixels is 4 pixels on the next lower level.
            // (This is a quadtree, after all!)
            int child = level - 1;
            double sParent = 32 << level;
            double sChild = 32 << child;
            double xmid = (cx + 0.5) * sParent;
            double ymid = (cy + 0.5) * sParent;

            // Ignoring kissed corners/edges (see the EPS check below),
            // we'll touch 1 to 3 subpixels.
            // Cut the segment [t0, t1] into the subsegements in each subpixel.
            Span<double> cuts = stackalloc double[4];
            int k = 0;
            cuts[k++] = t0; cuts[k++] = t1;
            if (dx != 0)
            {
                double tx = (xmid - ax) / dx;
                if (tx > t0 && tx < t1) cuts[k++] = tx;
            }
            if (dy != 0)
            {
                double ty = (ymid - ay) / dy;
                if (ty > t0 && ty < t1) cuts[k++] = ty;
            }
            cuts.Slice(0, k).Sort();

            int cw = levels[child].width;
            int ch = levels[child].height;
            for (int j = 0; j < k - 1; j++)
            {
                double ta = cuts[j], tb = cuts[j + 1];
                if (tb - ta < EPS) continue; // Skip, we're just kissing this subpixel.
                double tm = 0.5 * (ta + tb);
                int ccx = Math.Clamp((int)((ax + tm * dx) / sChild), 0, cw - 1);
                int ccy = Math.Clamp((int)((ay + tm * dy) / sChild), 0, ch - 1);
                Visit(child, ccx, ccy, ta, tb);
            }
        }

        Visit(levels.Count - 1, 0, 0, 0.0, 1.0); // Start our recursive walk!

        // Add our endpoints, then deduplicate (keep the higher elevation on collisions).
        profile.Add((0.0, GroundAt(ax, ay)));
        profile.Add((D, GroundAt(bx, by)));
        profile.Sort((p, q) => p.dist.CompareTo(q.dist));

        double epsDist = 0.25 * cellSizeM;
        int count = profile.Count;
        int w = 0;
        for (int r = 0; r < count; r++)
        {
            var p = profile[r];
            if (w > 0 && p.dist - profile[w - 1].dist < epsDist)
            {
                if (p.elev > profile[w - 1].elev) profile[w - 1] = (profile[w - 1].dist, p.elev);
            }
            else profile[w++] = p;
        }
        profile.RemoveRange(w, count - w);

        return (profile, !hadNativeDescent);
    }

    public void Dispose()
    {
        map.Dispose();
    }
}
