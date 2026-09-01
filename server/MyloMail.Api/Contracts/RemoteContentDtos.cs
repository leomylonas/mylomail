namespace MyloMail.Api.Contracts;

public record TrustedSenderDto(string Address);

public record TrustSenderRequest(string Address);
