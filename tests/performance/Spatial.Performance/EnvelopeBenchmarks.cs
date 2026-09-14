using BenchmarkDotNet.Attributes;
using Spatial.Core.Geometry;

namespace Spatial.Performance;

/// <summary>
/// Placeholder benchmark proving the BenchmarkDotNet scaffold runs end to
/// end (T-067). It exercises a structural core value (envelope computation)
/// only — no algorithms, no providers. Real micros land in the follow-on
/// slices (T-076/77/78).
/// </summary>
[MemoryDiagnoser]
public class EnvelopeBenchmarks
{
    private Coordinate[] _coordinates = [];

    [GlobalSetup]
    public void Setup()
    {
        _coordinates = new Coordinate[1_024];
        for (var i = 0; i < _coordinates.Length; i++)
        {
            _coordinates[i] = new Coordinate(i, i * 0.5);
        }
    }

    [Benchmark(Baseline = true, Description = "Envelope over 1k coordinates (placeholder)")]
    public Envelope FromCoordinates() => Envelope.FromCoordinates(_coordinates);
}
