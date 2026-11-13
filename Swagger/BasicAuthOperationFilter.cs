using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace PH.SapB1.Vaniperen.Swagger;

// Adds the Basic auth requirement (padlock + 401/403 responses) only to
// endpoints that require authorization, so /health and /setup stay open in Swagger.
public sealed class BasicAuthOperationFilter : IOperationFilter
{
    public const string SchemeId = "basic";

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var metadata = context.ApiDescription.ActionDescriptor.EndpointMetadata;

        var requiresAuth =
            metadata.OfType<IAuthorizeData>().Any() &&
            !metadata.OfType<IAllowAnonymous>().Any();

        if (!requiresAuth)
        {
            return;
        }

        operation.Security ??= new List<OpenApiSecurityRequirement>();
        operation.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = SchemeId
                }
            }] = Array.Empty<string>()
        });

        operation.Responses.TryAdd("401", new OpenApiResponse { Description = "Unauthorized" });
    }
}
