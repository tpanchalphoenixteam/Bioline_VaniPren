namespace PH.SapB1.Vaniperen.Orders;

public sealed class GetOrdersQuery
{
    public string? DateFrom { get; set; }

    public string? CustomerOrderNumber { get; set; }

    public string? Status { get; set; }

    public int? Page { get; set; }

    public int? PageSize { get; set; }
}