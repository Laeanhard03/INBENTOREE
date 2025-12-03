using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MongoDB.Driver;
using Microsoft.AspNetCore.Authorization;
using WebApplication11.Pages;
using Microsoft.AspNetCore.Http;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using System.Net.Http;
using System.Text;
using WebApplication11.Services;

namespace WebApplication11.Pages
{
    [AllowAnonymous]
    public class ShopModel : PageModel
    {
        private readonly IMongoDatabase _db;
        private readonly IConfiguration _config;
        private const string CollectionName = "Items";
        private const string StoreCollectionName = "Stores";
        private const string ChatCollectionName = "Chats";
        private const string NotifCollectionName = "Notifications";

        private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

        public ShopModel(IMongoDatabase db, IConfiguration config)
        {
            _db = db;
            _config = config;
        }

        public Store StoreInfo { get; set; } = new();
        public string CurrentView { get; set; } = "shop";
        public List<Item> Products { get; set; } = new();

        [BindProperty(SupportsGet = true)] public string? SearchTerm { get; set; }
        [BindProperty(SupportsGet = true)] public string? SortOrder { get; set; }
        [BindProperty(SupportsGet = true)] public decimal? MinPrice { get; set; }
        [BindProperty(SupportsGet = true)] public decimal? MaxPrice { get; set; }

        public List<CartItemDetail> CartItems { get; set; } = new();
        public decimal GrandTotal { get; set; }
        public Order? ReceiptOrder { get; set; }

        [BindProperty(SupportsGet = true)]
        public string? StoreId { get; set; }

        public async Task<IActionResult> OnGetAsync(string? view = "shop", string? orderId = null)
        {
            if (string.IsNullOrEmpty(StoreId)) return Page();

            var storeCollection = _db.GetCollection<Store>(StoreCollectionName);
            StoreInfo = await storeCollection.Find(s => s.Id == StoreId).FirstOrDefaultAsync();
            if (StoreInfo == null) return NotFound("Store not found.");

            var session = HttpContext.Session;
            string visitedKey = $"Visited_{StoreId}";
            if (string.IsNullOrEmpty(session.GetString(visitedKey)))
            {
                await CreateNotification(StoreId, "A customer is browsing your store.", "info");
                session.SetString(visitedKey, "true");
            }

            CurrentView = view ?? "shop";

            if (CurrentView == "cart")
            {
                await LoadCartData();
            }
            else if (CurrentView == "receipt" && !string.IsNullOrEmpty(orderId))
            {
                ReceiptOrder = await _db.GetCollection<Order>("Orders").Find(o => o.Id == orderId).FirstOrDefaultAsync();
                if (ReceiptOrder == null) CurrentView = "shop";
            }
            else
            {
                var builder = Builders<Item>.Filter;
                var filter = builder.Eq(i => i.StoreId, StoreId);

                if (!string.IsNullOrEmpty(SearchTerm))
                {
                    var regex = new MongoDB.Bson.BsonRegularExpression(SearchTerm, "i");
                    filter &= builder.Or(
                        builder.Regex("Name", regex),
                        builder.Regex("Category", regex)
                    );
                }

                if (MinPrice.HasValue) filter &= builder.Gte(i => i.Price, MinPrice.Value);
                if (MaxPrice.HasValue) filter &= builder.Lte(i => i.Price, MaxPrice.Value);

                var sort = Builders<Item>.Sort.Ascending(i => i.Position);
                if (SortOrder == "price_asc") sort = Builders<Item>.Sort.Ascending(i => i.Price);
                else if (SortOrder == "price_desc") sort = Builders<Item>.Sort.Descending(i => i.Price);
                else if (SortOrder == "name") sort = Builders<Item>.Sort.Ascending(i => i.Name);

                Products = await _db.GetCollection<Item>(CollectionName).Find(filter).Sort(sort).ToListAsync();
            }

            return Page();
        }

        public async Task<IActionResult> OnPostAddToCartAsync(string itemId, int quantity)
        {
            var session = HttpContext.Session;
            string cartJson = session.GetString("Cart") ?? "[]";
            var cart = JsonSerializer.Deserialize<List<CartItem>>(cartJson) ?? new List<CartItem>();

            if (quantity < 1) quantity = 1;

            var existingItem = cart.FirstOrDefault(c => c.ItemId == itemId);
            if (existingItem != null) existingItem.Quantity += quantity;
            else cart.Add(new CartItem { ItemId = itemId, Quantity = quantity });

            session.SetString("Cart", JsonSerializer.Serialize(cart));

            var item = await _db.GetCollection<Item>(CollectionName).Find(i => i.Id == itemId).FirstOrDefaultAsync();
            if (item != null && !string.IsNullOrEmpty(item.StoreId))
            {
                await CreateNotification(item.StoreId, $"Customer added {quantity}x {item.Name} to cart.", "cart");
            }

            return new JsonResult(new { success = true, count = cart.Sum(c => c.Quantity) });
        }

        public async Task<IActionResult> OnPostCheckoutAsync()
        {
            // Update: LoadCartData now populates CostPrice
            await LoadCartData();
            if (CartItems.Count == 0) return RedirectToPage(new { StoreId, view = "shop" });

            var itemsCollection = _db.GetCollection<Item>("Items");
            var ordersCollection = _db.GetCollection<Order>("Orders");

            var session = HttpContext.Session;
            string cartJson = session.GetString("Cart") ?? "[]";
            var cartSession = JsonSerializer.Deserialize<List<CartItem>>(cartJson) ?? new List<CartItem>();

            foreach (var c in cartSession)
            {
                var item = await itemsCollection.Find(i => i.Id == c.ItemId).FirstOrDefaultAsync();
                if (item != null)
                {
                    if (item.Quantity < c.Quantity)
                    {
                        TempData["error"] = $"Not enough stock for {item.Name}";
                        return RedirectToPage(new { StoreId, view = "cart" });
                    }
                    var update = Builders<Item>.Update.Inc(i => i.Quantity, -c.Quantity);
                    await itemsCollection.UpdateOneAsync(i => i.Id == item.Id, update);
                }
            }

            var newOrder = new Order
            {
                StoreId = StoreId!,
                CustomerName = User.Identity?.Name ?? "Guest",
                // Items now include the 'Cost' property automatically from LoadCartData
                Items = CartItems,
                TotalAmount = GrandTotal,
                OrderCode = "OR-" + new Random().Next(1000, 9999),
                Status = "Pending"
            };

            await ordersCollection.InsertOneAsync(newOrder);
            session.Remove("Cart");
            await CreateNotification(StoreId!, $"New Order {newOrder.OrderCode} received! Total: ₱{GrandTotal:N2}", "order");
            return RedirectToPage(new { StoreId, view = "receipt", orderId = newOrder.Id });
        }

        private async Task LoadCartData()
        {
            var session = HttpContext.Session;
            string cartJson = session.GetString("Cart") ?? "[]";
            var cartSession = JsonSerializer.Deserialize<List<CartItem>>(cartJson) ?? new List<CartItem>();
            var itemsCollection = _db.GetCollection<Item>("Items");

            CartItems = new List<CartItemDetail>();
            foreach (var c in cartSession)
            {
                var dbItem = await itemsCollection.Find(i => i.Id == c.ItemId).FirstOrDefaultAsync();
                if (dbItem != null)
                {
                    // UPDATE: Map CostPrice from DB to the Cart Item
                    CartItems.Add(new CartItemDetail
                    {
                        ItemName = dbItem.Name,
                        Price = dbItem.Price,
                        Cost = dbItem.CostPrice, // SNAPSHOT COST
                        Quantity = c.Quantity
                    });
                }
            }
            GrandTotal = CartItems.Sum(x => x.Total);
        }

        private async Task CreateNotification(string storeId, string msg, string type)
        {
            var notif = new Notification
            {
                StoreId = storeId,
                Message = msg,
                Type = type,
                IsRead = false,
                Timestamp = DateTime.UtcNow
            };
            await _db.GetCollection<Notification>(NotifCollectionName).InsertOneAsync(notif);
        }

        public async Task<IActionResult> OnPostSendUserMessageAsync(string storeId, string guestId, string content)
        {
            var msg = new ChatMessage
            {
                StoreId = storeId,
                GuestId = guestId,
                Sender = "User",
                Content = content,
                Timestamp = DateTime.UtcNow
            };
            await _db.GetCollection<ChatMessage>(ChatCollectionName).InsertOneAsync(msg);
            await CreateNotification(storeId, "New message from customer.", "chat");
            return new JsonResult(new { success = true });
        }

        public async Task<IActionResult> OnGetCheckMessagesAsync(string storeId, string guestId)
        {
            var chats = await _db.GetCollection<ChatMessage>(ChatCollectionName)
                                 .Find(c => c.StoreId == storeId && c.GuestId == guestId)
                                 .SortBy(c => c.Timestamp)
                                 .ToListAsync();
            return new JsonResult(new { messages = chats });
        }

        public async Task<IActionResult> OnPostAiChatAsync(string input, string storeId, string guestId)
        {
            string systemPrompt = "You are Sari, a helpful store assistant. Return ONLY raw JSON: { \"handoff\": boolean, \"reply\": \"string\" }";
            string finalPrompt = $"{systemPrompt}\nUser: {input}";
            string rawAiResponse = await CallGeminiApi(finalPrompt);
            rawAiResponse = rawAiResponse.Replace("```json", "").Replace("```", "").Trim();

            bool handoff = false;
            string reply = "Sorry, I didn't quite get that.";

            try
            {
                var responseObj = JsonSerializer.Deserialize<AiChatResponse>(rawAiResponse, _jsonOptions);
                if (responseObj != null)
                {
                    handoff = responseObj.Handoff;
                    reply = responseObj.Reply;
                }
            }
            catch { reply = "Connecting you to the seller..."; handoff = true; }

            if (handoff)
            {
                var collection = _db.GetCollection<ChatMessage>(ChatCollectionName);
                await collection.InsertOneAsync(new ChatMessage { StoreId = storeId, GuestId = guestId, Sender = "User", Content = input, Timestamp = DateTime.UtcNow });
                await collection.InsertOneAsync(new ChatMessage { StoreId = storeId, GuestId = guestId, Sender = "Sari (AI)", Content = reply, Timestamp = DateTime.UtcNow.AddMilliseconds(500) });
                await CreateNotification(storeId, "Customer requested human assistance.", "chat");
            }
            return new JsonResult(new { reply = reply, handoff = handoff });
        }

        private async Task<string> CallGeminiApi(string prompt)
        {
            string apiKey = EncryptionHelper.Decrypt(_config["Gemini:ApiKey"] ?? string.Empty);
            if (string.IsNullOrEmpty(apiKey)) return "{}";

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
                    return candidates[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "{}";
                }
                return "{}";
            }
            catch { return "{}"; }
        }

        public class AiChatResponse { public bool Handoff { get; set; } public string Reply { get; set; } = string.Empty; }
    }
}