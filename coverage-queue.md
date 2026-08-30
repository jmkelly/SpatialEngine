**Authored-code coverage**: 90.9% lines  85.3% branches  (1073/1181 lines)
**Queue**: 32 untested authored methods, 42 uncovered lines (worst 30 below; full list in coverage-report.json).

| Cx   | UncovLn | Method                                  | Location                      |
| ---: | ---:    | ---                                     | ---                           |
| 10   | 12      | GeometryCodec.TryReadPolygon            | GeometryCodec.cs:493          |
| 8    | 8       | GeometryCodec.TryReadNodeHeader         | GeometryCodec.cs:300          |
| 14   | 6       | GeometryCodec.TryReadCrs                | GeometryCodec.cs:362          |
| 4    | 6       | GeometryFactory.CreatePolygon           | GeometryFactory.cs:56         |
| 1    | 5       | GeometryCodec.Fail                      | GeometryCodec.cs:677          |
| 4    | 4       | GeometryCodec.TryReadMultiLineString    | GeometryCodec.cs:551          |
| 4    | 4       | GeometryCodec.TryReadMultiPolygon       | GeometryCodec.cs:569          |
| 16   | 3       | GeometryCodec.WriteNode                 | GeometryCodec.cs:156          |
| 8    | 3       | GeometryCodec.TryReadPoint              | GeometryCodec.cs:408          |
| 6    | 3       | GeometryCodec.TryReadLineString         | GeometryCodec.cs:464          |
| 4    | 3       | GeometryCodec.TryReadDoubles            | GeometryCodec.cs:659          |
| 2    | 3       | Reader.TryReadByte                      | GeometryCodec.cs:706          |
| 6    | 2       | Reader.TryReadString                    | GeometryCodec.cs:740          |
| 4    | 2       | GeometryAggregates.AllEmpty             | GeometryAggregates.cs:54      |
| 4    | 2       | GeometryCodec.WriteParts                | GeometryCodec.cs:221          |
| 4    | 2       | GeometryCodec.TryReadGeometryCollection | GeometryCodec.cs:587          |
| 2    | 2       | GeometryCodec.Encode                    | GeometryCodec.cs:54           |
| 2    | 2       | Envelope.ToString                       | Envelope.cs:164               |
| 2    | 2       | Reader.TryReadInt32                     | GeometryCodec.cs:718          |
| 16   | 1       | GeometryCodec.ComputeBodyLength         | GeometryCodec.cs:138          |
| 14   | 1       | GeometryComparer.GetHashCode            | GeometryComparer.cs:39        |
| 11   | 1       | ArrayCoordinateSequence.GetOrdinate     | ArrayCoordinateSequence.cs:49 |
| 10   | 1       | GeometryTraversal.Parts                 | GeometryTraversal.cs:10       |
| 8    | 1       | GeometryCodec.TryDecodeBody             | GeometryCodec.cs:336          |
| 2    | 1       | MultiLineString.Equals                  | MultiLineString.cs:20         |
| 2    | 1       | MultiPoint.Equals                       | MultiPoint.cs:20              |
| 2    | 1       | MultiPolygon.Equals                     | MultiPolygon.cs:20            |
| 2    | 1       | GeometryCollection.Equals               | GeometryCollection.cs:23      |
| 2    | 1       | Point.get_Z                             | Point.cs:37                   |
| 2    | 1       | Point.get_M                             | Point.cs:39                   |

**Gate**: authored branch coverage 85.3% >= 70% (default policy) -> PASS

Generated code is excluded (hard-coded): .cshtml, Migrations/, compiler-generated async/lambda classes, obj/. Trend: coverage-history.csv.

Fix (implementor): add REAL unit/integration tests for the untested methods at the top of this queue (most uncovered lines first) — mocks/smoke tests only where the method is I/O-bound. Never ExcludeFromCodeCoverage or fake tests.
