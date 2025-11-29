using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using MongoDB.Driver;
using WebApplication11.Pages;

var builder = WebApplication.CreateBuilder(args);

// --- 1. MongoDB Service ---
// Connection string from original source
var client = new MongoClient("mongodb://localhost:27017");
var db = client.GetDatabase("inventorydb");
builder.Services.AddSingleton(db);

// --- 2. Email Service ---
// Configuration setup
builder.Services.Configure<MailSettings>(builder.Configuration.GetSection("MailSettings"));

// --- 3. Authentication Services ---
builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
})
.AddCookie(options =>
{
    options.LoginPath = "/Index";
    options.LogoutPath = "/Index";
    options.AccessDeniedPath = "/AccessDenied";
    options.ExpireTimeSpan = TimeSpan.FromDays(7);
    options.SlidingExpiration = true;
})
.AddGoogle(googleOptions =>
{
    googleOptions.ClientId = builder.Configuration["Authentication:Google:ClientId"] ?? "YOUR_CLIENT_ID";
    googleOptions.ClientSecret = builder.Configuration["Authentication:Google:ClientSecret"] ?? "YOUR_CLIENT_SECRET";
});

// --- 4. Session Services (NEW: Required for Shopping Cart) ---
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(60);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

builder.Services.AddRazorPages();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

// --- Middleware Order is Critical ---
app.UseAuthentication();
app.UseAuthorization();
app.UseSession(); // <--- Enable Session for Cart
// ------------------------------------

app.MapRazorPages();
app.Run();