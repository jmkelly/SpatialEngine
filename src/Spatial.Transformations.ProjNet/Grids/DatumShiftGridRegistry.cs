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
    public static readonly DatumShiftGridRegistry Empty = new([]);

    private readonly string[] _directories;
    private readonly ConcurrentDictionary<string, DatumShiftGrid?> _loaded = new(StringComparer.Ordinal);
    private readonly List<GridLoadFailure> _failures = [];

    private DatumShiftGridRegistry(IReadOnlyList<string> directories) => _directories = [.. directories];

    /// <summary>
    /// Builds a registry over a priority-ordered set of directories. The
    /// order given is the order kept; a directory that does not exist is
    /// remembered (so an operator sees it echoed back) and otherwise ignored.
    /// </summary>
    public static DatumShiftGridRegistry Load(IReadOnlyList<string> directories) => new(directories);

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
    /// Resolves one datum's grid from the first directory holding its bundle.
    /// A directory that cannot supply it — because the file is absent, or
    /// because it is present and unreadable — is passed over so that a later
    /// directory can still serve it; a file that is present and broken is
    /// recorded once, because an operator needs to be told about it.
    /// </summary>
    private DatumShiftGrid? Load(string datumName)
    {
        var operation = EpsgGridShiftOperations.For(datumName);
        if (operation is null)
        {
            return null;
        }

        foreach (var directory in _directories)
        {
            var path = Path.Combine(directory, operation.FileName);
            if (!File.Exists(path))
            {
                continue;
            }

            if (!Ntv2GridReader.TryRead(path, out var grids, out var reason))
            {
                Record(operation.FileName, reason!);
                continue;
            }

            // A bundle holds one sub-grid per published block. A grid
            // operation names the datum it serves, so any sub-grid in it will
            // do; a bundle that somehow holds none is a failure, not an empty
            // grid.
            if (grids.Count == 0)
            {
                Record(operation.FileName, $"'{Path.GetFileName(path)}' holds no sub-grid to shift with.");
                continue;
            }

            return grids[0];
        }

        return null;
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
