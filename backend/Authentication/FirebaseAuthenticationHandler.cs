using System;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using FirebaseAdmin.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PrPrioritizerService.Services
{
    public class FirebaseAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public FirebaseAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder,
            ISystemClock clock)
            : base(options, logger, encoder, clock) { }

        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey("Authorization"))
            {
                return AuthenticateResult.Fail("Missing Authorization Header.");
            }

            string authHeader = Request.Headers["Authorization"]!;
            if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return AuthenticateResult.Fail("Invalid Authorization Header format.");
            }

            string token = authHeader.Substring("Bearer ".Length).Trim();

            try
            {
                // Verify the JWT token via Firebase Admin SDK
                FirebaseToken decodedToken = await FirebaseAuth.DefaultInstance.VerifyIdTokenAsync(token);

                var claims = new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, decodedToken.Uid),
                    new Claim(ClaimTypes.Email, decodedToken.Claims.TryGetValue("email", out var email) ? email?.ToString() ?? "" : "")
                };

                var identity = new ClaimsIdentity(claims, nameof(FirebaseAuthenticationHandler));
                var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);

                return AuthenticateResult.Success(ticket);
            }
            catch (Exception ex)
            {
                return AuthenticateResult.Fail($"Firebase token validation failed: {ex.Message}");
            }
        }
    }
}