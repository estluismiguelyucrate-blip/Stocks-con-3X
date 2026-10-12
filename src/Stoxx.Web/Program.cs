using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stoxx.Web.Contracts;
using Stoxx.Web.Data;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
var dataDirectory = builder.Configuration["STOXX_DATA_DIR"];
if (string.IsNullOrWhiteSpace(dataDirectory))
    dataDirectory = Path.Combine(builder.Environment.ContentRootPath, "data");
Directory.CreateDirectory(dataDirectory);
var keyDirectory = Path.Combine(dataDirectory, "keys");
Directory.CreateDirectory(keyDirectory);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory))
    .SetApplicationName("Stoxx.Web");
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? $"Data Source={Path.Combine(dataDirectory, "stoxx.db")}";

builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));
builder.Services.AddScoped<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "Stoxx.Session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }

            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            }

            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

app.UseExceptionHandler(handler => handler.Run(async context =>
{
    var exception = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerPathFeature>()?.Error;
    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("UnhandledException");
    logger.LogError(exception, "Error no controlado al atender {Path}", context.Request.Path);
    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    context.Response.ContentType = "application/json";
    await context.Response.WriteAsJsonAsync(new { message = "Ocurrió un error interno. Revisa los datos e inténtalo nuevamente." });
}));

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();
}

app.MapGet("/api/auth/status", async (AppDbContext db, ClaimsPrincipal user) =>
{
    var setupRequired = !await db.Users.AnyAsync();
    return Results.Ok(new
    {
        setupRequired,
        authenticated = user.Identity?.IsAuthenticated == true,
        user = user.Identity?.IsAuthenticated == true
            ? new { name = user.Identity.Name, role = user.FindFirstValue(ClaimTypes.Role) }
            : null
    });
});

app.MapPost("/api/auth/setup", async (
    SetupRequest request,
    AppDbContext db,
    IPasswordHasher<AppUser> hasher) =>
{
    var validation = ValidateCredentials(request.Name, request.UserName, request.Password);
    if (validation is not null) return Results.BadRequest(new { message = validation });
    if (await db.Users.AnyAsync()) return Results.Conflict(new { message = "La configuración inicial ya se completó." });

    var user = new AppUser
    {
        Name = request.Name.Trim(),
        UserName = NormalizeUserName(request.UserName),
        Role = Roles.Admin
    };
    user.PasswordHash = hasher.HashPassword(user, request.Password);
    db.Users.Add(user);
    try
    {
        await db.SaveChangesAsync();
    }
    catch (DbUpdateException)
    {
        return Results.Conflict(new { message = "No se pudo crear la cuenta inicial. Recarga la página e inténtalo otra vez." });
    }

    return Results.Ok(new { message = "Cuenta administradora creada. Inicia sesión para continuar." });
}).AllowAnonymous();

app.MapPost("/api/auth/login", async (
    LoginRequest request,
    AppDbContext db,
    IPasswordHasher<AppUser> hasher,
    HttpContext context) =>
{
    if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrEmpty(request.Password))
        return Results.Unauthorized();
    var userName = NormalizeUserName(request.UserName);
    var user = await db.Users.SingleOrDefaultAsync(x => x.UserName == userName);
    if (user is null || hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
        return Results.Unauthorized();

    var claims = new[]
    {
        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new Claim(ClaimTypes.Name, user.Name),
        new Claim(ClaimTypes.Role, user.Role),
        new Claim("username", user.UserName)
    };
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    return Results.Ok(new { name = user.Name, role = user.Role, userName = user.UserName });
}).AllowAnonymous();

app.MapPost("/api/auth/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.NoContent();
}).RequireAuthorization();

app.MapGet("/api/users", async (AppDbContext db) =>
    Results.Ok(await db.Users.OrderBy(x => x.Name)
        .Select(x => new { x.Id, x.Name, userName = x.UserName, x.Role })
        .ToListAsync()))
    .RequireAuthorization(policy => policy.RequireRole(Roles.Admin));

app.MapPost("/api/users", async (
    CreateUserRequest request,
    AppDbContext db,
    IPasswordHasher<AppUser> hasher) =>
{
    var validation = ValidateCredentials(request.Name, request.UserName, request.Password);
    if (validation is not null) return Results.BadRequest(new { message = validation });
    if (request.Role is not (Roles.Admin or Roles.Cashier or Roles.Inventory))
        return Results.BadRequest(new { message = "Selecciona un rol válido." });

    var userName = NormalizeUserName(request.UserName);
    if (await db.Users.AnyAsync(x => x.UserName == userName))
        return Results.Conflict(new { message = "Ese usuario ya existe." });

    var user = new AppUser { Name = request.Name.Trim(), UserName = userName, Role = request.Role };
    user.PasswordHash = hasher.HashPassword(user, request.Password);
    db.Users.Add(user);
    await db.SaveChangesAsync();
    return Results.Created($"/api/users/{user.Id}", new { user.Id, user.Name, userName = user.UserName, user.Role });
}).RequireAuthorization(policy => policy.RequireRole(Roles.Admin));

app.MapGet("/api/products", async (AppDbContext db, bool all = false) =>
{
    var query = db.Products.AsNoTracking();
    if (!all) query = query.Where(x => x.Active);
    var products = await query.OrderBy(x => x.Name).Select(x => new
    {
        x.Id, x.Code, x.Name, x.Category, costPrice = x.CostPrice,
        unitPrice = x.UnitPrice, x.Stock, minimumStock = x.MinimumStock, x.Active,
        lowStock = x.Stock <= x.MinimumStock
    }).ToListAsync();
    return Results.Ok(products);
}).RequireAuthorization();

app.MapPost("/api/products", async (ProductRequest request, AppDbContext db) =>
{
    var validation = ValidateProduct(request);
    if (validation is not null) return Results.BadRequest(new { message = validation });
    var code = request.Code.Trim();
    if (await db.Products.AnyAsync(x => x.Code == code)) return Results.Conflict(new { message = "Ya existe un producto con ese código." });

    var product = new Product
    {
        Code = code,
        Name = request.Name.Trim(),
        Category = request.Category?.Trim() ?? "",
        CostPrice = decimal.Round(request.CostPrice, 2),
        UnitPrice = decimal.Round(request.UnitPrice, 2),
        Stock = request.Stock,
        MinimumStock = request.MinimumStock
    };
    db.Products.Add(product);
    await db.SaveChangesAsync();
    return Results.Created($"/api/products/{product.Id}", new { product.Id, product.Name });
}).RequireAuthorization(policy => policy.RequireRole(Roles.Admin, Roles.Inventory));

app.MapPut("/api/products/{id:int}", async (int id, ProductRequest request, AppDbContext db) =>
{
    var validation = ValidateProduct(request);
    if (validation is not null) return Results.BadRequest(new { message = validation });
    var product = await db.Products.FindAsync(id);
    if (product is null) return Results.NotFound(new { message = "No se encontró el producto." });
    var code = request.Code.Trim();
    if (await db.Products.AnyAsync(x => x.Id != id && x.Code == code)) return Results.Conflict(new { message = "Ya existe otro producto con ese código." });

    product.Code = code;
    product.Name = request.Name.Trim();
    product.Category = request.Category?.Trim() ?? "";
    product.CostPrice = decimal.Round(request.CostPrice, 2);
    product.UnitPrice = decimal.Round(request.UnitPrice, 2);
    product.Stock = request.Stock;
    product.MinimumStock = request.MinimumStock;
    await db.SaveChangesAsync();
    return Results.NoContent();
}).RequireAuthorization(policy => policy.RequireRole(Roles.Admin, Roles.Inventory));

app.MapDelete("/api/products/{id:int}", async (int id, AppDbContext db) =>
{
    var product = await db.Products.FindAsync(id);
    if (product is null) return Results.NotFound(new { message = "No se encontró el producto." });
    product.Active = false;
    await db.SaveChangesAsync();
    return Results.NoContent();
}).RequireAuthorization(policy => policy.RequireRole(Roles.Admin, Roles.Inventory));

app.MapGet("/api/suppliers", async (AppDbContext db, bool all = false) =>
{
    var query = db.Suppliers.AsNoTracking();
    if (!all) query = query.Where(x => x.Active);
    return Results.Ok(await query.OrderBy(x => x.Name).ToListAsync());
}).RequireAuthorization();

app.MapPost("/api/suppliers", async (SupplierRequest request, AppDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(request.Name)) return Results.BadRequest(new { message = "El nombre del proveedor es obligatorio." });
    var supplier = new Supplier
    {
        Name = request.Name.Trim(), ContactName = request.ContactName?.Trim() ?? "",
        Phone = request.Phone?.Trim() ?? "", Email = request.Email?.Trim() ?? "", Notes = request.Notes?.Trim() ?? ""
    };
    db.Suppliers.Add(supplier);
    await db.SaveChangesAsync();
    return Results.Created($"/api/suppliers/{supplier.Id}", new { supplier.Id, supplier.Name });
}).RequireAuthorization(policy => policy.RequireRole(Roles.Admin, Roles.Inventory));

app.MapPut("/api/suppliers/{id:int}", async (int id, SupplierRequest request, AppDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(request.Name)) return Results.BadRequest(new { message = "El nombre del proveedor es obligatorio." });
    var supplier = await db.Suppliers.FindAsync(id);
    if (supplier is null) return Results.NotFound(new { message = "No se encontró el proveedor." });
    supplier.Name = request.Name.Trim();
    supplier.ContactName = request.ContactName?.Trim() ?? "";
    supplier.Phone = request.Phone?.Trim() ?? "";
    supplier.Email = request.Email?.Trim() ?? "";
    supplier.Notes = request.Notes?.Trim() ?? "";
    await db.SaveChangesAsync();
    return Results.NoContent();
}).RequireAuthorization(policy => policy.RequireRole(Roles.Admin, Roles.Inventory));

app.MapDelete("/api/suppliers/{id:int}", async (int id, AppDbContext db) =>
{
    var supplier = await db.Suppliers.FindAsync(id);
    if (supplier is null) return Results.NotFound(new { message = "No se encontró el proveedor." });
    supplier.Active = false;
    await db.SaveChangesAsync();
    return Results.NoContent();
}).RequireAuthorization(policy => policy.RequireRole(Roles.Admin, Roles.Inventory));

app.MapGet("/api/cash", async (AppDbContext db) =>
{
    var session = await db.CashSessions.AsNoTracking().Where(x => x.ClosedAtUtc == null)
        .OrderByDescending(x => x.OpenedAtUtc).FirstOrDefaultAsync();
    if (session is null) return Results.Ok(new { open = false });
    var cashSales = await db.Sales.AsNoTracking()
        .Where(x => x.CashSessionId == session.Id && x.PaymentMethod == "Efectivo")
        .Select(x => x.Total).ToListAsync();
    var expected = session.OpeningAmount + cashSales.Sum();
    return Results.Ok(new
    {
        open = true, session.Id, openedAtUtc = session.OpenedAtUtc,
        session.OpeningAmount, expectedCash = expected, openedBy = session.OpenedBy
    });
}).RequireAuthorization();

app.MapPost("/api/cash/open", async (OpenCashRequest request, AppDbContext db, ClaimsPrincipal user) =>
{
    if (request.OpeningAmount < 0) return Results.BadRequest(new { message = "El monto inicial no puede ser negativo." });
    if (await db.CashSessions.AnyAsync(x => x.ClosedAtUtc == null))
        return Results.Conflict(new { message = "Ya existe una caja abierta." });
    var session = new CashSession
    {
        OpeningAmount = decimal.Round(request.OpeningAmount, 2),
        OpenedBy = user.Identity?.Name ?? ""
    };
    db.CashSessions.Add(session);
    await db.SaveChangesAsync();
    return Results.Created($"/api/cash/{session.Id}", new { session.Id, session.OpenedAtUtc, session.OpeningAmount });
}).RequireAuthorization(policy => policy.RequireRole(Roles.Admin, Roles.Cashier));

app.MapPost("/api/cash/close", async (CloseCashRequest request, AppDbContext db, ClaimsPrincipal user) =>
{
    if (request.CountedAmount < 0) return Results.BadRequest(new { message = "El monto contado no puede ser negativo." });
    var session = await db.CashSessions.Where(x => x.ClosedAtUtc == null).OrderByDescending(x => x.OpenedAtUtc).FirstOrDefaultAsync();
    if (session is null) return Results.Conflict(new { message = "No hay una caja abierta." });
    var cashSales = await db.Sales.AsNoTracking()
        .Where(x => x.CashSessionId == session.Id && x.PaymentMethod == "Efectivo")
        .Select(x => x.Total).ToListAsync();
    var expected = session.OpeningAmount + cashSales.Sum();
    session.ClosedAtUtc = DateTime.UtcNow;
    session.ClosingAmount = decimal.Round(request.CountedAmount, 2);
    session.ClosedBy = user.Identity?.Name ?? "";
    await db.SaveChangesAsync();
    return Results.Ok(new { expectedCash = expected, countedCash = session.ClosingAmount, difference = session.ClosingAmount - expected });
}).RequireAuthorization(policy => policy.RequireRole(Roles.Admin, Roles.Cashier));

app.MapPost("/api/sales", async (
    SaleRequest request,
    AppDbContext db,
    ClaimsPrincipal user,
    CancellationToken cancellationToken) =>
{
    if (request.PaymentMethod is not ("Efectivo" or "QR"))
        return Results.BadRequest(new { message = "El método de pago debe ser Efectivo o QR." });
    if (request.Items is null || request.Items.Count == 0 || request.Items.Any(x => x.ProductId <= 0 || x.Quantity <= 0))
        return Results.BadRequest(new { message = "Agrega productos con cantidades mayores que cero." });

    var groupedQuantities = request.Items.GroupBy(x => x.ProductId)
        .Select(group => new { ProductId = group.Key, Quantity = group.Sum(line => (long)line.Quantity) })
        .ToList();
    if (groupedQuantities.Any(x => x.Quantity > int.MaxValue))
        return Results.BadRequest(new { message = "La cantidad solicitada es demasiado grande." });
    var quantities = groupedQuantities.ToDictionary(x => x.ProductId, x => (int)x.Quantity);
    await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
    var session = await db.CashSessions.Where(x => x.ClosedAtUtc == null)
        .OrderByDescending(x => x.OpenedAtUtc).FirstOrDefaultAsync(cancellationToken);
    if (session is null) return Results.Conflict(new { message = "Abre la caja antes de registrar ventas." });

    var products = await db.Products.Where(x => quantities.Keys.Contains(x.Id) && x.Active).ToListAsync(cancellationToken);
    if (products.Count != quantities.Count) return Results.BadRequest(new { message = "Uno o más productos ya no están disponibles." });
    foreach (var product in products)
        if (product.Stock < quantities[product.Id])
            return Results.Conflict(new { message = $"Stock insuficiente para {product.Name}. Disponible: {product.Stock}." });

    var sale = new Sale
    {
        PaymentMethod = request.PaymentMethod,
        CashierName = user.Identity?.Name ?? "",
        CashSessionId = session.Id
    };
    foreach (var product in products)
    {
        var quantity = quantities[product.Id];
        product.Stock -= quantity;
        sale.Items.Add(new SaleItem
        {
            ProductId = product.Id,
            ProductName = product.Name,
            Quantity = quantity,
            UnitPrice = product.UnitPrice,
            UnitCost = product.CostPrice
        });
        sale.Total += product.UnitPrice * quantity;
    }
    sale.Total = decimal.Round(sale.Total, 2);
    db.Sales.Add(sale);
    await db.SaveChangesAsync(cancellationToken);
    await transaction.CommitAsync(cancellationToken);
    return Results.Created($"/api/sales/{sale.Id}", new
    {
        sale.Id, sale.CreatedAtUtc, sale.PaymentMethod, sale.Total,
        items = sale.Items.Select(x => new { x.ProductName, x.Quantity, x.UnitPrice, subtotal = x.UnitPrice * x.Quantity })
    });
}).RequireAuthorization(policy => policy.RequireRole(Roles.Admin, Roles.Cashier));

app.MapGet("/api/sales", async (AppDbContext db, int limit = 50) =>
{
    limit = Math.Clamp(limit, 1, 200);
    var sales = await db.Sales.AsNoTracking().OrderByDescending(x => x.CreatedAtUtc).Take(limit)
        .Select(x => new { x.Id, x.CreatedAtUtc, x.PaymentMethod, x.Total, x.CashierName,
            items = x.Items.Select(i => new { i.ProductName, i.Quantity, i.UnitPrice }) })
        .ToListAsync();
    return Results.Ok(sales);
}).RequireAuthorization(policy => policy.RequireRole(Roles.Admin, Roles.Cashier));

app.MapPost("/api/purchases", async (
    PurchaseRequest request,
    AppDbContext db,
    ClaimsPrincipal user) =>
{
    if (request.Quantity <= 0 || request.UnitCost < 0)
        return Results.BadRequest(new { message = "La cantidad debe ser positiva y el costo no puede ser negativo." });
    var supplier = await db.Suppliers.SingleOrDefaultAsync(x => x.Id == request.SupplierId && x.Active);
    var product = await db.Products.SingleOrDefaultAsync(x => x.Id == request.ProductId && x.Active);
    if (supplier is null) return Results.BadRequest(new { message = "Selecciona un proveedor activo." });
    if (product is null) return Results.BadRequest(new { message = "Selecciona un producto activo." });
    if ((long)product.Stock + request.Quantity > int.MaxValue)
        return Results.BadRequest(new { message = "La cantidad excede el límite permitido." });

    var unitCost = decimal.Round(request.UnitCost, 2);
    var purchase = new Purchase
    {
        SupplierId = supplier.Id, SupplierName = supplier.Name,
        ProductId = product.Id, ProductName = product.Name,
        Quantity = request.Quantity, UnitCost = unitCost,
        TotalCost = decimal.Round(unitCost * request.Quantity, 2),
        RecordedBy = user.Identity?.Name ?? ""
    };
    product.Stock += request.Quantity;
    product.CostPrice = unitCost;
    db.Purchases.Add(purchase);
    await db.SaveChangesAsync();
    return Results.Created($"/api/purchases/{purchase.Id}", new { purchase.Id, purchase.ProductName, purchase.Quantity, purchase.TotalCost });
}).RequireAuthorization(policy => policy.RequireRole(Roles.Admin, Roles.Inventory));

app.MapGet("/api/purchases", async (AppDbContext db, int limit = 100) =>
{
    limit = Math.Clamp(limit, 1, 300);
    var purchases = await db.Purchases.AsNoTracking().OrderByDescending(x => x.CreatedAtUtc).Take(limit)
        .Select(x => new { x.Id, x.CreatedAtUtc, x.SupplierName, x.ProductName, x.Quantity, x.UnitCost, x.TotalCost, x.RecordedBy })
        .ToListAsync();
    return Results.Ok(purchases);
}).RequireAuthorization(policy => policy.RequireRole(Roles.Admin, Roles.Inventory));

app.MapGet("/api/dashboard", async (AppDbContext db) =>
{
    var today = DateOnly.FromDateTime(DateTime.Now);
    var (startUtc, endUtc) = UtcRangeForLocalDay(today);
    var sales = await db.Sales.AsNoTracking().Include(x => x.Items)
        .Where(x => x.CreatedAtUtc >= startUtc && x.CreatedAtUtc < endUtc).ToListAsync();
    var products = await db.Products.AsNoTracking().Where(x => x.Active).ToListAsync();
    var session = await db.CashSessions.AsNoTracking().Where(x => x.ClosedAtUtc == null)
        .OrderByDescending(x => x.OpenedAtUtc).FirstOrDefaultAsync();
    decimal? expectedCash = null;
    if (session is not null)
    {
        var cashSales = await db.Sales.AsNoTracking().Where(x => x.CashSessionId == session.Id && x.PaymentMethod == "Efectivo")
            .Select(x => x.Total).ToListAsync();
        expectedCash = session.OpeningAmount + cashSales.Sum();
    }
    var recentSales = sales.OrderByDescending(x => x.CreatedAtUtc).Take(5)
        .Select(x => new { x.Id, x.CreatedAtUtc, x.PaymentMethod, x.Total, x.CashierName }).ToList();
    return Results.Ok(new
    {
        date = today.ToString("yyyy-MM-dd"),
        salesCount = sales.Count,
        salesTotal = sales.Sum(x => x.Total),
        grossProfit = sales.Sum(x => x.Items.Sum(i => (i.UnitPrice - i.UnitCost) * i.Quantity)),
        activeProducts = products.Count,
        lowStockCount = products.Count(x => x.Stock <= x.MinimumStock),
        lowStock = products.Where(x => x.Stock <= x.MinimumStock).OrderBy(x => x.Stock).Take(8)
            .Select(x => new { x.Id, x.Code, x.Name, x.Stock, minimumStock = x.MinimumStock }),
        cash = session is null ? null : new { open = true, session.OpeningAmount, expectedCash },
        recentSales
    });
}).RequireAuthorization();

app.MapGet("/api/reports/daily", async ([FromQuery] DateOnly? date, AppDbContext db) =>
{
    var day = date ?? DateOnly.FromDateTime(DateTime.Now);
    var (startUtc, endUtc) = UtcRangeForLocalDay(day);
    var sales = await db.Sales.AsNoTracking().Include(x => x.Items)
        .Where(x => x.CreatedAtUtc >= startUtc && x.CreatedAtUtc < endUtc).ToListAsync();
    var items = sales.SelectMany(x => x.Items).ToList();
    var topProducts = items.GroupBy(x => x.ProductName)
        .Select(group => new { product = group.Key, units = group.Sum(x => x.Quantity), total = group.Sum(x => x.UnitPrice * x.Quantity) })
        .OrderByDescending(x => x.units).Take(10).ToList();
    return Results.Ok(new
    {
        date = day.ToString("yyyy-MM-dd"),
        saleCount = sales.Count,
        totalSales = sales.Sum(x => x.Total),
        cashTotal = sales.Where(x => x.PaymentMethod == "Efectivo").Sum(x => x.Total),
        qrTotal = sales.Where(x => x.PaymentMethod == "QR").Sum(x => x.Total),
        costOfGoods = items.Sum(x => x.UnitCost * x.Quantity),
        grossProfit = items.Sum(x => (x.UnitPrice - x.UnitCost) * x.Quantity),
        topProducts,
        sales = sales.OrderByDescending(x => x.CreatedAtUtc).Select(x => new
        {
            x.Id, x.CreatedAtUtc, x.PaymentMethod, x.Total, x.CashierName,
            items = x.Items.Select(i => new { i.ProductName, i.Quantity, i.UnitPrice, subtotal = i.UnitPrice * i.Quantity })
        })
    });
}).RequireAuthorization(policy => policy.RequireRole(Roles.Admin, Roles.Cashier));

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

app.Run();

static string NormalizeUserName(string value) => value.Trim().ToUpperInvariant();

static string? ValidateCredentials(string name, string userName, string password)
{
    if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100)
        return "Ingresa un nombre de hasta 100 caracteres.";
    if (string.IsNullOrWhiteSpace(userName) || userName.Trim().Length < 3 || userName.Trim().Length > 40)
        return "El usuario debe tener entre 3 y 40 caracteres.";
    if (string.IsNullOrEmpty(password) || password.Length < 10 || password.Length > 200)
        return "La contraseña debe tener al menos 10 caracteres.";
    return null;
}

static string? ValidateProduct(ProductRequest request)
{
    if (string.IsNullOrWhiteSpace(request.Code) || request.Code.Trim().Length > 50)
        return "El código es obligatorio y debe tener como máximo 50 caracteres.";
    if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 160)
        return "El nombre es obligatorio y debe tener como máximo 160 caracteres.";
    if ((request.Category ?? "").Trim().Length > 100)
        return "La categoría debe tener como máximo 100 caracteres.";
    if (request.CostPrice < 0 || request.UnitPrice <= 0 || request.Stock < 0 || request.MinimumStock < 0)
        return "El costo y el stock no pueden ser negativos; el precio de venta debe ser mayor que cero.";
    if (decimal.Round(request.CostPrice, 2) != request.CostPrice || decimal.Round(request.UnitPrice, 2) != request.UnitPrice)
        return "Los precios deben tener como máximo dos decimales.";
    return null;
}

static (DateTime StartUtc, DateTime EndUtc) UtcRangeForLocalDay(DateOnly day)
{
    var localStart = DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Local);
    return (localStart.ToUniversalTime(), localStart.AddDays(1).ToUniversalTime());
}

public partial class Program { }
