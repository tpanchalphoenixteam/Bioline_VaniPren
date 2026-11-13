namespace PH.SapB1.Vaniperen.Orders;

public sealed class GetOrdersResponse
{
    public int Count { get; set; }

    public List<GetOrderResult> Results { get; set; } = new();
}

public sealed class GetOrderResult
{
    public string SapObject { get; set; } = "";

    public string IntegrationId { get; set; } = "";

    public string CustomerOrderNumber { get; set; } = "";

    public string ProcessingStatus { get; set; } = "";

    public string OrderStatus { get; set; } = "";

    public string DocDate { get; set; } = "";

    public string DeliveryDate { get; set; } = "";

    public string Confirmed { get; set; } = "";

    public List<GetOrderLineResult> Lines { get; set; } = new();
}

public sealed class GetOrderLineResult
{
    public int LineNumber { get; set; }

    public string ItemCode { get; set; } = "";

    public decimal OrderedQuantity { get; set; }

    public decimal OpenQuantity { get; set; }

    public string LineStatus { get; set; } = "";
}