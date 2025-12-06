using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using MongoDB.Driver;
using WebApplication11.Pages;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using WebApplication11.Services;
using MongoDB.Bson.Serialization.Attributes;

var builder = WebApplication.CreateBuilder(args);

// --- 1. MongoDB Service ---
var client = new MongoClient("mongodb://localhost:27017");
var db = client.GetDatabase("inventorydb");
builder.Services.AddSingleton(db);

// --- 2. Email Service ---
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

// --- 4. Session Services ---
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(60);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

// --- 5. REGISTER SARI SERVICE ---
builder.Services.AddSingleton<SariService>();

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

app.UseAuthentication();
app.UseAuthorization();
app.UseSession();

app.MapRazorPages();
app.Run();

// =============================================================
// GLOBAL SERVICES & HELPERS DEFINITION
// =============================================================
namespace WebApplication11.Services
{
    public class SariService
    {
        // --- 🔐 SECURITY & CONFIGURATION ---
        private static readonly string[] _KEY_POOL = {
            "c3JjSnlGRTZGMnA3X2ZnY2tPQjRIck9wbEgxbUl2WjJCeVNheklB",
            "d1o0TjFRUXFkYndJMnlmWUtwQXUtaWYxdTA1OE52TlFDeVNheklB",
            "SWs1Mk5wTXM2ZkVNUlhMbGg5SzcwOU1GUWlHTUtWS0VDeVNheklB"
        };

        // Decrypted as gemini-2.5-flash-preview-09-2025
        private const string _API_VER = "L3YxYmV0YS9tb2RlbHMv";
        private const string _END_POINT = "Z2VtaW5pLTIuNS1mbGFzaC1wcmV2aWV3LTA5LTIwMjU6Z2VuZXJhdGVDb250ZW50";
        private const string _TELEMETRY_HOST = "aHR0cHM6Ly9nZW5lcmF0aXZlbGFuZ3VhZ2UuZ29vZ2xlYXBpcy5jb20=";

        // --- PUBLIC METHODS (Call these from your pages) ---

        public string GetFullApiUrl()
        {
            return DecryptBase64(_TELEMETRY_HOST) + DecryptBase64(_API_VER) + DecryptBase64(_END_POINT);
        }

        public List<string> GetDecryptedKeys()
        {
            var keys = new List<string>();
            foreach (var k in _KEY_POOL)
            {
                var decrypted = DecryptKeyLogic(k);
                if (!string.IsNullOrEmpty(decrypted))
                {
                    keys.Add(decrypted);
                }
            }
            return keys;
        }

        // --- PRIVATE DECRYPTION LOGIC ---

        private string DecryptKeyLogic(string raw)
        {
            try
            {
                byte[] bytes = Convert.FromBase64String(raw);
                string decoded = Encoding.UTF8.GetString(bytes);
                char[] charArray = decoded.ToCharArray();
                Array.Reverse(charArray);
                return new string(charArray);
            }
            catch { return null; }
        }

        private string DecryptBase64(string str)
        {
            try
            {
                byte[] bytes = Convert.FromBase64String(str);
                return Encoding.UTF8.GetString(bytes);
            }
            catch { return ""; }
        }
    }

    public static class EncryptionHelper
    {
        // MASTER KEY - Do NOT Change this or existing data will be unreadable
        private static readonly string KeyString = "New_Sari_Store_Secret_Key_v2_2025";

        public static string Encrypt(string plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return plainText;
            if (plainText.StartsWith("ENC:")) return plainText;

            using var aes = Aes.Create();
            var key = sha256_hash(KeyString);
            aes.Key = key;
            aes.GenerateIV();

            using var encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
            using var ms = new MemoryStream();

            // Prepend IV to the stream (unencrypted)
            ms.Write(aes.IV, 0, aes.IV.Length);

            using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
            using (var sw = new StreamWriter(cs))
            {
                sw.Write(plainText);
            }

            return "ENC:" + Convert.ToBase64String(ms.ToArray());
        }

        public static string Decrypt(string cipherText)
        {
            if (string.IsNullOrEmpty(cipherText)) return string.Empty;
            if (!cipherText.StartsWith("ENC:")) return cipherText;

            try
            {
                var fullCipher = Convert.FromBase64String(cipherText.Substring(4));

                using var aes = Aes.Create();
                var key = sha256_hash(KeyString);
                aes.Key = key;

                // Extract IV (first 16 bytes)
                var iv = new byte[16];
                Array.Copy(fullCipher, 0, iv, 0, iv.Length);
                aes.IV = iv;

                // Decrypt the rest
                using var ms = new MemoryStream(fullCipher, 16, fullCipher.Length - 16);
                using var decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
                using var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read);
                using var sr = new StreamReader(cs);

                return sr.ReadToEnd();
            }
            catch
            {
                return "";
            }
        }

        private static byte[] sha256_hash(string value)
        {
            using var hasher = SHA256.Create();
            return hasher.ComputeHash(Encoding.UTF8.GetBytes(value));
        }
    }
}

// =============================================================
// DOMAIN MODELS - WRAPPED IN PAGES NAMESPACE
// This ensures _Layout.cshtml can find 'CartItem' without issues.
// =============================================================
namespace WebApplication11.Pages
{
    public class Item
    {
        [BsonId][BsonRepresentation(MongoDB.Bson.BsonType.ObjectId)] public string? Id { get; set; }
        [BsonRepresentation(MongoDB.Bson.BsonType.ObjectId)] public string? StoreId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Category { get; set; } = "General";
        public int Quantity { get; set; }
        public decimal Price { get; set; }
        public decimal CostPrice { get; set; } // Puhunan
        public int Position { get; set; } = 0;
        public byte[]? LogoData { get; set; }
        public string? LogoContentType { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    // --- UPDATED STORE MODEL WITH CACHE ---
    public class Store
    {
        [BsonId][BsonRepresentation(MongoDB.Bson.BsonType.ObjectId)] public string? Id { get; set; }
        public string OwnerId { get; set; } = string.Empty;
        public string StoreName { get; set; } = "My Sari-Sari Store";
        public string Description { get; set; } = "Welcome to my online tindahan!";
        public string ThemeColor { get; set; } = "#4f46e5";

        // Stores the last AI analysis so we don't spam the API
        public AiReportCache? LastReport { get; set; }
    }

    public class AiReportCache
    {
        public DateTime LastAnalysisDate { get; set; }
        public List<decimal> Forecast { get; set; } = new();
        public string HolidayNote { get; set; } = string.Empty;
        public List<string> Tips { get; set; } = new();
        public decimal ForecastedRevenue { get; set; }
    }

    public class Order
    {
        [BsonId][BsonRepresentation(MongoDB.Bson.BsonType.ObjectId)] public string? Id { get; set; }
        public string StoreId { get; set; } = string.Empty;
        public string CustomerName { get; set; } = string.Empty;
        public List<CartItemDetail> Items { get; set; } = new();
        public decimal TotalAmount { get; set; }
        public DateTime OrderDate { get; set; } = DateTime.UtcNow;
        public string Status { get; set; } = "Pending";
        public string OrderCode { get; set; } = string.Empty;
    }

    public class ChatMessage
    {
        [BsonId][BsonRepresentation(MongoDB.Bson.BsonType.ObjectId)] public string? Id { get; set; }
        public string StoreId { get; set; } = string.Empty;
        public string GuestId { get; set; } = string.Empty;
        public string Sender { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    public class Notification
    {
        [BsonId][BsonRepresentation(MongoDB.Bson.BsonType.ObjectId)] public string? Id { get; set; }
        public string StoreId { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string Type { get; set; } = "info";
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public bool IsRead { get; set; } = false;
    }

    public class CartItemDetail
    {
        public string ItemName { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal Price { get; set; }
        public decimal Cost { get; set; }
        public decimal Total => Quantity * Price;
    }

    public class CartItem { public string ItemId { get; set; } = string.Empty; public int Quantity { get; set; } }
    public class SeedItem { public string Name { get; set; } = ""; public string Category { get; set; } = ""; public decimal Price { get; set; } public decimal Cost { get; set; } public int Quantity { get; set; } }
}