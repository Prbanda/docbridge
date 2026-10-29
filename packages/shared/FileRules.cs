namespace DocBridge.Shared;

/// <summary>Supported file types (requirements: PDF, DOCX, XLSX, PNG, JPG; no executables) and the 1 GB ceiling.</summary>
public static class FileRules
{
    public const long MaxFileBytes = 1L << 30;

    public static readonly IReadOnlyDictionary<string, string[]> AllowedTypes = new Dictionary<string, string[]>
    {
        ["application/pdf"] = [".pdf"],
        ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = [".docx"],
        ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = [".xlsx"],
        ["image/png"] = [".png"],
        ["image/jpeg"] = [".jpg", ".jpeg"],
    };

    /// <summary>Content type must be allow-listed AND match the file name's extension.</summary>
    public static bool IsAllowed(string? contentType, string? fileName) =>
        contentType is not null && fileName is not null
        && AllowedTypes.TryGetValue(contentType, out var extensions)
        && extensions.Contains(Path.GetExtension(fileName).ToLowerInvariant());

    public static bool IsSha256Hex(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    public static string HexToBase64(string hex) => Convert.ToBase64String(Convert.FromHexString(hex));
}
