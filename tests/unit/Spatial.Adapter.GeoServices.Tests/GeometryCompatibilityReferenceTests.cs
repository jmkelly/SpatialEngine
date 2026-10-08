using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Spatial.Esri.Codec;
using Spatial.Operations.NetTopologySuite;
using Spatial.Transformations.ProjNet;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// The compatibility reference's Geometry Service table (spec §7) has to agree
/// with what the service actually dispatches. That table is the document a
/// client-parity reader consults before writing a client, so a row claiming a
/// served operation is "Missing" understates the surface, and a row claiming
/// one the dispatch table does not carry overstates it — both are promises the
/// adapter does not keep (SpatialEngine-q33).
///
/// The served truth is the dispatch dictionary, read here through
/// <see cref="GeometryService.ServedOperations"/>: a row may name an operation
/// the dispatch table does not serve only by calling it Missing, and a row
/// calling a dispatched operation Missing is the same false claim in the other
/// direction. The <c>Info()</c> capabilities string is checked against the same
/// dictionary, because a third hand-kept copy of the served surface is where
/// this table drifted from.
/// </summary>
public sealed class GeometryCompatibilityReferenceTests
{
    [Fact]
    public void Every_section_two_row_agrees_with_the_dispatch_table()
    {
        var served = Served();

        // Claimed served, and served: the status does not say Missing.
        var overstated = SectionTwoRows()
            .Where(row => !row.Missing && !served.Contains(row.Operation))
            .Select(row => row.Operation)
            .ToArray();

        // Claimed Missing, and missing: the honest gap rows still stand.
        var understated = SectionTwoRows()
            .Where(row => row.Missing && served.Contains(row.Operation))
            .Select(row => row.Operation)
            .ToArray();

        Assert.Empty(overstated);
        Assert.Empty(understated);
    }

    [Fact]
    public void The_verdict_does_not_contradict_the_section_two_table()
    {
        // The Verdict is the first thing a client-parity reader reads, and it
        // used to state "roughly 3 of 19 Geometry Service operations map" —
        // the 2026-09-11 baseline, restated as if it were current, which the
        // section 2 table a few lines below contradicts (SpatialEngine-qhw).
        //
        // A count in the Verdict is allowed to be stale only if the sentence
        // says so: read as a live claim it has to agree with the table, which
        // is read against the dispatch table rather than restated.
        var rows = SectionTwoRows();
        var served = rows.Count(row => !row.Missing);

        var claims = CountClaims();

        Assert.NotEmpty(claims);
        foreach (var claim in claims)
        {
            var datedAsBaseline = claim.Sentence.Contains("baseline", StringComparison.OrdinalIgnoreCase);

            Assert.True(
                datedAsBaseline || (claim.Mapped == served && claim.Of == rows.Length),
                $"The Verdict claims '{claim.Mapped} of {claim.Of} Geometry Service operations map' as a current count; "
                + $"the section 2 table has {served} served rows of {rows.Length}. Restate the count or mark it as a dated baseline.");
        }
    }

    [Fact]
    public void The_verdict_feature_service_clause_is_readable_against_the_section_three_table()
    {
        // The Feature Service clause of the same Verdict carried "1 of 6
        // Feature Service operations has an analogue" — the 2026-09-11
        // baseline, never restated and never naming which six it counted, so
        // it was neither a current count nor auditable against anything
        // (SpatialEngine-cll; dated as a baseline by SpatialEngine-qhw).
        //
        // Unlike the geometry count, which is allowed to say "baseline" and
        // move on, a Feature Service count has to be readable: it names the
        // operations it counts and agrees with what section 3's table says
        // about them. The six are the layer operations the spec numbers in
        // section 9.1 — which is what the table's own rows cite, so the set is
        // read from the markdown rather than restated here — and a historical
        // count belongs in the prose as a hyphenated aside ("1-of-6"), not as
        // a claim this check has to reconcile.
        var rows = SectionThreeNumberedOperations();
        var served = rows.Count(row => !row.Refused);
        var claims = FeatureServiceClaims();

        Assert.Equal(6, rows.Length);
        Assert.NotEmpty(claims);
        foreach (var claim in claims)
        {
            Assert.True(
                claim.Mapped == served && claim.Of == rows.Length,
                $"The Verdict claims '{claim.Mapped} of {claim.Of} Feature Service operations' as a current count; "
                + $"the section 3 table has {served} served rows of {rows.Length}.");

            var unnamed = rows
                .Select(row => row.Operation)
                .Where(operation => !claim.Sentence.Contains($"`{operation}`", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            Assert.True(
                unnamed.Length == 0,
                $"The Verdict counts Feature Service operations without naming {string.Join(", ", unnamed)}; "
                + "a count whose operations are unnamed cannot be read against the section 3 table.");
        }
    }

    private static IEnumerable<CountClaim> FeatureServiceClaims()
    {
        var verdict = Verdict();

        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
            verdict, @"(\d+)\s+of\s+(\d+)\s+Feature\s+Service\s+operations", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            yield return new CountClaim(
                int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                Sentence(verdict, match.Index));
        }
    }

    /// <summary>
    /// The rows of the compatibility reference's section 3 table that name a
    /// layer operation the spec numbers in section 9.1 — the six the Verdict
    /// counts. Rows citing a resource rather than an operation (the catalog,
    /// the FeatureServer root, layer metadata) carry no section 9.1 subsection
    /// and are out of the count.
    /// </summary>
    private static (string Operation, bool Refused)[] SectionThreeNumberedOperations() =>
        TableRows(Section("## 3. Feature Service"))
            .Where(cells => cells[0].Contains("§9.1.", StringComparison.Ordinal))
            .Select(cells =>
            {
                var status = string.Join(' ', cells.Skip(2));
                var operation = cells[0].Split('(')[0].Trim().Trim('`');
                var end = operation.IndexOfAny([' ', ',', '/']);
                return (
                    Operation: (end < 0 ? operation : operation[..end]).ToLowerInvariant(),
                    Refused: status.Contains("Missing", StringComparison.OrdinalIgnoreCase)
                        || status.Contains("Non-goal", StringComparison.OrdinalIgnoreCase)
                        || status.Contains("Honestly rejected", StringComparison.OrdinalIgnoreCase));
            })
            .ToArray();

    private static IEnumerable<CountClaim> CountClaims()
    {
        var verdict = Verdict();

        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
            // Whitespace-tolerant between the words: the claim is wrapped
            // across lines in the prose, and reflowing it must not turn the
            // check off.
            verdict, @"(\d+)\s+of\s+(\d+)\s+Geometry\s+Service\s+operations", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            yield return new CountClaim(
                int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                Sentence(verdict, match.Index));
        }
    }

    /// <summary>
    /// The sentence carrying a count: from the previous sentence end to the
    /// next one, so a baseline marker anywhere in it counts.
    /// </summary>
    private static string Sentence(string verdict, int index)
    {
        var start = verdict.LastIndexOfAny(['.', '\n'], index) + 1;
        var tail = verdict[index..].IndexOf('.');
        return verdict[start..(tail < 0 ? verdict.Length : index + tail)];
    }

    private sealed record CountClaim(int Mapped, int Of, string Sentence);

    [Fact]
    public void The_section_two_preamble_points_at_the_verb_inventory()
    {
        var preamble = SectionTwoPreamble();

        // The preamble used to read "IGeometryOperations ships four verbs" as
        // if that were the whole engine surface, which understated every verb
        // the adapter reaches for outside IGeometryOperations.
        Assert.DoesNotContain("ships four verbs", preamble, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Spatial.Contracts", preamble, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_advertised_capability_is_dispatched()
    {
        var advertised = await AdvertisedAsync();

        var undispatched = advertised
            .Where(name => !Served().Contains(name))
            .ToArray();

        Assert.Empty(undispatched);
    }

    [Fact]
    public async Task Every_dispatched_but_unadvertised_operation_rejects_by_name()
    {
        // The dispatch table carries two names the capabilities string does not
        // advertise: the GeoCoordinateString pair, which dispatches straight to
        // the honest "no coordinate-notation codec" reject. So the dispatch keys
        // and the advertised list are equal up to exactly this kind of entry —
        // pinned by behaviour rather than by a second hand-kept name list.
        var advertised = await AdvertisedAsync();

        foreach (var operation in Served().Except(advertised, StringComparer.Ordinal))
        {
            var exception = await Assert.ThrowsAsync<EsriInteropException>(
                async () => await GeometryService.Dispatch(operation, await EmptyParametersAsync(), Capabilities(), CancellationToken.None).ExecuteAsync(new DefaultHttpContext()));

            Assert.Equal(EsriErrorCodes.InvalidParameters, exception.Code);
        }
    }

    private static async Task<string[]> AdvertisedAsync()
    {
        var info = await ExecuteAsync(GeometryService.Info());
        return info.GetProperty("capabilities").GetString()!
            .Split(',')
            .Select(name => name.Trim().ToLowerInvariant())
            .ToArray();
    }

    private static GeometryServiceCapabilities Capabilities() => new(
        new NtsGeometryOperations(),
        new NtsGeometryMeasures(),
        new NtsGeometryProcessing(),
        new NtsGeometryRelations(),
        new ProjNetTransforms(),
        new ProjNetTransforms(),
        new ProjNetGeodesicBuffering(new NtsGeometryOperations(), new NtsGeometryProcessing()));

    private static Task<EsriRequestParameters> EmptyParametersAsync()
    {
        var context = new DefaultHttpContext();
        return EsriRequestParameters.ReadAsync(context, CancellationToken.None);
    }

    /// <summary>The operations the Geometry Service dispatches, lower-cased.</summary>
    private static HashSet<string> Served() =>
        new(GeometryService.ServedOperations.Select(name => name.ToLowerInvariant()), StringComparer.Ordinal);

    /// <summary>
    /// The rows of the compatibility reference's section 2 table, read from the
    /// markdown rather than restated: a copy in the test would drift from the
    /// document exactly as the document drifted from the dispatch table.
    /// </summary>
    private static (string Operation, bool Missing)[] SectionTwoRows() =>
        TableRows(SectionTwoPreamble() + SectionTwoTable())
            .Select(cells =>
            {
                var status = string.Join(' ', cells.Skip(2));
                return (
                    Operation(cells[0]),
                    Missing: status.Contains("Missing", StringComparison.OrdinalIgnoreCase));
            })
            .ToArray();

    private static string SectionTwoPreamble() =>
        Section("## 2. Geometry Service").Split("| GeoServices op", 2, StringSplitOptions.None)[0];

    private static string SectionTwoTable() => Section("## 2. Geometry Service").Split("| GeoServices op", 2)[1];

    /// <summary>The markdown of a <c>##</c> section, up to the next heading.</summary>
    private static string Section(string heading)
    {
        var document = Read();
        var start = document.IndexOf(heading, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException($"The compatibility reference has no '{heading}' section.");
        }

        var end = document.IndexOf("\n## ", start, StringComparison.Ordinal);
        return document[start..end];
    }

    /// <summary>The markdown of <c>## Verdict</c>, up to the next heading.</summary>
    private static string Verdict()
    {
        var document = Read();
        var start = document.IndexOf("## Verdict", StringComparison.Ordinal);
        var end = document.IndexOf("\n## ", start, StringComparison.Ordinal);
        return document[start..end];
    }

    private static string[][] TableRows(string markdown) =>
        markdown.Split('\n')
            .Where(line => line.StartsWith("| ", StringComparison.Ordinal))
            .Select(line => line.Trim().Trim('|').Split('|').Select(cell => cell.Trim()).ToArray())
            .Where(cells => cells.Length >= 3 && !cells[0].StartsWith("---", StringComparison.Ordinal))
            .ToArray();

    /// <summary>The operation name in a row's first cell, without the spec's trailing notes.</summary>
    private static string Operation(string cell)
    {
        var text = cell.Replace("`", string.Empty, StringComparison.Ordinal);
        var end = text.IndexOfAny([' ', '(']);
        return (end < 0 ? text : text[..end]).ToLowerInvariant();
    }

    private static string Read() => File.ReadAllText(
        Path.Combine(RepositoryRoot(), "architecture", "references", "geoservices-compatibility.md"));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("The repository root (Directory.Build.props) was not found above the test output.");
    }

    private static async Task<JsonElement> ExecuteAsync(IResult result)
    {
        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return document.RootElement.Clone();
    }
}
