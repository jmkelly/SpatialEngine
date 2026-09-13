using System.Xml.Linq;

namespace Spatial.Adapter.Ogc.Tests;

/// <summary>
/// The OGC failure envelope (ADR-0052 §3): the exception report is well-formed
/// XML carrying the OGC code the adapter selected.
/// </summary>
public sealed class OgcXmlTests
{
    [Fact]
    public void The_service_exception_report_carries_the_code_and_message()
    {
        var xml = OgcXml.ServiceExceptionReport("LayerNotDefined", "Layer 'roads' is not defined.");

        var document = XDocument.Parse(xml);
        var report = document.Root!;
        Assert.Equal("ServiceExceptionReport", report.Name.LocalName);
        Assert.Equal("1.3.0", report.Attribute("version")!.Value);
        var exception = Assert.Single(report.Elements(XName.Get("ServiceException", "http://www.opengis.net/wms")));
        Assert.Equal("LayerNotDefined", exception.Attribute("code")!.Value);
        Assert.Equal("Layer 'roads' is not defined.", exception.Value);
    }
}
