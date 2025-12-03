using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using MongoDB.Driver;
using WebApplication11.Pages;
using System.IO;
using System.Security.Cryptography;
using System.Text;

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

// --- GLOBAL HELPER: ENCRYPTION ---
namespace WebApplication11.Services
{
    public static class EncryptionHelper
    {
        // 32-byte Key. In prod, use Environment Variable.
        private static readonly string Key = "SariSariStoreSecureKey2025!!xxYY";

        public static string Encrypt(string plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return plainText;
            if (plainText.StartsWith("ENC:")) return plainText;

            using var aes = Aes.Create();
            aes.Key = Encoding.UTF8.GetBytes(Key);
            aes.GenerateIV();

            using var encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
            using var ms = new MemoryStream();

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
            if (!cipherText.StartsWith("ENC:")) return cipherText; // Return raw if not encrypted

            try
            {
                var fullCipher = Convert.FromBase64String(cipherText.Substring(4));

                using var aes = Aes.Create();
                aes.Key = Encoding.UTF8.GetBytes(Key);

                var iv = new byte[16];
                Array.Copy(fullCipher, 0, iv, 0, iv.Length);
                aes.IV = iv;

                using var ms = new MemoryStream(fullCipher, 16, fullCipher.Length - 16);
                using var decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
                using var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read);
                using var sr = new StreamReader(cs);

                return sr.ReadToEnd();
            }
            catch
            {
                return ""; // Fail silently
            }
        }
    }
}