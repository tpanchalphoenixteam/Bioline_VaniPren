namespace PH.SapB1.Vaniperen.Orders;

public sealed class OrderPostingOptions
{
    public string BusinessTimeZone { get; set; } = "Asia/Dubai";

    public string CustomerOrderNumberField { get; set; } = "U_CustOrderNum";

    public string OrderStatusField { get; set; } = "U_OrderStatus";

    public string IntegrationIdField { get; set; } = "U_APIRef";

    public string ApiUserField { get; set; } = "U_APIUser";

    public string ProcessingStatusField { get; set; } = "U_ProcessingStatus";

    public string AcceptedProcessingStatus { get; set; } = "ACCEPTED";

    public string MissingLimitStatus { get; set; } = "OOR";

    // Item master (OITM) UDF that holds the item's cost centre.
    public string ItemCostCenterField { get; set; } = "U_BIO_CostCenter";

    // Document line fields that receive the item's cost centre (dimension 2 = "Products").
    // CostingCode2     = RDR1."OcrCode2"   (checked by TransNot SD_001, error -17001)
    // COGSCostingCode2 = RDR1."CogsOcrCo2" (COGS distribution rule)
    // Defaults to COGSCostingCode2 so the server still sends it when
    // appsettings.json has no LineCostCenterFields entry.
    public List<string> LineCostCenterFields { get; set; } = new() { "COGSCostingCode2" };
}