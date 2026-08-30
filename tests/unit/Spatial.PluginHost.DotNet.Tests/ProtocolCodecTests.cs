using System.Globalization;
using System.Text.Json.Nodes;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.PluginHost.DotNet.Manifest;
using Spatial.PluginHost.DotNet.Protocol;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Resources;
using Spatial.PluginSdk.Transformations;

namespace Spatial.PluginHost.DotNet.Tests;

/// <summary>
/// Phase 5 worker wire protocol (ADR-0025): the versioned envelope codec, the
/// inline value codec (scalars, $i64, $bytes, $resource — and the deliberate
/// rejection of spatial values, which require canonical binary interchange,
/// ADR-0020) and the shared payload builders/readers.
/// </summary>
public sealed class ProtocolCodecTests
{
    [Fact]
    public void Envelope_round_trips_through_the_wire_shape()
    {
        var payload = new JsonObject { ["n"] = 7 };
        var line = WorkerWireCodec.Encode(new WorkerEnvelope(WorkerProtocol.Version, WorkerProtocol.Ping, "abc", payload));
        var envelope = WorkerWireCodec.Decode(line);

        Assert.Equal(WorkerProtocol.Version, envelope.Protocol);
        Assert.Equal(WorkerProtocol.Ping, envelope.Type);
        Assert.Equal("abc", envelope.Id);
        Assert.Equal(7, envelope.Payload!["n"]!.GetValue<int>());
    }

    [Fact]
    public void Envelope_omits_absent_id_and_payload()
    {
        var line = WorkerWireCodec.Encode(new WorkerEnvelope(WorkerProtocol.Version, WorkerProtocol.Hello, null, null));
        Assert.DoesNotContain("\"id\"", line);
        Assert.DoesNotContain("\"payload\"", line);
        Assert.Equal(WorkerProtocol.Hello, WorkerWireCodec.Decode(line).Type);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1,2,3]")]
    [InlineData("\"just a string\"")]
    public void Malformed_envelopes_are_rejected(string line)
    {
        Assert.Throws<WorkerProtocolException>(() => WorkerWireCodec.Decode(line));
    }

    [Fact]
    public void Wrong_protocol_and_missing_type_are_rejected()
    {
        Assert.Throws<WorkerProtocolException>(() => WorkerWireCodec.Decode("{\"protocol\":\"spatial.worker/2\",\"type\":\"ping\"}"));
        Assert.Throws<WorkerProtocolException>(() => WorkerWireCodec.Decode("{\"protocol\":\"spatial.worker/1\"}"));
        Assert.Throws<WorkerProtocolException>(() =>
            WorkerWireCodec.Encode(new WorkerEnvelope(WorkerProtocol.Version, string.Empty, null, null)));
    }

    [Fact]
    public void Scalar_values_round_trip()
    {
        Assert.Null(WorkerValueCodec.Decode(WorkerValueCodec.Encode(null)));
        Assert.Equal(true, WorkerValueCodec.Decode(WorkerValueCodec.Encode(true)));
        Assert.Equal(5, WorkerValueCodec.Decode(WorkerValueCodec.Encode(5)));
        Assert.Equal(80L, WorkerValueCodec.Decode(WorkerValueCodec.Encode(80L)));
        Assert.Equal(2.5, WorkerValueCodec.Decode(WorkerValueCodec.Encode(2.5)));
        Assert.Equal("hello", WorkerValueCodec.Decode(WorkerValueCodec.Encode("hello")));
    }

    [Fact]
    public void Int64_travels_as_a_tagged_decimal_string()
    {
        var node = WorkerValueCodec.Encode(80L);
        Assert.IsType<JsonObject>(node);
        Assert.Equal("80", node!["$i64"]!.GetValue<string>());
        Assert.Equal(80L, WorkerValueCodec.Decode(node));

        var huge = 9_000_000_000L;
        Assert.Equal(huge, WorkerValueCodec.Decode(WorkerValueCodec.Encode(huge)));
    }

    [Fact]
    public void Integers_decode_to_int_when_they_fit()
    {
        Assert.Equal(5, WorkerValueCodec.Decode(JsonNode.Parse("5")));
        Assert.Equal(80, WorkerValueCodec.Decode(JsonNode.Parse("80")));
        Assert.Equal(2.5, WorkerValueCodec.Decode(JsonNode.Parse("2.5")));
    }

    [Fact]
    public void Bytes_travel_as_base64()
    {
        var bytes = new byte[] { 1, 2, 3, 255 };
        var node = WorkerValueCodec.Encode(bytes);
        Assert.Equal("AQID/w==", node!["$bytes"]!.GetValue<string>());
        Assert.Equal(bytes, WorkerValueCodec.Decode(node));
    }

    [Fact]
    public void Geometry_travels_as_tagged_canonical_binary()
    {
        // Phase 6 (ADR-0026/0020): geometry crosses the worker boundary as
        // canonical binary interchange (SGEOM) in a dedicated $geometry tag,
        // never as JSON geometry.
        var polygon = GeometryFactory.CreatePolygon(
        [
            new Coordinate(0, 0),
            new Coordinate(4, 0),
            new Coordinate(4, 4),
            new Coordinate(0, 4),
            new Coordinate(0, 0),
        ], new CoordinateReference("EPSG", "4326"));

        var node = WorkerValueCodec.Encode(polygon);
        var tag = Assert.IsType<JsonObject>(node);
        var base64 = tag["$geometry"]!.GetValue<string>();
        Assert.StartsWith("U0dFT00", base64); // 'SGEOM' magic, base64
        Assert.Equal(GeometryCodec.Encode(polygon), Convert.FromBase64String(base64));

        var back = Assert.IsType<Polygon>(WorkerValueCodec.Decode(node));
        Assert.Equal(polygon, back);
        Assert.Equal(new CoordinateReference("EPSG", "4326"), back.CoordinateReference);
    }

    [Fact]
    public void Geometry_arguments_and_results_round_trip_through_the_invoke_payload()
    {
        var point = GeometryFactory.CreatePoint(1.5, -2.25);
        var payload = WorkerPayload.Invoke(
            "spatial.geometry.buffer@1",
            new Dictionary<string, object?> { ["geometry"] = point, ["distance"] = 1.0 },
            [],
            null);

        Assert.Equal(point, Assert.IsType<Point>(WorkerValueCodec.Decode(payload["arguments"]!["geometry"])));
        Assert.Equal(point, Assert.IsType<Point>(WorkerValueCodec.Decode(WorkerValueCodec.Encode(point))));
        Assert.Equal(1.0, WorkerValueCodec.Decode(payload["arguments"]!["distance"]));

        var success = WorkerPayload.ResultSuccess(point);
        var outcome = WorkerPayload.ReadOutcome(success);
        Assert.Equal(point, Assert.IsType<Point>(outcome.Value));
    }

    [Fact]
    public void Malformed_geometry_tags_are_actionable()
    {
        var notBase64 = Assert.Throws<WorkerValueException>(() =>
            WorkerValueCodec.Decode(JsonNode.Parse("{\"$geometry\":\"!!!\"}")));
        Assert.Contains("base64", notBase64.Message);

        var notCanonical = Assert.Throws<WorkerValueException>(() =>
            WorkerValueCodec.Decode(JsonNode.Parse("{\"$geometry\":\"aGVsbG8=\"}")));
        Assert.Contains("canonical geometry", notCanonical.Message);
    }

    [Fact]
    public void Resource_handles_travel_as_opaque_tags()
    {
        var handle = new ResourceHandle(
            new ResourceId(Guid.Parse("11111111-2222-3333-4444-555555555555")),
            ResourceKind.Parse("fixture.dataset"),
            ProviderId.Parse("fixture@1"),
            DateTimeOffset.Parse("2025-01-02T03:04:05.0000000Z", CultureInfo.InvariantCulture));

        var node = WorkerValueCodec.Encode(handle);
        var back = Assert.IsType<ResourceHandle>(WorkerValueCodec.Decode(node));

        Assert.Equal(handle.Id, back.Id);
        Assert.Equal(handle.Kind, back.Kind);
        Assert.Equal(handle.Owner, back.Owner);
        Assert.Equal(handle.CreatedAt, back.CreatedAt);
    }

    [Fact]
    public void Provider_ids_travel_as_their_canonical_string()
    {
        var node = WorkerValueCodec.Encode(ProviderId.Parse("fixture@1"));
        Assert.Equal("fixture@1", node!.GetValue<string>());
    }

    [Fact]
    public void Crs_descriptions_travel_as_tagged_objects()
    {
        // Phase 7 (ADR-0027): a structured CRS description crosses the worker
        // boundary in a dedicated $crs tag, the same explicit-tag pattern as
        // $geometry/$bytes — never as bare JSON objects.
        var description = new CrsDescription(
            "EPSG",
            "4326",
            "WGS 84",
            CrsKind.Geographic,
            2,
            [new CrsAxis("Lon", AxisOrientation.East, "degree"), new CrsAxis("Lat", AxisOrientation.North, "degree")],
            "World Geodetic System 1984",
            new CrsEllipsoid("WGS 84", 6378137.0, 6356752.314245179, "metre"));

        var node = WorkerValueCodec.Encode(description);
        var tag = Assert.IsType<JsonObject>(node);
        var inner = Assert.IsType<JsonObject>(tag["$crs"]);
        Assert.Equal("EPSG", inner["authority"]!.GetValue<string>());
        Assert.Equal("geographic", inner["kind"]!.GetValue<string>());

        var back = Assert.IsType<CrsDescription>(WorkerValueCodec.Decode(node));
        Assert.Equal(description.Authority, back.Authority);
        Assert.Equal(description.Code, back.Code);
        Assert.Equal(description.Name, back.Name);
        Assert.Equal(description.Kind, back.Kind);
        Assert.Equal(description.Dimension, back.Dimension);
        Assert.Equal(description.Datum, back.Datum);
        Assert.Equal(description.Axes.Select(axis => (axis.Name, axis.Orientation, axis.UnitName)),
            back.Axes.Select(axis => (axis.Name, axis.Orientation, axis.UnitName)));
        Assert.Equal(description.Ellipsoid!.Name, back.Ellipsoid!.Name);
        Assert.Equal(description.Ellipsoid.SemiMajorAxis, back.Ellipsoid.SemiMajorAxis);
        Assert.Equal(description.Ellipsoid.SemiMinorAxis, back.Ellipsoid.SemiMinorAxis);
        Assert.Equal(description.Ellipsoid.UnitName, back.Ellipsoid.UnitName);
    }

    [Fact]
    public void Malformed_crs_tags_are_actionable()
    {
        var missingFields = Assert.Throws<WorkerValueException>(() =>
            WorkerValueCodec.Decode(JsonNode.Parse("{\"$crs\":{\"authority\":\"EPSG\"}}")));
        Assert.Contains("structured CRS description", missingFields.Message);
        Assert.Contains("'code'", missingFields.Message);

        var badKind = Assert.Throws<WorkerValueException>(() =>
            WorkerValueCodec.Decode(JsonNode.Parse(
                "{\"$crs\":{\"authority\":\"EPSG\",\"code\":\"4326\",\"name\":\"WGS 84\","
                + "\"kind\":\"warp\",\"dimension\":2,\"axes\":[]}}")));
        Assert.Contains("not a valid CrsKind", badKind.Message);

        var notAnObject = Assert.Throws<WorkerValueException>(() =>
            WorkerValueCodec.Decode(JsonNode.Parse("{\"$crs\":\"EPSG:4326\"}")));
        Assert.Contains("$crs", notAnObject.Message);
    }

    [Fact]
    public void Spatial_values_are_rejected_with_an_override_hint()
    {
        var feature = new FeatureBatch(
            new FeatureSchema([new FieldDefinition("count", AttributeKind.Int64)]), []);
        var exception = Assert.Throws<WorkerValueException>(() => WorkerValueCodec.Encode(feature));
        Assert.Contains("canonical binary interchange (ADR-0020)", exception.Message);
        Assert.Throws<WorkerValueException>(() => WorkerValueCodec.Encode(new object()));
        Assert.Throws<WorkerValueException>(() => WorkerValueCodec.Decode(JsonNode.Parse("[1,2]")));
        Assert.Throws<WorkerValueException>(() => WorkerValueCodec.Decode(JsonNode.Parse("{\"x\":1}")));
    }

    [Fact]
    public void Tag_errors_are_actionable()
    {
        Assert.Throws<WorkerValueException>(() => WorkerValueCodec.Decode(JsonNode.Parse("{\"$i64\":\"not-a-number\"}")));
        Assert.Throws<WorkerValueException>(() => WorkerValueCodec.Decode(JsonNode.Parse("{\"$bytes\":\"!!!\"}")));
        Assert.Throws<WorkerValueException>(() => WorkerValueCodec.Decode(JsonNode.Parse("{\"$resource\":{}}")));
        Assert.Throws<WorkerValueException>(() => WorkerValueCodec.Decode(JsonNode.Parse("{\"$resource\":{\"token\":\"x\",\"kind\":\"a\",\"owner\":\"b@1\",\"createdAt\":\"2025-01-01T00:00:00Z\"}}")));
    }

    [Fact]
    public void Invoke_payload_builds_and_round_trips_arguments_and_deadline()
    {
        var deadline = DateTimeOffset.Parse("2025-06-01T12:00:00Z", CultureInfo.InvariantCulture);
        var payload = WorkerPayload.Invoke(
            "spatial.fixture.sleep@1",
            new Dictionary<string, object?> { ["milliseconds"] = 80L, ["label"] = "x" },
            ["spatial.fixture.read"],
            deadline);

        Assert.Equal("spatial.fixture.sleep@1", payload["capability"]!.GetValue<string>());
        Assert.Equal(80L, WorkerValueCodec.Decode(payload["arguments"]!["milliseconds"]));
        Assert.Equal("x", WorkerValueCodec.Decode(payload["arguments"]!["label"]));
        Assert.Equal("x", payload["arguments"]!["label"]!.GetValue<string>());
        Assert.Equal(deadline, DateTimeOffset.Parse(payload["deadline"]!.GetValue<string>(), CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Result_outcomes_round_trip_success_and_failure()
    {
        var success = WorkerPayload.ResultSuccess(80L);
        var outcome = WorkerPayload.ReadOutcome(success);
        Assert.Equal(WorkerOutcomeKind.Success, outcome.Kind);
        Assert.Equal(80L, outcome.Value);

        var failure = WorkerPayload.ResultFailure(CapabilityError.Cancelled(CapabilityId.Parse("spatial.fixture.sleep@1")));
        var failed = WorkerPayload.ReadOutcome(failure);
        Assert.Equal(WorkerOutcomeKind.Failure, failed.Kind);
        Assert.Equal(CapabilityErrorKind.Cancelled, failed.Error!.Kind);
        Assert.Equal("operation.cancelled", failed.Error.Code);
    }

    [Fact]
    public void Hello_payload_builds_and_reads()
    {
        var manifest = FixtureManifest.V1();
        var hello = WorkerPayload.Hello(manifest);
        var document = WorkerPayload.ReadHello(hello);

        Assert.Equal("fixture@1", document.Id);
        Assert.Equal("dotnet", document.Runtime);
        Assert.Equal(FixtureManifest.AssemblyName, document.Assembly);
        Assert.Equal(FixtureManifest.V1ProviderType, document.AssemblyType);
        Assert.Equal(manifest.Capabilities.Select(capability => capability.Id), document.Capabilities);
    }

    [Fact]
    public void Unknown_and_missing_result_shapes_are_rejected()
    {
        Assert.Throws<WorkerProtocolException>(() => WorkerPayload.ReadOutcome(JsonNode.Parse("{\"kind\":\"maybe\"}")));
        Assert.Throws<WorkerProtocolException>(() => WorkerPayload.ReadOutcome(JsonNode.Parse("{\"kind\":\"failure\"}")));
        Assert.Throws<WorkerProtocolException>(() => WorkerPayload.ReadHello(JsonNode.Parse("{}")));
    }
}
