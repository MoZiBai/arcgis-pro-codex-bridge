using System.IO;
using System.Text.Json;
using ArcGIS.Desktop.Mapping;

namespace ArcGISCodexBridgeAddin;

internal static class ExportSupport
{
    internal static ExportFormat CreateFormat(string output, JsonElement payload)
    {
        var dpi = Math.Clamp(BridgeSupport.Int(payload, "dpi", 300), 72, 1200);
        ExportFormat format = Path.GetExtension(output).ToLowerInvariant() switch
        {
            ".pdf" => new PDFFormat
            {
                OutputFileName = output,
                Resolution = dpi,
                DoEmbedFonts = BridgeSupport.Bool(payload, "embed_fonts", true),
                HasGeoRefInfo = BridgeSupport.Bool(payload, "georeference", true),
                IncludeAccessibilityTags = BridgeSupport.Bool(payload, "accessibility_tags", false),
                Title = BridgeSupport.String(payload, "title"),
                Author = BridgeSupport.String(payload, "author"),
                Subject = BridgeSupport.String(payload, "subject"),
                Keywords = BridgeSupport.String(payload, "keywords"),
            },
            ".png" => new PNGFormat
            {
                OutputFileName = output,
                Resolution = dpi,
                HasTransparentBackground = BridgeSupport.Bool(payload, "transparent_background", false),
                HasWorldFile = BridgeSupport.Bool(payload, "world_file", false),
            },
            ".jpg" or ".jpeg" => new JPEGFormat
            {
                OutputFileName = output,
                Resolution = dpi,
                ImageCompressionQuality = Math.Clamp(
                    BridgeSupport.Int(payload, "image_quality", 90), 1, 100),
                HasWorldFile = BridgeSupport.Bool(payload, "world_file", false),
            },
            ".tif" or ".tiff" => new TIFFFormat
            {
                OutputFileName = output,
                Resolution = dpi,
                HasTransparentBackground = BridgeSupport.Bool(payload, "transparent_background", false),
                HasWorldFile = BridgeSupport.Bool(payload, "world_file", false),
                HasGeoTiffTags = BridgeSupport.Bool(payload, "geotiff_tags", true),
            },
            ".svg" => new SVGFormat
            {
                OutputFileName = output,
                Resolution = dpi,
                DoEmbedFonts = BridgeSupport.Bool(payload, "embed_fonts", true),
            },
            ".svgz" => new SVGFormat
            {
                OutputFileName = output,
                Resolution = dpi,
                DoCompressToSVGZ = true,
                DoEmbedFonts = BridgeSupport.Bool(payload, "embed_fonts", true),
            },
            ".eps" => new EPSFormat
            {
                OutputFileName = output,
                Resolution = dpi,
                DoEmbedFonts = BridgeSupport.Bool(payload, "embed_fonts", true),
                DoFullRasterization = BridgeSupport.Bool(payload, "full_rasterization", false),
                DoConvertCharacterMarkerSymbolsToPolygon = BridgeSupport.Bool(
                    payload, "convert_character_markers", false),
            },
            ".aix" => new AIXFormat
            {
                OutputFileName = output,
                Resolution = dpi,
                DoEmbedFonts = BridgeSupport.Bool(payload, "embed_fonts", true),
                DoCompressVectorGraphics = BridgeSupport.Bool(payload, "compress_vector_graphics", true),
                DoConvertCharacterMarkerSymbolsToPolygon = BridgeSupport.Bool(
                    payload, "convert_character_markers", false),
                IncludeNonVisibleMapLayers = BridgeSupport.Bool(
                    payload, "include_nonvisible_map_layers", false),
            },
            _ => throw new ArgumentException(
                "Supported export formats are PDF, PNG, JPEG, TIFF, SVG, SVGZ, EPS, and AIX"),
        };
        format.DoShowSelectionSymbology = BridgeSupport.Bool(
            payload, "show_selection_symbology", false);
        return format;
    }
}
