using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Spatial.Transformations.ProjNet.Tests;

/// <summary>
/// What PROJ answered for one control point, and the two ways that answer can
/// be wrong for this exercise: it printed nothing, or it printed something that
/// did not come from a grid.
/// </summary>
internal static class ProjReference
{
    /// <summary>
    /// The tokens PROJ's own output uses when a grid took part in a
    /// transformation. PROJ 6 and later print the pipeline it built, which
    /// names the file, and the older <c>+nadgrids=</c> form is still in wide
    /// use; a Helmert prints <c>+towgs84=</c> and nothing else, which is the
    /// answer that proves nothing here.
    /// </summary>
    private static readonly string[] GridTokens =
        ["hgridshift", "vgridshift", "+nadgrids", "+grids", ".gsb", ".tif", ".gtx", ".lla", ".byn", ".los"];

    /// <summary>
    /// The reference answer for one point: PROJ, run on the same bundle, given
    /// the same EPSG pair and the same coordinate, under the same
    /// longitude-then-latitude convention every control point in this project
    /// is written in.
    /// </summary>
    public static (double X, double Y) Answer(
        string executable,
        string source,
        string target,
        double longitude,
        double latitude) =>
        RequireAnswer(executable, source, target, longitude, latitude, Run(executable, source, target, longitude, latitude));

    /// <summary>
    /// Reads the answer out of what PROJ printed, and refuses it when there is
    /// no answer or when the run did not open a grid.
    /// <para>
    /// The second refusal is the one that matters. PROJ will happily answer a
    /// pair from a seven-parameter transformation when the grid it wanted is
    /// not in its data directory, and the Helmert answer agrees with the
    /// engine's Helmert fallback to well inside a metre — so a comparison that
    /// accepted it would report agreement about a bundle neither side read.
    /// </para>
    /// </summary>
    public static (double X, double Y) RequireAnswer(
        string executable,
        string source,
        string target,
        double longitude,
        double latitude,
        string output)
    {
        if (!TryReadAnswer(output, out var answer))
        {
            throw new InvalidOperationException(
                $"PROJ ({executable}) printed no coordinate for {longitude}, {latitude} {source} → {target}: "
                + $"“{output.Trim()}”. The usual cause is a grid that is not in PROJ's data directory "
                + "(PROJ_DATA, or PROJ_LIB on PROJ 8 and earlier), which is a deployment to fix rather "
                + "than a disagreement with the engine.");
        }

        if (!NamesAGrid(output))
        {
            throw new InvalidOperationException(
                $"PROJ ({executable}) answered {source} → {target} for {longitude}, {latitude} without "
                + $"opening a grid: “{output.Trim()}”. That is the Helmert path, which both sides already "
                + "agree on, so it measures nothing about the bundle; install the bundle in PROJ's data "
                + "directory and run it again.");
        }

        return answer;
    }

    /// <summary>
    /// The pair of numbers at the end of what PROJ printed. <c>cs2cs -v</c>
    /// writes the input, the operation it built and then the answer on a line
    /// that begins with <c>output:</c>, so the <em>last</em> adjacent pair of
    /// numbers is the answer: taking the first would read the input back as the
    /// reference, and every control point would pass exactly.
    /// </summary>
    public static bool TryReadAnswer(string output, out (double X, double Y) answer)
    {
        answer = default;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Reverse())
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            for (var index = fields.Length - 1; index > 0; index--)
            {
                if (double.TryParse(fields[index - 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                    && double.TryParse(fields[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                {
                    answer = (x, y);
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Whether what PROJ printed says a grid took part in it.</summary>
    public static bool NamesAGrid(string output) =>
        GridTokens.Any(token => output.Contains(token, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Runs <c>cs2cs</c> for one coordinate. PROJ is invoked by its EPSG
    /// authority codes rather than by a hand-written <c>+proj</c> string, so the
    /// comparison is between the engine's catalogue and PROJ's, and the bundle
    /// each of them reads is the one each of them was configured with.
    /// </summary>
    private static string Run(
        string executable,
        string source,
        string target,
        double longitude,
        double latitude)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        // -v so the pipeline PROJ built is printed and the grid it opened can be
        // seen; six decimals because a millimetre is far finer than the residual
        // this suite holds a bundle to.
        foreach (var argument in new[] { "-v", "-f", "%.6f", source, target })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"PROJ's cs2cs could not be started at '{executable}'.");

        process.StandardInput.WriteLine(
            string.Create(CultureInfo.InvariantCulture, $"{longitude} {latitude}"));
        process.StandardInput.Close();

        var output = new StringBuilder();
        output.Append(process.StandardOutput.ReadToEnd());
        output.Append(process.StandardError.ReadToEnd());

        if (!process.WaitForExit(milliseconds: 60_000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException(
                $"PROJ ({executable}) did not answer {source} → {target} for {longitude}, {latitude} within "
                + "60 seconds. A grid of a size a national agency publishes is read in well under that, so a "
                + "hang here is PROJ waiting for something this suite cannot supply.");
        }

        return output.ToString();
    }
}
