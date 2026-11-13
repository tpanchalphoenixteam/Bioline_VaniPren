namespace PH.SapB1.Vaniperen.Orders;

public sealed class OrderSubmitUnprocessableException : Exception
{
    public OrderSubmitUnprocessableException(
        string code,
        string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}