using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Spatial.Transformations.ProjNet.Grids;

/// <summary>
/// A grid file that was in a configured directory but could not be read, and
/// why. A datum shift is the one thing in the engine that must not be applied
/// half-understood, so an unreadable bundle is reported rather than guessed at
/// — and reported rather than thrown, because the host still serves every
/// other datum through the Helmert path.
/// </summary>
/// <param name="FileName">The bundle's file name, never its full path.</param>
/// <param name="Reason">Why the reader refused it.</param>
internal sealed record GridLoadFailure(string FileName, string Reason);

/// <summary>
/// The datum-shift grid registry (ADR-0105): the configured directories datum
/// shift bundles may be deployed into, and the grids the catalogue's grid
/// operations resolve to from them.
/// <para>
/// Grids are third-party data, so they are <em>not</em> embedded: a host is
/// pointed at one or more directories and a bundle is deployed by dropping a
/// file in. That keeps the licence question (§licence) with whoever holds the
/// licence, and it means the engine behaves identically whether or not any
/// bundle is present — an absent grid is an ordinary state, not an error.
/// </para>
/// <para>
/// The directories are a priority order, not a set: the first one holding the
/// bundle for an operation wins, so an operator can shadow a shipped default
/// without editing it. A bundle is read the first time a grid is asked for and
/// then served from a cache, because a bundle runs to megabytes and a
/// transform on the hot path must not re-read and re-parse one per request.
/// A file dropped into a configured directory after start-up is therefore
/// picked up without a restart, and a file rewritten under a running host is
/// not re-read until the process restarts — which is the trade the cache makes
/// and is why a redeploy of a grid is a restart.
/// </para>
/// </summary>
internal sealed class DatumShiftGridRegistry
{
    /// <summary>A registry with no configured directory: no datum is grid-backed.</summary>
    public static readonly DatumShiftGridRegistry Empty = new([], EpsgGridShiftOperations.Bundles);

    private readonly string[] _directories;
    private readonly IReadOnlyList<EpsgGridShiftOperations.GridShiftOperation> _operations;
    private readonly ConcurrentDictionary<string, DatumShiftGrid?> _loaded = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, EpsgGridShiftOperations.GridShiftOperation?> _served = new(StringComparer.Ordinal);
    private readonly List<GridLoadFailure> _failures = [];

    private DatumShiftGridRegistry(
        IReadOnlyList<string> directories,
        IReadOnlyList<EpsgGridShiftOperations.GridShiftOperation> operations)
    {
        _directories = [.. directories];
        _operations = operations;
    }

    /// <summary>
    /// Builds a registry over a priority-ordered set of directories, serving
    /// the datums the catalogue publishes grid operations for. The order given
    /// is the order kept; a directory that does not exist is remembered (so an
    /// operator sees it echoed back) and otherwise ignored.
    /// </summary>
    public static DatumShiftGridRegistry Load(IReadOnlyList<string> directories) =>
        Load(directories, EpsgGridShiftOperations.Bundles);

    /// <summary>
    /// The same registry over a caller-supplied set of grid operations. A seam
    /// for the tests, which exercise the two container shapes against datums
    /// of their own choosing rather than only the bundles the catalogue
    /// happens to name; the catalogue is what production resolves.
    /// </summary>
    public static DatumShiftGridRegistry Load(
        IReadOnlyList<string> directories,
        IReadOnlyList<EpsgGridShiftOperations.GridShiftOperation> operations) =>
        new(directories, operations);

    /// <summary>The configured directories, in priority order, as configured.</summary>
    public IReadOnlyList<string> Directories => _directories;

    /// <summary>
    /// The bundles that were present but unreadable, with the reason. A host
    /// surfaces these at start-up: a silently ignored grid would leave the
    /// Helmert path in place while an operator believed the accurate one was
    /// deployed.
    /// </summary>
    public IReadOnlyList<GridLoadFailure> Failures
    {
        get
        {
            lock (_failures)
            {
                return [.. _failures];
            }
        }
    }

    /// <summary>
    /// The grid serving a datum, or null when the catalogue publishes no grid
    /// for it or the bundle is not deployed. Null is the ordinary answer and
    /// is what puts the Helmert path in place.
    /// <para>
    /// Only a grid that was found is cached. A bundle that is absent is
    /// re-probed, so a host picks one up when it is deployed underneath it
    /// without a restart; what that costs is a stat per lookup for the few
    /// datums that have a grid operation at all, and it is paid while a
    /// transform plan is being built rather than per coordinate.
    /// </para>
    /// </summary>
    public bool TryGet(string datumName, [NotNullWhen(true)] out DatumShiftGrid? grid)
    {
        if (_loaded.TryGetValue(datumName, out var cached))
        {
            grid = cached;
            return cached is not null;
        }

        grid = Load(datumName);
        if (grid is not null)
        {
            _loaded[datumName] = grid;
        }

        return grid is not null;
    }

    /// <summary>
    /// The grid serving a datum together with the published operation it came
    /// from, so a caller can read what the row says and not only what the file
    /// holds — in particular the datum the bundle's shifts land in, which is
    /// not always the pivot (ADR-0180). A grid is only half the claim; the row
    /// is the other half, and a caller that composes a path has to honour both
    /// or it publishes one the registry did not.
    /// </summary>
    public bool TryGet(
        string datumName,
        [NotNullWhen(true)] out DatumShiftGrid? grid,
        [NotNullWhen(true)] out EpsgGridShiftOperations.GridShiftOperation? operation)
    {
        if (!TryGet(datumName, out grid))
        {
            operation = null;
            return false;
        }

        operation = _served[datumName];
        return operation is not null;
    }

    /// <summary>
    /// Resolves one datum's grid from the first directory holding its bundle.
    /// A directory that cannot supply it — because the file is absent, or
    /// because it is present and unreadable — is passed over so that a later
    /// directory can still serve it; a file that is present and broken is
    /// recorded once, because an operator needs to be told about it.
    /// </summary>
    private DatumShiftGrid? Load(string datumName)
    {
        foreach (var operation in _operations.Where(operation =>
            string.Equals(operation.GraphName, datumName, StringComparison.Ordinal)))
        {
            foreach (var directory in _directories)
            {
                var path = Path.Combine(directory, operation.FileName);
                if (!File.Exists(path))
                {
                    continue;
                }

                // A row naming its own second file is a NADCON pair: the
                // latitude shifts and the longitude shifts are two containers,
                // and the reader joins them. Everything else is a single
                // NTv2 bundle. Which of the two a row is is stated by the
                // row, never guessed from the file's extension.
                var read = operation.LongitudeFileName is { } longitudeFileName
                    ? ReadNadconPair(directory, operation, path, longitudeFileName)
                    : ReadNtv2Bundle(path);

                if (read.Grid is not null)
                {
                    _served[datumName] = operation;
                    return read.Grid;
                }

                Record(operation.FileName, read.Reason);
            }
        }

        return null;
    }

    private static (DatumShiftGrid? Grid, string Reason) ReadNtv2Bundle(string path)
    {
        if (!Ntv2GridReader.TryRead(path, out var grids, out var reason))
        {
            return (null, reason!);
        }

        // A bundle holds one sub-grid per published block. A grid operation
        // names the datum it serves, so any sub-grid in it will do; a bundle
        // that somehow holds none is a failure, not an empty grid.
        return grids.Count > 0
            ? (grids[0], string.Empty)
            : (null, $"'{Path.GetFileName(path)}' holds no sub-grid to shift with.");
    }

    private static (DatumShiftGrid? Grid, string Reason) ReadNadconPair(
        string directory,
        EpsgGridShiftOperations.GridShiftOperation operation,
        string latitudePath,
        string longitudeFileName)
    {
        // A NADCON grid's shift record states no accuracy, so the figure the
        // operation is published with is the one its row carries. A row that
        // names no figure has nothing to publish, which is a row that cannot
        // be read rather than a grid with an accuracy of nothing.
        if (operation.AccuracyMetres is not { } accuracy)
        {
            return (null, $"The grid operation for {operation.GraphName} names a NADCON pair but no accuracy to publish it at.");
        }

        return NadconGridReader.TryRead(
            latitudePath,
            Path.Combine(directory, longitudeFileName),
            accuracy,
            out var grids,
            out var reason)
            ? grids.Count > 0
                ? (grids[0], string.Empty)
                : (null, $"'{Path.GetFileName(latitudePath)}' and '{longitudeFileName}' hold no sub-grid to shift with.")
            : (null, reason!);
    }

    /// <summary>
    /// Records a bundle problem once, so a host that probes the same broken
    /// file on every request reports one problem rather than thousands.
    /// </summary>
    private void Record(string fileName, string reason)
    {
        lock (_failures)
        {
            if (!_failures.Exists(failure => string.Equals(failure.FileName, fileName, StringComparison.Ordinal)))
            {
                _failures.Add(new GridLoadFailure(fileName, reason));
            }
        }
    }
}
