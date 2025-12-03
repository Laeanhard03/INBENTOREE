using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using WebApplication11.Services;

namespace WebApplication11.Pages;

[Authorize]
public class DashModel(IMongoDatabase db, ILogger<DashModel> logger, IConfiguration config) : PageModel
{
    private readonly IMongoDatabase _db = db;
    private readonly ILogger<DashModel> _logger = logger;
    private readonly IConfiguration _config = config;
    private const string CollectionName = "Items";
    private const string StoreCollectionName = "Stores";
    private const string ChatCollectionName = "Chats";
    private const string NotifCollectionName = "Notifications";

    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public List<Item> Items { get; set; } = new();
    public Store CurrentStore { get; set; } = new();

    [BindProperty] public Item NewItem { get; set; } = new();
    [BindProperty] public IFormFile? LogoFile { get; set; }
    [BindProperty] public Item EditItem { get; set; } = new();
    [BindProperty] public string? DeleteId { get; set; }
    [BindProperty] public string[]? ItemsToSwitch { get; set; }
    [BindProperty] public string? ItemsToDelete { get; set; }

    [BindProperty] public Store StoreSettings { get; set; } = new();

    public async Task OnGetAsync()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var storeCollection = _db.GetCollection<Store>(StoreCollectionName);
        var store = await storeCollection.Find(s => s.OwnerId == userId).FirstOrDefaultAsync();

        if (store == null)
        {
            store = new Store
            {
                OwnerId = userId ?? "unknown",
                StoreName = $"{User.Identity?.Name}'s Store",
                ThemeColor = "#4f46e5"
            };
            await storeCollection.InsertOneAsync(store);
        }
        CurrentStore = store;

        Items = await _db.GetCollection<Item>(CollectionName)
                             .Find(i => i.StoreId == store.Id)
                             .SortBy(item => item.Position)
                             .ToListAsync();
    }

    public async Task<IActionResult> OnGetAiInsightAsync(string mode, string input = "")
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var store = await _db.GetCollection<Store>(StoreCollectionName).Find(s => s.OwnerId == userId).FirstOrDefaultAsync();
        var items = await _db.GetCollection<Item>(CollectionName).Find(i => i.StoreId == store.Id).ToListAsync();

        var inventorySummary = string.Join("\n", items.Select(i => $"- {i.Name} ({i.Category}): {i.Quantity} units @ SRP: ${i.Price} / Cost: ${i.CostPrice}"));

        string prompt = "";
        switch (mode)
        {
            case "categorize":
                prompt = $"Categorize this item: '{input}' into exactly ONE category: [Canned Goods, Snacks, Beverages, Toiletries, Condiments, Rice, Household, Others]. Respond ONLY with category name.";
                break;
            case "restock":
                prompt = $"Analyze this inventory:\n{inventorySummary}\nSuggest which items need restocking (< 5). Suggest 3 popular Filipino items to add.";
                break;
            case "design":
                prompt = "Give me 3 creative tips to design a Filipino Sari-Sari store.";
                break;
            case "joke":
                prompt = "Tell me a joke about Sari-Sari stores.";
                break;
            default:
                prompt = $"Analyze this inventory:\n{inventorySummary}\n1. Total Value (Retail vs Cost).";
                break;
        }

        string aiResponse = await CallGeminiApi(prompt);
        if (mode == "categorize") aiResponse = aiResponse.Replace("\"", "").Replace(".", "").Trim();

        return new JsonResult(new { message = aiResponse });
    }

    public async Task<IActionResult> OnGetFetchMessagesAsync()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var store = await _db.GetCollection<Store>(StoreCollectionName).Find(s => s.OwnerId == userId).FirstOrDefaultAsync();
        if (store == null) return new JsonResult(new { messages = new List<object>() });

        var chats = await _db.GetCollection<ChatMessage>(ChatCollectionName)
                             .Find(c => c.StoreId == store.Id)
                             .SortBy(c => c.Timestamp)
                             .ToListAsync();

        return new JsonResult(new { messages = chats });
    }

    public async Task<IActionResult> OnGetFetchNotificationsAsync()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var store = await _db.GetCollection<Store>(StoreCollectionName).Find(s => s.OwnerId == userId).FirstOrDefaultAsync();
        if (store == null) return new JsonResult(new { notifications = new List<object>() });

        var notifs = await _db.GetCollection<Notification>(NotifCollectionName)
                              .Find(n => n.StoreId == store.Id && !n.IsRead)
                              .SortByDescending(n => n.Timestamp)
                              .Limit(20)
                              .ToListAsync();

        return new JsonResult(new { notifications = notifs });
    }

    public async Task<IActionResult> OnPostClearNotificationsAsync()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var store = await _db.GetCollection<Store>(StoreCollectionName).Find(s => s.OwnerId == userId).FirstOrDefaultAsync();
        if (store != null)
        {
            var update = Builders<Notification>.Update.Set(n => n.IsRead, true);
            await _db.GetCollection<Notification>(NotifCollectionName).UpdateManyAsync(n => n.StoreId == store.Id, update);
        }
        return new JsonResult(new { success = true });
    }

    public async Task<IActionResult> OnPostReplyMessageAsync(string guestId, string content)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var store = await _db.GetCollection<Store>(StoreCollectionName).Find(s => s.OwnerId == userId).FirstOrDefaultAsync();
        if (store == null) return BadRequest();

        var msg = new ChatMessage
        {
            StoreId = store.Id,
            GuestId = guestId,
            Sender = "Seller",
            Content = content,
            Timestamp = DateTime.UtcNow
        };

        await _db.GetCollection<ChatMessage>(ChatCollectionName).InsertOneAsync(msg);
        return new JsonResult(new { success = true });
    }

    public async Task<IActionResult> OnPostSeedInventoryAsync()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var store = await _db.GetCollection<Store>(StoreCollectionName).Find(s => s.OwnerId == userId).FirstOrDefaultAsync();
        if (store == null) return RedirectToPage();

        int currentCount = (int)await _db.GetCollection<Item>(CollectionName).CountDocumentsAsync(i => i.StoreId == store.Id);
        var newItems = new List<Item>();
        List<SeedItem> seedItems = null;

        try
        {
            string prompt = "Generate a JSON list of 5 Filipino Sari-Sari store items. Fields: Name, Category, Price, Cost, Quantity.";
            string jsonResponse = await CallGeminiApi(prompt);
            int start = jsonResponse.IndexOf('[');
            int end = jsonResponse.LastIndexOf(']');
            if (start >= 0 && end > start)
            {
                jsonResponse = jsonResponse.Substring(start, end - start + 1);
                seedItems = JsonSerializer.Deserialize<List<SeedItem>>(jsonResponse, _jsonOptions);
            }
        }
        catch { }

        if (seedItems == null || seedItems.Count == 0)
        {
            seedItems = new List<SeedItem> { new() { Name = "Sample Item", Category = "General", Price = 10, Cost = 8, Quantity = 50 } };
        }

        foreach (var seed in seedItems)
        {
            newItems.Add(new Item
            {
                StoreId = store.Id,
                Name = seed.Name,
                Category = seed.Category ?? "General",
                Price = seed.Price,
                CostPrice = seed.Cost > 0 ? seed.Cost : seed.Price * 0.8m,
                Quantity = seed.Quantity,
                Position = ++currentCount,
                CreatedAt = DateTime.UtcNow
            });
        }

        if (newItems.Count > 0) await _db.GetCollection<Item>(CollectionName).InsertManyAsync(newItems);
        return RedirectToPage();
    }

    private async Task<string> CallGeminiApi(string prompt)
    {
        string apiKey = EncryptionHelper.Decrypt(_config["Gemini:ApiKey"] ?? string.Empty);
        if (string.IsNullOrEmpty(apiKey)) return "AI Error: Missing API Key";

        string endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.0-flash:generateContent?key={apiKey}";
        try
        {
            using var client = new HttpClient();
            var requestBody = new { contents = new[] { new { parts = new[] { new { text = prompt } } } } };
            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await client.PostAsync(endpoint, content);
            var responseString = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseString);
            if (doc.RootElement.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
            {
                return candidates[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "";
            }
            return "";
        }
        catch { return ""; }
    }

    public async Task<IActionResult> OnPostUpdateStoreAsync()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var update = Builders<Store>.Update.Set(s => s.StoreName, StoreSettings.StoreName).Set(s => s.ThemeColor, StoreSettings.ThemeColor).Set(s => s.Description, StoreSettings.Description);
        await _db.GetCollection<Store>(StoreCollectionName).UpdateOneAsync(s => s.OwnerId == userId, update);
        TempData["success"] = "Store updated!";
        return RedirectToPage();
    }
    public async Task<IActionResult> OnPostLogoutAsync() { await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme); return RedirectToPage("/Index"); }
    public async Task<IActionResult> OnPostAddAsync()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var store = await _db.GetCollection<Store>(StoreCollectionName).Find(s => s.OwnerId == userId).FirstOrDefaultAsync();
        if (store == null) return RedirectToPage();
        if (LogoFile != null && LogoFile.Length > 0) { NewItem.LogoContentType = LogoFile.ContentType; using var ms = new MemoryStream(); await LogoFile.CopyToAsync(ms); NewItem.LogoData = ms.ToArray(); }
        var highest = await _db.GetCollection<Item>(CollectionName).Find(i => i.StoreId == store.Id).SortByDescending(i => i.Position).Limit(1).FirstOrDefaultAsync();
        NewItem.Position = (highest?.Position ?? 0) + 1; NewItem.StoreId = store.Id;
        await _db.GetCollection<Item>(CollectionName).InsertOneAsync(NewItem); return RedirectToPage();
    }
    public async Task<IActionResult> OnPostEdit()
    {
        if (string.IsNullOrEmpty(EditItem.Id)) return RedirectToPage();
        var col = _db.GetCollection<Item>(CollectionName);
        var ex = await col.Find(x => x.Id == EditItem.Id).FirstOrDefaultAsync();
        if (ex == null) return NotFound();
        EditItem.Position = ex.Position; EditItem.CreatedAt = ex.CreatedAt; EditItem.StoreId = ex.StoreId;
        if (LogoFile != null && LogoFile.Length > 0) { EditItem.LogoContentType = LogoFile.ContentType; using var ms = new MemoryStream(); await LogoFile.CopyToAsync(ms); EditItem.LogoData = ms.ToArray(); }
        else { EditItem.LogoContentType = ex.LogoContentType; EditItem.LogoData = ex.LogoData; }
        await col.ReplaceOneAsync(x => x.Id == EditItem.Id, EditItem); return RedirectToPage();
    }
    public async Task<IActionResult> OnPostSwapAsync()
    {
        if (ItemsToSwitch?.Length != 2) return RedirectToPage();
        var col = _db.GetCollection<Item>(CollectionName);
        var sw = await col.Find(Builders<Item>.Filter.In(i => i.Id, ItemsToSwitch)).ToListAsync();
        if (sw.Count != 2) return RedirectToPage();
        (sw[0].Position, sw[1].Position) = (sw[1].Position, sw[0].Position);
        var ups = new[] {
            new UpdateOneModel<Item>(Builders<Item>.Filter.Eq(i => i.Id, sw[0].Id), Builders<Item>.Update.Set(i => i.Position, sw[0].Position)),
            new UpdateOneModel<Item>(Builders<Item>.Filter.Eq(i => i.Id, sw[1].Id), Builders<Item>.Update.Set(i => i.Position, sw[1].Position))
        };
        await col.BulkWriteAsync(ups); return RedirectToPage();
    }
    public async Task<IActionResult> OnPostReIndex()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var store = await _db.GetCollection<Store>(StoreCollectionName).Find(s => s.OwnerId == userId).FirstOrDefaultAsync();
        var col = _db.GetCollection<Item>(CollectionName);
        var all = await col.Find(i => i.StoreId == store.Id).SortBy(i => i.Position).ThenBy(i => i.Id).ToListAsync();
        var b = new List<WriteModel<Item>>(); int p = 1;
        foreach (var i in all) { if (i.Position != p) { b.Add(new UpdateOneModel<Item>(Builders<Item>.Filter.Eq(x => x.Id, i.Id), Builders<Item>.Update.Set(x => x.Position, p))); } p++; }
        if (b.Count > 0) await col.BulkWriteAsync(b); return RedirectToPage();
    }
    public async Task<IActionResult> OnPostMassDeleteAsync()
    {
        if (string.IsNullOrEmpty(ItemsToDelete)) return RedirectToPage();
        await _db.GetCollection<Item>(CollectionName).DeleteManyAsync(Builders<Item>.Filter.In(i => i.Id, ItemsToDelete.Split(',', StringSplitOptions.RemoveEmptyEntries)));
        return RedirectToPage();
    }
    public async Task<IActionResult> OnPostDelete()
    {
        if (string.IsNullOrEmpty(DeleteId)) return RedirectToPage();
        await _db.GetCollection<Item>(CollectionName).DeleteOneAsync(x => x.Id == DeleteId); return RedirectToPage();
    }
}

// --- SHARED MODELS ---
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
public class Store
{
    [BsonId][BsonRepresentation(MongoDB.Bson.BsonType.ObjectId)] public string? Id { get; set; }
    public string OwnerId { get; set; } = string.Empty;
    public string StoreName { get; set; } = "My Sari-Sari Store";
    public string Description { get; set; } = "Welcome to my online tindahan!";
    public string ThemeColor { get; set; } = "#4f46e5";
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

// UPDATE: Added Cost property to CartItemDetail for accurate Profit Reporting
public class CartItemDetail
{
    public string ItemName { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal Price { get; set; } // Selling Price
    public decimal Cost { get; set; } // Cost Price at time of sale
    public decimal Total => Quantity * Price;
}
public class CartItem { public string ItemId { get; set; } = string.Empty; public int Quantity { get; set; } }
public class SeedItem { public string Name { get; set; } = ""; public string Category { get; set; } = ""; public decimal Price { get; set; } public decimal Cost { get; set; } public int Quantity { get; set; } }