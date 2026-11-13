using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PH.SapB1.Vaniperen.Auth;
using PH.SapB1.Vaniperen.Logging;
using PH.SapB1.Vaniperen.Orders;
using PH.SapB1.Vaniperen.Sap;
using PH.SapB1.Vaniperen.Setup;
using Serilog;
using System.Security.Claims;
using System.Text.Json.Nodes;
using PH.SapB1.Vaniperen.Audit;
using PH.SapB1.Vaniperen.Swagger;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// Load dynamic SAP metadata JSON for UDT/UDF creation
builder.Configuration.AddJsonFile(
    "sap-metadata.json",
    optional: false,
    reloadOnChange: true);

// JSON response should be camelCase:
// Count => count
// Results => results
// IntegrationId => integrationId


builder.Host.UseSerilog((context, services, configuration) =>
{
    configuration.ReadFrom.Configuration(context.Configuration);
});

builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});

// Configuration sections
builder.Services.Configure<BasicAuthOptions>(
    builder.Configuration.GetSection("BasicAuth"));

builder.Services.Configure<SapServiceLayerOptions>(
    builder.Configuration.GetSection("SapServiceLayer"));

builder.Services.Configure<SapMetadataOptions>(
    builder.Configuration.GetSection("SetupMetadata"));

builder.Services.Configure<OrderQueryOptions>(
    builder.Configuration.GetSection("OrderQuery"));

builder.Services.Configure<OrderPostingOptions>(
    builder.Configuration.GetSection("OrderPosting"));

// Basic Auth validator
// Currently this uses appsettings BasicAuth users.
// Later, replace AppSettingsApiUserValidator with SAP UDT-based validator.
builder.Services.AddSingleton<IApiUserValidator, AppSettingsApiUserValidator>();

builder.Services
    .AddAuthentication(BasicAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, BasicAuthenticationHandler>(
        BasicAuthenticationHandler.SchemeName,
        _ => { });

builder.Services.AddAuthorization();

// SAP Service Layer HttpClient
builder.Services.AddHttpClient("SapServiceLayer", (sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<SapServiceLayerOptions>>().Value;

    client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(120);
})
.ConfigurePrimaryHttpMessageHandler(sp =>
{
    var options = sp.GetRequiredService<IOptions<SapServiceLayerOptions>>().Value;

    var handler = new HttpClientHandler
    {
        UseCookies = false
    };

    if (options.IgnoreSslCertificateErrors)
    {
        handler.ServerCertificateCustomValidationCallback =
            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
    }

    return handler;
});

// Application services
builder.Services.AddSingleton<SapSessionManager>();
builder.Services.AddScoped<SapServiceLayerRawClient>();
builder.Services.AddScoped<SapMetadataInstaller>();
builder.Services.AddScoped<SapOrderReadService>();
builder.Services.AddScoped<SapOrderSubmitService>();
builder.Services.AddScoped<OrderPostAuditService>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    // Lets testers use the "Authorize" button in Swagger UI with the same
    // Basic auth credentials they would send via curl.
    options.AddSecurityDefinition(BasicAuthOperationFilter.SchemeId, new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "basic",
        Description = "Basic authentication using your API username and password."
    });

    options.OperationFilter<BasicAuthOperationFilter>();
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<HttpRequestResponseLoggingMiddleware>();

app.UseAuthentication();
app.UseAuthorization();

// Configurable base path
// If Api:BasePath = "" then endpoint will be exactly /get/orders
// If Api:BasePath = "/api/v1" then endpoint will be /api/v1/get/orders
var configuredBasePath = builder.Configuration["Api:BasePath"] ?? "";
var trimmedBasePath = configuredBasePath.Trim('/');

var api = string.IsNullOrWhiteSpace(trimmedBasePath)
    ? app.MapGroup("")
    : app.MapGroup("/" + trimmedBasePath);

// Public health endpoint
api.MapGet("/health", () =>
{
    return Results.Ok(new
    {
        status = "OK",
        service = "PH SAP B1 Vaniperen Order Gateway API",
        timeUtc = DateTimeOffset.UtcNow
    });
});

// Internal setup endpoint for UDT/UDF creation
// This uses X-Setup-Key, not Basic Auth.
api.MapPost("/setup/install-metadata",
    async (
        HttpContext httpContext,
        IConfiguration configuration,
        SapMetadataInstaller installer,
        CancellationToken cancellationToken
    ) =>
    {
        var setupKey = configuration["Setup:SetupKey"];

        if (string.IsNullOrWhiteSpace(setupKey))
        {
            return Results.Problem("Setup key is not configured.");
        }

        if (!httpContext.Request.Headers.TryGetValue("X-Setup-Key", out var providedSetupKey) ||
            providedSetupKey != setupKey)
        {
            return Results.Unauthorized();
        }

        var result = await installer.InstallAsync(cancellationToken);

        if (result.Success)
        {
            return Results.Ok(result);
        }

        return Results.BadRequest(result);
    });

// All endpoints below this require Basic Auth
var protectedApi = api.MapGroup("")
    .RequireAuthorization();

protectedApi.MapGet("/whoami", (ClaimsPrincipal user) =>
{
    var username =
        user.FindFirstValue("api_username");

    var normalCardCode =
        user.FindFirstValue("normal_card_code");

    var specialCardCode =
        user.FindFirstValue("special_card_code");

    var placeholderItemCode =
        user.FindFirstValue("placeholder_item_code");

    var allowedCardCodes = user
        .FindAll("allowed_card_code")
        .Select(x => x.Value)
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    return Results.Ok(new
    {
        username,
        normalCardCode,
        specialCardCode,
        placeholderItemCode,
        allowedCardCodes
    });
});

// Main GET orders endpoint
protectedApi.MapGet("/get/orders",
    async (
        [FromQuery] string? dateFrom,
        [FromQuery] string? customerOrderNumber,
        [FromQuery] string? status,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        ClaimsPrincipal user,
        SapOrderReadService orderReadService,
        CancellationToken cancellationToken
    ) =>
    {
        var username = user.FindFirstValue("api_username");
        var allowedCardCodes = user.FindAll("allowed_card_code")
     .Select(x => x.Value)
     .Where(x => !string.IsNullOrWhiteSpace(x))
     .Distinct(StringComparer.OrdinalIgnoreCase)
     .ToList();

        if (string.IsNullOrWhiteSpace(username) || allowedCardCodes.Count == 0)
        {
            return Results.Unauthorized();
        }

        var query = new GetOrdersQuery
        {
            DateFrom = dateFrom,
            CustomerOrderNumber = customerOrderNumber,
            Status = status,
            Page = page,
            PageSize = pageSize
        };

        try
        {
            var response = await orderReadService.GetOrdersAsync(
     query,
     allowedCardCodes,
     cancellationToken);
            return Results.Ok(response);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new
            {
                message = ex.Message
            });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(
                title: "Failed to read orders from SAP Service Layer.",
                detail: ex.Message,
                statusCode: StatusCodes.Status502BadGateway);
        }
        catch (Exception ex)
        {
            return Results.Problem(
                title: "Unexpected error while reading orders.",
                detail: ex.Message,
                statusCode: StatusCodes.Status500InternalServerError);
        }
    });

// Optional old placeholder endpoints.
// You can remove these later once final APIs are ready.
protectedApi.MapGet("/orders/{docEntry:int}", (int docEntry, ClaimsPrincipal user) =>
{
    var username = user.FindFirstValue("api_username");
    var cardCode = user.FindFirstValue("card_code");

    return Results.Ok(new
    {
        message = "Old placeholder GET order endpoint. Main endpoint is /get/orders.",
        docEntry,
        authenticatedUser = username,
        allowedCardCode = cardCode
    });
});

protectedApi.MapPost("/orders", (JsonObject payload, ClaimsPrincipal user) =>
{
    var username = user.FindFirstValue("api_username");
    var cardCode = user.FindFirstValue("card_code");

    return Results.Ok(new
    {
        message = "Old placeholder POST order endpoint. Final post order endpoint will be added later.",
        authenticatedUser = username,
        allowedCardCode = cardCode,
        receivedPayload = payload
    });
});

protectedApi.MapPost("/post/orders",
    async (
        SubmitOrderRequest request,
        ClaimsPrincipal user,
        HttpContext httpContext,
        SapOrderSubmitService orderSubmitService,
        OrderPostAuditService auditService,
        CancellationToken cancellationToken
    ) =>
    {
        var username =
            user.FindFirstValue("api_username") ?? "";

        var placeholderItemCode =
            user.FindFirstValue("placeholder_item_code") ?? "";

        var allowedCardCodes = user
            .FindAll("allowed_card_code")
            .Select(x => x.Value)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (string.IsNullOrWhiteSpace(username) ||
            string.IsNullOrWhiteSpace(placeholderItemCode) ||
            allowedCardCodes.Count == 0)
        {
            return Results.Unauthorized();
        }

        var correlationId =
            httpContext.Items[
                CorrelationIdMiddleware.ItemName]?.ToString()
            ?? httpContext.TraceIdentifier;

        string auditRequestId;

        try
        {
            auditRequestId = await auditService.StartAsync(
                request,
                username,
                correlationId,
                httpContext.Request.Method,
                httpContext.Request.Path,
                cancellationToken);
        }
        catch (Exception ex)
        {
            return Results.Problem(
                title: "API request audit is unavailable.",
                detail: ex.Message,
                statusCode:
                    StatusCodes.Status503ServiceUnavailable);
        }

        async Task<IResult> CompleteAndReturnAsync(
            int statusCode,
            object body,
            string auditStatus,
            string errorText = "",
            string businessStatus = "",
            string operationType = "",
            string sapObject = "",
            int sapDocEntry = 0,
            string orderStatus = "",
            IReadOnlyCollection<SubmitOrderMessage>? warnings = null)
        {
            await auditService.CompleteSafelyAsync(
                auditRequestId,
                new OrderPostAuditCompletion
                {
                    HttpStatusCode = statusCode,
                    AuditStatus = auditStatus,
                    BusinessStatus = businessStatus,
                    OperationType = operationType,
                    SapObject = sapObject,
                    SapDocEntry = sapDocEntry,
                    OrderStatus = orderStatus,
                    ResponseBody = body,
                    Warnings =
                        warnings ??
                        Array.Empty<SubmitOrderMessage>(),
                    ErrorText = errorText
                },
                cancellationToken);

            return Results.Json(
                body,
                statusCode: statusCode);
        }

        try
        {
            var response =
                await orderSubmitService.SubmitAsync(
                    request,
                    username,
                    allowedCardCodes,
                    placeholderItemCode,
                    cancellationToken);

            var warnings = response.Messages
                .Where(x => string.Equals(
                    x.Type,
                    "WARN",
                    StringComparison.OrdinalIgnoreCase))
                .ToList();

            return await CompleteAndReturnAsync(
                StatusCodes.Status200OK,
                response,
                auditStatus: "SUCCESS",
                businessStatus: response.ProcessingStatus,
                operationType: response.OperationType,
                sapObject: response.SapObject,
                sapDocEntry: response.SapDocEntry,
                orderStatus: response.OrderStatus,
                warnings: warnings);
        }
        catch (UnauthorizedAccessException ex)
        {
            var body = new
            {
                code = "BILL_TO_PARTY_NOT_ALLOWED",
                message = ex.Message
            };

            return await CompleteAndReturnAsync(
                StatusCodes.Status403Forbidden,
                body,
                auditStatus: "REJECTED",
                errorText: ex.ToString());
        }
        catch (OrderSubmitUnprocessableException ex)
        {
            var body = new
            {
                code = ex.Code,
                message = ex.Message
            };

            return await CompleteAndReturnAsync(
                StatusCodes.Status422UnprocessableEntity,
                body,
                auditStatus: "REJECTED",
                errorText: ex.ToString());
        }
        catch (ArgumentException ex)
        {
            var body = new
            {
                code = "INVALID_REQUEST",
                message = ex.Message
            };

            return await CompleteAndReturnAsync(
                StatusCodes.Status400BadRequest,
                body,
                auditStatus: "REJECTED",
                errorText: ex.ToString());
        }
        catch (OrderSubmitConflictException ex)
        {
            var body = new
            {
                code = "ORDER_CONFLICT",
                message = ex.Message
            };

            return await CompleteAndReturnAsync(
                StatusCodes.Status409Conflict,
                body,
                auditStatus: "REJECTED",
                errorText: ex.ToString());
        }
        catch (InvalidOperationException ex)
        {
            var body = new
            {
                code = "SAP_SERVICE_LAYER_ERROR",
                message = ex.Message
            };

            return await CompleteAndReturnAsync(
                StatusCodes.Status502BadGateway,
                body,
                auditStatus: "FAILED",
                errorText: ex.ToString());
        }
        catch (Exception ex)
        {
            var body = new
            {
                code = "UNEXPECTED_ERROR",
                message = "An unexpected error occurred while submitting the order."
            };

            return await CompleteAndReturnAsync(
                StatusCodes.Status500InternalServerError,
                body,
                auditStatus: "FAILED",
                errorText: ex.ToString());
        }
    });

app.Run();