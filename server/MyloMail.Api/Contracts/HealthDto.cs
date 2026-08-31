using Tapper;
namespace MyloMail.Api.Contracts;

/// <summary>The health probe's response body (§9).</summary>
[TranspilationSource]
public record HealthDto(string Status);
