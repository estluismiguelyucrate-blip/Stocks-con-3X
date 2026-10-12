namespace Stoxx.Web.Contracts;

public sealed record SetupRequest(string Name, string UserName, string Password);
public sealed record LoginRequest(string UserName, string Password);
public sealed record CreateUserRequest(string Name, string UserName, string Password, string Role);
public sealed record ProductRequest(string Code, string Name, string Category, decimal CostPrice, decimal UnitPrice, int Stock, int MinimumStock);
public sealed record SupplierRequest(string Name, string ContactName, string Phone, string Email, string Notes);
public sealed record SaleRequest(string PaymentMethod, List<SaleLineRequest> Items);
public sealed record SaleLineRequest(int ProductId, int Quantity);
public sealed record OpenCashRequest(decimal OpeningAmount);
public sealed record CloseCashRequest(decimal CountedAmount);
public sealed record PurchaseRequest(int SupplierId, int ProductId, int Quantity, decimal UnitCost);
