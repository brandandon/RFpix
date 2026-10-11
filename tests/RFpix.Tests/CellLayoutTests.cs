// CellLayoutTests.cs - checks RFpix's cell layout matches QSSTV's, cell for cell.
//
// tests/reference/qsstv-cell-layouts.txt was dumped from QSSTV's own transmitter
// (tools/qsstv-testgen/dumpcells.cpp). It lists every pilot and FAC cell with
// its value; every cell it doesn't list is picture data.

using System.Globalization;
using System.Numerics;
using RFpix.HamDrm;
using Xunit;

namespace RFpix.Tests;

public class CellLayoutTests
{
    public static IEnumerable<object[]> AllLayouts() =>
        from mode in HamDrmModes.All
        from bandwidth in new[] { HamDrmBandwidth.Narrow, HamDrmBandwidth.Wide }
        select new object[] { mode, bandwidth };

    [Theory]
    [MemberData(nameof(AllLayouts))]
    public void Matches_QSSTV_cell_for_cell(HamDrmMode mode, HamDrmBandwidth bandwidth)
    {
        var expected = Reference(mode, bandwidth);
        var layout = CellLayout.For(mode, bandwidth);

        int checkedCells = 0;
        for (int s = 0; s < layout.SymbolsPerFrame; s++)
            for (int k = layout.FirstCarrier; k <= layout.LastCarrier; k++)
            {
                var kind = layout.Kind(s, k);
                if (expected.TryGetValue((s, k), out var cell))
                {
                    Assert.True(cell.Kind == kind, $"symbol {s}, carrier {k}: QSSTV {cell.Kind}, RFpix {kind}");
                    if (layout.IsPilot(s, k))
                        Assert.True((layout.PilotValue(s, k) - cell.Value).Magnitude < 1e-4,
                            $"symbol {s}, carrier {k}: QSSTV {cell.Value}, RFpix {layout.PilotValue(s, k)}");
                }
                else
                {
                    Assert.True(kind == CellKind.Data, $"symbol {s}, carrier {k}: QSSTV Data, RFpix {kind}");
                }
                checkedCells++;
            }

        Assert.Equal(expected.Count, Enumerable.Range(0, layout.SymbolsPerFrame)
            .Sum(s => Enumerable.Range(layout.FirstCarrier, layout.LastCarrier - layout.FirstCarrier + 1)
                .Count(k => layout.Kind(s, k) != CellKind.Data)));
        Assert.True(checkedCells > 400);
    }

    [Theory]
    [MemberData(nameof(AllLayouts))]
    public void Every_frame_has_45_FAC_cells(HamDrmMode mode, HamDrmBandwidth bandwidth)
    {
        Assert.Equal(45, CellLayout.For(mode, bandwidth).FacCells.Count);
    }

    // ---- reading the reference file ----

    private static Dictionary<(int, int), (CellKind Kind, Complex Value)> Reference(HamDrmMode mode, HamDrmBandwidth bandwidth)
    {
        string wanted = $"MODE {mode} BANDWIDTH {(bandwidth == HamDrmBandwidth.Narrow ? "2.2" : "2.5")}";
        var path = Path.Combine(Recordings.Folder, "..", "reference", "qsstv-cell-layouts.txt");
        var cells = new Dictionary<(int, int), (CellKind, Complex)>();
        bool inBlock = false;
        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith('#')) continue;
            if (line.StartsWith("MODE")) { inBlock = line == wanted; continue; }
            if (!inBlock) continue;

            var p = line.Split(' ');
            var kind = p[2] switch
            {
                "Fac" => CellKind.Fac,
                "Time" => CellKind.TimePilot,
                "Freq" => CellKind.FrequencyPilot,
                "Gain" => CellKind.GainPilot,
                "Gain+Boost" => CellKind.GainPilot | CellKind.Boosted,
                "Gain+Freq" => CellKind.GainPilot | CellKind.FrequencyPilot,
                "Gain+Time" => CellKind.GainPilot | CellKind.TimePilot,
                _ => throw new InvalidDataException(line),
            };
            var value = new Complex(double.Parse(p[3], CultureInfo.InvariantCulture), double.Parse(p[4], CultureInfo.InvariantCulture));
            cells[(int.Parse(p[0]), int.Parse(p[1]))] = (kind, value);
        }
        Assert.NotEmpty(cells);
        return cells;
    }
}
