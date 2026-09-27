using System.Collections;
using System.Reflection;
using System.Text.Json;
using Cove.HevcReencode;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
Check(!new ReencodeSettings().EnableRetries, "New settings must disable aggressive retries");
Check(!JsonSerializer.Deserialize<ReencodeSettings>("{}", jsonOptions)!.EnableRetries, "Missing retry setting must default false");
foreach (var enabled in new[] { false, true })
{
    var value = JsonSerializer.Deserialize<ReencodeSettings>($"{{\"enableRetries\":{enabled.ToString().ToLowerInvariant()},\"cq\":23}}", jsonOptions)!;
    Check(value.EnableRetries == enabled && value.Cq == 23, "Saved overrides must survive");
}
var build = typeof(HevcReencodeExtension).GetMethod("BuildEncodeMethods", BindingFlags.NonPublic | BindingFlags.Static)!;
foreach (var encoder in new[] { "hevc_nvenc", "av1_nvenc", "hevc_amf", "av1_amf" })
foreach (var low in new[] { false, true })
{
    var settings = new ReencodeSettings { OutputFormat = encoder.StartsWith("av1") ? "av1" : "hevc" };
    var methods = ((IEnumerable)build.Invoke(null, new object[] { encoder, settings.OutputFormat, low, settings })!).Cast<object>().ToArray();
    Check(methods.Length == (settings.OutputFormat == "hevc" ? 2 : 1), "Retries must be absent by default");
    foreach (var method in methods)
    {
        var arguments = ((IEnumerable<string>)method.GetType().GetProperty("Args")!.GetValue(method)!).ToArray();
        string Value(string key) => arguments[Array.IndexOf(arguments, key) + 1];
        Check(!arguments.Contains("-qp") && !arguments.Contains("-qp_i"), "Constant QP must not remain");
        var expected = settings.OutputFormat == "av1" ? low ? settings.Av1LowBitrateCq : settings.Av1Cq : low ? settings.CqLowBitrate : settings.Cq;
        if (encoder.EndsWith("nvenc"))
        {
            Check(Value("-rc") == "vbr" && Value("-b:v") == "0", "NVENC must use unconstrained quality VBR");
            Check(Value("-cq") == expected.ToString(), "CQ must use selected normal/low setting");
            Check(Value("-rc-lookahead") == "32" && Value("-aq-strength") == "8", "NVENC tuning must match");
        }
        else
        {
            Check(Value("-rc") == "qvbr" && Value("-preanalysis") == "1" && Value("-b:v") == "0", "AMF requires QVBR and preanalysis");
            Check(Value("-qvbr_quality_level") == Math.Clamp(expected, 1, 51).ToString(), "AMF QVBR range");
            Check(Value("-quality") == (encoder.StartsWith("av1") ? "high_quality" : "quality"), "AMF codec-specific preset");
        }
    }
}
Console.WriteLine("Encoder configuration and saved/default settings checks passed.");
if (args.Contains("--gpu"))
{
    var probe = typeof(HevcReencodeExtension).GetMethod("ProbeGpuEncoderAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
    foreach (var encoder in new[] { "hevc_nvenc", "av1_nvenc" })
    {
        var result = await (Task<string?>)probe.Invoke(null, new object[] { "ffmpeg", encoder, CancellationToken.None })!;
        Check(result is null, $"{encoder}: {result}");
        Console.WriteLine($"{encoder}: production health probe passed.");
    }
}
