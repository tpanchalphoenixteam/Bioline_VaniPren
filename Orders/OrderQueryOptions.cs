namespace PH.SapB1.Vaniperen.Orders;

public sealed class OrderQueryOptions
{
    public string CustomerOrderNumberField { get; set; } = "NumAtCard";

    public string IntegrationIdField { get; set; } = "U_APIRef";

    public string ProcessingStatusField { get; set; } = "U_ProcessingStatus";

    public string OrderStatusField { get; set; } = "U_OrderStatus";

    public string ConfirmedField { get; set; } = "Confirmed";

    public string DefaultProcessingStatus { get; set; } = "UPDATED";

    public int DefaultPageSize { get; set; } = 50;

    public int MaxPageSize { get; set; } = 200;

    public int MaxServiceLayerRows { get; set; } = 5000;
    public string LineCancellationField { get; set; } =
    "U_VI_LineStatus";
}