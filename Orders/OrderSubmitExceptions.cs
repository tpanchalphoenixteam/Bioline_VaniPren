namespace PH.SapB1.Vaniperen.Orders;

public sealed class OrderSubmitConflictException : Exception
{
    public OrderSubmitConflictException(string message) : base(message)
    {
    }
}