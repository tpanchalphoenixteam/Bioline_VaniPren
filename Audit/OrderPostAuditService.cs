using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PH.SapB1.Vaniperen.Orders;
using PH.SapB1.Vaniperen.Sap;
using System.Text.Encodings.Web;

namespace PH.SapB1.Vaniperen.Audit;

public sealed class OrderPostAuditService
{
    private const string SapEntityName = "U_PH_API_LOG";

    private static readonly JsonSerializerOptions SerializerOptions =
    new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly SapServiceLayerRawClient _client;
    private readonly ILogger<OrderPostAuditService> _logger;

    public OrderPostAuditService(
        SapServiceLayerRawClient client,
        ILogger<OrderPostAuditService> logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <summary>
    /// Creates the log row before any SAP order processing starts.
    /// If this fails, the order request should not be processed.
    /// </summary>
    public async Task<string> StartAsync(
        SubmitOrderRequest request,
        string username,
        string correlationId,
        string httpMethod,
        string endpoint,
        CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var receivedAt = DateTimeOffset.UtcNow;

        var requestJson = JsonSerializer.Serialize(
            request,
            SerializerOptions);

        var payload = new JsonObject
        {
            ["Code"] = requestId,
            ["Name"] = requestId,

            ["U_CorrId"] = correlationId,
            ["U_ReceivedAt"] = receivedAt.ToString("O"),
            ["U_HttpMethod"] = httpMethod,
            ["U_Endpoint"] = endpoint,
            ["U_UserName"] = username,
            ["U_CustOrderNum"] = request.CustomerOrderNumber?.Trim() ?? "",
            ["U_BillToParty"] = request.BillToParty?.Trim() ?? "",

            ["U_HttpStatus"] = "",
            ["U_AuditStatus"] = "STARTED",
            ["U_BizStatus"] = "",
            ["U_Operation"] = "",
            ["U_SapObject"] = "",
            ["U_SapDocEntry"] = 0,
            ["U_OrderStatus"] = "",

            ["U_HasWarnings"] = "N",
            ["U_EmailNotified"] = "N",
            ["U_EmailNotifiedAt"] = "",
            ["U_EmailAttempts"] = 0,

            ["U_RequestJson"] = requestJson,
            ["U_ResponseJson"] = "",
            ["U_WarningsJson"] = "[]",
            ["U_ErrorText"] = "",
            ["U_CompletedAt"] = ""
        };

        _logger.LogInformation(
            "ORDER AUDIT CREATE START RequestId={RequestId} CorrelationId={CorrelationId} CustomerOrderNumber={CustomerOrderNumber}",
            requestId,
            correlationId,
            request.CustomerOrderNumber);

        var result = await _client.SendAsync(
            HttpMethod.Post,
            SapEntityName,
            payload,
            cancellationToken);

        if (!result.IsSuccessStatusCode)
        {
            _logger.LogError(
                "ORDER AUDIT CREATE FAILED RequestId={RequestId} StatusCode={StatusCode} Body={Body}",
                requestId,
                (int)result.StatusCode,
                result.Body);

            throw new InvalidOperationException(
                "The request audit record could not be created. " +
                "Order processing was stopped to prevent an unlogged request. " +
                result.Body);
        }

        _logger.LogInformation(
            "ORDER AUDIT CREATED RequestId={RequestId} CorrelationId={CorrelationId}",
            requestId,
            correlationId);

        return requestId;
    }

    public async Task CompleteAsync(
        string requestId,
        OrderPostAuditCompletion completion,
        CancellationToken cancellationToken)
    {
        var warnings = completion.Warnings
            .Where(x => string.Equals(
                x.Type,
                "WARN",
                StringComparison.OrdinalIgnoreCase))
            .ToList();

        var responseJson = completion.ResponseBody is null
            ? "{}"
            : JsonSerializer.Serialize(
                completion.ResponseBody,
                SerializerOptions);

        var warningsJson = JsonSerializer.Serialize(
            warnings,
            SerializerOptions);

        var payload = new JsonObject
        {
            ["U_HttpStatus"] = completion.HttpStatusCode.ToString(
                CultureInfo.InvariantCulture),

            ["U_AuditStatus"] = completion.AuditStatus,
            ["U_BizStatus"] = completion.BusinessStatus,
            ["U_Operation"] = completion.OperationType,
            ["U_SapObject"] = completion.SapObject,
            ["U_SapDocEntry"] = completion.SapDocEntry,
            ["U_OrderStatus"] = completion.OrderStatus,

            ["U_HasWarnings"] = warnings.Count > 0 ? "Y" : "N",

            // Always remains N until the email process sends the notification.
            ["U_EmailNotified"] = "N",

            ["U_ResponseJson"] = responseJson,
            ["U_WarningsJson"] = warningsJson,
            ["U_ErrorText"] = completion.ErrorText,
            ["U_CompletedAt"] = DateTimeOffset.UtcNow.ToString("O")
        };

        var relativeUrl =
            $"{SapEntityName}('{EscapeODataKey(requestId)}')";

        var result = await _client.SendAsync(
            HttpMethod.Patch,
            relativeUrl,
            payload,
            cancellationToken);

        if (!result.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Failed to complete API audit record '{requestId}': " +
                result.Body);
        }

        _logger.LogInformation(
            "ORDER AUDIT COMPLETED RequestId={RequestId} AuditStatus={AuditStatus} HttpStatus={HttpStatus} HasWarnings={HasWarnings}",
            requestId,
            completion.AuditStatus,
            completion.HttpStatusCode,
            warnings.Count > 0);
    }

    /// <summary>
    /// Completion failures must not change an already-created SAP order response.
    /// The row remains STARTED and can be reconciled later.
    /// </summary>
    public async Task CompleteSafelyAsync(
        string requestId,
        OrderPostAuditCompletion completion,
        CancellationToken cancellationToken)
    {
        try
        {
            await CompleteAsync(
                requestId,
                completion,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(
                ex,
                "ORDER AUDIT COMPLETION FAILED RequestId={RequestId}. Audit row may remain in STARTED status.",
                requestId);
        }
    }

    public async Task MarkEmailNotifiedAsync(
        string requestId,
        CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["U_EmailNotified"] = "Y",
            ["U_EmailNotifiedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["U_EmailError"] = ""
        };

        var relativeUrl =
            $"{SapEntityName}('{EscapeODataKey(requestId)}')";

        var result = await _client.SendAsync(
            HttpMethod.Patch,
            relativeUrl,
            payload,
            cancellationToken);

        if (!result.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Failed to mark warning email as notified for '{requestId}': " +
                result.Body);
        }
    }

    private static string EscapeODataKey(string value)
    {
        return value.Replace("'", "''");
    }
}