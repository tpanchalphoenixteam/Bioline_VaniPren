using PH.SapB1.Vaniperen.Orders;

namespace PH.SapB1.Vaniperen.Audit;

public sealed class OrderPostAuditCompletion
{
    public int HttpStatusCode { get; set; }

    // SUCCESS, REJECTED, FAILED
    public string AuditStatus { get; set; } = "";

    // ACCEPTED etc.
    public string BusinessStatus { get; set; } = "";

    public string OperationType { get; set; } = "";

    public string SapObject { get; set; } = "";

    public int SapDocEntry { get; set; }

    public string OrderStatus { get; set; } = "";

    public object? ResponseBody { get; set; }

    public IReadOnlyCollection<SubmitOrderMessage> Warnings { get; set; } =
        Array.Empty<SubmitOrderMessage>();

    public string ErrorText { get; set; } = "";
}