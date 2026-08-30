using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using MyloMail.Api.Domain;

namespace MyloMail.Api.Persistence;

/// <summary>
/// JSON column conversions. Address lists, provider config and cursor state are values that
/// are only ever read and written whole; giving each its own table would add joins to the
/// list-view query for data that is never queried independently.
/// </summary>
internal static class JsonConversionExtensions
{
	/// <summary>
	/// Stores the property as JSON. The comparer is explicit: without one EF Core compares
	/// reference-typed values by reference, and an in-place edit of a list would not be
	/// detected as a change.
	/// </summary>
	public static PropertyBuilder<T?> HasJsonConversion<T>(this PropertyBuilder<T?> builder)
		where T : class
	{
		builder.HasConversion(
			value => SqliteJson.Serialize(value),
			json => SqliteJson.Deserialize<T>(json),
			new ValueComparer<T?>(
				(left, right) => SqliteJson.Serialize(left) == SqliteJson.Serialize(right),
				value => value == null ? 0 : SqliteJson.Serialize(value).GetHashCode(),
				value => value == null ? null : SqliteJson.Deserialize<T>(SqliteJson.Serialize(value))
			)
		);
		return builder;
	}

	/// <inheritdoc cref="HasJsonConversion{T}(PropertyBuilder{T})"/>
	public static PropertyBuilder<IReadOnlyList<T>> HasJsonConversion<T>(
		this PropertyBuilder<IReadOnlyList<T>> builder
	)
	{
		builder.HasConversion(
			value => SqliteJson.Serialize(value),
			json => SqliteJson.Deserialize<List<T>>(json) ?? new List<T>(),
			new ValueComparer<IReadOnlyList<T>>(
				(left, right) => SqliteJson.Serialize(left) == SqliteJson.Serialize(right),
				value => SqliteJson.Serialize(value).GetHashCode(),
				value => SqliteJson.Deserialize<List<T>>(SqliteJson.Serialize(value)) ?? new List<T>()
			)
		);
		return builder;
	}

	/// <summary>Address lists, stored as JSON on the owning row.</summary>
	public static PropertyBuilder<IReadOnlyList<Address>> HasAddressListConversion(
		this PropertyBuilder<IReadOnlyList<Address>> builder
	) => builder.HasJsonConversion();
}
