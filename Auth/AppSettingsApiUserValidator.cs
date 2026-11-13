using Microsoft.Extensions.Options;

namespace PH.SapB1.Vaniperen.Auth;

public sealed class AppSettingsApiUserValidator : IApiUserValidator
{
    private readonly BasicAuthOptions _options;

    public AppSettingsApiUserValidator(
        IOptions<BasicAuthOptions> options)
    {
        _options = options.Value;
    }

    public Task<ApiUserValidationResult> ValidateAsync(
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        var user = _options.Users.FirstOrDefault(x =>
            x.Active &&
            string.Equals(
                x.Username,
                username,
                StringComparison.OrdinalIgnoreCase));

        if (user is null)
        {
            return Task.FromResult(
                ApiUserValidationResult.Failed(
                    "Invalid username or password."));
        }

        if (!string.Equals(
                user.Password,
                password,
                StringComparison.Ordinal))
        {
            return Task.FromResult(
                ApiUserValidationResult.Failed(
                    "Invalid username or password."));
        }

        if (string.IsNullOrWhiteSpace(user.NormalCardCode) &&
            string.IsNullOrWhiteSpace(user.SpecialCardCode))
        {
            return Task.FromResult(
                ApiUserValidationResult.Failed(
                    "No CardCode is configured for this user."));
        }

        if (string.IsNullOrWhiteSpace(user.PlaceholderItemCode))
        {
            return Task.FromResult(
                ApiUserValidationResult.Failed(
                    "Placeholder item code is not configured for this user."));
        }

        return Task.FromResult(
            ApiUserValidationResult.Success(
                user.Username,
                user.NormalCardCode,
                user.SpecialCardCode,
                user.PlaceholderItemCode));
    }
}