using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using PH.SapB1.Vaniperen.Sap;

namespace PH.SapB1.Vaniperen.Orders;

public sealed class SapOrderReadService
{
    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "TBC",
        "OOR",
        "CONF",
        "OORC",
    "CANC"
    };

    private readonly SapServiceLayerRawClient _client;
    private readonly OrderQueryOptions _options;
    private readonly ILogger<SapOrderReadService> _logger;

    public SapOrderReadService(
        SapServiceLayerRawClient client,
        IOptions<OrderQueryOptions> options,
        ILogger<SapOrderReadService> logger)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<GetOrdersResponse> GetOrdersAsync(
    GetOrdersQuery query,
    List<string> allowedCardCodes,
    CancellationToken cancellationToken)
    {
        ValidateQuery(query);
        _logger.LogInformation(
    "GET ORDERS START DateFrom={DateFrom} CustomerOrderNumber={CustomerOrderNumber} Status={Status} Page={Page} PageSize={PageSize} AllowedCardCodes={AllowedCardCodes}",
    query.DateFrom,
    query.CustomerOrderNumber,
    query.Status,
    query.Page,
    query.PageSize,
    string.Join(",", allowedCardCodes));
        var dateFrom = DateOnly.ParseExact(
            query.DateFrom!,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture);

        var page = query.Page.GetValueOrDefault(1);
        var pageSize = query.PageSize.GetValueOrDefault(_options.DefaultPageSize);

        if (page <= 0)
            page = 1;

        if (pageSize <= 0)
            pageSize = _options.DefaultPageSize;

        if (pageSize > _options.MaxPageSize)
            pageSize = _options.MaxPageSize;

        var orders = await FetchDocumentsAsync(
    entityName: "Orders",
    sapObject: "Orders",
    dateFrom: dateFrom,
    allowedCardCodes: allowedCardCodes,
    customerOrderNumber: query.CustomerOrderNumber,
    cancellationToken: cancellationToken);

        var drafts = await FetchDocumentsAsync(
            entityName: "Drafts",
            sapObject: "Draft",
            dateFrom: dateFrom,
            allowedCardCodes: allowedCardCodes,
            customerOrderNumber: query.CustomerOrderNumber,
            cancellationToken: cancellationToken);
        _logger.LogInformation(
    "GET ORDERS FETCHED OrdersCount={OrdersCount} DraftsCount={DraftsCount}",
    orders.Count,
    drafts.Count);

        var allOrders = orders
            .Concat(drafts)
            .Select(MapDocument)
            .Where(x => string.IsNullOrWhiteSpace(query.Status) ||
                        string.Equals(x.OrderStatus, query.Status, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.DocDate)
            .ThenByDescending(x => x.IntegrationId)
            .ToList();

        var totalCount = allOrders.Count;

        var pagedResults = allOrders
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();
        _logger.LogInformation(
    "GET ORDERS END TotalCount={TotalCount} ReturnedCount={ReturnedCount}",
    totalCount,
    pagedResults.Count);
        return new GetOrdersResponse
        {
            Count = totalCount,
            Results = pagedResults
        };
    }

    private static void ValidateQuery(GetOrdersQuery query)
    {
        if (string.IsNullOrWhiteSpace(query.DateFrom))
        {
            throw new ArgumentException("dateFrom is required. Format: YYYY-MM-DD");
        }

        if (!DateOnly.TryParseExact(
                query.DateFrom,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _))
        {
            throw new ArgumentException("dateFrom must be in YYYY-MM-DD format.");
        }

        if (!string.IsNullOrWhiteSpace(query.Status) &&
            !AllowedStatuses.Contains(query.Status))
        {
            throw new ArgumentException("status must be one of: TBC, OOR, CONF, OORC, CANC.");
        }
    }

    private async Task<List<SapDocumentWrapper>> FetchDocumentsAsync(
      string entityName,
      string sapObject,
      DateOnly dateFrom,
      List<string> allowedCardCodes,
      string? customerOrderNumber,
      CancellationToken cancellationToken)
    {
        var all = new List<SapDocumentWrapper>();

        var top = 100;
        var skip = 0;

        while (all.Count < _options.MaxServiceLayerRows)
        {
            var filter = BuildFilter(
     entityName,
     dateFrom,
     allowedCardCodes,
     customerOrderNumber);

            var relativeUrl =
                $"{entityName}" +
                "?$filter=" + Uri.EscapeDataString(filter) +
                $"&$top={top}" +
                $"&$skip={skip}";
            _logger.LogInformation(
    "GET ORDERS SAP FETCH Entity={EntityName} Filter={Filter} Top={Top} Skip={Skip}",
    entityName,
    filter,
    top,
    skip);

            var response = await _client.SendAsync(
                HttpMethod.Get,
                relativeUrl,
                null,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"SAP Service Layer failed while reading {entityName}: {response.Body}");
            }

            var root = JsonNode.Parse(response.Body);
            var values = root?["value"]?.AsArray();

            if (values is null || values.Count == 0)
            {
                break;
            }

            foreach (var item in values)
            {
                if (item is JsonObject obj)
                {
                    all.Add(new SapDocumentWrapper
                    {
                        SapObject = sapObject,
                        Document = obj
                    });
                }
            }

            if (values.Count < top)
            {
                break;
            }

            skip += top;
        }

        return all;
    }

    private string BuildFilter(
     string entityName,
     DateOnly dateFrom,
     List<string> allowedCardCodes,
     string? customerOrderNumber)
    {
        var cleanCardCodes = allowedCardCodes
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (cleanCardCodes.Count == 0)
        {
            throw new InvalidOperationException("No allowed CardCode found for this user.");
        }

        var cardCodeFilter = cleanCardCodes.Count == 1
            ? $"CardCode eq '{EscapeODataString(cleanCardCodes[0])}'"
            : "(" + string.Join(" or ", cleanCardCodes.Select(x =>
                $"CardCode eq '{EscapeODataString(x)}'")) + ")";

        var parts = new List<string>
    {
        cardCodeFilter,
        $"DocDate ge '{dateFrom:yyyy-MM-dd}'"
    };

        if (entityName.Equals("Drafts", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add("DocObjectCode eq 'oOrders'");
        }

        if (!string.IsNullOrWhiteSpace(customerOrderNumber))
        {
            parts.Add(
                $"{_options.CustomerOrderNumberField} eq '{EscapeODataString(customerOrderNumber.Trim())}'");
        }

        return string.Join(" and ", parts);
    }

    private GetOrderResult MapDocument(SapDocumentWrapper wrapper)
    {
        var doc = wrapper.Document;

        var integrationId = GetString(doc, _options.IntegrationIdField);

        if (string.IsNullOrWhiteSpace(integrationId))
        {
            var docEntry = GetString(doc, "DocEntry");
            integrationId = $"{wrapper.SapObject}-{docEntry}";
        }

        var customerOrderNumber = GetString(doc, _options.CustomerOrderNumberField);

        var processingStatus = GetString(doc, _options.ProcessingStatusField);

        if (string.IsNullOrWhiteSpace(processingStatus))
        {
            processingStatus = _options.DefaultProcessingStatus;
        }

        var confirmed = ResolveConfirmed(doc, wrapper.SapObject);

        var orderStatus = ResolveOrderStatus(
            doc,
            wrapper.SapObject,
            confirmed);

        var lines = MapLines(doc, orderStatus);
        _logger.LogWarning(
    "CANC CHECK CustomerOrderNumber={CustomerOrderNumber}, BeforeOrderStatus={OrderStatus}, LinesCount={LinesCount}, LineStatuses={LineStatuses}, AreAllCancelled={AreAllCancelled}",
    customerOrderNumber,
    orderStatus,
    lines.Count,
    string.Join(",", lines.Select(x => x.LineStatus)),
    AreAllLinesCancelled(lines));

        if (AreAllLinesCancelled(lines))
        {
            orderStatus = "CANC";
            confirmed = "N";
        }

        return new GetOrderResult
        {
            SapObject = wrapper.SapObject,
            IntegrationId = integrationId,
            CustomerOrderNumber = customerOrderNumber,
            ProcessingStatus = processingStatus,
            OrderStatus = orderStatus,
            DocDate = FormatSapDate(GetString(doc, "DocDate")),
            // Client's "Delivery Date" is ORDR.TaxDate (DocDueDate is their "Shipping Date").
            DeliveryDate = FormatSapDate(GetString(doc, "TaxDate")),
            Confirmed = confirmed,
            Lines = lines
        };
    }
    private static bool AreAllLinesCancelled(
    List<GetOrderLineResult> lines)
    {
        return lines.Count > 0 &&
               lines.All(x =>
                   x.LineStatus.Equals(
                       "CANCELLED",
                       StringComparison.OrdinalIgnoreCase));
    }

    private string ResolveOrderStatus(JsonObject doc, string sourceType, string confirmed)
    {
        var configuredStatus = GetString(doc, _options.OrderStatusField);

        if (!string.IsNullOrWhiteSpace(configuredStatus))
        {
            return configuredStatus;
        }

        if (sourceType.Equals("DRAFT", StringComparison.OrdinalIgnoreCase))
        {
            return "TBC";
        }

        var documentStatus = GetString(doc, "DocumentStatus");

        if (documentStatus.Contains("Close", StringComparison.OrdinalIgnoreCase) ||
            documentStatus.Contains("Closed", StringComparison.OrdinalIgnoreCase))
        {
            return "OORC";
        }

        if (confirmed == "Y")
        {
            return "CONF";
        }

        return "OOR";
    }

    private string ResolveConfirmed(JsonObject doc, string sapObject)
    {
        if (sapObject.Equals("Draft", StringComparison.OrdinalIgnoreCase))
        {
            return "N";
        }

        var value = GetString(doc, _options.ConfirmedField);

        if (string.IsNullOrWhiteSpace(value))
        {
            return sapObject.Equals("Orders", StringComparison.OrdinalIgnoreCase) ? "Y" : "N";
        }

        if (value.Equals("Y", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Yes", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("tYES", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return "Y";
        }

        return "N";
    }

    private List<GetOrderLineResult> MapLines(
    JsonObject doc,
    string headerOrderStatus)
    {
        var result = new List<GetOrderLineResult>();

        var lines = doc["DocumentLines"]?.AsArray();

        if (lines is null)
        {
            return result;
        }

        foreach (var lineNode in lines)
        {
            if (lineNode is not JsonObject line)
            {
                continue;
            }

            var lineNumber =
                GetInt(line, "LineNum") + 1;

            var cancellationValue =
                GetString(
                    line,
                    _options.LineCancellationField);

            if (cancellationValue.Equals(
                    "CANCELLED",
                    StringComparison.OrdinalIgnoreCase) ||
                cancellationValue.Equals(
                    "Cancelled",
                    StringComparison.OrdinalIgnoreCase))
            {
                result.Add(new GetOrderLineResult
                {
                    LineNumber = lineNumber,
                    ItemCode =
                        GetString(line, "ItemCode"),
                    OrderedQuantity = 0,
                    OpenQuantity = 0,
                    LineStatus = "CANCELLED"
                });

                continue;
            }

            var orderedQuantity =
                GetDecimal(line, "Quantity");

            var openQuantity =
                GetDecimalNullable(
                    line,
                    "RemainingOpenQuantity")
                ?? GetDecimalNullable(
                    line,
                    "OpenQuantity")
                ?? ResolveOpenQuantityFromLineStatus(
                    line,
                    orderedQuantity);

            var sapLineStatus =
                GetString(line, "LineStatus");

            var isClosed =
                sapLineStatus.Contains(
                    "Close",
                    StringComparison.OrdinalIgnoreCase) ||
                sapLineStatus.Equals(
                    "C",
                    StringComparison.OrdinalIgnoreCase);

            string externalLineStatus;

            if (isClosed)
            {
                externalLineStatus = "DISPATCHED";
            }
            else if (
                headerOrderStatus.Equals(
                    "CONF",
                    StringComparison.OrdinalIgnoreCase) ||
                headerOrderStatus.Equals(
                    "OORC",
                    StringComparison.OrdinalIgnoreCase))
            {
                externalLineStatus = "CONFIRMED";
            }
            else
            {
                externalLineStatus = "PENDING";
            }

            result.Add(new GetOrderLineResult
            {
                LineNumber = lineNumber,
                ItemCode =
                    GetString(line, "ItemCode"),
                OrderedQuantity = orderedQuantity,
                OpenQuantity = openQuantity,
                LineStatus = externalLineStatus
            });
        }

        return result;
    }

    private static decimal ResolveOpenQuantityFromLineStatus(JsonObject line, decimal orderedQty)
    {
        var status = GetString(line, "LineStatus");

        if (status.Contains("Close", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return orderedQty;
    }

    private static string ResolveLineStatus(JsonObject line, decimal openQty)
    {
        var sapLineStatus = GetString(line, "LineStatus");

        if (sapLineStatus.Contains("Open", StringComparison.OrdinalIgnoreCase))
        {
            return "OPEN";
        }

        if (openQty > 0)
        {
            return "OPEN";
        }

        return "CLOSED";
    }

    private static string GetString(JsonObject obj, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            return "";
        }

        if (!obj.TryGetPropertyValue(propertyName, out var node) || node is null)
        {
            return "";
        }

        try
        {
            return node.GetValue<string>() ?? "";
        }
        catch
        {
            return node.ToString();
        }
    }

    private static int GetInt(JsonObject obj, string propertyName)
    {
        if (!obj.TryGetPropertyValue(propertyName, out var node) || node is null)
        {
            return 0;
        }

        try
        {
            return node.GetValue<int>();
        }
        catch
        {
            return 0;
        }
    }

    private static decimal GetDecimal(JsonObject obj, string propertyName)
    {
        return GetDecimalNullable(obj, propertyName) ?? 0;
    }

    private static decimal? GetDecimalNullable(JsonObject obj, string propertyName)
    {
        if (!obj.TryGetPropertyValue(propertyName, out var node) || node is null)
        {
            return null;
        }

        try
        {
            return node.GetValue<decimal>();
        }
        catch
        {
            if (decimal.TryParse(node.ToString(), out var value))
            {
                return value;
            }

            return null;
        }
    }

    private static string FormatSapDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        if (DateTimeOffset.TryParse(value, out var dto))
        {
            return dto.ToString("yyyy-MM-dd");
        }

        if (DateTime.TryParse(value, out var dt))
        {
            return dt.ToString("yyyy-MM-dd");
        }

        return value.Length >= 10 ? value[..10] : value;
    }

    private static string EscapeODataString(string value)
    {
        return value.Replace("'", "''");
    }

    private sealed class SapDocumentWrapper
    {
        public string SapObject { get; set; } = "";

        public JsonObject Document { get; set; } = new();
    }
}