using Microsoft.AspNetCore.Authentication.Cookies;
using MongoDB.Driver;
using WebApplication11.Pages; // ADDED: To find the MailSettings class

var builder = WebApplication.CreateBuilder(args);

// ------------------------------------
// MongoDB Service Configuration
// ------------------------------------
var client = new MongoClient("mongodb://localhost:27017");
var db = client.GetDatabase("inventorydb");

builder.Services.AddSingleton(db);

// ------------------------------------
// NEW: Email Service Configuration
// ------------------------------------
// Binds the "MailSettings" section from appsettings.json to the MailSettings class
// We defined the MailSettings class inside Index.cshtml.cs
builder.Services.Configure<MailSettings>(builder.Configuration.GetSection("MailSettings"));

// ------------------------------------
// Authentication Service Configuration
// ------------------------------------
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Index";
        options.LogoutPath = "/Index";
        options.AccessDeniedPath = "/AccessDenied";
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;
    });

builder.Services.AddRazorPages();

// ------------------------------------
// Application Build and Run
// ------------------------------------
var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

// --- CRITICAL: Auth middleware must be in this order ---
app.UseAuthentication(); // 1. Identifies who the user is
app.UseAuthorization();  // 2. Checks if they are allowed to access a resource

app.MapRazorPages();
app.Run();