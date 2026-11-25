using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace StepRecorder.Core;

/// <summary>session.json の読み書き。</summary>
public static class SessionStore
{
    public const string FileName = "session.json";

    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        // 日本語をエスケープせずに書き、手で読める JSON にする
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    public static void Save(Session session, string dir)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, FileName);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(session, Options));
        File.Move(tmp, path, overwrite: true); // 書きかけで壊さない
    }

    public static Session Load(string dir)
    {
        var s = JsonSerializer.Deserialize<Session>(File.ReadAllText(Path.Combine(dir, FileName)), Options)
            ?? throw new InvalidDataException($"{FileName} が空です");
        s.Upgrade();
        return s;
    }

    /// <summary>セッションの深い写し。</summary>
    public static Session Clone(Session s) =>
        JsonSerializer.Deserialize<Session>(JsonSerializer.Serialize(s, Options), Options)!;
}
