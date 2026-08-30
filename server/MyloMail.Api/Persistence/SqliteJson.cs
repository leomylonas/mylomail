using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyloMail.Api.Persistence;

/// <summary>
/// The one serializer used for every JSON-valued column. Stored JSON is a persisted format,
/// so the options are fixed here rather than taken from ambient defaults that could change
/// underneath existing rows.
/// </summary>
internal static class SqliteJson
{
	public static readonly JsonSerializerOptions Options = new()
	{
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		PropertyNamingPolicy = null,
		WriteIndented = false,
	};

	public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

	public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
