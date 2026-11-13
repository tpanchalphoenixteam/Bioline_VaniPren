using System.ComponentModel.DataAnnotations;

namespace PH.SapB1.Vaniperen.Orders;

// [Required] attributes are for Swagger documentation only; minimal APIs do not
// enforce them. Actual validation happens in SapOrderSubmitService.ValidateBaseRequest.
public sealed class SubmitOrderRequest
{
    [Required]
    public string? CustomerOrderNumber { get; set; }

    // ISO 8601 date-time, for example 2026-04-08T10:15:00Z.
    [Required]
    public string? OrderDateTime { get; set; }

    // Optional. Defaults to today's date when omitted.
    public string? DeliveryDate { get; set; }

    // SAP Business Partner CardCode.
    [Required]
    public string? BillToParty { get; set; }

    // GLN maintained against a Ship-To address in BPAddresses.
    public string? ShipToGLN { get; set; }

    public string? Remarks { get; set; }

    [Required]
    public List<SubmitOrderLineRequest> Lines { get; set; } = new();
}

public sealed class SubmitOrderLineRequest
{
    public int? LineNumber { get; set; }

    public string? ItemCode { get; set; }

    public string? ItemDescription { get; set; }

    public decimal? Quantity { get; set; }

    public string? ShipDate { get; set; }
}