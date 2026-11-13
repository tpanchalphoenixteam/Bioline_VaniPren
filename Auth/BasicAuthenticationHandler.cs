using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;

namespace PH.SapB1.Vaniperen.Auth
{
    public sealed class BasicAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "BasicAuth";

        private readonly IApiUserValidator _apiUserValidator;

        public BasicAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder,
            IApiUserValidator apiUserValidator)
            : base(options, logger, encoder)
        {
            _apiUserValidator = apiUserValidator;
        }

        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("Authorization", out var authorizationHeader))
            {
                return AuthenticateResult.Fail("Authorization header is missing.");
            }

            if (!AuthenticationHeaderValue.TryParse(authorizationHeader, out var authHeader))
            {
                return AuthenticateResult.Fail("Invalid Authorization header.");
            }

            if (!string.Equals(authHeader.Scheme, "Basic", StringComparison.OrdinalIgnoreCase))
            {
                return AuthenticateResult.Fail("Invalid authentication scheme.");
            }

            if (string.IsNullOrWhiteSpace(authHeader.Parameter))
            {
                return AuthenticateResult.Fail("Basic Auth value is missing.");
            }

            string username;
            string password;

            try
            {
                var credentialBytes = Convert.FromBase64String(authHeader.Parameter);
                var credentials = Encoding.UTF8.GetString(credentialBytes);

                var separatorIndex = credentials.IndexOf(':');

                if (separatorIndex <= 0)
                {
                    return AuthenticateResult.Fail("Invalid Basic Auth format.");
                }

                username = credentials[..separatorIndex];
                password = credentials[(separatorIndex + 1)..];
            }
            catch
            {
                return AuthenticateResult.Fail("Invalid Basic Auth encoding.");
            }

            var validationResult = await _apiUserValidator.ValidateAsync(
                username,
                password,
                Context.RequestAborted);

            if (!validationResult.Succeeded)
            {
                return AuthenticateResult.Fail(validationResult.ErrorMessage);
            }

            var claims = new List<Claim>
{
    new Claim(
        ClaimTypes.Name,
        validationResult.Username),

    new Claim(
        "api_username",
        validationResult.Username),

    new Claim(
        "normal_card_code",
        validationResult.NormalCardCode),

    new Claim(
        "special_card_code",
        validationResult.SpecialCardCode),

    new Claim(
        "placeholder_item_code",
        validationResult.PlaceholderItemCode)
};

            foreach (var cardCode in validationResult.AllowedCardCodes)
            {
                claims.Add(
                    new Claim(
                        "allowed_card_code",
                        cardCode));
            }

            var identity = new ClaimsIdentity(claims, SchemeName);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, SchemeName);

            return AuthenticateResult.Success(ticket);
        }
    }
}
