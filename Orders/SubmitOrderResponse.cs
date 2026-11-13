namespace PH.SapB1.Vaniperen.Orders;

public sealed class SubmitOrderResponse
{
    public string IntegrationId { get; set; } = "";

    public string OperationType { get; set; } = "";

    public string CustomerOrderNumber { get; set; } = "";

    public string ProcessingStatus { get; set; } = "";

    public string SapObject { get; set; } = "";

    public int SapDocEntry { get; set; }

    public string OrderStatus { get; set; } = "";

    public List<SubmitOrderMessage> Messages { get; set; } = new();

    public DateTimeOffset ReceivedAt { get; set; }
}

public sealed class SubmitOrderMessage
{
    public string Type { get; set; } = "";

    public string Code { get; set; } = "";

    public string Text { get; set; } = "";
}