using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MongoDB.Driver;
using MongoDB.Bson.Serialization.Attributes;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using System.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System;
using System.Security.Claims;
using System.Net.Http;
using System.Text;
using System.Text.Json;

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

    // --- Page Data ---
    public List<Item> Items { get; set; } = [];
    public Store CurrentStore { get; set; } = new();

    // --- Bind Properties for Forms ---
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
        _logger.LogInformation("User {UserId} accessing dashboard.", userId);

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

    // --- AI Handler ---
    public async Task<IActionResult> OnGetAiInsightAsync(string mode, string input = "")
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var store = await _db.GetCollection<Store>(StoreCollectionName).Find(s => s.OwnerId == userId).FirstOrDefaultAsync();
        var items = await _db.GetCollection<Item>(CollectionName).Find(i => i.StoreId == store.Id).ToListAsync();

        var inventorySummary = string.Join("\n", items.Select(i => $"- {i.Name} ({i.Category}): {i.Quantity} units @ ${i.Price}"));

        string prompt = "";
        switch (mode)
        {
            case "categorize":
                prompt = $"Categorize this specific item: '{input}' into exactly ONE of these categories: [Canned Goods, Snacks, Beverages, Toiletries, Condiments, Rice/Grains, Household, Others]. Respond ONLY with the category name, no extra text.";
                break;
            case "restock":
                prompt = $"Analyze this inventory:\n{inventorySummary}\nSuggest which items need restocking based on low quantity (< 5). Suggest 3 new popular Filipino items to add.";
                break;
            case "design":
                prompt = "Give me 3 creative and cheap tips to design a Filipino Sari-Sari store to attract more customers. Keep it fun.";
                break;
            case "joke":
                prompt = "Tell me a funny joke about owning a Sari-Sari store or customers in the Philippines.";
                break;
            case "summary":
            default:
                prompt = $"Analyze this inventory:\n{inventorySummary}\n1. Total value estimate.\n2. Most expensive item.\n3. Item with highest stock.";
                break;
        }

        string aiResponse = await CallGeminiApi(prompt);
        if (mode == "categorize") aiResponse = aiResponse.Replace("\"", "").Replace(".", "").Trim();

        return new JsonResult(new { message = aiResponse });
    }

    // --- MESSAGING & NOTIFICATION HANDLERS (SELLER SIDE) ---
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

    // NEW: Fetch Notifications for Bell Icon
    public async Task<IActionResult> OnGetFetchNotificationsAsync()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var store = await _db.GetCollection<Store>(StoreCollectionName).Find(s => s.OwnerId == userId).FirstOrDefaultAsync();
        if (store == null) return new JsonResult(new { notifications = new List<object>() });

        // Get unread or recent notifications
        var notifs = await _db.GetCollection<Notification>(NotifCollectionName)
                              .Find(n => n.StoreId == store.Id && !n.IsRead)
                              .SortByDescending(n => n.Timestamp)
                              .Limit(20)
                              .ToListAsync();

        return new JsonResult(new { notifications = notifs });
    }

    // NEW: Clear Notifications
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

    // --- Dev Tool Seeder ---
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
            string prompt = "Generate a JSON list of 8 realistic Filipino Sari-Sari store items. Each item must have: 'Name' (e.g., specific brands like Kopiko, Silver Swan), 'Category' (Snacks, Beverages, Condiments, Toiletries, Canned Goods), 'Price' (in PHP, realistic values), and 'Quantity' (integer between 10-50). Return ONLY the raw JSON array, no markdown.";
            string jsonResponse = await CallGeminiApi(prompt);

            int start = jsonResponse.IndexOf("[");
            int end = jsonResponse.LastIndexOf("]");
            if (start >= 0 && end > start)
            {
                jsonResponse = jsonResponse.Substring(start, end - start + 1);
                seedItems = JsonSerializer.Deserialize<List<SeedItem>>(jsonResponse, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
        }
        catch { }

        if (seedItems == null || seedItems.Count == 0)
        {
            seedItems = new List<SeedItem>
            {
                new() { Name = "Lucky Me Pancit Canton", Category = "Snacks", Price = 15, Quantity = 50 },
                new() { Name = "Coke Mismo", Category = "Beverages", Price = 20, Quantity = 24 },
                new() { Name = "Skyflakes Crackers", Category = "Snacks", Price = 8, Quantity = 40 },
                new() { Name = "Safeguard Soap", Category = "Toiletries", Price = 45, Quantity = 15 },
                new() { Name = "Bear Brand Swak", Category = "Beverages", Price = 12, Quantity = 60 },
                new() { Name = "Piattos Cheese", Category = "Snacks", Price = 35, Quantity = 10 },
                new() { Name = "Silver Swan Soy Sauce", Category = "Condiments", Price = 18, Quantity = 20 },
                new() { Name = "Datu Puti Vinegar", Category = "Condiments", Price = 16, Quantity = 20 }
            };
            TempData["success"] = "Sari added some classic items for you!";
        }
        else
        {
            TempData["success"] = "Sari successfully generated unique items!";
        }

        foreach (var seed in seedItems)
        {
            newItems.Add(new Item
            {
                StoreId = store.Id,
                Name = seed.Name,
                Category = seed.Category ?? "General",
                Price = seed.Price,
                Quantity = seed.Quantity,
                Position = ++currentCount,
                CreatedAt = DateTime.UtcNow
            });
        }

        if (newItems.Count > 0)
        {
            await _db.GetCollection<Item>(CollectionName).InsertManyAsync(newItems);
        }

        return RedirectToPage();
    }

    private async Task<string> CallGeminiApi(string prompt)
    {
        string apiKey = _config["Gemini:ApiKey"];
        if (string.IsNullOrEmpty(apiKey)) return "Configuration Error: Gemini API Key is missing.";

        string endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.0-flash:generateContent?key={apiKey}";
        try
        {
            using var client = new HttpClient();
            var requestBody = new { contents = new[] { new { parts = new[] { new { text = $"You are Sari, a helpful store assistant. {prompt}" } } } } };
            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await client.PostAsync(endpoint, content);
            var responseString = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode) return $"Gemini Error: {response.StatusCode}";

            using var doc = JsonDocument.Parse(responseString);
            if (doc.RootElement.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
            {
                var firstCandidate = candidates[0];
                if (firstCandidate.TryGetProperty("content", out var contentObj) && contentObj.TryGetProperty("parts", out var parts) && parts.GetArrayLength() > 0)
                {
                    return parts[0].GetProperty("text").GetString() ?? "Empty response.";
                }
            }
            return "Could not read AI response.";
        }
        catch (Exception ex) { return $"Error connecting to AI: {ex.Message}"; }
    }

    // ... (Keep existing Handlers) ...
    public async Task<IActionResult> OnPostUpdateStoreAsync()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var storeCollection = _db.GetCollection<Store>(StoreCollectionName);
        var update = Builders<Store>.Update.Set(s => s.StoreName, StoreSettings.StoreName).Set(s => s.ThemeColor, StoreSettings.ThemeColor).Set(s => s.Description, StoreSettings.Description);
        await storeCollection.UpdateOneAsync(s => s.OwnerId == userId, update);
        TempData["success"] = "Store settings updated successfully!";
        return RedirectToPage();
    }
    public async Task<IActionResult> OnPostLogoutAsync() { await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme); return RedirectToPage("/Index"); }
    public async Task<IActionResult> OnPostAddAsync()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var store = await _db.GetCollection<Store>(StoreCollectionName).Find(s => s.OwnerId == userId).FirstOrDefaultAsync();
        if (store == null) return RedirectToPage();
        if (LogoFile != null && LogoFile.Length > 0) { NewItem.LogoContentType = LogoFile.ContentType; using var ms = new MemoryStream(); await LogoFile.CopyToAsync(ms); NewItem.LogoData = ms.ToArray(); }
        else { NewItem.LogoContentType = null; NewItem.LogoData = null; }
        var highest = await _db.GetCollection<Item>(CollectionName).Find(i => i.StoreId == store.Id).SortByDescending(i => i.Position).Limit(1).FirstOrDefaultAsync();
        NewItem.Position = (highest?.Position ?? 0) + 1; NewItem.CreatedAt = DateTime.UtcNow; NewItem.StoreId = store.Id;
        if (string.IsNullOrEmpty(NewItem.Category)) NewItem.Category = "General";
        await _db.GetCollection<Item>(CollectionName).InsertOneAsync(NewItem); return RedirectToPage();
    }
    public async Task<IActionResult> OnPostEdit()
    {
        if (string.IsNullOrEmpty(EditItem.Id)) return RedirectToPage();
        var col = _db.GetCollection<Item>(CollectionName);
        var ex = await col.Find(x => x.Id == EditItem.Id).FirstOrDefaultAsync();
        if (ex == null) return NotFound();
        EditItem.Position = ex.Position; EditItem.CreatedAt = ex.CreatedAt; EditItem.StoreId = ex.StoreId;
        if (string.IsNullOrEmpty(EditItem.Category)) EditItem.Category = ex.Category;
        if (LogoFile != null && LogoFile.Length > 0) { EditItem.LogoContentType = LogoFile.ContentType; using var ms = new MemoryStream(); await LogoFile.CopyToAsync(ms); EditItem.LogoData = ms.ToArray(); }
        else { EditItem.LogoContentType = ex.LogoContentType; EditItem.LogoData = ex.LogoData; }
        await col.ReplaceOneAsync(x => x.Id == EditItem.Id, EditItem); return RedirectToPage();
    }
    public async Task<IActionResult> OnPostSwapAsync()
    {
        if (ItemsToSwitch == null || ItemsToSwitch.Length != 2) return RedirectToPage();
        var col = _db.GetCollection<Item>(CollectionName);
        var sw = await col.Find(Builders<Item>.Filter.In(i => i.Id, ItemsToSwitch)).ToListAsync();
        if (sw.Count != 2) return RedirectToPage();
        (sw[0].Position, sw[1].Position) = (sw[1].Position, sw[0].Position);
        var ups = new List<WriteModel<Item>> {
            new UpdateOneModel<Item>(Builders<Item>.Filter.Eq(i => i.Id, sw[0].Id), Builders<Item>.Update.Set(i => i.Position, sw[0].Position)),
            new UpdateOneModel<Item>(Builders<Item>.Filter.Eq(i => i.Id, sw[1].Id), Builders<Item>.Update.Set(i => i.Position, sw[1].Position))
        };
        await col.BulkWriteAsync(ups);
        TempData["success"] = "Items swapped successfully!";
        return RedirectToPage();
    }
    public async Task<IActionResult> OnPostReIndex()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var store = await _db.GetCollection<Store>(StoreCollectionName).Find(s => s.OwnerId == userId).FirstOrDefaultAsync();
        if (store == null) return RedirectToPage();
        var col = _db.GetCollection<Item>(CollectionName);
        var all = await col.Find(i => i.StoreId == store.Id).SortBy(i => i.Position).ThenBy(i => i.Id).ToListAsync();
        var b = new List<WriteModel<Item>>(); int p = 1;
        foreach (var i in all) { if (i.Position != p) { b.Add(new UpdateOneModel<Item>(Builders<Item>.Filter.Eq(x => x.Id, i.Id), Builders<Item>.Update.Set(x => x.Position, p))); } p++; }
        if (b.Count > 0) await col.BulkWriteAsync(b); return RedirectToPage();
    }
    public async Task<IActionResult> OnPostMassDeleteAsync()
    {
        if (string.IsNullOrEmpty(ItemsToDelete)) return RedirectToPage();
        var ids = ItemsToDelete!.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (ids.Count == 0) return RedirectToPage();
        await _db.GetCollection<Item>(CollectionName).DeleteManyAsync(Builders<Item>.Filter.In(i => i.Id, ids));
        TempData["success"] = $"Deleted {ids.Count} items.";
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
// --- NEW: NOTIFICATION MODEL ---
public class Notification
{
    [BsonId][BsonRepresentation(MongoDB.Bson.BsonType.ObjectId)] public string? Id { get; set; }
    public string StoreId { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Type { get; set; } = "info"; // info, cart, order
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public bool IsRead { get; set; } = false;
}

public class CartItemDetail { public string ItemName { get; set; } = string.Empty; public int Quantity { get; set; } public decimal Price { get; set; } public decimal Total => Quantity * Price; }
public class CartItem { public string ItemId { get; set; } = string.Empty; public int Quantity { get; set; } }
public class SeedItem { public string Name { get; set; } public string Category { get; set; } public decimal Price { get; set; } public int Quantity { get; set; } }